# Local knowledge base check

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Contributors and coding agents
**Last verified:** 2026-11-14 against b3ed981
**Code:** `scripts/kb/check_kb.py`, `scripts/kb/test_check_kb.py`

## Run the check

Python 3.10 or later is required. The script uses only the standard library.
Run `python scripts/kb/check_kb.py` from the repository root.
Before merging, run `python scripts/kb/check_kb.py --diff origin/main`.
Run the focused tests with `python -m unittest discover -s scripts/kb -p test_check_kb.py`.

## Errors and warnings

Errors return exit code 1. Warnings do not prevent a merge.
Fix every error before merging.
The script reports these errors:

- A Code path does not exist, leaves the repository, or contains a wildcard.
- A documentation page has missing or invalid header metadata.
- A folder has no README, or its README omits a Markdown page or immediate subfolder.
- A local Markdown link points to a missing file.

With `--diff`, the script compares the working tree with the named Git reference.
It includes untracked files. It warns about changed source files without a Code map.
A Code folder covers its child files. A Code file covers only that file.
It also warns when the diff deletes or renames a path explicitly named by a Code line.
Missing named paths still produce an error.

The script skips generated folders, including `bin`, `obj`, `.tools`, `.models`, and `artifacts`.
It ignores fenced code examples when reading metadata and links.
It checks local link targets, not remote URLs or heading anchors.
It does not verify behavior claims. Contributors must check those against the code.

No repository-local Git hook is configured. The pre-merge command is therefore the enforcement point.
If a local pre-push hook is added, run this check there as a warning only.
