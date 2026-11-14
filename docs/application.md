# Application orchestration

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `src/AudioTranscriber.Application`, `src/AudioTranscriber.Core`, `src/AudioTranscriber.App`

`AudioTranscriber.Application` coordinates a session from capture or import
through transcription, speaker analysis, playback, and export. The desktop
window and headless MCP tools use this application layer.

## Service boundaries

`IAppController` is the application-facing contract used by the desktop view
models. `AppController` implements it and owns the session operations, selected
provider settings, job scheduling, cloud-consent checks, and speaker updates.
The controller receives audio and provider services through constructor
parameters, so tests can supply controlled implementations.

`AudioTranscriber.Core` defines the shared records and service contracts. For
example, `IAudioCaptureService` starts and stops capture, while
`IMediaNormalizer` prepares local audio and `IAudioPlaybackService` plays a
track. Core types describe tracks, chunks, provider requests, transcript
segments, and session-time coordinates. The UI and platform audio code stay
outside Core.

## Recording and transcription flow

1. The controller creates a session and its output and optional microphone
   tracks, then starts the capture service.
2. Sealed native chunks are registered in the library. The media normalizer
   reads them and writes bounded mono PCM16 chunks at 16 kHz.
3. The controller queues recognition work for the session's selected provider.
   When diarization models are available, it also queues local speaker work.
4. The storage layer claims eligible jobs. The controller invokes the selected
   service, records the response, and commits transcript rows or speaker data.
5. Transcript-change events tell the desktop view models to refresh. Pause,
   resume, cancellation, and cloud consent remain explicit session operations.

This flow does not select another provider after a general provider failure.
Local Whisper has the separately documented, consent-gated low-confidence
fallback in [ASR providers](providers.md#low-confidence-fallback-for-local-whisper).

## Desktop and MCP entry points

The Avalonia app creates `AppController` for its desktop window. The
`--mcp` startup option instead creates the same controller for the headless
MCP server. The separate UI MCP tool operates the real desktop app through
Windows UI Automation. See [MCP hooks](mcp.md).

The service boundaries are implementation details, not a promise that every
operation runs in a separate process. Speaker inference uses the owned worker
process described in [local speaker diarization](diarization.md). The controller
invokes the ASR services; some inference paths use native libraries in-process.
