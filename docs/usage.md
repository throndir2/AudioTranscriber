# Windows desktop usage

AudioTranscriber is a native .NET 10 x64 WPF application. It works out of the
box: open it and click **Start recording**. Defaults are the default Windows
output device, your default microphone as a separate track (when one exists),
local Whisper, and English. It never starts audio capture or requests cloud
consent when its window opens. On first start it downloads and verifies the
default local models (Whisper large-v3-turbo and the small speaker models) in the
background; you can record meanwhile and queued audio is transcribed once they
are ready. Recording and audio import do not require a NVIDIA key. Existing
consented jobs may be resumed by the application controller; revoke consent or
pause jobs when you do not want further processing.

## Layout

The window uses a dark, candlelit theme and a single custom title bar instead of
the Windows caption plus a banner. The title bar shows the selected session and
its status, the recording indicator (a pulsing red **RECORDING** pill with the
recording's session name), a **Record** quick-start button (or **Stop recording**
while capturing), **Restart to update** when an update is staged, and the window
buttons. Drag the title bar to move the window; double-click it to maximize. The
session list is on the left (drag its edge to resize), and the tabs fill the rest.
**Record / import** puts the start/stop/import actions and settings on the left
and the **Live activity** log, sized to the window, on the right.

## Prerequisites

Release ZIPs bundle FFmpeg/FFprobe (an unmodified LGPL shared build) in the
`ffmpeg` folder next to `AudioTranscriber.App.exe`; keep that folder with the app.
FFmpeg is resolved in this order: the bundled `ffmpeg` folder, the app folder,
the process PATH, the current user/machine PATH from the registry (so a newly
installed FFmpeg is found even if Explorer's environment is stale), then WinGet,
Scoop, Chocolatey, and `C:\ffmpeg\bin`.

Local Whisper needs Microsoft's Visual C++ 2015-2022 runtime (x64, 14.40 or
newer). On startup the app checks both. If the runtime is missing or outdated it
downloads Microsoft's official installer and runs it automatically (approve the
Windows administrator prompt). **Privacy / models → Prerequisites** shows the current
state, **Install Visual C++ runtime…**, and **Re-check prerequisites**. Recording
and NVIDIA transcription do not need the Visual C++ runtime. A GPU driver with
Vulkan is optional; without it Whisper uses the CPU.

A session whose normalization paused because FFmpeg was missing keeps its original
recording. Choose **Resume transcription** after fixing the prerequisite to replay it.

## Updates

Release builds update themselves from the repository's **latest GitHub release**
(drafts and prereleases are ignored). The installed version comes from the
`BUILD-PROVENANCE.json` shipped in the release ZIP; development builds never
update. With **Automatically download new releases and install them when the app
closes** on (the default), the app checks shortly after startup and every six
hours, downloads `AudioTranscriber-<tag>-win-x64.zip` into
`%LOCALAPPDATA%\AudioTranscriber.Updates`, and verifies it against both GitHub's
asset SHA-256 digest and the published `.sha256` file before unpacking.

Nothing is replaced while the app runs. **Restart to update** (title bar, or
Privacy / models → Updates) closes the app through the normal safe shutdown, then
a helper waits for it to exit, copies the new files over the application folder,
and reopens it with the same arguments. Otherwise the update installs the next
time you close the app. Only the application folder changes; the library,
recordings, settings, and models are untouched. A folder that needs administrator
rights (for example under Program Files) triggers a Windows approval prompt.
The helper logs to `%LOCALAPPDATA%\AudioTranscriber.Updates\update.log`.
Turn the checkbox off to only check when you choose **Check for updates now**.

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

1. Open **Record / import**. Every field has a default: a timestamped session
   name, local Whisper, and `en`. Provider, language, and the microphone choice
   are remembered from your last recording. Nothing silently translates the transcript.
   Examples include Parakeet `en-GB`, Canary `en-US`, and Whisper `en`; consult the
   provider's supported locales for other languages.
2. Leave cloud consent off for private/local work. Missing local models do not
   enable a NVIDIA provider or change cloud consent.
3. The default Windows output endpoint is preselected. The **separate
   microphone track** is on by default when a microphone exists; loopback does not implicitly contain your microphone.
   Mic rows that speaker analysis hasn't attributed show as **Me (mic)**, so the
   live file separates your lines from everyone else's. **Reduce echo from
   speakers** (on by default) lets you skip the headset: the output track is
   exactly what your speakers played, so before transcribing each mic chunk the
   app runs WebRTC's acoustic echo canceller (AEC3) to subtract that audio from
   the mic. Remote voices are then not transcribed a second time as "Me", while
   your own voice is kept, including when you talk over someone. It only
   affects what is sent for recognition; the original mic audio is kept
   unchanged. With headphones it has nothing to remove and leaves the mic as-is.
   During recording, a mic chunk waits until the matching speaker audio is
   ready (up to about 90 seconds while nothing is playing).
4. Click **Start recording** (or **Record** in the title bar from any tab). The title bar displays recording state and the
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
refreshes the current transcript page. Unsaved text in the correction box and a
typed speaker name survive live refreshes of the same line.

Click a line to edit it in **Selected line** below the grid (drag the splitter to
resize). The left box is your correction; click **Save text**. The right box is
the preserved raw recognition, and **Restore raw** drops your correction. Hover the
raw box for the line's provenance and timing.

### Setting who is speaking

- **Right-click** one or more lines (Ctrl/Shift-click selects several) and choose
  **Set speaker for …** → an existing speaker, **Unknown / unassigned**, or
  **New speaker…**; or **Type a speaker name…** to enter your own.
- Or pick/type a name in the **Speaker** box of **Selected line** and press Enter
  or **Set speaker**. A name that already exists (case-insensitive) reuses that
  speaker; a new name creates one.
- **Rename "X" everywhere…** (right-click) or the **Speaker names** tab renames a
  speaker on every line. Choosing a name another speaker already has **merges** the
  two after a confirmation: every line of both gets the name.

Lines you label show **Set by you** and are never changed by later analysis.

How labels improve future lines: the speaker model matches **voices, not names**.
When you label a line, the app embeds that line's clear speech (at least about two
seconds; shorter lines are labeled but not learned) into that speaker's voice
profile, then re-checks windows that still contain Unknown speech. If the voice
already belongs to an automatic placeholder such as "Speaker 2", that placeholder is
folded into your named speaker; if it resembles a speaker you named, the status
line suggests renaming to merge them. Merging combines both voice profiles, so
lines from either voice are matched to the one speaker from then on. **Analyze
speakers** re-checks every window of the session with the current profiles.
Speaker names are session display labels, not verified identities.

Double-click a line, right-click → **Play line**, or choose **▶ Play** to play its own track from its stored
start timestamp. **Track playback** separately chooses an original track and
timestamp. Playback is one track at a time, not an undisclosed mix of loopback and
microphone. Stop playback is independent of recording. Coarse cues do not imply
word-accurate seeking; a VTT-only track without linked audio cannot be played.

**Export** writes the entire selected transcript, independent of current
search/page, to TXT, JSON, SRT, or WebVTT. JSON retains raw text/corrections and
timing/source fields. Text/subtitle exports include a provenance sidecar. Export
does not manufacture finer timing than the stored rows.

### Live transcript file (read it in VS Code while recording)

On **Record / import**, check **Write the transcript to a live text file** and
choose a path (default `Documents\AudioTranscriber\live-transcript.txt`; an existing
file is overwritten; the path and checkbox are remembered). Checking the box while
a recording is running starts mirroring that recording immediately; otherwise the
next recording or import is mirrored. Unchecking it stops updates and leaves the file.
The app rewrites that file as soon as each chunk is recognized (and at least every
~3 seconds) whenever the transcript changes, in
the same `[hh:mm:ss.fff - hh:mm:ss.fff] Speaker: text` format as the TXT export.
Corrections and speaker renames are reflected on the next refresh. The status line
under the path shows the last write time, line count, and bytes on disk.

Recordings are cut into short chunks at natural pauses (at least 1.5 seconds,
at most 6 seconds), so a line usually lands in the file a few seconds after
the speaker pauses (depending on Whisper speed); silent chunks are skipped. Text is written first with the
speaker shown as `Unknown`; speaker analysis runs separately on ~20-second
windows and fills the names in afterwards.

The file is opened without an exclusive lock (`FileShare.ReadWrite | Delete`) and
closed after each update, so VS Code, Copilot, or any other reader can keep it open
alongside other documents while it grows. VS Code reloads it automatically as long
as you have not edited it. Treat it as read-only: edits made elsewhere are
overwritten. To mirror an existing session instead, select it and click
**Live file** on the Transcript tab; **Stop live file** stops updating and leaves
the file in place.

### Live activity

The **Live activity** panel on Record / import follows the current recording (or
the mirrored / selected session) and shows, with timestamps: audio captured so far,
each chunk as it starts and finishes transcribing (including "no speech detected"),
every new transcript line, errors and waits (for example a model still downloading),
and every live file write. **Copy** puts the log on the clipboard.

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

With **local Whisper**, the same consent checkbox turns on the low-confidence
fallback instead of full upload: only chunks Whisper is unsure about are sent to
hosted Parakeet (English sessions, key required). Those transcript lines show
`nvidia-parakeet-tdt-v3` and the Whisper score in their provenance. On a PC where
Parakeet runs on the local NVIDIA GPU, those chunks are re-checked locally instead,
without upload or consent. Those lines show `local-gpu-parakeet-…`, and Privacy /
models shows what the GPU check found.

Enter a key only in the **Privacy / models** password box. **Use entered key**
clears the field and uses memory-only storage by default. Explicitly checking
Remember uses the controller's Windows current-user DPAPI store. **Clear key**
removes the active and remembered key. The UI never binds the secret into a view
model string property, logs it, exports it, or accepts it as an argument.

## Local models

The small speaker models (approximately 33.49 MB, 33,488,994 bytes of official
release artifacts: MIT CNRS 2023 segmentation and CC BY 4.0 embedding, with
attribution/package notices) download automatically on first start, so speakers
are labeled by default. They do not need a Hugging Face token. If the automatic
download fails, **Install small diarization models** retries it. Speaker analysis
that waited for the models continues automatically once they are ready;
**Analyze selected session speakers** re-checks every window of a session against
the current voice profiles (including voices learned from lines you labeled).

Short turns, overlap, crowded windows, and character voices can remain uncertain.
The model's local-window limits do not impose a three-person maximum on an entire
session, and labels are not verified identities.

Local Whisper's default model is **large-v3-turbo** (1,624,555,275 bytes,
MIT). It's close to large-v3 accuracy at several times the speed. The app
downloads it automatically on first start, verifies its SHA256, and selects it;
progress appears under **Start recording**. Local Whisper jobs queued while it
downloads wait and then run automatically. If the download fails, **Install
recommended model (large-v3-turbo)…** on **Privacy & models** retries it, and
blocked audio is transcribed once a model is installed. If
`ggml-large-v3-turbo.bin` is already in the app's `whisper` model folder, it is
selected at startup without downloading. You can also pick any existing
compatible model file.

Inference uses the GPU through **Vulkan** when a Vulkan-capable driver is
present (NVIDIA, AMD, or Intel). A dedicated GPU is preferred over an integrated
one. Otherwise it falls back to the CPU. After each chunk, the status bar
diagnostics show `whisper-runtime:Vulkan` or `whisper-runtime:Cpu`. CUDA
runtimes are not bundled because they would require the CUDA Toolkit. Whisper
transcribes every voice in the mix but does not label speakers; speaker analysis
does that separately. Overlapping speech can be merged or dropped.
The selected file name, path, and size—not any secret—are shown.

In the checkout, `.\scripts\Install-WhisperModel.ps1 -List` lists pinned model
sizes, licenses, and SHA256 values without downloading anything. Its installer
requires explicit license acceptance and acceptance of the exact download byte
count; see [provider setup](providers.md). After an explicitly requested install,
select the resulting `.bin` file in the desktop picker.

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
