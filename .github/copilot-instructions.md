# AudioTranscriber repository instructions

AudioTranscriber is a public, Windows and Linux x64 (.NET/Avalonia) recording-first audio
transcriber with local/hosted speech recognition, speaker diarization, a session
library, output templates, and an MCP server. Releases are public on GitHub and
installed copies self-update from the latest release.

## Layout

- `src\AudioTranscriber.App` – Avalonia desktop app for Windows and Linux (UI, updater, templates). Keep UI code cross-platform; guard Windows-only APIs with `OperatingSystem.IsWindows()`.
- `src\AudioTranscriber.Worker` – speaker/diarization worker process.
- `src\AudioTranscriber.{Core,Application,Audio,Diarization,Providers,Storage,Integrations,Discord}` – `net10.0` libraries; `Core` has no UI, capture, inference, or native dependencies. Audio capture/playback is WASAPI on Windows and PulseAudio/PipeWire (`PulseAudio.cs`) on Linux.
- `tools\AudioTranscriber.UiMcp` – Windows-only UI Automation MCP server that drives the real app window.
- `installer\` – NSIS Windows setup script and Linux desktop entry, icons, AppStream metadata, `install.sh`/`uninstall.sh`.
- `tests\*.Tests` – xUnit tests; `tests\Release.Tests.ps1` – offline release-script tests.
- `scripts\` – `Setup.ps1`, `Test.ps1`, `Start-App.ps1`, `Publish.ps1` (`-Runtime win-x64|linux-x64`), `Release.ps1` (ZIP, setup.exe, tar.gz, .deb, .rpm).
- `docs\` – user and maintainer docs (`usage.md`, `releases.md`, `privacy.md`, ...).

## Build and test (local only)

- Run `.\scripts\Setup.ps1` once per checkout/worktree, then use `.tools\dotnet\dotnet.exe` (pinned SDK in `global.json`).
- Test with `.\scripts\Test.ps1` (optionally `-Project <csproj> -Filter <expr>`); release scripts with `pwsh -NoProfile -File .\tests\Release.Tests.ps1`.
- Never run tests, lint, or validation in GitHub Actions. The only workflow is the build-only `release.yml`.

## Changelog (required for user-facing changes)

Every GitHub Release uses its version's section of `CHANGELOG.md` as its release
notes, and the release fails if that section is missing.

- In the same PR as any user-facing change (feature, behavior or default change, fix, removal), add a bullet under `## Unreleased` → `### Added`, `### Changed`, `### Fixed`, or `### Removed`.
- One line per bullet, plain language describing what users see, ending with the PR number, e.g. `(#38)`.
- Skip internal-only changes (CI, scripts, refactors, tests, docs).
- Cutting a release: rename `## Unreleased` to `## vX.Y.Z - YYYY-MM-DD`, add a new empty `## Unreleased` above it, verify entries against `git log <last-tag>..origin/main`, merge, then `gh workflow run release.yml --ref main -f version=vX.Y.Z`. See `docs\releases.md`.

## Public repository hygiene

- Never commit secrets, API keys, recordings, transcripts, databases, or model weights.
- Keep `README.md` and `docs\` accurate when behavior changes.
