# Architecture and Conventions

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users and contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `src`, `tools`

The solution contains these main projects:

- `AudioTranscriber.Core`
- `AudioTranscriber.Audio`
- `AudioTranscriber.Providers`
- `AudioTranscriber.Diarization`
- `AudioTranscriber.Worker`
- `AudioTranscriber.Storage`
- `AudioTranscriber.Integrations`
- `AudioTranscriber.Discord`
- `AudioTranscriber.Application`
- `AudioTranscriber.App`
- `tools\AudioTranscriber.Benchmarks`
- `tools\AudioTranscriber.UiMcp`

Test projects cover Storage, Application, Audio, Providers, Diarization, and App.

## Project responsibilities

| Project | Responsibility |
| --- | --- |
| Core | Shared audio, transcription, and speaker contracts. |
| Audio | Capture, import, normalization, archive, and playback. |
| Providers | Local and hosted recognition, model setup, and credential protection. |
| Diarization | Speaker segmentation and voice embeddings. |
| Worker | Separate process for speaker analysis. |
| Storage | SQLite library, transcript export, and live transcript file. |
| Integrations | Transcript and meeting integration code. |
| Discord | Discord bot, voice reception, and network setup. |
| Application | Controller, recording pipeline, jobs, and speaker state. |
| App | Avalonia desktop, settings, templates, updater, and headless MCP. |
| Benchmarks | Public model comparison tool. |
| UiMcp | Windows UI Automation MCP server. |

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

Constructors and device enumeration do not capture. Explicit capture starts WASAPI on
Windows or PulseAudio on Linux. Event handlers must be short, nonthrowing, and enqueue application persistence
or UI work; never perform ASR in capture callbacks.

Original audio, normalized derivatives, provider evidence, speaker state, and SQLite
updates are owned by the application layer and storage transactions.

## Identity conventions

Speaker names are display labels. Acoustic similarity can suggest or merge profiles only
through documented user actions and conservative matching; it is not participant
authentication.

More detail is in
[docs/audio.md](../audio.md),
[docs/diarization.md](../diarization.md),
and
[docs/providers.md](../providers.md).
