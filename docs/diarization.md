# Local speaker diarization

## What runs locally

`AudioTranscriber.Diarization` implements the Core `IDiarizationService`.
Production callers must use `WorkerDiarizationService`; the in-process
`SherpaDiarizationService` is for the owned worker and controlled tests. Neither
transmits audio, calls an ASR provider, nor downloads models during inference.
The worker receives only local request/result paths on its command line. Its
bounded JSON request contains local PCM/model paths, timing, matching settings,
and the session registry—not provider credentials. Only an allowlist of runtime
environment variables is inherited.

Dependencies are pinned:

- `org.k2fsa.sherpa.onnx` **1.13.8**, Apache-2.0.
- `org.k2fsa.sherpa.onnx.runtime.win-x64` **1.13.8**, Windows x64 ONNX Runtime
  and sherpa native binaries; retain upstream dependency licenses when publishing.
- `SharpZipLib` **1.4.2**, MIT, for bounded bzip2 archive decoding.

Upstream sherpa source tag `v1.13.8` resolves to
`11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf`. Build/publish the worker for
`win-x64`; do not distribute every platform runtime restored transitively by
the managed package.

The neural pipeline is real: pyannote segmentation, sherpa bounded-call
clustering, standalone WeSpeaker embeddings from clean individual intervals,
then conservative persistent speaker matching. It is not energy-based speaker
guessing, an ASR speaker-label passthrough, or an embedding of a whole mixed chunk.

## Explicit model installation

No models are installed automatically. After reviewing the sizes/licenses,
run from the repository with the local SDK environment configured:

```powershell
$env:DOTNET_ROOT = (Resolve-Path .tools\dotnet).Path
$env:NUGET_PACKAGES = (Resolve-Path .tools\nuget).Path
$env:DOTNET_CLI_HOME = (Resolve-Path .tools\cli-home).Path
& .tools\dotnet\dotnet.exe build src\AudioTranscriber.Worker --verbosity quiet
& .tools\dotnet\dotnet.exe `
    .\src\AudioTranscriber.Worker\bin\Debug\net10.0-windows\win-x64\AudioTranscriber.Worker.dll `
    --install-models .models\diarization
```

The API equivalent is `await DiarizationModels.InstallAsync(modelDirectory,
progress, cancellationToken)`. The installer streams over HTTPS, enforces exact
byte counts, checks SHA256 before publishing, and deletes failed partial files.
An existing but incorrect artifact is rejected, not silently trusted or replaced.
The tar must pass its pinned hash before extraction. Extraction rejects links,
absolute paths, traversal, alternate streams, duplicate paths and reparse-point
destinations; it limits entry count, expanded data and decompressed bytes.
Only the verified float model and upstream archive license are installed from
the extraction staging directory. No scripts from the archive are executed.

Total approved compressed/model download: **33,488,994 bytes (33.49 MB)**.
Additional space is needed for the archive, staging files and extracted model.

| Artifact | Exact bytes | SHA256 |
| --- | ---: | --- |
| `sherpa-onnx-pyannote-segmentation-3-0.tar.bz2` | 6,958,444 | `24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488` |
| Extracted `model.onnx` | 5,992,913 | `220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079` |
| `wespeaker_en_voxceleb_resnet34_LM.onnx` | 26,530,550 | `e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012` |

Artifacts, downloaded and independently hashed during implementation:

- [Segmentation archive](https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2).
- [WeSpeaker ONNX](https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx).
  `speaker-recongition-models` is the actual upstream release spelling.

The release artifacts are anonymously downloadable. The original
[Hugging Face segmentation distribution](https://huggingface.co/pyannote/segmentation-3.0)
is **gated**; the installer does not access or bypass that gate.
Preserve both the [original MIT CNRS 2023 notice](https://huggingface.co/pyannote/segmentation-3.0/blob/main/LICENSE)
and the conversion archive's MIT CNRS **2022** notice. These are separately
bundled under `src\AudioTranscriber.Diarization\Licenses` and installed next to
the models; the archive's original `LICENSE` is retained verbatim.

The WeSpeaker model is **CC BY 4.0**, not the project's separate Apache-2.0
code license. Attribution to the WeSpeaker authors and sherpa ONNX conversion
contributors, source/model links, a license link and the conversion notice are
bundled in `WeSpeaker-NOTICE.txt`. The application does not modify the ONNX bytes.
The conversion includes sherpa-specific runtime metadata.
See the [specific LM model card](https://huggingface.co/Wespeaker/wespeaker-voxceleb-resnet34-LM/blob/f0c48c298fd835726c27956a5d617bad7115627e/README.md),
[WeSpeaker pretrained-model policy](https://github.com/wenet-e2e/wespeaker/blob/dfa741957e5c11f477623b6e583d67d0af25ee88/docs/pretrained.md),
and the [CC BY 4.0 license](https://creativecommons.org/licenses/by/4.0/).

## Calling and persisting the service

```csharp
using AudioTranscriber.Core;
using AudioTranscriber.Diarization;

var registry = savedJson is null
    ? new SpeakerRegistrySnapshot(sessionId, 0, [])
    : CoreRegistrySerializer.Deserialize(savedJson);

await using var diarizer = new WorkerDiarizationService(
    workerExePath, ownedJobDirectory,
    DiarizationModelPaths.InDirectory(modelDirectory));

var request = new DiarizationRequest(
    sessionId, trackId, rawPcm16Path, sampleCount, sessionStartTicks,
    CoreStartSample: contextSamplesBeforeCore,
    CoreSampleCount: ownedCoreSamples,
    NormalizedStartSample: normalizedStartSample);
var result = await diarizer.DiarizeAsync(request, registry, cancellationToken);
var sourceTurns = DiarizationCoordinates.ToSourceIntervals(
    result, request, normalizedStartSample);
var updatedJson = CoreRegistrySerializer.Serialize(result.Registry);
// Persist turns, updatedJson, revision, and job completion atomically.
// Use registry.Revision as the optimistic-concurrency expected revision.
```

Input is **raw signed little-endian PCM16, mono, 16 kHz**, not a WAV container.
Declared sample count must match the file exactly. Every supplied input is
between one sample and **60 seconds**. Callers split multi-hour recordings and
process the session's registry serially; never pass a complete multi-hour array.
The application normalizer's 24/30-second derivatives are suitable bounded inputs.
The application groups short consecutive live chunks into windows of about 20
seconds (at most 30) per speaker job, queued on the window's last chunk.
Inputs below 30 seconds, including final short tails, are silence-padded to
30 seconds **only inside inference**. All returned turns are clipped back to
the real input and its owned core. This avoids sherpa's <=10-second
no-clustering branch without inventing speech past EOF.

`SpeakerTurn.StartTicks/EndTicks` are absolute session ticks.
`NormalizedStartSample/NormalizedEndSample` are track-global normalized
sample positions. If the request supplies the correct `SourceFrameOffset` and
`SourceSampleRate` for the supplied PCM origin's continuity run,
`SourceStartFrame/SourceEndFrame` also contain original native frame coordinates;
otherwise those fields remain null. `ToSourceIntervals` additionally exposes
input-relative positions. Core `AudioTimeMapping` uses the same rational mapping;
normalized samples must not be mislabeled native source frames.

The returned registry uses immutable **GUID** speaker IDs plus permanent,
monotonic numbers displayed initially as **Speaker 1**, **Speaker 2**, etc.
The reconciler's internal `Speaker1` keys correspond to these numbers, not names.
Renames preserve GUIDs, numbers and existing display names during reprocessing.
Registry JSON includes normalized 256-dimensional centroids, up to five diverse
representatives per profile, evidence metadata, revisions and merged tombstones.
Representative evidence timestamps bound the clean fragments actually used;
an average of disjoint clean fragments does not imply that intervening overlap
or silence contributed to the embedding. Clean evidence duration is stored separately.
It is validated and capped at 8 MiB and 256 total identities/tombstones.
These are explicit resource limits, not a four-speaker limit.

User merges are explicit metadata: set the source `SpeakerIdentity.MergedIntoId`
to the retained GUID, keep both entries, and increment/persist the revision.
The adapter resolves merge chains, retains tombstones and representative
evidence, and never reuses the removed number. Display names and real external
participant identifiers are preserved, never inferred from acoustic similarity.
Character labels are separate application annotations; changing a character
voice is not proof of a different person or a participant identity.

For worktree-local SDK hosting, pass the absolute worker `.dll` path and
`dotnetHostPath: absoluteDotnetExePath` to `WorkerDiarizationService`.
This still runs native inference in a separately owned Windows process with
identical hard-cancellation behavior. It avoids relying on an installed
machine-wide runtime or apphost discovery. Packaged builds may instead pass the
published worker `.exe` without `dotnetHostPath`.

## Matching, overlap and limitations

- Sherpa clustering is confined to each 30–60-second native call. It is not
  the session registry and is never run over hours of samples.
- The segmentation network has a ten-second internal receptive window,
  up to three local speakers and two simultaneous speakers per frame. This
  does **not** limit the overall session to three speakers.
- Other speakers' intervals are subtracted before embedding. Clean fragments
  are edge-trimmed by 120 ms; each must have >=1.5 seconds afterward.
  Up to three fragments, capped at eight seconds each, are embedded separately.
  At least two seconds of accepted clean evidence is required per local cluster.
  Inconsistent fragment embeddings abstain rather than contaminate the registry.
- Default session match score threshold is **0.70**, novel-speaker threshold
  **0.45**, runner-up margin **0.08**. Normal profiles score the average of
  centroid cosine and best representative cosine. Explicitly user-merged
  profiles use their representative evidence to honor that merge. The gray
  zone and near ties return Unknown, not forced old/new identities.
- Local clusters that overlap cannot map to the same stable identity.
  Output intervals are split at overlap boundaries and carry
  `Overlap | Ambiguous` flags. A known speaker may still be present in an
  overlapping interval; this must **not** force an ASR word onto that speaker.
  Null IDs, insufficient evidence and ambiguity must remain unattributed.
- `Confidence` is a registry similarity score, **not a calibrated probability**
  or measured model accuracy. Algorithm version is reported in diagnostics.
- Transcript lines take the speaker whose turns cover at least 80% of the line's
  detected speech and at least 40% of the line; recognizer segment edges that graze
  a neighbour's turn no longer force Unknown. Overlap, unattributed or mixed speech
  keeps the line unattributed.

## User labels, merges and voice enrollment

The app never infers identity from names, but user actions feed the registry:

- **Labeling lines** (`AppController.AssignSpeaker`) marks them manual and, when the
  models are installed, runs the worker in enrollment mode (`--diarize` with
  `DiarizationWorkerRequest.Enrollment`, `ISpeakerEnrollmentService.EnrollAsync`).
  Up to 60 seconds of the labeled lines' audio is segmented; the dominant local
  speaker's clean evidence (same rules as above, >=2 seconds) becomes a
  representative of that speaker's profile, or a new profile under the speaker's
  GUID and name. Too little clean speech abstains. If the voice confidently matches
  another profile (match threshold and runner-up margin), the result carries
  `SimilarTo:<guid>:<score>`; an automatic "Speaker N" is then merged into the
  labeled speaker, a user-named one is only reported.
- **Renaming to an existing name** (`RenameSpeakerAsync`) merges: the dropped
  identity gets `MergedIntoId`, its rows and turns move to the kept ID, and its
  speaker row is removed. The ID with a voice profile is kept.
- After enrollment or a merge, finished speaker windows that still contain an
  unattributed turn are re-queued; **Analyze speakers** re-queues every window.
  Registry reads/writes by analysis, enrollment and merges are serialized.

## Cross-session voice library

`LibraryStore` keeps a `voices` table shared by all sessions: a user-given name,
the embedding model SHA256, and up to 15 normalized 256-dimensional samples. Each
sample is tagged with the session and session speaker it came from.
`AppController.SyncVoiceLocked` adds up to three diverse representatives of a
session speaker, including those of speakers merged into it. It runs only when the
user names that speaker, by renaming or by line-label enrollment. When a speaker is
renamed, the samples it gave under its old name are withdrawn. Automatic matches
never add samples.

After each speaker job commits, `RecognizeVoicesLocked` compares every session
speaker that still has an automatic "Speaker N" name and no library link against
the library. It uses `VoiceLibrary.BestMatch`: the same 0.70 score and 0.08
runner-up margin as in-session matching. The score is centroid cosine averaged
with the best sample-pair cosine. A match renames the speaker, or merges it with
the session speaker that already has the name, and stores `voice:<id>` in
`speakers.participant_id`. `voice:manual` marks a speaker the user renamed back to
"Speaker N", so the library never renames it. Voices from a different embedding
model are ignored. Deleting a voice clears its links; session registries are
untouched. The preference `RememberVoices` (on by default) gates both remembering
and automatic recognition. The explicit **Match known voices**, **Remember this
session's named voices** and **Learn from past sessions** actions ignore it.
`RememberAllSessionVoicesAsync` runs `SyncVoiceLocked` for every named speaker of
every session, oldest first; with `firstRunOnly` it runs once per library (when
remembering is on) and then sets the `VoiceLibraryBackfilled` preference, so
speakers named before the library existed are learned on the first start.

Short interjections, overlap-only speech, crowding, noise, roleplayed voices,
similar voices and recording changes can remain Unknown or split one person.
Thresholds are conservative defaults, not calibrated D&D operating points.
The models do not identify people by name or perform reliable speech separation.

## Cancellation and local data

The worker uses only owned process handles and `Kill(entireProcessTree: true)`.
Cancel, dispose and timeout terminate the owned worker, await exit, then remove
its per-call JSON directory. Calls are serialized per service. Default timeout is
five minutes, configurable up to thirty minutes. There is no reliance on the
native progress callback's return value, which does not reliably cancel inference.
The worker protocol is versioned, request/result JSON is capped at 12 MiB, and
each redirected output channel is capped at 65,536 characters. Output is not
forwarded as private native error text. App crashes can leave a job directory;
application recovery should clean its own abandoned directories.

Registry embeddings, voice-library samples and request files are local
biometric-like voice data. Store them in the private user-data directory, not a
shared/export folder. Delete session data with the session according to the app's
retention policy; voice-library entries are deleted from **Privacy / models →
Voice library**. Exports never include embeddings.
Installing models makes public model-download requests; inference makes none.

## Verification performed, not a quality benchmark

Windows x64 .NET SDK **10.0.401**, runtime **10.0.12**, actual pinned sherpa
native binaries and both verified models were exercised locally:

The initial installed-model test run passed **26 tests, zero skipped** with
the generated fixture supplied. A subsequently added public read-speech smoke
test also passed independently against the actual native worker.

- Real 12-second silence/sine-wave input, padded to 30 seconds, passed neural
  inference and owned-core/session-offset validation.
- Locally generated Windows SAPI speech was normalized by FFmpeg to mono16k
  PCM16 (**671,852 bytes; 20.995375 seconds**). Real segmentation produced turns; separately extracted clean speech
  embeddings produced 256-dimensional registry entries. A second owned-worker
  call over the same speech retained serialized GUIDs and renamed display names.
- Cancellation after an owned worker started killed that process and cleaned
  its request files; no native callback cancellation was assumed.
- Simulated six-identity tests cover cross-window remapping, serialization,
  bounded representatives, rename/merge stability, overlap incompatibility,
  runner-up/gray-zone abstention, short speech and cancellation.
- Model tests cover checksum failure, over-limit download, cached verification,
  traversal/links/alternate-stream rejection and bounded input validation.
- The benchmark-prepared public clip `1272-128104-0000` also passed actual
  segmentation and clean-embedding inference: **93,680 samples, 5.855 seconds**,
  mono16k PCM16; PCM SHA256
  `156e1e1821700d4c4ad31a63e6d901a5088d5ee155fafd02a9d0d2de37f771e6`.
  The test verifies the exact PCM hash before calling the worker. This is one
  clean read utterance from one speaker, not a multi-speaker evaluation.

The public fixture comes from `hf-internal-testing/librispeech_asr_dummy`,
revision `5be91486e11a2d616f4ec5db8d3fd248585ac07a`, with source Parquet SHA256
`4e69a06fa5edc90921e5e7e39a7084881f8b3ed9c805c574f4f39c6fde27c603`.
Attribution: LibriSpeech ASR corpus, Vassil Panayotov, Guoguo Chen, Daniel Povey
and Sanjeev Khudanpur (2015), [OpenSLR 12](https://www.openslr.org/12/),
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), via the Hugging Face
dummy subset and the production FFmpeg normalizer. The subset card omits a
license; it does not independently grant a different one. See `docs\benchmark.md`
and the benchmark cache's `fixture-manifest.json` for preparation provenance.

These synthetic and single-speaker read-speech fixtures establish **model
loading, real inference, protocol, and reconciliation behavior only**.
They establish no DER, speaker-count
accuracy, robustness to character voices, or D&D-session quality.

To run unit tests and explicitly enable installed-model tests:

```powershell
$env:DIARIZATION_MODEL_DIRECTORY = (Resolve-Path .models\diarization).Path
$env:DIARIZATION_WORKER_EXE = (Resolve-Path `
    src\AudioTranscriber.Worker\bin\Debug\net10.0-windows\win-x64\AudioTranscriber.Worker.dll).Path
$env:DIARIZATION_DOTNET_HOST = (Resolve-Path .tools\dotnet\dotnet.exe).Path
# Optional: local synthetic speech, raw mono16k PCM16, <=60 seconds.
$fixture = & .\tests\AudioTranscriber.Diarization.Tests\Create-SyntheticFixture.ps1
$env:DIARIZATION_SYNTHETIC_PCM = $fixture.Path
# Optional, only after the public benchmark fixture has been prepared:
$env:DIARIZATION_PUBLIC_PCM = (Resolve-Path `
    tools\AudioTranscriber.Benchmarks\artifacts\public-validation\fixture\1272-128104-0000.pcm).Path
& .tools\dotnet\dotnet.exe test tests\AudioTranscriber.Diarization.Tests --verbosity minimal
```

Native tests explicitly skip when their paths are not supplied; they do not
install models. Synthetic speech is generated locally. The optional public
test reads the already-prepared benchmark cache without downloading or copying
a natural-speaker dataset into the source tree.

This environment exhibited an apphost-only startup access violation after some
incremental rebuilds; the same worker DLL ran correctly through the explicitly
selected local `dotnet.exe`. Use that documented host mode for local development.
This is not evidence of a native model inference failure, and published apphost
behavior has not been claimed as verified here.

Implementation references:
[C# diarization API](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/scripts/dotnet/OfflineSpeakerDiarization.cs),
[standalone embedding API](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/scripts/dotnet/SpeakerEmbeddingExtractor.cs),
[bounded-call segmentation/clustering implementation](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/offline-speaker-diarization-pyannote-impl.h),
[quadratic clustering implementation](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/fast-clustering.cc),
[model documentation](https://k2-fsa.github.io/sherpa/onnx/speaker-identification/models.html).
