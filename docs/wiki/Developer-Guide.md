# Developer Guide

Use PowerShell from the repository root on Windows x64.

```powershell
.\scripts\Setup.ps1
.\scripts\Start-App.ps1
.\scripts\Start-App.ps1 -Smoke
.\scripts\Test.ps1
.\scripts\Publish.ps1
```

## Local SDK

`global.json` pins SDK **10.0.401** with roll-forward disabled. `Setup.ps1` installs
that SDK only into `.tools\dotnet`; it does not install globally or change machine PATH.
Downloads, CLI home, and NuGet cache stay under `.tools`.

Setup finds the Windows x64 SDK ZIP in Microsoft's official .NET 10 release metadata,
accepts only approved Microsoft HTTPS download hosts, verifies SHA-512, and reuses an
already installed pinned local SDK. It does not download models, start capture, read
credentials, or upload audio.

## Running the app

`Start-App.ps1` builds the desktop project with locked restore and runs it with the
pinned local `dotnet`. `-DataRoot PATH` selects a custom data root. `-Smoke` creates a
fresh isolated root under `artifacts\smoke` unless you provide one, rejects nonempty
smoke roots, and reports `app-smoke.json`.

## Tests

`Test.ps1` restores in locked mode, runs `dotnet test` serially (`-m:1`), writes results
under `artifacts\TestResults`, and passes the pinned local dotnet host to VSTest. Use
`-Project` for targeted runs and `-Filter` for test filters.

## Publish

`Publish.ps1` publishes the self-contained Windows x64 app and worker to
`artifacts\publish\win-x64` by default; `-Runtime linux-x64` cross-builds the Linux app to
`artifacts\publish\linux-x64` from Windows or Linux. Keep the entire output directory together. It
includes FFmpeg/FFprobe, docs, model notices, dependency license files, package
inventory, and provenance.

To try the Linux build on a Windows PC, run the published `linux-x64` folder in Docker or WSL
with Xvfb and PulseAudio (`pulseaudio -D`, `pactl load-module module-null-sink`), then
`./AudioTranscriber.App --smoke --data-root /tmp/smoke`. `tools\AudioTranscriber.UiMcp` (the UI
Automation MCP) is Windows-only.

Native apphost or image-loading failures were observed intermittently on the development
machine despite valid on-disk bytes. Published executables are a separate validation
target; do not treat that environment symptom as a model or source-code defect.
