# Recording and Importing

Open **Record / import** to start a new recording or import an existing audio/video
file.

## Recording

New sessions default to a timestamped name, local Parakeet, English, the default output
endpoint, and the default microphone when one exists. Provider, language, and microphone
choice are remembered from your last recording.

The selected output endpoint is captured by loopback. The microphone is a separate
optional track; loopback does not implicitly contain your mic. Mic rows that speaker
analysis has not attributed show as **Me (mic)**.

**Reduce echo from speakers** is on by default. It uses the output track to remove
speaker echo from the temporary microphone recognition file. The original microphone
audio is kept unchanged.

Click **Start recording** or the title bar **Record** button. Click **Stop recording**
to seal original audio tails. Device removal, disk trouble, permission trouble, and
processing errors are reported instead of silently switching devices or dropping old
data.

## Phrase pauses

Live recordings seal chunks at duration limits and at natural pauses. The **Pause that
ends a phrase** setting defaults to 800 ms and can be set from 200-3000 ms. Longer
pauses produce fewer, longer phrases.

## Importing media

Click **Import audio / video** and choose a local file. FFprobe enumerates audio
streams; one stream is selected automatically, while multiple streams require a choice.
The app makes a managed original copy and normalizes it for transcription.

Remote URLs, UNC paths, link files, playlists, and media without valid audio streams are
rejected by the audio layer. Import progress reports what the controller knows; it does
not invent percentages.

## Live activity

The **Live activity** panel follows the recording, mirrored session, or selected
session. It shows audio captured so far, chunks starting and finishing, new lines, waits
such as model downloads, errors, and live-file writes. **Copy** places the log on the
clipboard.

For low-level audio details, see
[docs/audio.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/audio.md).
