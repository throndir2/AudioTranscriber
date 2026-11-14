# MCP Hooks

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users and contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `src/AudioTranscriber.App/Mcp`, `src/AudioTranscriber.App/Program.cs`, `tools/AudioTranscriber.UiMcp`, `scripts/Start-Mcp.ps1`

AudioTranscriber provides two stdio Model Context Protocol servers. The headless server
is in the desktop executable. The Windows-only UI server is a separate development tool.

| Mode | Command | What it drives |
| --- | --- | --- |
| Headless | `AudioTranscriber.App.exe --mcp [--data-root PATH]` | The real `AppController` with no window |
| UI | `.\scripts\Start-Mcp.ps1 -Mode ui` | The real desktop window through Windows UI Automation |

Without a data root, each MCP session uses a fresh isolated library under the system temporary directory, so
the personal library is not touched unless named explicitly. Only one process can own a
library at a time.

## Setup

Run `scripts\Start-Mcp.ps1` to launch either server. For headless mode, `-AppPath` runs
the specified executable directly. Otherwise, the script builds the required project unless
`-NoBuild` is set and the assembly already exists. The repository `.github/mcp.json`
registers the documentation search server, not these two servers.

```powershell
.\scripts\Setup.ps1
.\scripts\Start-Mcp.ps1 -Mode headless
.\scripts\Start-Mcp.ps1 -Mode ui -AppPath C:\AudioTranscriber\AudioTranscriber.App.exe
```

Testing a published build with `-AppPath` exercises the release layout, including the
self-contained speaker worker.

## Useful tools

Headless tools include device and provider listing, recording, playback, import,
sessions, job waiting, transcripts, speaker analysis, job control, speaker
rename/assign, export, delete sessions, and notifications.

UI tools launch or attach the app, take snapshots/screenshots, find and click controls,
set text, select options, read text/grid data, wait, press keys, and play audio.

`scripts\New-SpeechFixture.ps1` writes a roughly 58-second two-voice Windows SAPI
fixture under `artifacts\fixtures`. It downloads and uploads nothing.

Full details are in
[docs/mcp.md](../mcp.md).
