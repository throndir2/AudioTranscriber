# Limits and Known Gaps

This page collects documented limits so expectations stay realistic.

## Timing and transcription

AudioTranscriber distinguishes word timing, segment timing, and coarse chunk timing.
Coarse text cannot support fabricated word-level seek or certain attribution across
several turns. Provider timestamps are relative to the supplied audio.

No automatic translation is performed. If a provider, language, key, quota, model, or
prerequisite fails, the app reports it instead of silently choosing another provider.

## Speakers

Local diarization is not source separation. The segmentation model has a ten-second
internal window, up to three local speakers, and at most two simultaneous speakers per
frame. That does not limit an entire session or voice library to three speakers.

Short turns, overlap, crowded windows, heavy compression, different rooms, similar
voices, and character voices can remain unknown or split one person. A speaker label is
not a verified identity.

## Discord

Discord server voice channels are recorded through your own visible bot (no user token
or selfbot); see [Discord](Discord). Bots cannot join DM or group-DM calls, so record
Windows output for those.

## Validation boundary

Tests use controlled synthetic or public data. No hardware capture test starts
automatically. Physical WASAPI capture, endpoint removal, audible multi-track playback,
actual disk exhaustion, hard power-loss recovery, tenant Graph access, and D&D
recognition quality require explicit acceptance testing or real setup.

The initial public hosted comparison used two clean single-speaker LibriSpeech clips. It
does not establish a best D&D model, diarization quality, overlap handling, or quota
reliability.
