# Initial hosted ASR baseline

This is the **one authorized live comparison**, not a D&D model ranking.
The canonical `results.json`, `results.csv`, and `fixture-manifest.json` are
unchanged copies of the sanitized public run. No private recording or credential
value appears in these artifacts.

## Data and budget

Source: `hf-internal-testing/librispeech_asr_dummy`, revision
`5be91486e11a2d616f4ec5db8d3fd248585ac07a`, derived from the LibriSpeech corpus
(OpenSLR 12, CC BY 4.0). Attribution, download URI, original/derived hashes, and
normalizer versions are in the manifests. The fixture Parquet SHA256 is
`4e69a06fa5edc90921e5e7e39a7084881f8b3ed9c805c574f4f39c6fde27c603`.

Both complete mono 16 kHz utterances were used without cropping:
`1272-128104-0000` (5.855 seconds) and `1272-128104-0001` (4.815 seconds).
The same production media components and Riva provider adapters used by the app
handled the recordings. References were not sent as recognition hints.

The run consumed **6 attempts and 32.010 seconds of submitted audio**, with
concurrency one and zero retries. The enforced ceilings were 9 attempts and
135 submitted audio seconds. Exit code **3** reports the failed request below;
it is not a script crash and was not automatically rerun.

## Observations

| Endpoint | First clip | Second clip | Timing returned |
| --- | --- | --- | --- |
| Parakeet TDT v3 | DeadlineExceeded after 30.418 s | Success, 9.196 s, zero scored word/character errors | 10 genuinely word-timed words on the successful clip |
| Canary | Success, 1.5254 s, zero scored errors | Success, 0.3396 s, zero scored errors | No word timing |
| Hosted Whisper large-v3 | Success, 1.9848 s, one scored word difference | Success, 0.4355 s, one scored word difference | No word timing |

Whisper's two differences were **`Mr.` versus `MISTER`**. The scorer removes
punctuation/case differences but does not expand abbreviations, so it counts these
as substitutions. They are formatting differences, not evidence of a misheard
name. The two-clip score is 2 errors across 28 reference words (7.14%); do not
interpret this as a meaningful acoustic-quality gap.

Only the second clip succeeded for every model. The matched comparison therefore
contains **one utterance**: scored WER is 0%, 0%, and 9.09%, respectively. Comparing
Parakeet's one successful clip against both clips from the other models would
confound coverage and accuracy. Failed calls have no invented WER.

All returned runtime model identities remained unobserved/null. The catalog
records the advertised route and function ID, not a falsely verified server model.
Wall latency includes client pacing/queue time as documented in `results.json`.
These are single observations, not repeated latency trials.

## Interpretation and reproduction

This small, clean, single-speaker English sample tests a real hosted transcription
path. It does not measure overlapping speech, participant identification, diarization
DER, D&D character names, background music, accents, long-session drift, or quota
reliability. It establishes **no best model**. Parakeet's initial selection is a
capability choice for word timing, not a claim that it won this comparison.

Use `scripts\Compare-Models.ps1` to reproduce with separately authorized quota.
Preparation can run without credentials using `-PrepareOnly`. Cloud mode requires
explicit public-audio approval and an authorized production configuration path
read inside the process. Never put a key in an argument, config example, or log.
Do not overwrite this historical snapshot or rerun merely to erase its timeout.

The canonical `results.json` SHA256 is
`5582c9b1e42b9c58717d205f57b89ad0ff650b0aa1f9959d254e7427ea1bc7bf`.
