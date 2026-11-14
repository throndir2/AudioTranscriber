# Benchmarks

**Status:** Partial
**Last Updated:** 2026-11-14
**Audience:** Users and contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `tools/AudioTranscriber.Benchmarks`, `scripts/Compare-Models.ps1`

AudioTranscriber keeps benchmark details in source-controlled docs so claims stay
bounded.

## Initial hosted ASR baseline

The one authorized live comparison used two complete public LibriSpeech dummy clips,
10.670 seconds of unique mono 16 kHz audio. It submitted the same two clips to three
NVIDIA routes: Parakeet TDT v3, Canary, and hosted Whisper large-v3.

The saved result records six attempts, 32.010 seconds of submitted audio, concurrency
one, and zero retries. Five calls succeeded; one Parakeet request timed out.

Parakeet returned real word timestamps on its successful clip. Canary and hosted Whisper
returned coarse text. Whisper's measured word differences were `Mr.` versus `MISTER`
formatting under the documented normalizer.

Only one utterance succeeded for all three models, so the matched comparison is one
clean read-speech clip. It does not identify a best D&D model or diarization quality.

## Local Parakeet comparison

Local comparison reports are historical samples, not universal accuracy guarantees.
See [the benchmark procedure and records](../benchmark.md) for their evidence and scope.

## Reproducing public fixtures

Preparation can run without credentials:

```powershell
.\scripts\Compare-Models.ps1 -PrepareOnly
```

At this revision the script points to a `net10.0-windows` output directory, but the
benchmark project targets `net10.0`. A normal build therefore does not put the assembly
where this script expects it. The command needs that source defect fixed before it works.

Cloud comparison requires explicit public-audio approval and a production configuration
path read inside the process. The path is not a key argument, and no key should be
printed or redirected.

Deep details:
[docs/benchmark.md](../benchmark.md)
and
[docs/benchmarks/2026-09-11/README.md](../benchmarks/2026-09-11/README.md).
The historical counts above were checked against
[results.json](../benchmarks/2026-09-11/results.json) and
[fixture-manifest.json](../benchmarks/2026-09-11/fixture-manifest.json).
