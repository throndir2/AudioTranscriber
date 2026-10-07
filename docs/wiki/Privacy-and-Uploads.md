# Privacy and Uploads

AudioTranscriber is local by default. Recording, retaining originals, importing,
playback, editing, local Parakeet, local Whisper, and local speaker analysis do not
require NVIDIA credentials.

## Recording consent

Start capture deliberately and keep the visible recording state in view. Obtain the
permission required for everyone whose audio you record. The app does not start desktop
capture merely because it was opened or because a model was selected.

## NVIDIA upload consent

Each session requires explicit permission before audio is sent to NVIDIA. This includes
every enabled track, so separate microphone and output transcription can send both
tracks. Revoking consent stops future submissions; it cannot recall audio already
received.

Only send material permitted by NVIDIA's current endpoint terms. The docs note NVIDIA
material warning against uploading confidential or personal information and describe
logging. Trial access is not a privacy guarantee or unlimited quota.

There is no automatic paid-provider fallback. Missing local models do not cause cloud
upload.

## Credentials

Use memory-only keys unless you explicitly choose Windows-protected persistence. Saved
keys use DPAPI CurrentUser. Keys do not belong in logs, transcripts, examples, model
catalogs, command-line arguments, benchmark output, or environment variables.

The authorized benchmark credential reader takes a configuration path, never a key
argument, and does not print parsed configuration.

## Output templates

Templates send transcript text, always-included files, and any reference text the model
reads to the selected LLM connection. Hosted services such as OpenRouter, NVIDIA Build,
and OpenAI apply their own terms, logging, and quotas. Ollama and LM Studio keep text on
your machines.

Template file tools are read-only and limited to the chosen context folder and
always-included files.

## Discord recordings

Discord voice channels are recorded through your own visible bot, never a user token or
selfbot. Everyone in the channel sees the bot join; get their consent. The bot token is
encrypted for your Windows account (DPAPI) in `discord.json` and sent only to Discord.
Discord display names become speaker names, and with **Remember voices** on their voice
prints (embeddings, never audio) are saved in the local voice library. Optional network
help adds a Windows Firewall rule (UAC) and a router UPnP mapping for the voice UDP port
only. See [Discord](Discord).

## Diagnostic logs

The app keeps a local log in the data root's `logs` folder. It is never uploaded. It
records app events, status and error messages, and exception details (which can
include file paths, session or device names, and provider error text), but never
audio, transcript lines, LLM output, or keys. **Save diagnostics ZIP…** replaces your
Windows profile path and user name; review the ZIP before attaching it to a public
issue.

## Speaker data

Speaker profiles and voice-library entries are local biometric-like voice data:
embeddings, not audio. Store them in the private user data directory. Exports never
include embeddings. Delete voice-library entries from **Privacy / models → Voice
library**.

More detail is in
[docs/privacy.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/privacy.md).
