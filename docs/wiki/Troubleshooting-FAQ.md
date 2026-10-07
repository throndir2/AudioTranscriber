# Troubleshooting / FAQ

## The app says FFmpeg is missing

Release ZIPs include FFmpeg/FFprobe in the `ffmpeg` folder. Keep that folder next to
`AudioTranscriber.App.exe`. Development runs look in the bundled folder, app folder,
PATH, registry PATH, WinGet, Scoop, Chocolatey, and `C:\ffmpeg\bin`.

After fixing FFmpeg, choose **Resume transcription** for sessions whose normalization
paused.

## Local Whisper asks for the Visual C++ runtime

Local Whisper needs Microsoft Visual C++ 2015-2022 x64 runtime 14.40 or newer. The app
checks on startup and can run Microsoft's official installer; approve the Windows
prompt.

## A model is still downloading

Default local models download and verify on first start. You can record while this
happens. Queued jobs run automatically once models are ready. Use **Install / repair
Parakeet model…** or **Install small diarization models** if a download failed.

## NVIDIA upload does not start

Check that the session has explicit upload consent, the key is set in **Privacy /
models**, the language/provider combination is supported, and the account has quota and
permission. Revoked consent, authentication, permission, quota, and resource errors stop
future uploads until fixed.

## Speakers are unknown or wrong

Label clear lines manually, then use **Analyze speakers** or **Fill speakers**
workflows. Very short, overlapping, noisy, compressed, crowded, similar, or
character-voice speech may stay unknown. Labels are display labels, not verified
identities.

## Playback or transcript seek is not word-perfect

Only routes with word timing can support word-level cues. Coarse text keeps coarse
timing. Playback is one stored track at a time, not a hidden mix.

## Can I run more than one app instance?

Only one process can own a data root. Use `-DataRoot PATH` for an isolated library.

## Can I edit the live transcript file?

Treat it as read-only. The app rewrites it whenever the transcript changes, so external
edits are overwritten.
