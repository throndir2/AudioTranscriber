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
- `scripts\` – `Setup.ps1`, `Test.ps1`, `Start-App.ps1`, `Publish.ps1` (`-Runtime win-x64|linux-x64`), `Release.ps1` (ZIP, setup.exe, tar.gz, .deb, .rpm), `Clean.ps1` (deletes generated output).
- `docs\` – user and maintainer docs (`usage.md`, `releases.md`, `privacy.md`, `cleanup.md`, ...).

## Build and test (local only)

- Run `.\scripts\Setup.ps1` once per checkout/worktree, then use `.tools\dotnet\dotnet.exe` (pinned SDK in `global.json`). NuGet packages use the shared per-user cache `%USERPROFILE%\.nuget\packages`, not `.tools\nuget`.
- Test with `.\scripts\Test.ps1` (optionally `-Project <csproj> -Filter <expr>`); release scripts with `pwsh -NoProfile -File .\tests\Release.Tests.ps1`.
- Never run tests, lint, or validation in GitHub Actions. The only workflow is the build-only `release.yml`.

## Disk cleanup (required before you finish)

Each worktree gets its own SDK, build output, and model downloads, often 5 GB or more. See `docs\cleanup.md` for the folders, sizes, and the shared caches.

- Before you finish a session, after your last build, test, or app run, run `.\scripts\Clean.ps1 -All` in your worktree. Use `-DryRun` first to see what it deletes. If you must build again later, run `.\scripts\Setup.ps1` first.
- `Clean.ps1` deletes only generated, git-ignored folders (`bin`, `obj`, known `artifacts\` subfolders, `.tools`, `.models`). It never deletes tracked files or uncommitted work.
- Stop the dev app and any running tests first. Files that are in use cannot be deleted.
- Do not use `git clean -x`/`-X`, `git reset --hard`, or `git worktree remove --force` to clean. They delete local settings, credentials, recordings, or uncommitted work.
- Clean other worktrees (`-AllWorktrees`, `-Path`) or remove whole worktrees only when the user asks. Remove a worktree only if its session is archived. Never remove `copilot-prewarm-*` worktrees, and never clean the main checkout from a worktree session.

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
