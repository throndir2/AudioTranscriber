# MCP Hooks

AudioTranscriber ships two stdio Model Context Protocol servers inside the desktop
executable so agents can drive and test the real app.

| Mode | Command | What it drives |
| --- | --- | --- |
| Headless | `AudioTranscriber.App.exe --mcp [--data-root PATH]` | The real `AppController` with no window |
| UI | `AudioTranscriber.App.exe --mcp-ui [--data-root PATH]` | The real desktop window through Windows UI Automation |

Without `--data-root`, each MCP session uses a fresh isolated library under `%TEMP%`, so
the personal library is not touched unless named explicitly. Only one process can own a
library at a time.

## Setup

The repository `.mcp.json` registers `audiotranscriber` and `audiotranscriber-ui`
through `scripts\Start-Mcp.ps1`, which builds the development app and starts it.

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
[docs/mcp.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/mcp.md).
