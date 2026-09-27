# ASR providers

Metadata checked **2026-09-11 UTC**. Catalog identity is not a guarantee of
availability on a particular account, a runtime model name, or free quota.
Source ASR only: no translation, chat request, reference hints, or automatic
cloud fallback on errors. The one cloud re-check is the opt-in
[low-confidence fallback](#low-confidence-fallback-for-local-whisper) below.
New sessions default to [local Parakeet](#local-parakeet-default); local sessions
require no NVIDIA credential.

## Hosted catalog and wire contract

| ID | Advertised model / card | NVCF `function-id` | Verified English locale | Timing |
|---|---|---|---|---|
| `nvidia-parakeet-tdt-v3` | `nvidia/parakeet-tdt-0_6b`, Parakeet TDT v3 | `2b940e91-a70e-4483-a958-d50408b96589` | `en-GB` | Word offsets |
| `nvidia-canary` | `nvidia/canary-1b-asr`, current Canary-1B-Flash2.0 card | `b0e8b4a5-217c-40b7-9b96-17d84e666317` | `en-US` | Coarse input interval |
| `nvidia-whisper-large-v3` | `openai/whisper-large-v3` | `b702f636-f60c-4a3d-a6f4-f3568c13bd7d` | `en` | Coarse input interval |

Primary configuration sources:

- <https://build.nvidia.com/nvidia/parakeet-tdt-0_6b/api>
- <https://build.nvidia.com/nvidia/canary-1b-asr/api>
- <https://build.nvidia.com/openai/whisper-large-v3/api>

These three routes **do not advertise native diarization in this application**.
`en` maps to the route-specific verified English locale; the corresponding exact
locale is also accepted. Other languages are rejected rather than guessing
undocumented hosted locale support. Local Whisper accepts explicit ISO language
codes supported by its installed model.

`NvidiaRivaProvider` uses the generated Riva unary `Recognize` client over
TLS to **`grpc.nvcf.nvidia.com:443` only**, with exactly `function-id` and Bearer
authorization metadata. There is no endpoint override; redirects, proxies,
cookies, retry policies, and cross-provider failover are disabled. The
`RecognitionConfig.model` field remains unset; an advertised catalog name is not
a valid `riva-build --name` inferred from the card. Unary responses do not report
an observed model name, so `ObservedModel` remains null.

Input is a bounded, headerless **signed little-endian PCM16, mono, 16 kHz file**.
The exact file length must equal twice the declared sample count. Sample counts
must be positive and at most 480,000 (30 seconds). This is an application-side
conservative bound, **not a verified NVIDIA hosted upload limit**.

Settings: one alternative, automatic punctuation, verbatim transcripts,
profanity filtering off, no speech context or custom translation configuration.
Word offsets are requested only for Parakeet. Timeout defaults to 60 seconds
(configurable 1–300 seconds), connect timeout 15 seconds, maximum decoded response
4 MiB. All NVIDIA instances in the same process share concurrency one and a
minimum 1.5-second start spacing; separate processes do not share that gate.
Catalog help has advertised 40 requests/minute and 10,000/day, with account/model
variance. These are **not free-credit or continued-availability guarantees**.

## Timing and evidence

All returned results are consumed, not just the first. The first alternative
of each result becomes a Core segment; all alternatives and provider numeric
values remain in the bounded raw-response JSON. That JSON contains recognition
results only, never request headers, credentials, configuration, server trailers,
or remote error bodies.

Riva word offsets are **integer milliseconds**, relative to the supplied audio
including context. Zero-duration words are valid. Zero and non-finite confidence
are represented as null, not invented certainty; negative finite provider
confidence is retained rather than mistaken for a probability.

Invalid/negative/reversed/out-of-input offsets remain in raw evidence and produce
diagnostics/partial status, not fabricated corrections. Text-only routes retain
the full input interval with `TimingGranularity.Chunk`; they do not get invented
word offsets or concatenation-based overlap removal. Multiple coarse results may
share that interval because the response does not establish their separate
boundaries. App-owned core/context reconciliation must use these Core contracts
and must not globally remove repeated words.

Empty output and mixed empty/nonempty or explicitly underprocessed responses are
distinguished. Missing `audio_processed` produces `audio-coverage-unreported`:
recognition success is not proof of complete acoustic coverage.

## Privacy and credential API

```csharp
using var keys = new NvidiaCredentialVault();
keys.SetMemoryOnly(keyFromPasswordInput); // never log or serialize this value
await using var provider = new NvidiaRivaProvider(
    NvidiaModelCatalog.Parakeet, keys, durableConsentStore);
TranscriptionResult result = await provider.TranscribeAsync(coreRequest, cancellationToken);
```

`durableConsentStore` implements Core `ICloudConsentStore`. The adapter checks
granted consent for the exact session, provider and track before reading audio,
again after waiting for the account gate, and immediately after credential
acquisition before sending. Missing disclosure version fails closed. Revocation
prevents subsequent uploads; already-sent audio cannot be recalled. The app owns
the disclosure UX and consent persistence. Disclosure must name the provider and
tracks, finite quota, applicable provider terms/logging and personal/confidential
information restrictions. Do not send such information where provider terms or
participant consent prohibit it.

Keys are memory-only unless `SaveForCurrentUserAsync(path, explicitlyApproved:
true)` is explicitly invoked. Persisted values use Windows DPAPI **CurrentUser**.
`LoadForCurrentUserAsync`, `Clear`, and `Dispose` are available. Managed-memory
string copies cannot be guaranteed erased by the CLR; storage buffers are cleared
where controllable. Do not put keys in CLI arguments, environment variables,
logs, settings JSON, model metadata, transcripts, or exports.

`ReadOnlyProductionCredentialLoader.LoadAsync(path)` is only for the authorized
comparison. It opens at most 2 MiB of JSON read-only, recursively locates a unique
nonempty NVIDIA row identified by `ProviderName == "Nvidia"` or the numeric enum
value 7 in `ProviderName`/`Provider`, with a nonempty `ApiKey`. Ambiguity or invalid input fails closed. It
never inherits the row's endpoint/model and never prints the parsed file or key.
No external secret-reading command is required or supported.

ASR calls throw Core `TranscriptionProviderException` with safe
`ProviderErrorCode` values: authentication, permission, quota, transient
unavailability, deadline, invalid audio, unsupported language, or invalid
response. Remote status details/trailers and inner exceptions are not retained.
Resource exhaustion stops the endpoint: it is not presumed to be a harmless
temporary rate limit. The provider does not retry; orchestration may schedule
capped transient retries after rechecking consent.

## Optional local Whisper

`LocalWhisperProvider(string modelPath)` implements the same Core interface.
It loads a local GGML model lazily on the first request, hashes it for observed
identity, converts the same validated PCM16 samples to normalized floats, and
invokes **Whisper.net 1.9.1 / Whisper.net.Runtime 1.9.1**, source commit
`98278acc38ae23590cdfa9859f78f089abae52a7`. Inference is serialized. The
factory uses `UseGpu = true` and `UseFlashAttention = true`. The bundled
**Whisper.net.Runtime.Vulkan 1.9.1** is tried before the CPU runtime. It needs
only the GPU driver's `vulkan-1.dll`, and ggml prefers dedicated GPUs over
integrated ones. The loaded runtime is reported as the `whisper-runtime:<name>`
diagnostic. CUDA runtimes are not bundled. Local measurement: large-v3-turbo on a
22 s chunk took about 6.8 s on Vulkan (Intel UHD 770 iGPU) and about 6.5 s on
the CPU (i7-13700K, 8 threads). A dedicated GPU should be much faster, but the
RTX card was not enumerated during testing, so that is unmeasured. The pinned
native whisper.cpp gitlink is
`f24588a272ae8e23280d9c220536437164e6ed28` (MIT, ggml authors).
Segment timing is retained; words are not fabricated. Each segment gets a
`Confidence`: the mean decoder probability of its text tokens (whisper.cpp
control tokens such as `[_BEG_]` and timestamps are excluded). The raw evidence
also keeps the lowest token probability, text-token count, and no-speech
probability. This is the model's own estimate, not a calibrated accuracy.
`WithLanguage` and `WithNoContext` are used; `WithTranslate` is never enabled.
No credential exists on this path, and model or inference errors never fall back
to the cloud. Cancellation is cooperative
through the binding, not a guarantee of immediately interrupting native model
load. Inference is bounded to 30-second inputs.

### Explicit command-line model installation

Review the catalog without downloading anything:

```powershell
.\scripts\Install-WhisperModel.ps1 -List
.\scripts\Install-WhisperModel.ps1 -Model tiny
```

`-List` succeeds without downloads. Selecting a model without acknowledgments
prints its exact byte count, license/source links and SHA256, then exits 2 without
creating model files or starting a download. After reviewing that disclosure,
explicitly acknowledge **both** the license and exact byte count:

```powershell
.\scripts\Install-WhisperModel.ps1 -Model tiny `
  -ModelDirectory '.models\whisper' `
  -AcceptModelLicense -AcceptDownloadBytes 77691713
```

This is an optional **77,691,713-byte** weights download, not an app-startup step.
Select the resulting `.models\whisper\ggml-tiny.bin` with the app's local model-file
picker. The byte acknowledgment must match the selected catalog entry exactly;
choosing another variant requires acknowledging its own size. Large-v3 exceeds
1 GiB and is never selected by default. Inference can require substantially more
memory than the file size.

The equivalent .NET command, after building the benchmark tool, is:

```powershell
.\.tools\dotnet\dotnet.exe `
  .\tools\AudioTranscriber.Benchmarks\bin\Release\net10.0-windows\AudioTranscriber.Benchmarks.dll `
  install-model --model tiny --directory '.models\whisper' `
  --accept-model-license --accept-download-bytes 77691713
```

`install-model --list` and `install-model --help` are also available.
This subcommand dispatches directly to the existing verified model downloader:
it does not prepare the benchmark dataset, read NVIDIA credentials, send audio,
or run inference. There is no endpoint override or arbitrary model URL. Only
listing, missing-approval paths, and fake-installer dispatch were exercised during
implementation; no Whisper weights were downloaded.

Use `LocalWhisperModelCatalog.All` to show the size/license **before**
`VerifiedModelDownload.InstallWhisperAsync(model, directory,
explicitlyApproved: true, progress, cancellationToken)`. Provider construction
never downloads. The desktop app no longer downloads Whisper at startup (Parakeet
is the default). The first time a Local Whisper session is recorded or imported, or
one of its jobs runs without a model, `AppController` installs the recommended
large-v3-turbo model through this downloader; Whisper jobs queued meanwhile wait
and run once it is verified.
The downloader
streams into an owned partial file, enforces exact bytes, checks SHA256, and
atomically renames only a verified file.
The installer retains the full OpenAI MIT license and model origin/hash alongside
each installed weight file. User-supplied differently named local
GGML models are accepted and hashed, but their origin/license is the user's
responsibility. A recognized catalog filename must match its catalog hash.

All downloadable variants below are multilingual OpenAI Whisper weights (MIT),
GGML converted by whisper.cpp contributors:

| Variant | Exact bytes | SHA256 |
|---|---:|---|
| tiny | 77,691,713 | `be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21` |
| base | 147,951,465 | `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe` |
| small | 487,601,967 | `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b` |
| large-v3 | 3,095,033,483 | `64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2` |
| large-v3-turbo (recommended) | 1,624,555,275 | `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69` |

`LocalWhisperModelCatalog.Recommended` is large-v3-turbo: large-v3's encoder
with a 4-layer decoder, roughly 5–8× faster at near-equal accuracy. The desktop
app installs it on demand into the sibling `whisper` directory of the
diarization model directory, and auto-selects it there at startup.

Download origin: `ggerganov/whisper.cpp` on Hugging Face, revision
`5359861c739e955e79d9a303bcbc70fb988958b1`.
Model metadata: <https://huggingface.co/ggerganov/whisper.cpp/tree/5359861c739e955e79d9a303bcbc70fb988958b1>.
License: <https://github.com/openai/whisper/blob/v20250625/LICENSE>.
Binding/runtime: <https://github.com/sandrohanea/whisper.net>,
<https://github.com/ggml-org/whisper.cpp> (MIT).
Small models are optional CPU-friendly choices, not an accuracy ranking.
Larger models exceed 1 GiB and can require substantial memory. No weights were
downloaded merely to validate the build.

## Local Parakeet (default)

`SherpaParakeetProvider` (id `local-parakeet`, in `AudioTranscriber.Diarization`) is
the default for new sessions. It runs NVIDIA **Parakeet TDT 0.6B v3** on the CPU
through the sherpa-onnx 1.13.8 runtime the app already ships for speaker analysis.
It needs no key and uploads nothing. The model is sherpa-onnx's int8 ONNX export:
`sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2`, **487,170,055 bytes**, SHA256
`5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf`. The weights
are CC BY 4.0 (NVIDIA); sherpa-onnx is Apache-2.0.

At startup the app downloads the archive into the sibling `parakeet` directory of
the diarization models and checks the archive hash. It then unpacks it through the
same bounded tar reader, checks the size and SHA256 of each loaded file, and writes
a `NOTICE.txt` next to the model. The files take about 640 MB on disk.

It covers 25 European languages (bg, cs, da, de, el, en, es, et, fi, fr, hr, hu, it,
lt, lv, mt, nl, pl, pt, ro, ru, sk, sl, sv, uk) and detects which one is spoken.
Sessions in other languages must use Local Whisper; Parakeet rejects them with
`UnsupportedLanguage`.

Output is one word-timed segment per chunk, so recognition windows get 3 s of
neighbouring context like the hosted word-timed routes. Words are built from the
SentencePiece tokens: a leading space starts a word. Times come from the token
timestamps plus TDT durations. Each word and segment has a `Confidence`, the mean
token probability from `ys_log_probs`. The managed wrapper doesn't expose these, so
the provider reads the native `SherpaOnnxGetOfflineStreamResultAsJson`. Inference is
serialized. Threads are `ProcessorCount / 4`, clamped to 2–4: ONNX Runtime's worker
threads spin, so more threads buy little speed for much more CPU.

Measured 2026-09-27 on an i7-13700K (24 logical CPUs), with the same 48 public
English clips as below: 7.4 minutes, first clip excluded as warm-up.

| Engine | WER | Speed | CPU seconds per audio second | Peak RAM |
|---|---|---|---|---|
| **Parakeet TDT v3, CPU, 2 threads** | **6.85%** | 14× real time | **0.21** | 0.95 GB |
| Parakeet TDT v3, CPU, 4 threads | 6.85% | 20× real time | 0.40 | 0.95 GB |
| Whisper large-v3-turbo, CPU, 8 threads | 10.56% | 1.3× real time | 5.94 | 1.8 GB |
| Whisper large-v3-turbo, Vulkan on the Intel UHD 770 iGPU | 10.56% | 1.4× real time | 0.15 (GPU does the work) | 2.2 GB |
| Hosted Parakeet TDT v3 (reference) | 6.44% | network | — | — |

So Parakeet is far lighter on the CPU than Whisper on the CPU, about 15–28× less CPU
per second of audio. It is about as light as Whisper offloaded to a GPU, while being
10–15× faster and making about a third fewer word errors.

## Low-confidence fallback for Local Whisper

When a Local Whisper chunk's lowest segment `Confidence` is below the threshold
(default **0.85**), the same core-only PCM window is re-recognized by Parakeet,
trying these in order:

1. The opted-in GPU container (below), when it is running and the session is English.
2. Local CPU Parakeet, when its model is installed and the language is supported. No
   upload and no consent needed.
3. Hosted `nvidia-parakeet-tdt-v3`. This needs **all** of: the session's NVIDIA upload
   consent, an NVIDIA key, an `en`/`en-GB` session, and cloud work not blocked.

A non-partial Parakeet result, including an empty one, replaces Whisper's text for
that chunk and brings word timestamps. The provenance notes the Whisper score, and
the raw attempt keeps both results (`LowConfidenceFallback`). A partial response or
any error keeps the Whisper text. Hosted authentication, permission, or quota errors
turn the hosted fallback off until a key is entered again. Override the threshold
with `"FallbackBelowConfidence": 0.9` in the data root's `preferences.json`.

Calibration (2026-09-26, 48 public English clips, 7.4 minutes, 1,212 words from
the Open ASR Leaderboard AMI, Earnings-22, VoxPopuli, and LibriSpeech test-other
sets): local large-v3-turbo scored 10.56% WER; hosted Parakeet TDT v3 scored 6.44%.
Replacing chunks below each threshold with hosted Parakeet gave:

| Threshold | Chunks sent | WER |
|---|---|---|
| none | 0% | 10.56% |
| 0.80 | 17% | 8.83% |
| **0.85** | **23%** | **8.09%** |
| 0.90 | 38% | 7.18% |
| all | 100% | 6.44% |

With local CPU Parakeet as the target, 0.85 gave 8.75%. The lowest segment score
predicted errors better than a token-weighted mean or the single lowest token. This
is a small sample, so treat about 1 point as noise.

## Paid LLM fallback: tested, not adopted

Parakeet's token confidence separates easy clips from hard ones well. The 12 least
confident of the 48 clips had 10.4% WER, against about 2% for the 12 most confident.
If those 12 were fixed perfectly, overall WER would fall from 6.85% to 4.04%. On
2026-09-27 six OpenRouter audio models were tested on all 48 clips at temperature 0
with a verbatim-transcript prompt, for $0.43 in total:

| Model | WER, all 48 | WER, 12 hardest | Overall with those 12 escalated | $ per audio hour |
|---|---|---|---|---|
| CPU Parakeet TDT v3 (baseline) | 6.85% | 10.4% | — | 0 |
| qwen/qwen3.8-omni-flash | 6.77% | 11.6% | 7.18% | 0.06 |
| mistralai/voxtral-small-24b-2507 | 6.93% | 11.0% | 7.01% | 0.34 |
| google/gemini-3.8-flash | 7.26% | 12.5% | 7.43% | 0.12 |
| google/gemini-3.1-pro-preview | 7.51% | 11.0% | 7.01% | 1.59 |
| openai/gpt-audio-mini | 10.56% | 13.4% | 7.67% | 0.06 |
| openai/gpt-audio | 27.64% | 14.9% | 8.09% | 1.31 |

On those hard clips, no model beat Parakeet by itself, so plain escalation made the
overall result worse. A second variant sent the audio **plus Parakeet's draft** and
asked for corrections, for $0.15 in total. That helped a little:

| Draft correction by | 12 hardest | Overall |
|---|---|---|
| Qwen 3.8 Omni Flash | 9.1% | 6.52% |
| Gemini 3.1 Pro | 10.1% | 6.77% |
| Gemini 3.8 Flash | 10.7% | 6.93% |
| Voxtral Small | 11.0% | 7.01% |

The best case was 4 fewer word errors out of 1,212, within noise, so no paid
fallback is built. The hard clips are overlapping, far-field, disfluent meeting
speech that every model struggles with. The free NVIDIA-hosted Nemotron 3 Nano Omni
scored 11.4% on the same 12 clips.

## Parakeet on a local NVIDIA GPU (NVIDIA speech NIM, opt-in)

After the default models are ready, the desktop app runs `nvidia-smi` to find the
largest NVIDIA GPU with **compute capability 8.0+** (RTX 30-series or newer) and at
least ~7.5 GB. It reports what it found in Privacy / models (and MCP
`status.localGpu`), but **does not use the GPU by default**. On Windows, NVIDIA's
speech container runs through Docker Desktop's WSL 2 backend, and NVIDIA's support
matrix marks only Parakeet CTC 0.6B and 1.1B as WSL 2-supported. TDT is marked
unsupported. CTC 1.1B (7.76% WER through NVIDIA's hosted route) and CTC 0.6B
(8.99%) are both less accurate than TDT on the CPU (6.85%), and the CPU route is
already 14–20× real time. The NIM licence also covers only research, development,
and testing for Developer Program members; production use needs NVIDIA AI Enterprise.
Docker Desktop's own licence is free only for personal use and small businesses.

To trade that accuracy for lower CPU load, set `"LocalGpuParakeet": true` in
`preferences.json`, run Docker Desktop, and set an NVIDIA key. The app then:
1. Signs in to `nvcr.io`; the key goes over stdin into Docker's credential store.
2. Pulls `nvcr.io/nim/nvidia/parakeet-1-1b-ctc-en-us` once, **8.8 GB compressed**.
3. Creates the container `audiotranscriber-parakeet` with the offline profile
   `mode=ofl,vad=default,diarizer=disabled` (5.83 GB GPU memory). It is published on
   `127.0.0.1:59051` (gRPC) and `127.0.0.1:59000` (health) only.

NIM then downloads and optimizes the model, which can take up to 30 minutes. While it
runs, English `local-parakeet` jobs and the Whisper fallback use the GPU; other
languages stay on the CPU. The container stops when the app closes and is reused next
time. `NGC_API_KEY` is passed from the app's environment rather than the command
line, but Docker keeps it in the container configuration (`docker inspect`). Requests
use plaintext gRPC to 127.0.0.1 with no credentials. A GPU failure keeps the Whisper
text in fallback mode. The app does not install Docker Desktop itself.

Verified 2026-09-26 on a PC without a usable NVIDIA GPU:
- The probe declined.
- A fake local Riva server on 127.0.0.1:59051 received the controller's request with
  no credentials.
- `nvcr.io` accepted the key.

The real container itself was not run.

## Protocol/dependency provenance

Unmodified upstream schemas under `src\AudioTranscriber.Providers\Protocol`:
NVIDIA Riva common revision **`09764438d0c3d950a2c1def7b514a976668941e5`**,
<https://github.com/nvidia-riva/common/tree/09764438d0c3d950a2c1def7b514a976668941e5/riva/proto>.

| File | SHA256 |
|---|---|
| riva_asr.proto | `e9b9443de9fd02c96935bf9fe32a7d459e5ca89c6f7731735de23d6e83dfd41e` |
| riva_audio.proto | `7ab2939dfe9f5ed121eba1c6b86cad0cd561fa483acfc653762a8b8af0b2a123` |
| riva_common.proto | `07d668e74d7c2c4e01fa5fee5323b9f6aff2f57f86f4f00d51656b9cb2a3a377` |

NVIDIA portions MIT; inherited Google ASR portions Apache-2.0. Original notices
remain in every schema, with NVIDIA MIT and Apache license texts alongside.
Generated C# uses pinned Grpc.Tools/Grpc.Net.Client 2.83.0 (Apache-2.0),
Google.Protobuf 3.36.1 (BSD-3-Clause) and
System.Security.Cryptography.ProtectedData 10.0.0 (MIT).
NuGet lock files pin dependency content hashes. Hosted service use is separately
subject to NVIDIA's API/service and model terms; protocol/code licenses do not
grant free service or override those terms.
