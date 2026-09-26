# Windows desktop usage

AudioTranscriber is a native .NET 10 x64 WPF application. It does not start audio
capture, download models, or request a new cloud consent when its window opens.
Recording and audio import do not require a NVIDIA key. Existing consented jobs
may be resumed by the application controller; revoke consent or pause jobs when
you do not want further processing.

## Start and isolated smoke check

From the worktree, use the pinned SDK and local caches:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools\nuget'
& .\.tools\dotnet\dotnet.exe run --project src\AudioTranscriber.App -- --data-root "$PWD\artifacts\desktop-library"
```

Without `--data-root`, the normal app uses
`%LOCALAPPDATA%\AudioTranscriber`. Never put credentials on the command line.

```powershell
& .\.tools\dotnet\dotnet.exe run --project src\AudioTranscriber.App -- --smoke --data-root "$PWD\artifacts\desktop-smoke"
Get-Content .\artifacts\desktop-smoke\app-smoke.json
```

Smoke mode requires an explicitly supplied isolated library with **no sessions**.
It shows the real WPF window, initializes SQLite, enumerates actual output and
microphone endpoints, then writes `app-smoke.json` **inside that data root** and
closes after dispatcher startup. Reusing an empty smoke library is supported;
using a populated library is rejected before constructing the controller, so
existing queued audio cannot upload. It never starts capture, playback, Graph
sign-in, NVIDIA requests, or model downloads. Zero device counts are valid on a
headless/no-audio machine, not evidence of capture coverage. This smoke checks
window/database/device enumeration, **not** actual WASAPI recording, seek quality,
provider accuracy, or live Graph access.

## Record or import

1. Open **Record / import**. Enter a name, select the provider, and enter its
   supported source language. Nothing silently translates the transcript.
   Examples include Parakeet `en-GB`, Canary `en-US`, and Whisper `en`; consult the
   provider's supported locales for other languages.
2. Leave cloud consent off for private/local work. Missing local models do not
   enable a NVIDIA provider or change cloud consent.
3. Select the exact Windows output endpoint. Optionally enable a **separate
   microphone track**; loopback does not implicitly contain your microphone.
4. Click **Start recording**. The global header displays recording state and the
   recording's session name, even if a different session is selected. Its red
   **Stop recording** control remains available across tabs. Meter activity is
   drawn from the controller during recording only; idle meters do not open a
   device or monitor private audio.
5. Stop waits for original tails to seal. It does **not** cancel transcription.
   Device removal does not silently select a replacement. Inspect the session
   error and Jobs tab if a device, disk, permission, or processing failure occurs.

For an existing file, click **Import audio / video** with the same new-session
settings. FFprobe enumerates audio streams. One stream is selected automatically;
multiple streams require an explicit choice. The backend creates the session and
retains its managed original. The status strip shows controller progress and an
indeterminate activity bar where no total is available; it does not invent a
percentage. Cancel operation is separate from recording Stop.

## Transcript, corrections, speakers, and playback

Select a session, then open **Transcript**. The virtualized DataGrid loads at most
200 rows per keyset page. Search uses effective text (including saved corrections)
and can be combined with a speaker filter and timestamp jump. Timestamps accept
seconds, `mm:ss`, or `hh:mm:ss`; hours can exceed 23. Previous/Next retain keyset
cursors rather than materializing the full transcript. Refresh library also
refreshes the current transcript page; save draft corrections before refreshing
or changing selection/page.

Select a row to inspect provenance, timing granularity, uncertainty flags, and
read-only raw recognition. Edit the correction box and click **Save correction**.
**Restore raw text** clears the correction without overwriting raw recognition.
Speaker names are stable session-wide display names, not verified participant
identities. **Speaker names** supports rename and an optional explicit
row assignment; it does not replace automatic diarization.

Double-click a row or choose **Play row** to play its own track from its stored
start timestamp. **Track playback** separately chooses an original track and
timestamp. Playback is one track at a time, not an undisclosed mix of loopback and
microphone. Stop playback is independent of recording. Coarse cues do not imply
word-accurate seeking; a VTT-only track without linked audio cannot be played.

**Export** writes the entire selected transcript, independent of current
search/page, to TXT, JSON, SRT, or WebVTT. JSON retains raw text/corrections and
timing/source fields. Text/subtitle exports include a provenance sidecar. Export
does not manufacture finer timing than the stored rows.

### Live transcript file (read it in VS Code while recording)

On **Record / import**, check **Write the new session's transcript to a live
text file** and choose a path (default `Documents\AudioTranscriber\live-transcript.txt`;
the path and checkbox are remembered). When you start a recording or import,
the app rewrites that file every ~3 seconds whenever the transcript changes, in
the same `[hh:mm:ss.fff - hh:mm:ss.fff] Speaker: text` format as the TXT export.
Corrections and speaker renames are reflected on the next refresh.

The file is opened without an exclusive lock (`FileShare.ReadWrite | Delete`) and
closed after each update, so VS Code, Copilot, or any other reader can keep it open
alongside other documents while it grows. VS Code reloads it automatically as long
as you have not edited it. Treat it as read-only: edits made elsewhere are
overwritten. To mirror an existing session instead, select it and click
**Live file** on the Transcript tab; **Stop live file** stops updating and leaves
the file in place.

## Processing and privacy

**Jobs** displays all-job aggregate counts and the newest 100 job details/errors.
Pause, resume, and cancel affect transcription for the selected session—not
recording. Original audio is retained. Resume is an explicit action after
repairing a key, consent, quota, or model issue; no alternative provider is chosen
automatically.

Before NVIDIA upload, review the disclosure and explicitly opt in for the new or
selected session. Audio from the session's configured tracks is sent to the chosen
NVIDIA service. NVIDIA terms and logging apply; free access has finite,
account/model-dependent quotas and is not guaranteed. Upload only permitted
audio, with no personal or confidential data. Consent is never inherited from the
new-session checkbox by the next session. **Revoke cloud consent** stops future
uploads; already submitted audio cannot be recalled.

Enter a key only in the **Privacy / models** password box. **Use entered key**
clears the field and uses memory-only storage by default. Explicitly checking
Remember uses the controller's Windows current-user DPAPI store. **Clear key**
removes the active and remembered key. The UI never binds the secret into a view
model string property, logs it, exports it, or accepts it as an argument.

## Local models

**Install small diarization models** explicitly confirms approximately 33.49 MB
(33,488,994 bytes) of official release artifacts: MIT CNRS 2023 segmentation and
CC BY 4.0 embedding, with attribution/package notices. Those artifacts do not need
a Hugging Face token; the original HF segmentation distribution is gated.
Progress comes from the actual installer. Once ready, **Analyze selected session
speakers** runs automatic local diarization.

Short turns, overlap, crowded windows, and character voices can remain uncertain.
The model's local-window limits do not impose a three-person maximum on an entire
session, and labels are not verified identities.

Local Whisper uses an explicitly selected, existing compatible model file.
Nothing downloads a large Whisper model automatically. Verify exact file size
and the model/runtime licenses before obtaining one: tiny is roughly 75 MiB,
whereas large models can occupy several GiB. Upstream OpenAI Whisper is MIT.
The selected file name, path, and size—not any secret—are shown.

In the checkout, `.\scripts\Install-WhisperModel.ps1 -List` lists pinned model
sizes, licenses, and SHA256 values without downloading anything. Its installer
requires explicit license acceptance and acceptance of the exact download byte
count; see [provider setup](providers.md). After an explicitly requested install,
select the resulting `.bin` file in the desktop picker. Opening the UI or choosing
the local provider does not run this installer.

## WebVTT and configured Teams retrieval

Select an existing session and use **VTT / Teams** to import local timed WebVTT
cues and source speaker labels. Source labels remain attributed metadata, not
inferred participant identities. Imported cue times retain the source transcript
clock; they are **not automatically aligned** to the selected session's existing
audio. Do not interpret a cue's timestamp as verified synchronization with an
unrelated audio track.

The Teams dialog calls real delegated Microsoft Graph transcript retrieval. It
requires a work/school tenant GUID, a public-client application GUID configured
for device-code flow, `OnlineMeetingTranscript.Read.All`, and explicit user,
online meeting, and transcript identifiers. A join link is not the meeting ID.
The actual device verification URL, code, and expiration appear in a dedicated
window; tokens are neither displayed nor mixed with NVIDIA credentials.
An explicit unattributed-content option is available only when tenant policy
permits it. No identities are reconstructed to bypass attribution restrictions.

Graph integration is **configuration-required**, not a claim of live tenant
validation. This is retrieval of an existing transcript, not live Teams audio.
Live Teams audio needs a separate Azure media-bot deployment and policies.
Discord live receive is deferred pending an authorized visible guild bot and
validated DAVE-compatible receive implementation; no fake connector is shown.

## Closing safely

Closing while recording or doing foreground work asks for confirmation. The
window waits for active operations and recording stop/seal, then awaits controller
disposal. It does not hide an exit-time cancellation behind capture Stop to skip
audio tails. If safe shutdown fails, the window stays open with a warning.
