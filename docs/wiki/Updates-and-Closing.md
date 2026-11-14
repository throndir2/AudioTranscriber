# Updates and Closing

**Status:** Implemented
**Last Updated:** 2026-11-14
**Audience:** Users and contributors
**Last verified:** 2026-11-14 against b3ed981
**Code:** `src/AudioTranscriber.App/AppUpdater.cs`, `src/AudioTranscriber.App/MainViewModel.cs`, `src/AudioTranscriber.App/MainWindow.axaml.cs`

Release builds update from the repository's latest GitHub release. Drafts and
prereleases are ignored. This works for the Windows setup and ZIP and for the Linux
tarball, `.deb`, and `.rpm`. Copies installed with the Windows setup from v0.2.9 or
earlier show "Development build" and cannot update themselves. Run the latest setup
once; later releases then install automatically.

## Automatic updates

With **Automatically download new releases and install them when the app closes** on,
the app checks shortly after startup and every six hours. Windows downloads
`AudioTranscriber-<tag>-win-x64.zip` into `%LOCALAPPDATA%\AudioTranscriber.Updates`.
Linux selects a tarball or a package that matches the installation.

The download is verified against at least one SHA-256 source: GitHub's asset digest or
the published `.sha256` file. When both exist, both must match. Development builds never update.

## Installing an update

Nothing is replaced while the app runs. **Restart to update** closes through the normal
safe shutdown, then a helper waits for exit, copies new files over the application
folder, and reopens the app with the same arguments.

If you do not restart, the update installs the next time you close the app. Only the
application folder changes; your library, recordings, settings, and models are
untouched. Folders requiring administrator rights trigger a Windows approval prompt.

The helper logs to `%LOCALAPPDATA%\AudioTranscriber.Updates\update.log`. Turn the
automatic checkbox off to check only when you choose **Check for updates now**.

## Closing safely

Closing while recording or doing foreground work asks for confirmation. The window waits
for active operations and recording stop/seal, then disposes the controller. It does not
hide exit-time cancellation behind capture Stop to skip audio tails.

If safe shutdown fails, the window stays open with a warning.
