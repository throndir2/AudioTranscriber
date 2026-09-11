# First-version validation

Validation used Windows x64, worktree-local SDK 10.0.401, and runtime 10.0.12.
The model comparison is separately retained in `benchmarks\2026-09-11`; no
private recording was uploaded and no desktop capture was started for testing.

## Final automated coverage

The full Release suite passed **170 tests, zero failures, zero skipped tests**,
with the explicit local-inference and real disk-soak settings below.

| Project | Tests | Evidence |
| --- | ---: | --- |
| App presentation/startup | 21 | Production presentation and startup-option logic |
| Application pipeline | 15 | Mock capture/provider orchestration, final tails, consent, future-chunk pause/cancel, failure recovery, native-clock mapping, timestamp ownership |
| Audio | 19 | Actual FFmpeg format conversion/import/seek, bounded queues, archive recovery, and actual >4 GiB synthetic disk writes |
| Diarization | 27 | Registry/overlap logic plus real installed neural inference on local synthetic and pinned public speech, owned worker cancellation and coordinates |
| Providers/benchmark | 54 | Shared adapters, consent/credential safety, format/timing contracts, metrics, request budgets, explicit model-download acknowledgments |
| Storage | 34 | Migrations, leases, persistent processing controls, FTS, corrections, raw attempts, atomic speaker checkpoints and exports |

The real disk soak wrote **4,296,015,872 audio bytes across 65 rotated WAVs** and
cleaned its generated files. This is actual disk IO, not a virtual counter test,
but it is still synthetic data, not physical WASAPI capture.

Application regression coverage includes SQL-triggered initial-checkpoint and
lease-renewal failures. It confirms that failed media registration is removable,
a transient renewal failure does not kill the scheduler, paused/canceled
sessions gate future chunks, and invalid audio affects its jobs rather than
unrelated sessions. Optional default immutable collections are serialized
without losing successful source responses.

## Reproducible local commands

Normal developer validation:

```powershell
.\scripts\Setup.ps1
.\scripts\Test.ps1
.\scripts\Start-App.ps1 -Smoke
.\scripts\Publish.ps1
```

Setup does not download models, start capture, read credentials, or upload audio.
The ordinary test run skips explicitly gated native-model cases when their
inputs are not supplied; the >4 GiB soak is also opt-in.

To reproduce the fully enabled run, explicitly install the small diarization
models through the app first, then prepare public and synthetic inputs:

```powershell
.\scripts\Compare-Models.ps1 -PrepareOnly -OutputDirectory 'artifacts\public-fixture'
$fixture = & .\tests\AudioTranscriber.Diarization.Tests\Create-SyntheticFixture.ps1
$env:AUDIO_RUN_SOAK = '1'
$env:DIARIZATION_MODEL_DIRECTORY = Join-Path $PWD '.models\diarization'
$env:DIARIZATION_WORKER_EXE = Join-Path $PWD 'src\AudioTranscriber.Worker\bin\Release\net10.0-windows\win-x64\AudioTranscriber.Worker.dll'
$env:DIARIZATION_DOTNET_HOST = Join-Path $PWD '.tools\dotnet\dotnet.exe'
$env:DIARIZATION_SYNTHETIC_PCM = $fixture.Path
$env:DIARIZATION_PUBLIC_PCM = Join-Path $PWD 'artifacts\public-fixture\fixture\1272-128104-0000.pcm'
.\scripts\Test.ps1
```

These commands do not submit audio to NVIDIA. The synthetic voice is written to
a file through Windows SAPI, not played through or recorded from a device.

## Actual desktop and package checks

The real WPF window was opened against an empty isolated library and all **five
tabs** were visited. This found and fixed read-only meter/raw-text bindings that
incorrectly defaulted to TwoWay. Smoke reports showed SQLite loaded and five
visited tabs, no sessions, capture false, and cloud submission false. Endpoint
availability changed during validation: one output was enumerated initially and
zero in the final portable checks; no microphone was detected.

The self-contained published **native EXE** also exited successfully after the
same smoke sequence, both in the worktree publish directory and after copying
the complete package outside the checkout. The separate published native
speaker-worker EXE processed local synthetic speech twice: four turns and one
identity per call, preserving the serialized speaker GUID and renamed display
name. The request used 55-hour offsets beyond Int32 in both normalized and native
sample coordinates.

These are real GUI/native-inference checks, not mock screenshots. They do not
prove physical endpoint capture, microphone operation, audible playback, tenant
Graph access, speaker-attribution accuracy, or D&D recognition quality.

## Environment qualification

Intermittent Windows image-loading failures affected test/apphost/native-library
launches during development. Captured loaded pages were zero-filled despite
valid matching on-disk bytes; unchanged binaries subsequently passed after file
recreation. This is **not a proven SQLite, compiler, or test-SDK package defect**,
and the precise environment trigger remains unknown.

The scripts use the pinned managed dotnet host for development/worker/test
launching, and the test script serializes project execution. Rebuilding fresh
owned output files resolved observed stale-image failures; neither mitigation
skips assertions. Native published EXEs were tested independently rather than
declared working solely because DLL-hosted tests passed.

Whisper model weights were not downloaded during this task. Its optional
provider and verified installer are wired and covered by adapter/configuration
tests, but this run is not evidence of local Whisper transcription quality.
