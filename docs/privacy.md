# Recording, uploads, and credentials

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users
**Last verified:** 2026-11-14 against b3ed981
**Code:** `src/AudioTranscriber.App`, `src/AudioTranscriber.Application`, `src/AudioTranscriber.Audio`, `src/AudioTranscriber.Providers`, `src/AudioTranscriber.Integrations`, `src/AudioTranscriber.Storage`, `src/AudioTranscriber.Discord`

AudioTranscriber is a Windows and Linux desktop application. A selected render endpoint
captures audio played to that endpoint, not every other output device. A local
microphone is a separate, optional track. Start capture deliberately, keep its
visible recording state in view, and obtain the permission required for everyone
whose audio you record. Desktop capture must never start merely because the app
was opened or a model was selected.

## Local by default

Recording, retaining originals, importing files, playback, editing, and local
speaker analysis do not require NVIDIA credentials. A missing local model must
not cause a recording to be sent to a cloud service. On first start the desktop
app downloads the default local models from their pinned public sources
(Parakeet TDT v3, 465 MiB, CC BY 4.0; speaker models, about 33.49 MB, MIT /
CC BY 4.0), verifies their SHA256, and stores them locally. Whisper large-v3-turbo
(about 1.51 GiB, MIT) is downloaded the same way the first time a Local Whisper
session needs it. No audio is sent while doing so.

Release builds check `api.github.com` for a newer AudioTranscriber release
(shortly after startup and every six hours) and download it from GitHub. That
request carries no audio, transcripts, credentials, or identifiers beyond what any
HTTPS request reveals. Turn off automatic updates in Privacy / models → Updates.

Each session requires explicit permission before its audio is sent to NVIDIA.
This includes each enabled track, so separately transcribing microphone and
output audio submits both tracks. Permission is checked for each outgoing job.
Revocation stops future submissions; it cannot recall audio already received by
the provider. Stopping recording and stopping transcription are different actions.

Only send material permitted by the NVIDIA endpoint's current terms. NVIDIA's
published material includes warnings against uploading confidential or personal
information and describes security logging. Do not assume a trial means private,
unlogged, unlimited, or suitable for recordings containing personal information.
Review the current model card and account terms before granting session consent.

There is no automatic paid-provider fallback. Catalog request rates are not a
guarantee of trial balance or endpoint availability. Authentication or quota
errors need attention rather than silent rerouting.

Local Parakeet sessions, the default, never upload audio. For a **local Whisper**
session, chunks Whisper is unsure about (lowest segment confidence below 0.85) are
re-checked by local Parakeet when its model is installed, which also uploads
nothing. Only if local Parakeet isn't available does NVIDIA upload consent enable
a hosted re-check. Then those chunks are sent to hosted Parakeet TDT v3, and its
text replaces Whisper's for that chunk. Confident chunks never leave the PC. It
needs an NVIDIA key and an English session; without consent, nothing is uploaded.
A hosted error keeps the local text; an authentication, permission, or quota error
stops the hosted fallback until the key is set again.

If this PC has a suitable NVIDIA GPU, the app asks once whether to download NVIDIA's
CUDA runtime, cuBLAS, cuDNN and a full-precision Parakeet model (about 4.4 GB) to
run Parakeet on the GPU. These come from NVIDIA's, GitHub's, and Hugging Face's public
download servers, are pinned by SHA256, and require accepting NVIDIA's CUDA and cuDNN
licence terms. GPU Parakeet runs in a local worker process. No audio is uploaded, and
no key or consent is involved.

No paid LLM is used for speech recognition. Six OpenRouter audio models were
evaluated on public clips only (see `providers.md`) and did not improve accuracy
enough to justify uploading audio. Output templates use separate LLM connections
when you choose to run them.

## Secrets

Use memory-only keys unless you explicitly choose protected persistence.
Windows encrypts saved keys with DPAPI for the current user. Linux uses AES-GCM
with a random key in `~/.config/AudioTranscriber/secret.key`, readable only by
that account; `XDG_CONFIG_HOME` can change the config directory. The application
does not store these keys as plaintext. A password field, a protected credential
file, and the in-process provider client are the credential boundary. Keys do not belong in logs,
transcripts, examples, model catalogs, command-line arguments, or benchmark output.

The authorized existing-production credential reader takes a **configuration
path**, not a key. It reads the configuration inside the benchmark process,
requires one nonempty NVIDIA provider entry, and does not reuse chat endpoints or
output the parsed configuration. Never print that production file to troubleshoot
the comparison.

NVIDIA credentials are attached only to the explicitly approved NVIDIA transport
authority. Endpoint overrides must not redirect credentials to arbitrary servers.

## Diagnostic logs

The diagnostic log (`logs\` in the data root) stays on this PC and is never
uploaded. It holds app events, status/error messages, and exception details; these
can include file paths, session and template names, device names, and provider
error text. It never records audio, transcript lines, LLM output, or keys. The
**Save diagnostics ZIP** export adds a system summary and replaces your Windows
profile path and user name; review it before attaching it to a public issue.

## Output templates (LLM connections)

Output templates never run until you choose **Update now** or tick **Keep updating**
for a template. A run sends the transcript text (not audio), the always-included
reference files, and any reference text the model asks to read to that template's
LLM connection. Hosted services (OpenRouter, NVIDIA Build, OpenAI) apply their own
terms, logging and quotas. A self-hosted Ollama or LM Studio endpoint receives the
text on the computer where that server runs. Template API keys are saved only when
you choose **Save key**, encrypted for the
current OS user in `templates.json`, and sent only to the connection's own base
URL. The model's file tools are read-only and limited to the
chosen context folder and always-included files. Unless you untick **Save a versioned
copy of each update**, every changed template output is also kept on disk as a
timestamped copy (next to the template's output file, or in `template-versions` in the
library folder); delete them there or limit them with **Keep only the latest N
versions**.

Templates with **Include a screenshot of the table** send the selected window or
screen with every run, so anything visible there goes to that template's LLM
connection. The capture code encodes the image as PNG bytes. On Linux, the
selected screenshot tool first writes a temporary PNG file. The app reads that
file and tries to delete it; a deletion failure is ignored. Windows capture uses
memory instead of this temporary file. The image bytes remain in memory while
the app sends them. The current LLM client labels these PNG bytes as JPEG in its
request, which is a format mismatch. A self-hosted Ollama or LM Studio endpoint
receives the image on the computer where that server runs.

## Participant metadata

`Speaker 1` is an acoustic cluster, not an authenticated person. A renamed speaker
is a user-assigned label. A WebVTT voice label is an attribution from the imported
source; identical names do not prove identical participants. Character annotations
must remain distinct from participant identifiers.

Discord server voice channels are recorded through the user's own visible bot
(never a user token or selfbot). Everyone in the channel sees the bot join; get
their consent. The bot token is protected for the current OS user in
`discord.json` and sent only to Discord. Windows uses DPAPI; Linux uses the
protected per-user key described above. Voice stays end-to-end encrypted (DAVE)
until this PC decrypts it; the audio is archived and transcribed like any other
track, following the session's provider and consent. Discord display names become
speaker names, and with Remember voices on their voice prints (embeddings, never
audio) are saved in the local voice library. Optional network help adds a Windows
Firewall rule (UAC) and a router UPnP mapping for the voice UDP port only.

## Public comparison

Only the pinned public LibriSpeech subset is authorized for the initial hosted
comparison. Its bounded request budget does not authorize uploading private
recordings, adding more endpoints, or running an unbounded evaluation. Its clean
single-speaker read speech cannot establish diarization accuracy or a best D&D
transcription model.
