# AudioTranscriber Wiki

<img src="https://raw.githubusercontent.com/throndir2/AudioTranscriber/main/src/AudioTranscriber.App/Assets/AppIcon.png" alt="AudioTranscriber icon" width="96">

AudioTranscriber is a native Windows and Linux x64 desktop app for recording, importing,
transcribing, searching, and exporting durable transcripts. It records the selected
output device (WASAPI on Windows, PulseAudio/PipeWire on Linux), can keep a separately timestamped microphone track, and keeps
original audio on disk so slow inference or a network problem does not erase the
recording.

It works locally by default with Parakeet TDT v3, speaker analysis, searchable
transcripts, corrections, playback, exports, WebVTT import, optional Teams transcript
retrieval, and optional output templates powered by OpenAI-compatible LLMs. NVIDIA cloud
routes are opt-in per session.

Latest release: download `AudioTranscriber-<tag>-win-x64.zip` from [the latest
release](https://github.com/throndir2/AudioTranscriber/releases/latest). Release ZIPs
are unsigned Windows x64 builds and update themselves from GitHub.

<img src="https://raw.githubusercontent.com/throndir2/AudioTranscriber/main/docs/images/transcript.png" alt="A transcribed game session with every line labeled by speaker">

## Start here

- New user: [Installing](Installing-AudioTranscriber) → [First Steps](First-Steps) → [Recording and Importing](Recording-and-Importing)
- Working with a transcript: [Transcripts and Search](Transcripts-and-Search) → [Speakers and Voice Library](Speakers-and-Voice-Library) → [Managing Sessions](Managing-Sessions)
- Cloud or model choices: [Transcription Providers](Transcription-Providers) and [Privacy and Uploads](Privacy-and-Uploads)

## Using AudioTranscriber

| Page | What it helps with |
| --- | --- |
| [Installing](Installing-AudioTranscriber) | Downloading the unsigned Windows x64 ZIP and keeping its folders together. |
| [First Steps](First-Steps) | The default first recording: output, microphone, local Parakeet, models, and consent. |
| [Recording and Importing](Recording-and-Importing) | Recording output/mic audio, importing media, phrase pauses, and live activity. |
| [Transcripts and Search](Transcripts-and-Search) | Searching, correcting, playing, exporting, and mirroring transcripts to a live file. |
| [Speakers and Voice Library](Speakers-and-Voice-Library) | Naming speakers, filling speakers, remembering voices, and merging names. |
| [Managing Sessions](Managing-Sessions) | Continuing recordings, merging, deleting, re-transcribing, and job controls. |
| [Output Templates](Output-Templates) | LLM-generated notes, summaries, file context, and output files. |
| [Transcription Providers](Transcription-Providers) | Local Parakeet/Whisper, NVIDIA cloud routes, GPU Parakeet, and CUDA downloads. |
| [Teams and WebVTT](Teams-and-WebVTT) | Importing WebVTT and retrieving configured Teams transcripts. |
| [Discord](Discord) | Recording Discord server voice channels with your own bot, speakers named per user. |
| [Privacy and Uploads](Privacy-and-Uploads) | Upload consent, credentials, local models, output templates, and participant metadata. |
| [Data and Recovery](Data-and-Recovery) | Data root contents, backups, originals, recovery, and isolated libraries. |
| [Updates and Closing](Updates-and-Closing) | Self-updates, restart-to-update, and safe shutdown. |
| [Limits and Known Gaps](Limits-and-Known-Gaps) | Documented limits around timing, diarization, Teams, and validation. |
| [Troubleshooting / FAQ](Troubleshooting-FAQ) | Common fixes from the shipped docs. |

## For developers

Start with [Developer Guide](Developer-Guide), then [Architecture and
Conventions](Architecture-and-Conventions), [MCP Hooks](MCP-Hooks), [Validation and
Testing](Validation-and-Testing), [Benchmarks](Benchmarks), and [Releasing](Releasing).
