# Transcription Providers

AudioTranscriber is source-language ASR only. It does not translate, use chat prompts
for ASR, or silently fall back to a cloud provider on errors.

## Local Parakeet, the default

New sessions default to `local-parakeet`: NVIDIA Parakeet TDT 0.6B v3 through
sherpa-onnx on the local CPU. It needs no key and uploads nothing.

The app downloads a 487,170,055-byte int8 ONNX archive on first start, verifies it, and
stores about 640 MB on disk. It covers 25 European languages: `bg`, `cs`, `da`, `de`,
`el`, `en`, `es`, `et`, `fi`, `fr`, `hr`, `hu`, `it`, `lt`, `lv`, `mt`, `nl`, `pl`,
`pt`, `ro`, `ru`, `sk`, `sl`, `sv`, `uk`.

Measured on an i7-13700K with 48 public English clips, CPU Parakeet made 6.85% WER, ran
14-20× real time, and used about 0.95 GB peak RAM. Treat the sample size and domain as
documented benchmark evidence, not a universal quality promise.

## Parakeet on an NVIDIA GPU

If the app finds an NVIDIA GPU that can run CUDA 12, it asks once before downloading
about 4.4 GB of CUDA runtime, cuBLAS, cuDNN, ONNX Runtime CUDA build, and the
full-precision model. Requirements are compute capability 6.0+ (GTX 10-series or newer),
4 GB+ GPU memory, and driver 527.41+.

No Docker, admin install, key, or upload consent is involved. If you decline or anything
fails, CPU Parakeet remains available. Change the choice later in **Privacy / models**.

## Local Whisper

Local Whisper is available for languages Parakeet does not support. The recommended
large-v3-turbo model is 1,624,555,275 bytes and downloads only when a Local Whisper
session needs it. Existing compatible GGML model files can also be selected.

Whisper uses Vulkan when a Vulkan-capable driver is present; otherwise it uses the CPU.
Whisper transcribes every voice in the mix but does not label speakers.

Unsure English Whisper chunks below the default confidence threshold 0.85 are re-checked
by local Parakeet when available, with no upload. If local Parakeet is unavailable,
hosted Parakeet can be used only with NVIDIA consent, a key, and an English session.

## NVIDIA cloud routes

Optional hosted routes are `nvidia-parakeet-tdt-v3`, `nvidia-canary`, and
`nvidia-whisper-large-v3`. Parakeet returns word offsets on the successful documented
route; Canary and hosted Whisper return coarse input intervals. These routes do not
advertise native diarization in this app.

NVIDIA upload requires a key and explicit per-session consent. Account quota, endpoint
availability, logging, and terms are NVIDIA's. Authentication, permission, quota, or
resource errors need attention; the app does not silently reroute.

Deep details live in
[docs/providers.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/providers.md).
