# Changelog

All notable user-facing changes to AudioTranscriber are recorded here. Each GitHub Release uses its version's section below as its release notes.

Format: newest first; each version is `## vX.Y.Z - YYYY-MM-DD` with `### Added`,
`### Changed`, `### Fixed`, or `### Removed` groups. Pending changes go under
`## Unreleased` until a release is cut.

## Unreleased

### Added

- **Add table (VTT) templates** adds a ready-made virtual tabletop assistant: turn order and token positions read from your table screenshot feed live DM reminders (whose turn, monster tactics, rules, story beats not presented yet). (#PR)
- Templates can include a screenshot of your virtual tabletop (Roll20, Foundry or any window or screen) for vision models, set up in the new **Table screenshot** card. (#40)
- Output template connections can check whether the selected model accepts images (**Check image support**, also run by **Test**). (#37)

### Changed

- The Ollama connection preset defaults to the `gemma4:e4b` model. (#37)

## v0.1.33 - 2026-10-05

### Added

- Re-transcribe a session using the current phrase pause setting. (#36)

## v0.1.32 - 2026-10-04

### Changed

- The live phrase pause is configurable, and its default is raised to 800 ms. (#35)

## v0.1.31 - 2026-10-04

### Added

- Output templates: LLM prompts that are kept updated from the live transcript. (#34)

## v0.1.30 - 2026-09-30

### Added

- Speakers named in past sessions are learned into the voice library. (#33)

## v0.1.29 - 2026-09-30

No changes; rebuild of v0.1.28.

## v0.1.28 - 2026-09-28

### Fixed

- Re-picking the same speaker for another line now works. (#32)

## v0.1.27 - 2026-09-28

### Changed

- Transcription providers are labeled Local CPU, Local GPU, or Internet (NVIDIA cloud). (#31)

## v0.1.26 - 2026-09-28

### Fixed

- Lists stay dark while the window is disabled during close. (#30)

## v0.1.25 - 2026-09-28

### Added

- Voice library: named speakers are remembered across sessions and matched automatically, with controls to rename, forget, or turn remembering off. (#29)

## v0.1.24 - 2026-09-28

### Added

- Shift-click or Ctrl-click to select several sessions. (#28)

### Changed

- The title bar shows the app icon. (#28)

## v0.1.23 - 2026-09-28

### Changed

- All microphone lines can be named, and consecutive same-speaker lines are joined after a speaker fill. (#27)

## v0.1.22 - 2026-09-28

### Added

- Fill in speakers across a recording from the lines you labeled. (#26)

## v0.1.21 - 2026-09-28

### Added

- Continue recording into an existing session, and merge split sessions. (#25)

## v0.1.20 - 2026-09-27

### Changed

- The whole transcript is shown and follows new lines while recording. (#24)

## v0.1.19 - 2026-09-27

### Added

- Delete sessions from the session list (right-click, Delete key, or in bulk by age). (#23)

### Fixed

- The app icon is set on every window so the taskbar shows it. (#22)

## v0.1.18 - 2026-09-27

### Changed

- Consecutive same-speaker transcript rows are merged into one line. (#21)

## v0.1.17 - 2026-09-27

### Added

- Parakeet TDT runs on NVIDIA GPUs via an automatically installed CUDA runtime. (#20)

## v0.1.16 - 2026-09-27

### Changed

- Local CPU Parakeet is the default transcription engine; Whisper remains optional. (#19)

## v0.1.15 - 2026-09-27

### Changed

- Redesigned desktop UI with a dark D&D theme and a compact custom title bar. (#18)

## v0.1.14 - 2026-09-27

### Added

- Parakeet runs on the local NVIDIA GPU when the machine supports it. (#17)

## v0.1.13 - 2026-09-27

### Added

- Falls back to hosted Parakeet when local Whisper is unsure. (#16)

## v0.1.12 - 2026-09-26

### Added

- Right-click speaker labeling that teaches the voice model. (#15)

### Fixed

- The line editor is no longer hidden. (#15)

## v0.1.11 - 2026-09-26

### Changed

- Lower live transcription latency with pause-aligned short chunks and speech-first scheduling. (#14)

## v0.1.10 - 2026-09-26

### Added

- Microphone echo reduction (WebRTC AEC3), so no headset is needed. (#12)

## v0.1.9 - 2026-09-26

### Added

- Live activity log.

### Fixed

- The live transcript file is written during recordings. (#11)

## v0.1.8 - 2026-09-26

### Added

- Headless and UI automation hooks for the MCP server. (#10)

### Fixed

- The diarization worker launches correctly. (#10)

## v0.1.7 - 2026-09-26

### Changed

- Works out of the box: default models download automatically and the microphone is on by default. (#9)

## v0.1.6 - 2026-09-26

### Added

- Automatic updates from the latest GitHub release. (#8)

## v0.1.5 - 2026-09-26

### Added

- FFmpeg is bundled, and prerequisites are checked at startup. (#7)

## v0.1.4 - 2026-09-26

### Added

- App icon for the desktop app and speaker worker. (#6)

## v0.1.3 - 2026-09-26

### Changed

- Unattributed microphone rows are labeled "Me (mic)", with a headphone hint. (#5)

## v0.1.2 - 2026-09-26

### Changed

- Local Whisper defaults to large-v3-turbo with Vulkan GPU acceleration. (#4)

## v0.1.1 - 2026-09-26

### Added

- A session's transcript is mirrored to a text file every few seconds without locking it, so editors can follow it live.

## v0.1.0 - 2026-09-12

Initial release: Windows recording-first audio transcriber with local
transcription, speaker diarization, and session library.
