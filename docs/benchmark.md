# Bounded public ASR comparison

The console tool uses **the shipping `NvidiaRivaProvider`**, Core request/result
contracts, `MediaImporter` and `PersistentNormalizer`. The PowerShell wrapper
only configures the local SDK and launches that tool. There is no independent
HTTP/ASR script and no remote dataset code, Python dependency, or hidden model
download. Parquet is parsed locally by **Parquet.Net 6.1.0** (MIT).

## Fixture and first-use validation

- Dataset: `hf-internal-testing/librispeech_asr_dummy`.
- Revision: `5be91486e11a2d616f4ec5db8d3fd248585ac07a`.
- Object: `clean/validation-00000-of-00001.parquet`.
- URL: <https://huggingface.co/datasets/hf-internal-testing/librispeech_asr_dummy/resolve/5be91486e11a2d616f4ec5db8d3fd248585ac07a/clean/validation-00000-of-00001.parquet>.
- SHA256: `4e69a06fa5edc90921e5e7e39a7084881f8b3ed9c805c574f4f39c6fde27c603`.
- Download is bounded to **15 MiB** regardless of response Content-Length.
  Existing caches are hashed again. A mismatch fails; it is never trusted.
- The two selected complete utterances are `1272-128104-0000` and
  `1272-128104-0001`. They must be mono 16 kHz and at most 15 seconds **each**.
  No cropping with a full reference, splicing, or silent fallback is performed.

Actual preparation was exercised on 2026-09-11 UTC: file downloaded, hash checked,
Parquet parsed, embedded FLAC probed, managed originals copied and decoded with
production FFmpeg normalization. Clip 0000: **93,680 samples / 5.855 seconds**.
Clip 0001: **77,040 samples / 4.815 seconds**. Total unique audio: **10.670 seconds**.
Preparation used installed `9.0.1-full_build-www.gyan.dev`; this identifies that
distribution, not permission to redistribute an assumed LGPL-only build.

Corpus attribution: Vassil Panayotov, Guoguo Chen, Daniel Povey and Sanjeev
Khudanpur, *LibriSpeech: An ASR corpus based on public domain audio books* (2015),
[OpenSLR 12](https://www.openslr.org/12/), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
The HF dummy subset/transcoding is derived from that corpus; its current card
omits a license and does not grant a different one. Attribution and provenance
are retained in every manifest/report.

## Commands

From the repository root, with the pinned local SDK installed:

```powershell
.\scripts\Compare-Models.ps1 -PrepareOnly `
  -OutputDirectory 'tools\AudioTranscriber.Benchmarks\artifacts\public-validation'
```

This mode never opens credential files or makes cloud inference requests.
Preparation outputs public references, source and normalized hashes, exact
sample counts, and media-tool version in `fixture-manifest.json`.

Only the authorized coordinator should run the comparison:

```powershell
.\scripts\Compare-Models.ps1 -ApprovePublicAudioCloud `
  -ProductionConfigPath 'D:\Repositories\NexaTalkAI\NexaTalkAI\bin\Release\net8.0-windows\appsettings.Production.json' `
  -OutputDirectory 'tools\AudioTranscriber.Benchmarks\artifacts\comparison'
```

The command contains **a file path, never a key**. The .NET process opens that
existing file read-only, locates exactly one nonempty NVIDIA provider credential,
and ignores all endpoint/chat/model settings. Neither the wrapper nor an
external reader opens or prints the config. Do not redirect secrets to another
file, CLI argument, environment variable, or log.

NVIDIA receives only these authorized public fixture clips. Service availability,
authentication, quota, and terms still apply. The provider rechecks explicit
session/provider/track consent immediately before each send. See
[provider security and terms](providers.md).

Optional `-FFmpeg` / `-FFprobe` select installed local tool paths.
`-MaxRetries 1` explicitly permits one retry after a transient failure; the
default is **zero**. There is no endpoint or model override.

The same console host also exposes a separate `install-model` subcommand for
user-selected local Whisper weights, with required
`--accept-model-license --accept-download-bytes EXACT_BYTES` acknowledgments.
It never runs a comparison or reads credentials. See the
[explicit installation instructions](providers.md#explicit-command-line-model-installation)
and `scripts\Install-WhisperModel.ps1`. Normal benchmark invocations never download
Whisper weights.

## Hard limits and fairness

Three catalog models × the same two clips = **six initial audio requests**,
concurrency one. One process-wide provider gate conservatively paces starts by
1.5 seconds. Per-request deadline is 60 seconds. No discovery RPC is issued.
Every attempted call—including failures and any explicit retries—is counted
**before** invocation; never more than **nine total attempts** or
**135 seconds** of submitted audio. Future probes must use the same budget.
Authentication, permission, quota or rate-limit failure stops the affected route;
the second clip is then marked skipped rather than secretly attempted.

All adapters use actual PCM16 mono 16 kHz, source language `en` mapped to verified
route locale, model-config field unset, one alternative, punctuation and verbatim
transcripts on, profanity filter off. Word offsets are enabled only on the route
where demonstrated. No reference text is sent, no chat prompt and no translation.

No cloud comparison was run by the implementation agent. A coordinator invocation
must produce `results.json` before any claim of measured hosted accuracy or live
account availability. Build success and mock transport tests are not cloud tests.

## Metrics and artifacts

`results.json` and `results.csv` are updated after each completed attempt, via
replace-after-write. JSON preserves public references, hypotheses, all parsed
segments/word timing and safe raw recognition alternatives. No key, config path,
request headers, server trailers, or remote error body is exported. CSV quotes
every cell and prefixes formula-leading text to avoid spreadsheet execution.
JSON remains the exact text evidence.

Normalization is explicit and identical for every model:

1. Unicode NFKC; invariant lowercase.
2. Retain Unicode letters and digits; replace other runes with spaces.
3. Collapse whitespace and trim. Thus contractions split at apostrophes, and
   hyphens are spaces; this is not spelling or grammatical correction.
4. WER tokens are whitespace-separated words. CER removes whitespace and uses
   Unicode scalar values, not UTF-16 code units.
5. Unit-cost Levenshtein substitutions/deletions/insertions; deterministic ties
   prefer substitution, deletion, insertion. Divide by reference unit count;
   rates can exceed 100%. An empty reference has no defined rate.

Reports expose all three edit counts, denominators, normalizations, each
attempt's success/failure/empty/partial/skipped status, latency, real-time factor,
missing word-timing segments and invalid-timing diagnostics. Latency is **adapter
wall time**, including its concurrency gate/pacing; RTF divides that by the exact
audio duration. This is not a repeated-trial network-only inference benchmark.
Unavailable/unset observed model identity stays null.

Coverage uses two planned clips per model. Failed/skipped attempts have **no**
WER/CER; they do not count as zero error. Empty/partial hypotheses retain their
individual metrics but are excluded from successful matched comparisons.
Aggregate comparative WER/CER use only clip IDs where **all three models
succeeded**, micro-averaged by reference units, with coverage displayed beside
the values. A missing matched set produces null metrics, never a winner.
On explicit retries, per-clip summaries use the last attempt and all earlier
attempts remain visible.

Both clips have the same speaker and clean read English; n=2, no repeated latency
trials. These observations **cannot establish a best D&D model**, diarization
quality, overlap handling, crowded/character voices, or multilingual accuracy.
No DER is calculated without real reference speaker/time annotations, and no
named-entity score is invented without explicit entity annotations.

Exit codes: 0 complete/prepared; 2 missing cloud authorization/path; 3 comparison
has failures, partial/empty results or skips; 4 safe credential/provider setup
failure; 5 public preparation/artifact IO failure; 6 invalid options/fixture/media
configuration; 130 cancellation. Completed-attempt artifacts are retained.
