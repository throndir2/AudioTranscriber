# Data and Recovery

The normal data root is `%LOCALAPPDATA%\AudioTranscriber`. Use `-DataRoot PATH` for an
isolated library. Only one application instance can own a data root at a time.

## What to back up

Back up the whole data root, not only exported text. It contains `library.sqlite3`,
session folders, original chunks or managed imports, normalized derivatives, provider
evidence, and speaker state.

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
