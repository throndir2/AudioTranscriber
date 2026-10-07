# MCP hooks

AudioTranscriber exposes two stdio [Model Context Protocol](https://modelcontextprotocol.io/) servers for agents.

| Mode | Command | What it drives |
| --- | --- | --- |
| Headless | `AudioTranscriber.App.exe --mcp [--data-root PATH]` | The real recording/transcription engine (`AppController`) with no Avalonia window |
| UI | `AudioTranscriber.UiMcp.exe [--data-root PATH]` (or `scripts\Start-Mcp.ps1 -Mode ui`) | A small Windows-only UI Automation client that launches the Avalonia desktop app and clicks through it |

Without `--data-root`, each MCP session uses a fresh isolated library, so your personal library is never touched unless you name it explicitly. Only one process can own a library at a time. The desktop window may download its default models on first start; pass `launch_app whisper_model=PATH` to preselect an existing smaller model in a new library.

## Setup

The repository's `.mcp.json` registers both servers (`audiotranscriber`, `audiotranscriber-ui`) through `scripts\Start-Mcp.ps1`, which builds the development app/tool (build output goes to stderr) and starts the selected server:

```powershell
.\scripts\Setup.ps1
.\scripts\Start-Mcp.ps1 -Mode headless
.\scripts\Start-Mcp.ps1 -Mode ui -AppPath C:\AudioTranscriber\AudioTranscriber.App.exe
```

`-AppPath` in UI mode points the UI MCP tool at a published Avalonia app build. The headless MCP remains built into `AudioTranscriber.App` and is handled before Avalonia initializes, so it works on headless hosts.

## Headless tools

`status`, `list_devices`, `list_providers`, `install_whisper_model` (tiny/base for quick tests), `set_whisper_model`, `install_diarization_models`, `start_recording`, `stop_recording`, `levels`, `play_audio`, `import_audio`, `list_sessions`, `session_details`, `wait_for_jobs`, `get_transcript`, `analyze_speakers`, `control_jobs`, `rename_speaker`, `assign_speaker`, `export_transcript`, `delete_sessions`, `notifications`.

Typical loopback test: `start_recording` → `play_audio` → `stop_recording` → `wait_for_jobs` → `get_transcript`.

## UI tools

`launch_app`, `attach_app`, `close_app`, `snapshot`, `find`, `click`, `set_text`, `select_option`, `read_text`, `read_grid`, `wait_for`, `press_key`, `screenshot`, `play_audio`, `app_status`.

`snapshot` lists every control of every app window with `[eN]` refs; other tools accept a `ref` or a `name` (+ `control_type`). `click right=true` right-clicks (context menus then appear in `snapshot`), and `press_key` accepts chords such as `Shift+Down`. Avalonia dialogs and native Open/Save dialogs appear as extra windows in the UIA tree.

## Test audio

`scripts\New-SpeechFixture.ps1` writes a ~58 s two-voice conversation and its script to `artifacts\fixtures` using local Windows SAPI. Nothing is downloaded or uploaded.
