"""Check the documentation map locally; no packages or hosted services are needed."""

import argparse
import re
import subprocess
import sys
from pathlib import Path, PurePosixPath
from urllib.parse import unquote

SKIP = {".git", ".tools", ".models", ".venv", "artifacts", "bin", "obj",
        "node_modules", "__pycache__"}
SOURCE_SUFFIXES = {".cs", ".axaml", ".csproj", ".slnx", ".ps1", ".py", ".json",
                   ".yml", ".yaml", ".sh", ".nsi", ".props", ".targets",
                   ".xml", ".config", ".desktop"}
LINK = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
CODE = re.compile(r"^\*\*Code:\*\*\s*(.*)$", re.MULTILINE)


def visible(path):
    return not any(part in SKIP for part in PurePosixPath(path).parts)


def unfenced(text):
    lines = []
    fence = None
    for line in text.splitlines():
        stripped = line.lstrip()
        marker = re.match(r"(`{3,}|~{3,})", stripped)
        if marker:
            if fence is None:
                fence = marker.group(1)[0]
            elif marker.group(1)[0] == fence:
                fence = None
            continue
        if fence is None:
            lines.append(line)
    return "\n".join(lines)


def local_target(root, page, target):
    target = unquote(target.split("#", 1)[0]).replace("\\", "/")
    if not target or re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target):
        return None
    resolved = (page.parent / target).resolve()
    if not resolved.is_relative_to(root):
        return None
    return resolved


def inspect(root):
    errors = []
    maps = []
    docs = root / "docs"
    pages = sorted(p for p in docs.rglob("*.md")
                   if visible(p.relative_to(root).as_posix()))
    pages += [p for p in (root / "README.md", root / "AGENTS.md",
                         root / "CONTRIBUTING.md", root / ".github/copilot-instructions.md")
              if p.is_file()]
    for page in pages:
        label = page.relative_to(root).as_posix()
        text = unfenced(page.read_text(encoding="utf-8-sig"))
        if page.is_relative_to(docs):
            required = [
                (r"^# .+", "H1 title"),
                (r"^\*\*Status:\*\* (Implemented|Partial|Planned|Superseded)\s*$", "Status"),
                (r"^\*\*Last Updated:\*\* \d{4}-\d{2}-\d{2}\s*$", "Last Updated"),
                (r"^\*\*Audience:\*\* .+", "Audience"),
                (r"^\*\*Last verified:\*\* \d{4}-\d{2}-\d{2} against [0-9a-f]{7,40}\s*$",
                 "Last verified"),
            ]
            for pattern, field in required:
                if not re.search(pattern, text, re.MULTILINE):
                    errors.append(f"{label}: missing or invalid {field}")
        for field in CODE.findall(text):
            paths = re.findall(r"`([^`]+)`", field)
            if not paths:
                errors.append(f"{label}: Code must contain backtick paths")
            for raw in paths:
                name = raw.replace("\\", "/").rstrip("/")
                path = PurePosixPath(name)
                if (path.is_absolute() or ".." in path.parts or
                        re.match(r"^[a-zA-Z]:", name) or any(c in name for c in "*?")):
                    errors.append(f"{label}: invalid Code path {raw}")
                    continue
                resolved = (root / name).resolve()
                if not resolved.is_relative_to(root) or not resolved.exists():
                    errors.append(f"{label}: Code path does not exist: {raw}")
                maps.append((name, label))
        for target in LINK.findall(text):
            resolved = local_target(root, page, target)
            if resolved is not None and not resolved.exists():
                errors.append(f"{label}: link target does not exist: {target}")

    folders = [docs] + sorted(p for p in docs.rglob("*") if p.is_dir()
                              and visible(p.relative_to(root).as_posix()))
    for folder in folders:
        index = folder / "README.md"
        label = folder.relative_to(root).as_posix()
        if not index.is_file():
            errors.append(f"{label}: missing folder README.md")
            continue
        text = unfenced(index.read_text(encoding="utf-8-sig"))
        targets = {local_target(root, index, target) for target in LINK.findall(text)}
        for child in sorted(folder.iterdir()):
            if child.name in SKIP or child == index:
                continue
            if child.is_dir():
                listed = child.resolve() in targets or (child / "README.md").resolve() in targets
            elif child.suffix.lower() == ".md":
                listed = child.resolve() in targets
            else:
                continue
            if not listed:
                errors.append(f"{label}/README.md: missing index entry for {child.name}")
    return errors, maps


def git(root, *args):
    result = subprocess.run(["git", "-C", str(root), *args], capture_output=True,
                            text=True, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "git command failed")
    return result.stdout


def changes(root, reference):
    # -z keeps spaces, tabs, and non-ASCII path names intact.
    fields = git(root, "diff", "--name-status", "-z", "-M", reference).split("\0")
    rows = []
    i = 0
    while i < len(fields) and fields[i]:
        status, old = fields[i:i + 2]
        i += 2
        if status.startswith(("R", "C")):
            new = fields[i]
            i += 1
        else:
            new = old
        rows.append((status, old, new))
    rows += [("A", p, p) for p in git(root, "ls-files", "--others", "--exclude-standard", "-z")
             .split("\0") if p]
    return rows


def diff_warnings(rows, maps):
    warnings = []
    for status, old, new in rows:
        if not visible(new):
            continue
        if status.startswith(("D", "R")):
            for path, page in maps:
                if old == path:
                    warnings.append(f"{page}: named Code path was deleted or renamed: {old}")
        if status.startswith("D") or PurePosixPath(new).suffix.lower() not in SOURCE_SUFFIXES:
            continue
        if not any(new == path or new.startswith(path + "/") for path, _ in maps):
            warnings.append(f"changed source has no Code map: {new}")
    return warnings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--diff", help="Compare tracked and untracked changes with a Git ref")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    root = args.root.resolve()
    if not (root / "docs").is_dir():
        parser.error(f"docs folder does not exist under {root}")
    errors, maps = inspect(root)
    warnings = []
    if args.diff:
        try:
            warnings = diff_warnings(changes(root, args.diff), maps)
        except RuntimeError as error:
            errors.append(str(error))
    for error in errors:
        print(f"ERROR: {error}")
    for warning in warnings:
        print(f"WARNING: {warning}")
    print(f"KB check: {len(errors)} error(s), {len(warnings)} warning(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
