# Installing AudioTranscriber

AudioTranscriber is a Windows x64 desktop app. Release packages are unsigned ZIP files
named like `AudioTranscriber-v0.1.33-win-x64.zip`, with a matching `.sha256` file.

## Install a release

1. Open [the latest release](https://github.com/throndir2/AudioTranscriber/releases/latest).
2. Download `AudioTranscriber-<tag>-win-x64.zip`.
3. Extract the ZIP to a normal folder you can write to.
4. Run `AudioTranscriber.App.exe` from the extracted folder.
5. Keep the whole extracted folder together, including `ffmpeg`, `licenses`, the
   worker, and native runtime libraries.

The ZIP includes a pinned LGPL FFmpeg/FFprobe build, so a clean PC does not need a
separate FFmpeg install. It also includes documentation, model notices, dependency
license files, and package inventory under `licenses`.

## First launch prerequisites

Local Whisper needs the Microsoft Visual C++ 2015-2022 x64 runtime, version 14.40 or
newer. The app checks this on startup and runs Microsoft's official installer if it is
missing or outdated; approve the Windows administrator prompt if shown.

The default local models download on first start and are verified before use. You can
start recording while they download; queued audio is transcribed when they are ready.

## Updates

Release builds check GitHub for the latest non-draft, non-prerelease release. With
automatic updates on, the app downloads and verifies the new ZIP in the background, then
installs it when the app closes or when you choose **Restart to update**.

For details, see [Updates and Closing](Updates-and-Closing) and the source docs at
[docs/usage.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/usage.md).
