# MCP hooks

AudioTranscriber ships two stdio [Model Context Protocol](https://modelcontextprotocol.io/) servers inside the
desktop executable, so an agent (Copilot CLI, VS Code, Claude, …) can drive and test the real app.

| Mode | Command | What it drives |
| --- | --- | --- |
| Headless | `AudioTranscriber.App.exe --mcp [--data-root PATH]` | The real recording/transcription engine (`AppController`) with no window |
| UI | `AudioTranscriber.App.exe --mcp-ui [--data-root PATH]` | Launches the real desktop window and clicks through it with Windows UI Automation |

Without `--data-root`, each MCP session uses a fresh isolated library under `%TEMP%`, so your personal
library is never touched unless you name it explicitly. Only one process can own a library at a time.
The desktop window downloads its default models (33 MB speaker models, 1.5 GiB Whisper large-v3-turbo) on
first start; pass `launch_app whisper_model=PATH` to preselect an existing smaller model in a new library.

## Setup

The repository's `.mcp.json` registers both servers (`audiotranscriber`, `audiotranscriber-ui`) through
`scripts\Start-Mcp.ps1`, which builds the development app (build output goes to stderr) and starts it:

```powershell
.\scripts\Setup.ps1                                     # once per worktree
.\scripts\Start-Mcp.ps1 -Mode headless                  # what .mcp.json runs
.\scripts\Start-Mcp.ps1 -Mode ui -AppPath C:\AudioTranscriber\AudioTranscriber.App.exe   # drive a published/release build
```

Testing a published build (`-AppPath`, or the exe directly) exercises the exact release layout, including the
self-contained speaker worker.

## Headless tools

`status`, `list_devices`, `list_providers`, `install_whisper_model` (tiny/base for quick tests),
`set_whisper_model`, `install_diarization_models`, `start_recording`, `stop_recording`, `levels`, `play_audio`,
`import_audio`, `list_sessions`, `session_details`, `wait_for_jobs`, `get_transcript`, `analyze_speakers`,
`control_jobs`, `rename_speaker` (an existing name merges), `assign_speaker` (label row ids from `get_transcript`;
the voice is learned in the background), `export_transcript`, `notifications`.

Typical loopback test: `start_recording` → `play_audio` (plays a file to the same output endpoint) →
`stop_recording` (reports peak capture level) → `wait_for_jobs` → `get_transcript`. Background failures, such as
a speaker-worker error, appear in `wait_for_jobs.jobErrors` and `notifications`.

## UI tools

`launch_app`, `attach_app`, `close_app`, `snapshot`, `find`, `click`, `set_text`, `select_option`, `read_text`,
`read_grid`, `wait_for`, `press_key`, `screenshot`, `play_audio`, `app_status`.

`snapshot` lists every control of every app window with `[eN]` refs; other tools accept a `ref` or a
`name` (+ `control_type`). `click right=true` right-clicks (context menus then appear in `snapshot`), and
`press_key` accepts chords such as `Shift+Down`. Message boxes and Open/Save dialogs appear as extra windows: click `Yes`, or
`set_text` the `File name:` edit and click `Open`. Examples:

```text
click name="Privacy / models" control_type=TabItem
click name="Install small diarization models" control_type=Button   → click name=Yes
click name="Start recording" control_type=Button → play_audio path=… → click name="Stop recording"
read_grid name="Paged transcript"
click name="Happy to be here" control_type=DataItem right=true → click name="Type a speaker name…" → set_text name="Speaker name" text=Zira → click name="Set speaker"
read_text name="Live activity log"                                   → recent job progress, transcript lines, live file writes
```

## Test audio

`scripts\New-SpeechFixture.ps1` writes a ~58 s two-voice (Microsoft David / Zira) conversation and its script to
`artifacts\fixtures` using local Windows SAPI. Nothing is downloaded or uploaded.

`play_audio` is audible on the selected output endpoint; loopback recording captures whatever that endpoint plays.
