# Knowledge base contract

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Contributors and coding agents
**Last verified:** 2026-11-14 against 0b9c0c8
**Code:** `.github/mcp.json`, `scripts/Setup.ps1`

## Find code through the knowledge base

Use `repo-kb` to search the Markdown pages in this repository.
Call `kb_search` with the question, then call `kb_read_section` for the cited sections.
Use the page's **Code:** paths to select the source files.
Read only the source needed for the change.
If the search has no answer, use the code to find it and update the page.

The tool uses local keyword search and CPU embeddings. It does not send repository text to a hosted service.
Node.js 20 or later and git are required. No separate package installation is required.
Run `npx -y github:throndir2/repo-kb index` from the repository root to refresh the index.
`Setup.ps1 -InstallKnowledgeBase` also indexes the repository.

## Page contract

Each Markdown page starts with an H1 title, followed by these fields:

```markdown
**Status:** Implemented
**Last Updated:** YYYY-MM-DD
**Audience:** Users or contributors
**Last verified:** YYYY-MM-DD against <short sha>
**Code:** `src/Area/File.cs`, `scripts/Tool.ps1`
```

Use one status: Implemented, Partial, Planned, or Superseded.
Use **Partial** when some described work is not implemented.
A page about implemented code must list its main existing source paths in **Code:**.
Use repository-relative file or folder paths without wildcards. Separate paths with commas.
Keep path text inside backticks. Use forward slashes in Markdown links and Code metadata for portable Git paths.
A folder path covers its files and child folders.
The verification SHA identifies the source revision inspected, not the commit that added the verification field.
Do not update verification fields without checking the related claims.

Describe the code that exists today. Put unbuilt design only under a **Planned** heading.
Mark historical results as historical evidence, not current acceptance results.
Do not report a hardware test or hosted test as passed without evidence.

## Folder indexes and generated pages

Each folder's `README.md` lists every Markdown page and immediate subfolder.
Each link has one line that states the question it answers.
List data files when they help explain a page. Image files do not need separate page entries.
Generated output folders are not part of the KB.

Scripts must regenerate generated pages. Do not edit generated pages by hand.
The checked-in pages are currently maintained source documents, not generated output.
The existing wiki publisher copies pages from `docs/wiki`; the wiki must not become a second source of truth.
The wiki repository currently returns "Repository not found"; the optional new mirror is skipped.

## Keep the source of truth current

Update the page, Code map, verification field, and folder index in the same pull request as the related change.
If documentation needs no change, state `KB: no change needed` and give the reason in the pull request body.
The local KB check and search evaluation are added in later rollout steps.

## Search quality

The evaluation uses real repository questions with expected page paths.
Record hit@1, hit@3, and hit@5 after running the local evaluation.
Results are not measured yet.
