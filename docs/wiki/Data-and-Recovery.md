# Data and Recovery

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users and contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `src/AudioTranscriber.Storage`, `src/AudioTranscriber.Audio`, `src/AudioTranscriber.Application`, `scripts/Start-App.ps1`

The normal data root is `%LOCALAPPDATA%\AudioTranscriber` on Windows. The app uses the
platform's local application data directory on Linux. Use `--data-root PATH` with the app
or `-DataRoot PATH` with `Start-App.ps1` for an isolated library. Only one application
instance can own a data root at a time.

## SQLite library location and backup

Back up the whole data root, not only exported text. It contains `library.sqlite3`,
session folders, original chunks or managed imports, normalized derivatives, provider
evidence, and speaker state.

The SQLite database is `library.sqlite3` directly inside the selected data root. The
session audio files are also needed for a complete backup.

Do not edit retained originals or move individual files behind the app.

## Durable originals

Capture rotates WAV chunks by duration and byte limits to avoid classic WAV's 4 GiB
per-file limit. Original audio is retained, and transcription jobs can continue or
resume after recording stops.

Overflow, device loss, write failure, and unavailable audio are reported rather than
silently dropping the oldest data. Periodic disk flushing reduces exposure, but no app
can guarantee the last unflushed samples survive power loss.

## Recovery

Startup recovery verifies manifests and hashes. It can recover checkpointed partials and
sealed-but-unindexed originals, and can replay a continuity run to reproduce the same
resampler state. That can take extra decoding time.

Missing, truncated, corrupt, or unjournaled originals are surfaced instead of silently
deleted. Incomplete derivative processing restarts from its continuity start.

## Isolated checks

`Start-App.ps1 -Smoke` uses a fresh isolated directory under `artifacts\smoke` and
reports the `app-smoke.json` path. Smoke mode rejects nonempty roots and never starts
capture, playback, Graph sign-in, NVIDIA requests, or model downloads.

Never point a smoke run at a populated personal library.
