# Updates and Closing

Release builds update from the repository's latest GitHub release. Drafts and
prereleases are ignored.

## Automatic updates

With **Automatically download new releases and install them when the app closes** on,
the app checks shortly after startup and every six hours. It downloads
`AudioTranscriber-<tag>-win-x64.zip` into `%LOCALAPPDATA%\AudioTranscriber.Updates`.

The download is verified against GitHub's asset SHA-256 digest and the published
`.sha256` file before unpacking. Development builds never update.

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
