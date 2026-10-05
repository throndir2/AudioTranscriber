# Windows audio module

## Application integration

The production implementations in `AudioTranscriber.Audio` implement the shared
Core contracts:

```csharp
IAudioCaptureService capture = new WasapiAudioCaptureService();
IMediaNormalizer media = new FfmpegMediaNormalizer(new MediaTools(ffmpegPath, ffprobePath));
IAudioPlaybackService playback = new AudioPlaybackService(new MediaTools(ffmpegPath, ffprobePath));
```

`WasapiAudioCaptureService.StartAsync(Core.CaptureOptions, cancellationToken)`
returns the shared QPC origin and actual native `TrackRecord`s. Pass the
application's session ID, separate output/microphone track IDs, selected endpoint
IDs, and archive directory. Device enumeration and constructors **do not capture**.
Only an explicit `StartAsync` starts WASAPI. Stop recording separately from
transcription cancellation. `StopAsync` honors cancellation while acquiring the
lifecycle lock, but after requesting stop it always waits for drain/finalization.

Subscribe to `ChunkSealed`, `Levels`, and `Fault` before starting. For full
durability/timing persistence the concrete service additionally exposes:

- `OriginalSealed: Action<OriginalChunk>`: file/hash/format, native source-frame
  offset/count, continuity run, and every packet's device position, QPC and flags.
- `Gap: Action<AudioGap>`: session-relative ticks, known loopback inactivity
  versus lost/unknown intervals.
- `DetailedFault: Action<AudioFault>`: accepted-through and durable-through frame
  watermarks and diagnostic code. A disk-full fault may prevent its sidecar from
  being saved; the event remains authoritative for the active application.
- `State`: Starting, Recording, Stopping, Recorded, or Faulted.

Event handlers execute off the UI thread. They must be short, nonthrowing and
enqueue application persistence/UI work; never perform ASR in these handlers.
The application owns SQLite transactions, idempotent chunk/job insertion,
notifications, consent and capture visibility. Track startup and first archive
events can race: persist/prepare caller-supplied track IDs before accepting jobs,
then replace placeholder formats with the returned actual native formats.

The concrete lower-level APIs are also usable independently:

- `CaptureSession(CaptureOptions)`: `StartAsync`, `StopAsync`, `Tracks`,
  `SessionQpcOrigin100ns`; events `ChunkSealed`, `Gap`, `Faulted`,
  `LevelChanged`, `StateChanged`.
- `NativeArchiveWriter(ArchiveOptions)`: `Write(PooledAudioPacket)`,
  `Checkpoint()`, `Finish(stopSessionTicks, healthyLoopback)`,
  `DurableThroughFrame`; `ChunkSealed`/`Gap`.
- `PooledPacketQueue(byteLimit)`: `TryWrite(..., copy)`, `ReadAllAsync()`,
  `Complete()`. Dispose every read packet to return its pooled buffer.
- `MediaImporter(MediaTools?)`: `ProbeAsync`, `ImportAsync`, `Recover`.
- `PersistentNormalizer(NormalizationOptions, MediaTools?, token)`:
  `AppendChunkAsync`, `CompleteAsync`, `EmittedSamples`, `ShardSealed`.
  Static `NormalizeChunksAsync` and `NormalizeImportAsync` provide batch helpers.
- `TimelinePlayer(MediaTools?)`: `PlayAsync(PlaybackRequest)`, `StopAsync`,
  `SetVolume(trackId, 0..2)`, `PlaybackFailed`. A request supports multiple
  separate native/imported tracks; volume zero mutes without changing alignment.
- `LocalPlaybackDecoder.Open(...)` opens only a local FFmpeg decoder, yielding
  stereo float32 little-endian PCM at 48 kHz; it opens no audio device.
- `PlaybackTimeline.FrameAt(original, sessionTicks)` resolves stored QPC anchors.
- `AudioContracts.ToCore(...)` maps rich archive/derivative records to Core.

## Capture, clocks and archive durability

The capture thread opens the exact `MMDevice`, checks its render/capture
direction, initializes its actual shared-mode mix format, and calls low-level
`AudioCaptureClient.GetBuffer` with device-frame positions, 100 ns QPC timestamps
and buffer flags. The high-level `WasapiCapture.DataAvailable` implementation is
not used. The microphone uses its own client/track; it is never presumed to be
included in loopback. Device removal faults the session; no fallback endpoint is
selected.

Sample formats remain native PCM integer or IEEE float. Extensible channel
masks, valid bits and the serialized native format are retained. Silent packets
are filled with correct native silence (128 for unsigned PCM8, zero otherwise).
Peak/RMS metering is informational only and never suppresses archival.

Queue capacity accounts for **actual ArrayPool bucket sizes**, including the
packet currently owned by the writer. The queue also has a 4,096-packet bound.
There is no DropOldest behavior. Exhaustion stops capture and reports the
unarchived packet interval. On normal stop, the client is stopped first, remaining
device packets are read (allowing bounded waiting for writer capacity), then the
queue drains, the original tail seals and final state changes. If the writer
failed, remaining packets cannot be promised durable and the fault watermarks
make this explicit.

Chunks rotate at both the configured duration (at most 30 seconds) and total WAV
file size (at most 64 MiB, including header/padding). Offsets, frame counts and
QPC values are 64-bit; rational conversion uses Int128 intermediates. Live
recordings use 6-second chunks with `PauseSplitAfterMilliseconds` = 1500: once a
chunk holds 1.5 seconds it also seals after a pause of quiet (RMS below about
-42 dBFS), and speech starting after a quiet stretch begins a fresh chunk. The
pause length is the **Pause that ends a phrase** recording setting (default
800 ms, 200–3000 ms, saved in `recording-defaults.json`); longer pauses give
fewer, longer phrases.

**Re-transcribe…** (Transcript tab) applies the current pause to an existing
recorded or imported session. It streams each track's normalized 16 kHz audio
twice (loudness per 10 ms, then copy), so multi-hour sessions need little
memory, and re-cuts it with the same rule (1.5 s minimum, pause-aligned) but a
20-second maximum: speech with no long-enough pause is cut at its quietest
moment. The new chunks replace the old ones in one transaction, their jobs and
recognized lines are dropped, and recognition plus speaker analysis are queued
again. Lines whose speaker was set by hand are kept as time-ranged hints
(`speaker_hints`) that label the new lines they cover by at least half; text
edits are discarded. Originals are untouched.

Active chunks have a `.wav.partial` and atomically replaced `.wav.journal.json`.
The writer fsyncs data before updating the journal, at a default **one-second
durability checkpoint**, including periods with no new packets. Seal repairs the
RIFF/data sizes, fsyncs, hashes, writes a ready-to-seal journal, atomically renames
the original and persists `.wav.json` before publishing its event. Disposing a
failed writer deliberately does not seal untrusted data.

This is not a power-loss-immunity claim: accepted packets since the last durable
checkpoint can be lost; disk/controller/filesystem behavior also matters.
Free space is checked before startup and throughout writing/importing.

Healthy loopback can stop producing packets when no application renders audio.
Such timestamp-bounded inactivity is stored as **timed silence**, not fabricated
captured samples. A discontinuity, lost source/device position, timestamp-error
flag or endpoint failure instead records an unknown/unavailable interval.
Missing intervals separate continuity runs. Playback inserts timeline silence;
normalization never concatenates across these session-time gaps as though they
were speech. Timestamp-error packets use an explicitly flagged approximate QPC.

## Microphone echo reduction

Recording sessions started with **Reduce echo from speakers** store the output
track ID in the microphone track's metadata. Before a microphone recognition
window is sent to a provider, `EchoReduction` reads the output track's
normalized PCM for the same session time (gaps are silence, plus 5 s of
pre-roll so the filter has converged) and runs WebRTC AEC3
(`SoundFlow.Extensions.WebRtc.Apm`, native `webrtc-apm.dll`) in 10 ms frames.
Only the temporary work file changes; originals and normalized chunks stay
raw. Windows with silent speakers are left untouched. AEC3 tracks echo delays
of roughly 0–500 ms. While recording, the job is deferred until the output
track covers the window, or 90 s have passed (loopback emits nothing while
nothing plays). Rows produced this way record `speaker echo removed` in their
provenance.

## Startup recovery

Call `AudioArchiveCatalog.RecoverOriginals(root)` before scheduling recovered
work. It recovers checkpointed partials and sealed-but-unindexed files, validates
all original manifests/hashes, and returns both originals and diagnostics.
Insert recovered IDs idempotently; never assume a previous process committed a
SQLite row merely because the file exists.

Recovery copies only sample-aligned checkpointed bytes, repairs/seals the copy,
and **retains the old partial as evidence**, including uncheckpointed tail data.
Missing/truncated/corrupt/unjournaled originals are surfaced, not silently
deleted. `NativeArchiveWriter.Recover(trackDirectory)` provides track-scoped
reconciliation. `MediaImporter.Recover(importDirectory)` reconciles hash-verified
managed imports interrupted between final rename and manifest persistence.

`AudioArchiveCatalog.ReadNormalizedManifests(root)` validates and enumerates
sealed derivatives. An incomplete derivative process is restarted from its
continuity start, not from an arbitrary source-chunk boundary.

## Imports and normalization

FFprobe has a 30-second deadline, a 1 MiB stdout bound and a 32 KiB diagnostic
tail. Only absolute regular local files are accepted, with an explicit
container-format allowlist and `file,pipe` protocol whitelist. Remote URLs, UNC
paths, link files, playlists and media without valid audio streams are rejected.
Multiple streams are enumerated; the caller must supply a selected audio index.

Import streams a managed copy with progress/space checks and SHA-256, probes that
copy, then seals it read-only. Provenance retains the original path/name, selected
stream, codec, decoded sample format/channel layout, start/duration/time-base,
import timestamp, application version and FFprobe version. The original media is
never transcoded in place.

All tools use `ProcessStartInfo.ArgumentList`, redirected pipes and no shell.
They are attached to a Windows kill-on-close job, drain bounded stderr, and kill
their owned process tree on cancellation/disposal. FFmpeg decodes only the
selected audio stream and has the same local protocol/container restriction.

One persistent FFmpeg process consumes native data bytes across all original
chunks in a continuity run. The resampler is not restarted at WAV boundaries.
Closing stdin and checking successful EOF flushes delayed samples before sealing
the final derivative. Output is headerless signed PCM16 little-endian, mono,
16 kHz, with default 24-second nonoverlapping shards. With `SealAtInput`
(used for recorded tracks), a shard also seals 200 ms before the end of each
original chunk of more than 400 ms, so it is emitted without waiting for the
next chunk (FFmpeg holds back under ~90 ms). The boundary depends only on chunk
sizes, and `run.json` records the layout, so replay reproduces identical shards.
ASR orchestration can add
up to three seconds of left/right context while keeping input at most 30 seconds.
The audio module does not add overlapping context or assign transcript ownership.

Manifests store actual emitted sample counts, track-global normalized offsets,
native-rate source offsets and session-time origins. Native final shards include
the actual final source-frame endpoint despite resampling rounding. Imported
compressed streams use their declared decoded sample-rate time axis and actual
emitted normalized counts; codec delay/padding semantics remain FFmpeg's.

Restart replays the continuity from its beginning. Existing PCM files are
byte-hash compared against replay, their original IDs/manifests are reused, and
divergence faults rather than silently replacing results. Reused IDs may be
emitted again for idempotent database reconciliation; PCM is not appended twice.
The normalizer's pending audio memory is one shard plus a 64 KiB read buffer and
bounded process pipes, independent of recording duration.

## Playback and verification limits

Playback disposes the prior output, all buffers and owned decoders before
seeking. Each track has eight 32 KiB float-PCM buffers. Seek uses native sample
offsets/QPC anchors; long imports first seek to a whole source second before the
target, then trim the exact remaining native samples. Track gaps and volumes
remain independent. Output underruns emit an error rather than being described
as source silence. Core playback-position notifications are output-clock
estimates (100 ms), not frame-accurate word-alignment claims.

Tests open **no capture or physical playback endpoint**. Executed validation:

- Native sample conservation, correct silent-flag representations, partial tails,
  both rotation limits, positions beyond 32 bits, pooled queue overflow and
  checkpoint recovery with retained evidence.
- Actual FFmpeg 44.1/48 kHz normalization across one-second native chunks:
  derivative bytes match a single uninterrupted resampler, including EOF tails;
  replay preserves IDs/hashes and Core offset mapping.
- Actual managed imports/probing/rejected invalid media, process cancellation,
  and native-frame-exact FFmpeg seek, with no speakers opened.
- The opt-in synthetic disk soak was actually executed: **4,296,015,872 native
  PCM bytes**, 65 independent WAVs, 16,781,312 conserved source frames.
  Test-created files were cleaned up. This is real disk I/O with synthetic
  samples, not a live desktop recording or hardware fault/power-cut test.

Run only this project's suite:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools\nuget'
$env:AUDIO_RUN_SOAK = '1' # optional: actually writes and deletes slightly over 4 GiB
& .tools\dotnet\dotnet.exe test tests\AudioTranscriber.Audio.Tests\AudioTranscriber.Audio.Tests.csproj
```

Live device startup/removal, audible multi-track playback, actual disk exhaustion
and hard power-loss recovery still require explicitly user-started Windows
acceptance tests.
