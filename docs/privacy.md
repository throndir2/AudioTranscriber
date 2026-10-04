# Recording, uploads, and credentials

AudioTranscriber is a Windows desktop application. A selected render endpoint
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

No paid or LLM transcription service is used. Six OpenRouter audio models were
evaluated on public clips only (see `docs\providers.md`) and did not improve
accuracy enough to justify uploading audio.

## Secrets

Use memory-only keys unless you explicitly choose Windows-protected persistence.
Persisted keys use DPAPI for the current Windows user, not plaintext application
configuration. A password field, a protected credential file, and the in-process
provider client are the credential boundary; keys do not belong in logs,
transcripts, examples, model catalogs, command-line arguments, or benchmark output.

The authorized existing-production credential reader takes a **configuration
path**, not a key. It reads the configuration inside the benchmark process,
requires one nonempty NVIDIA provider entry, and does not reuse chat endpoints or
output the parsed configuration. Never print that production file to troubleshoot
the comparison.

NVIDIA credentials are attached only to the explicitly approved NVIDIA transport
authority. Microsoft Graph uses its own Microsoft sign-in token and fixed Graph
authority. Endpoint overrides must not redirect credentials to arbitrary servers.

## Output templates (LLM connections)

Output templates never run until you choose **Update now** or tick **Keep updating**
for a template. A run sends the transcript text (not audio), the always-included
reference files, and any reference text the model asks to read to that template's
LLM connection. Hosted services (OpenRouter, NVIDIA Build, OpenAI) apply their own
terms, logging and quotas; Ollama or LM Studio on your own machines keep the text
local. Template API keys are saved only when you choose **Save key**, encrypted with
DPAPI for the current Windows user in `templates.json`, and sent only to the
connection's own base URL. The model's file tools are read-only and limited to the
chosen context folder and always-included files.

## Participant metadata

`Speaker 1` is an acoustic cluster, not an authenticated person. A renamed speaker
is a user-assigned label. A WebVTT voice label is an attribution from the imported
source; identical names do not prove identical participants. Character annotations
must remain distinct from participant identifiers.

Teams transcript access depends on a work/school account, configured application,
consent, meeting access, and tenant policy. Respect denied transcript access and
speaker-attribution restrictions. Unattributed access is not permission to
reconstruct identities that the tenant prohibited.

Discord live receive is not supported in this first version. A future connector
requires a visible authorized bot and verified DAVE-compatible receive/rekey/
reconnect behavior. User-token/selfbot access is not an acceptable substitute.

## Public comparison

Only the pinned public LibriSpeech subset is authorized for the initial hosted
comparison. Its bounded request budget does not authorize uploading private
recordings, adding more endpoints, or running an unbounded evaluation. Its clean
single-speaker read speech cannot establish diarization accuracy or a best D&D
transcription model.
