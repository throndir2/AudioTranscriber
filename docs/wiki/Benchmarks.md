# Benchmarks

AudioTranscriber keeps benchmark details in source-controlled docs so claims stay
bounded.

## Initial hosted ASR baseline

The one authorized live comparison used two complete public LibriSpeech dummy clips,
10.670 seconds of unique mono 16 kHz audio. It submitted the same two clips to three
NVIDIA routes: Parakeet TDT v3, Canary, and hosted Whisper large-v3.

The run made six attempts, submitted 32.010 seconds of audio, used concurrency one, and
used zero retries. Five calls succeeded; one Parakeet request timed out. Exit code 3
reported that failed request rather than a script crash.

Parakeet returned real word timestamps on its successful clip. Canary and hosted Whisper
returned coarse text. Whisper's measured word differences were `Mr.` versus `MISTER`
formatting under the documented normalizer.

Only one utterance succeeded for all three models, so the matched comparison is one
clean read-speech clip. It does not identify a best D&D model or diarization quality.

## Local Parakeet comparison

A later local comparison on 48 public English clips measured CPU Parakeet TDT v3 at
6.85% WER, 14-20× real time, and about 0.95 GB peak RAM. Whisper large-v3-turbo measured
10.56% WER on the same sample and was slower on CPU.

Treat these as documented samples, not universal accuracy guarantees. The public clips
are not private campaign audio.

## Reproducing public fixtures

Preparation can run without credentials:

```powershell
.\scripts\Compare-Models.ps1 -PrepareOnly
```

Cloud comparison requires explicit public-audio approval and a production configuration
path read inside the process. The path is not a key argument, and no key should be
printed or redirected.

Deep details:
[docs/benchmark.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/benchmark.md)
and
[docs/benchmarks/2026-09-11/README.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/benchmarks/2026-09-11/README.md).
