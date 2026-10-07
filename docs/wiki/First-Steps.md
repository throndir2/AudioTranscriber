# First Steps

The fastest path is: extract the release ZIP, run `AudioTranscriber.App.exe`, and click
**Start recording**.

## What the first session uses

Every field has a working default:

- Output device: the default Windows output endpoint.
- Microphone: your default microphone as a separate track when one exists.
- Echo reduction: on by default, so speaker audio picked up by the mic is removed before mic transcription instead of being transcribed twice.
- Transcription: local **Parakeet TDT v3** on the CPU, with English selected.
- Cloud upload: off. No NVIDIA key, upload consent, or cloud service is needed.

Parakeet also detects 24 other European languages. Use Local Whisper for other
languages.

## Model downloads

On first start, the app downloads and verifies:

- Parakeet TDT v3, a 487,170,055-byte archive that unpacks to about 640 MB.
- Small speaker-labeling models, about 33.49 MB.

You can record while these download. Audio waits in durable jobs and is processed when
the models are ready. Local Whisper's recommended large-v3-turbo model downloads only
when a Whisper session needs it.

## While recording

The title bar shows a red **RECORDING** state and the recording session name even if you
click another session. Stop recording seals original audio; it does not cancel
transcription. Use **Jobs** to pause, resume, or cancel processing separately.

Open **Transcript** to search, correct text, set speakers, double-click a row to play
it, and export TXT, JSON, SRT, or WebVTT.

## Optional choices

If the app finds a supported NVIDIA GPU, it asks once whether to download the CUDA
runtime and full-precision Parakeet model, about 4.4 GB, to run Parakeet locally on the
GPU. Declining keeps CPU Parakeet.

If you choose an NVIDIA cloud provider, you must enter a key and grant upload consent
for that session. Review [Privacy and Uploads](Privacy-and-Uploads) first.
