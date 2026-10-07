# Installing AudioTranscriber

AudioTranscriber runs on Windows x64 and Linux x64. Every
[release](https://github.com/throndir2/AudioTranscriber/releases/latest) has one download
per system, each with a matching `.sha256` file. Builds are unsigned.

| System | Asset | Install |
| --- | --- | --- |
| Windows 10/11 | `AudioTranscriber-<tag>-win-x64-setup.exe` | Run it. Installs for your user only (no admin) into `%LOCALAPPDATA%\Programs\AudioTranscriber` with a Start menu shortcut and an uninstaller (Settings → Apps). Add `/S` for a silent install. |
| Windows (portable) | `AudioTranscriber-<tag>-win-x64.zip` | Extract to a folder you can write to and run `AudioTranscriber.App.exe`. Keep the whole folder together. |
| Ubuntu, Debian, Mint, Pop!_OS | `audiotranscriber_<version>_amd64.deb` | `sudo apt install ./audiotranscriber_<version>_amd64.deb` |
| Fedora, openSUSE, RHEL | `audiotranscriber-<version>-1.x86_64.rpm` | `sudo dnf install ./audiotranscriber-<version>-1.x86_64.rpm` (`zypper install` on openSUSE) |
| Any other Linux | `AudioTranscriber-<tag>-linux-x64.tar.gz` | Extract, then `./install.sh` (home-folder install) or `./install.sh --system` (`/opt`, uses sudo). `./uninstall.sh` removes it. |

Every package bundles the .NET runtime, a pinned LGPL FFmpeg/FFprobe build, SQLite and the
speech/speaker runtimes, so nothing else needs to be installed by hand. The packages also
include documentation, model notices, dependency license files and a package inventory
under `licenses`.

## Prerequisites

**Windows:** local Whisper needs the Microsoft Visual C++ 2015-2022 x64 runtime (14.40 or
newer). The setup installs it if it is missing, and the app checks again on startup and
runs Microsoft's official installer if needed (approve the Windows prompt).

**Linux:** the app uses a few system libraries: the PulseAudio client (`libpulse`, served
by PipeWire on modern desktops) and `pactl` for recording, ICU, fontconfig and X11 for the
window (it runs under Wayland through XWayland). `apt` and `dnf` install them automatically
from the package's dependencies. `install.sh` checks for them and offers to install them
with `apt-get`, `dnf`, `zypper` or `pacman` (`--yes` answers for you).

The default local models download on first start and are verified before use. You can
start recording while they download; queued audio is transcribed when they are ready.

## Linux notes

- Recording captures the **monitor** of the selected output (what you hear) plus the
  selected microphone, through PulseAudio or PipeWire (`pipewire-pulse`).
- GPU Parakeet is Windows-only for now; Linux runs Parakeet on the CPU. Whisper can use the
  GPU through Vulkan (`libvulkan1`).
- Saved keys are encrypted with a random per-user key in `~/.config/AudioTranscriber`
  (readable only by your account). Windows uses DPAPI.
- The app appears in your launcher as **AudioTranscriber** with its icon, and in GNOME
  Software / KDE Discover through its AppStream metadata.

## Updates

Release builds check GitHub for the latest non-draft, non-prerelease release. With
automatic updates on, the app downloads and verifies the new version in the background,
then installs it when the app closes or when you choose **Restart to update**:

- Windows (setup or ZIP): the files are replaced in place.
- Linux home-folder installs (`install.sh`): the tarball is applied in place.
- Linux `.deb`/`.rpm` installs: the new package is installed through `apt`/`dnf` after you
  approve the system password prompt.

For details, see [Updates and Closing](Updates-and-Closing) and the source docs at
[docs/usage.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/usage.md).
