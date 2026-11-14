import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from check_kb import changes, diff_warnings, inspect

HEADER = """# Page

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Contributors
**Last verified:** 2026-11-14 against b3ed981
"""


class KnowledgeBaseCheckTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "docs").mkdir()
        (self.root / "docs/README.md").write_text(HEADER, encoding="utf-8")

    def write(self, path, text):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def test_missing_code_path_and_unlisted_page_are_errors(self):
        self.write("docs/page.md", HEADER + "\n**Code:** `src/Missing.cs`\n")
        errors, _ = inspect(self.root)
        self.assertTrue(any("Code path does not exist" in e for e in errors))
        self.assertTrue(any("missing index entry for page.md" in e for e in errors))

    def test_subfolder_requires_an_index_and_parent_entry(self):
        self.write("docs/feature/page.md", HEADER)
        errors, _ = inspect(self.root)
        self.assertTrue(any("missing folder README.md" in e for e in errors))
        self.assertTrue(any("missing index entry for feature" in e for e in errors))

    def test_linked_pages_and_folder_code_map_pass(self):
        self.write("src/Feature/File.cs", "")
        self.write("docs/README.md", HEADER + "\n[Feature](feature/README.md): What does it do?\n")
        self.write("docs/feature/README.md", HEADER + "\n[Page](page.md): How does it work?\n")
        self.write("docs/feature/page.md", HEADER + "\n**Code:** `src/Feature`\n")
        errors, maps = inspect(self.root)
        self.assertEqual([], errors)
        self.assertEqual([], diff_warnings([("M", "src/Feature/File.cs", "src/Feature/File.cs")], maps))

    def test_unmapped_source_and_renamed_exact_map_warn(self):
        warnings = diff_warnings([("R100", "src/Old.cs", "src/New.cs")],
                                 [("src/Old.cs", "docs/page.md")])
        self.assertEqual(2, len(warnings))
        self.assertTrue(any("deleted or renamed" in w for w in warnings))
        self.assertTrue(any("no Code map" in w for w in warnings))

    def test_generated_folders_and_fenced_examples_are_skipped(self):
        self.write("docs/bin/generated.md", "**Code:** `missing`\n")
        self.write("docs/README.md", HEADER + "\n```markdown\n**Code:** `missing`\n```\n")
        self.assertEqual([], inspect(self.root)[0])
        self.assertEqual([], diff_warnings([("M", "src/bin/generated.cs", "src/bin/generated.cs")], []))

    def test_diff_preserves_renamed_paths_and_includes_untracked_sources(self):
        with patch("check_kb.git", side_effect=[
            "R100\0src/old name.cs\0src/new name.cs\0D\0src/deleted.cs\0",
            "src/untracked.cs\0",
        ]):
            self.assertEqual([
                ("R100", "src/old name.cs", "src/new name.cs"),
                ("D", "src/deleted.cs", "src/deleted.cs"),
                ("A", "src/untracked.cs", "src/untracked.cs"),
            ], changes(self.root, "origin/main"))

    def test_invalid_metadata_and_dead_link_are_errors(self):
        self.write("docs/README.md", "# Page\n[Missing](missing.md)\n")
        errors, _ = inspect(self.root)
        self.assertTrue(any("Last verified" in e for e in errors))
        self.assertTrue(any("link target does not exist" in e for e in errors))


if __name__ == "__main__":
    unittest.main()
