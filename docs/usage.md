# Windows desktop usage

AudioTranscriber is a native .NET 10 x64 WPF application. It works out of the
box: open it and click **Start recording**. Defaults are the default Windows
output device, your default microphone as a separate track (when one exists),
local Parakeet, and English. It never starts audio capture or requests cloud
consent when its window opens. On first start it downloads and verifies the
default local models (Parakeet TDT v3 and the small speaker models) in the
background; you can record meanwhile and queued audio is transcribed once they
are ready. Local Whisper's model downloads the first time a Whisper session needs
it. Recording and audio import do not require a NVIDIA key. Existing
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
Right-click a session (or select it and press **Delete**) to delete it. Shift-click
selects a range of sessions and Ctrl-click adds or removes one; right-click the
selection to **Merge N selected sessions…** or **Delete N selected sessions…** (Delete
works too). The trash
button above the list opens **Delete sessions**, where you can check sessions by
hand or check everything older than a week, 30/90 days, 6 months, or a year, and
see how much disk space they use. Deleting removes the session's transcript,
speakers, jobs, and retained audio from this PC and cannot be undone; exported
files and the live transcript file are left alone. Stop a recording before
deleting its session.
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
   name, local Parakeet, and `en`. Provider, language, and the microphone choice
   are remembered from your last recording (a saved Local Whisper default from an
   older version moves to Parakeet once, if Parakeet supports its language).
   Nothing silently translates the transcript. Local Parakeet accepts 25 European
   languages (`en`, `de`, `fr`, …); choose Local Whisper for others. Hosted examples
   include Parakeet `en-GB`, Canary `en-US`, and Whisper `en`; consult the provider's
   supported locales for other languages.
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

Select a session, then open **Transcript**. The virtualized DataGrid shows the whole
transcript, with no pages. While a session is recording, new lines appear at the
bottom and the grid keeps scrolling to the newest line as long as you are scrolled to
the end; scroll up to read and it stays put. Search uses effective text (including
saved corrections) and can be combined with a speaker filter; the grid then shows
every matching line. **Find / jump** with a timestamp scrolls that time to the top.
Timestamps accept seconds, `mm:ss`, or `hh:mm:ss`; hours can exceed 23. Refresh
library also refreshes the transcript. Unsaved text in the correction box and a
typed speaker name survive live refreshes of the same line.

Recognition splits long speech at sentence ends, short pauses and chunk edges. When
consecutive rows on the same track belong to the same known speaker and are at most
~2 seconds apart, the grid shows them merged as one wrapped line (up to ~600
characters), so a long passage reads as one line instead of many fragments. The
TXT export and the live file use the same merged lines; JSON, SRT and WebVTT keep
the individual rows. Merging is display-only: the stored rows keep their own timing,
so it follows speaker changes automatically. Unknown/uncertain rows are never
merged, and merging is off while a search or speaker filter is applied. Setting a
speaker on a merged line labels all of its rows; editing its text changes only the
rows your edit touches.

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

### Remembering speakers across sessions (voice library)

Name someone once and later recordings name them for you. When you give a speaker
a name (rename, or label their lines), up to three voice samples from that session
are saved to the **voice library**. The samples are 256-number voice embeddings,
not audio. Each new session's automatic speakers ("Speaker 1", "Speaker 2", …) are
compared with the library as soon as speaker analysis finds them. A close, clear
match renames the speaker. If the session already has a speaker with that name,
the two are merged. Nothing re-reads hours of audio: matching uses the voice
profiles that speaker analysis already stores, so it is instant.

- Only names **you** give add samples; automatic matches never feed the library,
  so one wrong match can't reinforce itself. A voice keeps the newest 15 samples
  (at most 3 per session speaker). Naming someone in two or three sessions,
  ideally with different mics or rooms, makes matching much more reliable.
- Matching uses the same conservative thresholds as within a session. A speaker
  who sounds like two remembered people, or like nobody, stays unnamed.
- Renaming a remembered speaker to a different name moves that session's samples
  to the new name. Renaming a speaker back to "Speaker N" keeps it unnamed for good.
- **Speaker names → Match known voices** applies the library to an older session.
  **Remember this session's named voices** adds one session's named speakers.
- Speakers you named before the voice library existed are learned automatically
  the first time the app starts with remembering on (oldest session first, so
  each voice keeps its newest samples). **Privacy / models → Voice library →
  Learn from past sessions** does the same again at any time. Only speakers with
  a voice profile (from speaker analysis or labeled lines) can be remembered.
- **Privacy / models → Voice library** lists remembered voices. From there you can
  **Rename…** a voice (choosing another remembered name combines the two),
  **Forget selected voice**, **Forget all voices…**, or turn remembering off.
  Forgetting deletes the samples at once. Past sessions keep their speaker names
  and their own in-session voice profiles.
- Different mics, heavy compression, a cold, or character voices can prevent a
  match. Those speakers simply stay "Speaker N" for you to name.

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
the speaker pauses (Parakeet takes well under a second per chunk; Whisper can take several); silent chunks are skipped. Text is written first with the
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

## Output templates (AI notes from the transcript)

The **Templates** tab turns the transcript into documents you define with a prompt,
for example "Give me DM guidance for the current scene based on the PDFs", a running
session summary, or a list of NPCs, places or items. Four starter templates are
included (**Add starter templates** brings them back).

- **Which session:** templates that use the transcript run on the session being
  recorded, otherwise on the session selected on the left.
- **Keep updating when its inputs change:** the template re-runs whenever its inputs
  change (new or corrected transcript lines, or a new output from a template it
  uses), at most once per **Every (seconds)**. Nothing runs while the inputs are
  unchanged. **Update now** runs it once with the current inputs; **Stop** cancels.
- **Context (inputs):** choose what the model gets: the **Transcript**, the
  **Reference files**, its **Previous output** (it updates its last answer, which
  keeps lists and summaries stable), and the **Outputs of other templates**. A
  template without the transcript runs without any session selected.
- **Chaining:** a template that uses other templates' outputs waits while any of
  them is updating, then re-runs on their fresh results. Templates that would form
  a loop can't be selected. Tip for small local models: give each template one
  narrow job (for example only the turn order, or only the open story beats) and
  combine their outputs in a final template that writes the suggestions.
- **Transcript characters:** only the most recent part of a long transcript is sent
  (about 4 characters per token); lower it for small local models.
- **Output:** always shown on the right (with **Copy**). **Also write the output to
  a file** rewrites a `.md`/`.txt` file on every update without locking it, so VS
  Code, Obsidian or a browser can keep it open.

**Reference files** are shared by all templates (untick **Reference files** in a
template's inputs). The **context folder** is browsable by the model through
read-only tools (list, search, read) covering PDF, DOCX, Markdown, text, JSON, CSV
and similar files in it and its subfolders; it cannot reach anything outside the
folder. **Always-included files** are sent in full with every update (about 60,000
characters in total), so keep them short: a campaign summary, roster or cheat sheet.
Files are read without locking them. The folder tools need a model with tool
(function) calling; other models still get the transcript and always-included files.

**Table screenshot** (shared by all templates) lets templates see your virtual
tabletop. Pick your Roll20 or Foundry browser window, or a whole screen, from the
**Capture** list (**Refresh** reloads it; a shortened title such as `Roll20` matches
any window whose title contains it), set the **max width** (default 1280 px), and use
**Test capture** to preview. Window capture works while other windows cover it, but
not while it is minimized. Tick **Include a screenshot of the table** on a template to
attach a fresh JPEG on each update; templates updating together share the same frame,
and they also re-run when the screen changes even if the transcript did not. This
needs a vision model (for example Ollama `gemma4:e4b`); keep each template to one
narrow job, such as turn order or token positions.

**LLM connections** use the OpenAI-compatible `/v1/chat/completions` API. Pick a
type and choose **Add**: OpenRouter (`https://openrouter.ai/api/v1`), NVIDIA Build
(`https://integrate.api.nvidia.com/v1`), OpenAI, Ollama (`http://localhost:11434/v1`,
or `http://other-pc:11434/v1` for Ollama on another machine started with
`OLLAMA_HOST=0.0.0.0`), LM Studio, or any custom server. The Ollama preset defaults
to `gemma4:e4b` (Gemma 4 E4B: reads text and images, supports tool calling, runs
locally; install it with `ollama pull gemma4:e4b`). **Load models** lists what
the server offers; **Test** sends a one-line check and also asks the server whether
the model accepts images (Ollama `/api/show` capabilities, LM Studio model type, or
the OpenRouter-style `/models` input modalities). **Check image support** runs just
that check; the result shows under the connection as supported, not supported, or
unknown, and resets when you change the model. API keys are encrypted with
Windows DPAPI for your account in the library's `templates.json` and sent only to
that connection's URL. Running a template sends the transcript text, and any
reference text the model reads, to that endpoint; Ollama and LM Studio keep it on
your machines.

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

With **local Whisper**, English chunks Whisper is unsure about are re-checked by
local Parakeet when its model is installed, with no upload or consent. Those
transcript lines show `local-parakeet` and the Whisper score in their provenance.
If local Parakeet isn't installed, the consent checkbox turns on a hosted re-check
instead of full upload: only those unsure chunks go to hosted Parakeet (key
required) and show `nvidia-parakeet-tdt-v3`.

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

Local Parakeet's model is NVIDIA **Parakeet TDT 0.6B v3** (CC BY 4.0), as
sherpa-onnx's int8 ONNX export: a 487,170,055-byte archive that unpacks to about
640 MB. The app downloads it on first start, verifies the archive and each file's
SHA256, and uses it for every Parakeet session. It runs on the CPU with 2–4 threads,
about 14–20× real time on a desktop CPU, using ~1 GB of RAM. If the download fails,
**Install / repair Parakeet model…** on **Privacy & models** retries it, and blocked
audio is transcribed afterward.

**Parakeet on the NVIDIA GPU:** when the app finds an NVIDIA GPU that can run CUDA
12 (GTX 10-series or newer, 4 GB+, driver 527.41+), it asks once whether to download
the GPU runtime. That is about 4.4 GB: NVIDIA's CUDA runtime, cuBLAS and cuDNN, ONNX
Runtime's CUDA build, and the full-precision model, and it requires accepting
NVIDIA's licence terms. If you say yes, Parakeet runs on the GPU in a separate worker
process, several times faster on long imports and with almost no CPU load. If you
say no, or anything fails, it stays on the CPU. **Use the GPU for Parakeet…** and
**Use the CPU only** on the same page change the choice later (see
[provider setup](providers.md)).

Local Whisper's default model is **large-v3-turbo** (1,624,555,275 bytes,
MIT). It's close to large-v3 accuracy at several times the speed. The app
downloads it the first time you record or import with Local Whisper, verifies its
SHA256, and selects it; progress appears under **Start recording**. Local Whisper
jobs queued while it downloads wait and then run automatically. If the download
fails, **Install recommended model (large-v3-turbo)…** on **Privacy & models**
retries it, and blocked audio is transcribed once a model is installed. If
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
