# AudioTranscriber agent bootstrap

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Coding agents
**Last verified:** 2026-11-14 against dd2b650
**Code:** `.github/github-app.yml`, `.github/mcp.json`, `scripts/kb/check_kb.py`

Follow the repository rules in [.github/copilot-instructions.md](.github/copilot-instructions.md).
Start at [docs/README.md](docs/README.md) for the source map.

## Knowledge Base First (Required)

Always use the KB when you must find, search, or navigate repository content.
Call `kb_search` on the `repo-kb` MCP server, then open cited sections with `kb_read_section`.
Use their **Code:** lines to select the source. Read only the code needed.
Do not explore with broad grep, glob, directory listings, or file reads.
Use those tools only for exact names that the KB supplied.
If the KB has no answer or a wrong answer, find it in code and fix the page in the same pull request.

## Keep the KB true (Required)

A pull request that changes code, config, scripts, commands, or behavior updates the related KB page.
This includes added, renamed, moved, and deleted items.
Update its **Code:** and **Last verified:** lines and its folder index.
A new feature gets a page or section. Delete a removed feature's page or mark it Superseded.
If no page needs a change, the pull request body states `KB: no change needed` and gives the reason.
Run `python scripts/kb/check_kb.py --diff origin/main` before merge. Fix every error.
Use [the KB contract](docs/internal/knowledge-base.md) and [local check](docs/internal/local-check.md) for details.

## Definition of done

Check the change locally. Update the KB and run its check before committing, pushing, and merging.
Follow the existing merge and cleanup rules. Do not run hosted validation or change workflows for KB work.
