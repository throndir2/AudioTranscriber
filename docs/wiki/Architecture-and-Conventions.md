# Architecture and Conventions

The solution contains these main projects:

- `AudioTranscriber.Core`
- `AudioTranscriber.Audio`
- `AudioTranscriber.Providers`
- `AudioTranscriber.Diarization`
- `AudioTranscriber.Worker`
- `AudioTranscriber.Storage`
- `AudioTranscriber.Integrations`
- `AudioTranscriber.Application`
- `AudioTranscriber.App`
- `tools\AudioTranscriber.Benchmarks`

Test projects cover Storage, Application, Audio, Providers, Diarization, and App.

## Core boundaries

`AudioTranscriber.Core` has no UI, capture, inference, or native dependencies. Native
frames, track-global normalized samples, and session-relative 100 ns ticks are distinct
64-bit coordinates.

Normalized ASR files are headerless little-endian signed PCM16, mono, 16 kHz. A request
owns a non-overlapping core within a maximum 30-second context window.

Provider timestamps are integer milliseconds relative to supplied audio. Timing
granularity and nullable confidence must reflect actual provider output. Do not
fabricate word timing, confidence, or model identity.

Preserve raw recognition separately from corrections. Loopback and optional microphone
are separate tracks. Stop recording separately from transcription cancellation.

## Audio conventions

Constructors and device enumeration do not capture. Only explicit `StartAsync` starts
WASAPI. Event handlers must be short, nonthrowing, and enqueue application persistence
or UI work; never perform ASR in capture callbacks.

Original audio, normalized derivatives, provider evidence, speaker state, and SQLite
updates are owned by the application layer and storage transactions.

## Identity conventions

Speaker names are display labels. Acoustic similarity can suggest or merge profiles only
through documented user actions and conservative matching; it is not participant
authentication.

More detail is in
[docs/audio.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/audio.md),
[docs/diarization.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/diarization.md),
and
[docs/providers.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/providers.md).
