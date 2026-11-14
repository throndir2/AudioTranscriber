# Validation and Testing

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users and contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `tests`, `scripts/Test.ps1`, `scripts/Start-App.ps1`, `src/AudioTranscriber.App`

The repository retains historical first-version validation separately from current
source checks. See [the validation record](../validation.md) for its scope.

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

## Historical final coverage

Historical suite counts and disk-soak reports are in [the validation record](../validation.md).
They are not results for the current revision. This documentation audit did not run tests.

## Desktop/package checks

The validation record includes historical desktop, package, and worker checks.
Those reports do not establish current package behavior.

These checks do not prove physical endpoint capture, microphone operation, audible
playback, tenant Graph access, speaker-attribution accuracy, or D&D recognition quality.

See
[docs/validation.md](../validation.md)
for commands and evidence.

The current desktop uses Avalonia, not WPF. The historical results are not a current
acceptance test.
