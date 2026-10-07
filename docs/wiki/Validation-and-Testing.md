# Validation and Testing

The first-version validation used Windows x64, SDK 10.0.401, and runtime 10.0.12. The
model comparison is retained separately, and no private recording was uploaded or
desktop capture started for testing.

## Normal local checks

```powershell
.\scripts\Setup.ps1
.\scripts\Test.ps1
.\scripts\Start-App.ps1 -Smoke
.\scripts\Publish.ps1
```

Setup does not download models, start capture, read credentials, or upload audio.
Ordinary tests skip explicitly gated native-model cases when inputs are not supplied.
The >4 GiB audio soak is opt-in.

## Documented final coverage

The documented full Release suite passed 170 tests with zero failures and zero skipped
tests:

| Area | Tests |
| --- | ---: |
| App presentation/startup | 21 |
| Application pipeline | 15 |
| Audio | 19 |
| Diarization | 27 |
| Providers/benchmark | 54 |
| Storage | 34 |

The real disk soak wrote 4,296,015,872 synthetic audio bytes across 65 rotated WAVs and
cleaned them up. This is real disk I/O with synthetic samples, not live capture or a
power-cut test.

## Desktop/package checks

The real WPF window was opened against an empty isolated library and all five tabs were
visited. The self-contained native EXE also passed the same smoke sequence in the
publish directory and after copying the package outside the checkout.

The separate published speaker-worker EXE processed local synthetic speech twice,
preserving the speaker GUID and renamed display name.

These checks do not prove physical endpoint capture, microphone operation, audible
playback, tenant Graph access, speaker-attribution accuracy, or D&D recognition quality.

See
[docs/validation.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/validation.md)
for commands and evidence.
