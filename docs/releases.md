# GitHub release build

`.github\workflows\release.yml` builds releases only. It has **one Ubuntu 24.04 job**, a 25-minute timeout, and no matrix, PR/branch-push checks, schedules, tests, lint, benchmarks, model downloads, or deployment. It installs only NSIS and rpm packaging tools before building release assets.

## Release notes (CHANGELOG.md)

Release notes come from `CHANGELOG.md`. Every user-facing change adds a bullet
under `## Unreleased` in the same pull request. To release, move those entries
under a new `## vX.Y.Z - YYYY-MM-DD` heading (leave an empty `## Unreleased`
above it), merge that to `main`, then dispatch with the same version:

```powershell
gh workflow run release.yml --ref main -f version=vX.Y.Z
```

`Release.ps1` copies that section into the GitHub Release body (below the
ownership marker, followed by download/licensing notes and the source commit).
The manual-run tag step and the `Prepare` phase fail before creating a tag or
building if the version has no non-empty section, so a release can never ship
with missing notes. To correct notes on an already-published release, edit the
release body on GitHub and keep the marker line; also fix `CHANGELOG.md`.

## Trigger

After these changes are pushed, create and push a version tag on the exact commit
to release. The tagged commit must contain this workflow and its scripts:

```powershell
# Set these to your chosen version and reviewed commit before running.
git tag $tag $commit
git push origin "refs/tags/$tag"
```

Accepted tags are `vMAJOR.MINOR.PATCH` and SemVer prereleases such as
`v1.2.3-rc.1`. Leading-zero numeric identifiers, branch names, arbitrary refs,
and build metadata (`+...`) are rejected. Tags with a prerelease suffix create
prereleases; stable tags do not. Stable releases are marked as the repository's
Latest release when published; prereleases are not.

### Manual release (one click)

Actions → **Release build** → **Run workflow**, pick the branch, and optionally
enter a version. Leaving the version empty bumps the patch of the highest existing
`vX.Y.Z` tag (for example `v0.1.0` → `v0.1.1`). The run creates that tag on the
selected branch's current commit, then builds and publishes exactly like a tag
push. An existing tag is reused only if it already points to that commit.
Tags created by the run do not trigger a second run.

Retry a failed run from the Actions UI. A rerun revalidates the remote tag,
including annotated tags, against the exact checked-out commit and original event SHA.
Deleted, moved, missing, or non-commit tags fail instead of building `main`.
Nothing runs on an ordinary branch push.

**No tag, remote workflow run, or GitHub Release is created by adding this code.**
Pushing code and choosing/pushing a release version are separate user actions.

## Cost controls and build path

- Linux packaging is built from the Avalonia desktop port with `Publish.ps1 -Runtime linux-x64`; Windows remains `win-x64` and both outputs are self-contained.
- Checkout is shallow and pinned to the event SHA. Official checkout and
  setup-dotnet actions are pinned to verified commit SHAs.
- setup-dotnet reads the exact `global.json` SDK and caches NuGet packages using
  that file plus checked-in source-project lockfiles. CI never downloads the
  worktree-local SDK or runs `Setup.ps1`.
- `Publish.ps1` restores **only the app/worker dependency graph once in locked
  mode**. The app publish builds the referenced worker; its subsequent publish
  uses `--no-build --no-restore`. The solution's tests and benchmark tool are not
  built by the release workflow.
- Locked package restore remains enabled for release publishes; runtime-specific native assets are selected during publish.
- Verified assets from a completed run skip SDK setup, cache restoration, and
  rebuilding entirely. No duplicate `upload-artifact` staging is used.

The existing local command still uses `.tools\dotnet` and the usual publish
directory. CI supplies its SDK and a fresh output directory:

```powershell
.\scripts\Publish.ps1 -DotnetPath (Get-Command dotnet).Source `
  -OutputDirectory $publishDirectory -RequireEmptyOutput
```

This publishes the self-contained app and worker for the selected runtime (`-Runtime win-x64` by default, or `linux-x64`), framework/native libraries, the pinned LGPL FFmpeg/FFprobe build (downloaded once from BtbN's
GitHub releases, SHA-256-verified, cached in `.tools\downloads` or `RUNNER_TEMP`),
README, model-license notices, package licenses, and provenance
inventory. It does not install or execute the application.

## Release assets and reruns

Each GitHub Release receives exactly these workflow-owned assets (each with a matching `.sha256` file):

- `AudioTranscriber-<tag>-win-x64.zip` (kept for the existing auto-updater)
- `AudioTranscriber-<tag>-win-x64-setup.exe` (per-user NSIS installer; unsigned)
- `AudioTranscriber-<tag>-linux-x64.tar.gz` (portable folder plus install/uninstall scripts)
- `audiotranscriber_<version>_amd64.deb`
- `audiotranscriber-<version>-1.x86_64.rpm`

The Windows ZIP contains the complete Windows x64 distribution and a
`BUILD-PROVENANCE.json` identifying its tag, commit, SDK, and platform. It omits
other-architecture Whisper runtimes and the documentation's raw public benchmark
transcripts/data. It bundles a pinned, SHA-256-verified LGPL FFmpeg/FFprobe build
in `ffmpeg\` with its license and provenance under `licenses\`. The Linux tarball,
DEB, and RPM contain the Linux x64 app/worker, executable `ffmpeg/ffmpeg` and
`ffmpeg/ffprobe`, desktop metadata, and package metadata. Model weights,
credentials, recordings, databases, and tool caches are not release payloads.
Framework/runtime JSON and documentation remain included.

The in-app updater relies on this layout: it reads the tag from
`BUILD-PROVENANCE.json`, uses the Windows ZIP on Windows, the Linux tarball for
writable Linux installs, and the `.deb`/`.rpm` package for non-writable `/opt`
installs. It verifies each asset with GitHub SHA-256 metadata and the matching
`.sha256` file. Keep the asset names and provenance file stable. The GitHub API it
uses is unauthenticated, so the repository must be public for updates to be found.

Publishing uses GitHub's supported release API with the job's `GITHUB_TOKEN`:
`contents: write` is the only permission granted. The token is supplied only to
the release-inspection/publication steps, not the build command; checkout does
not persist credentials. There is no PAT or third-party release action.

The script creates a **draft for an already-existing tag** with an ownership
marker containing that exact tag and commit. GitHub's tag lookup omits drafts,
so a missing tag lookup falls back to the authenticated, paginated release list.
Ambiguous duplicate releases for the same tag are rejected. All asset digests
and checksum contents are verified using GitHub's SHA256 asset metadata before
publishing. The remote tag is checked again immediately before and after publication.

An interrupted upload leaves a draft. A rerun may replace only the expected named
assets of that matching, workflow-owned draft. A complete matching release is a
no-op; a complete matching draft is finalized without rebuilding. Unrelated
releases, moved tags, changed prerelease metadata, and incomplete/inconsistent
already-published assets fail clearly and are never silently overwritten.
Preserve the ownership marker when editing release notes.

Runs for the same tag share a concurrency group. `cancel-in-progress: false`
prevents a later run from canceling a publish halfway through.

## Local validation boundary

Run the offline release-discovery and draft-finalization regression tests with
`pwsh -NoProfile -File .\tests\Release.Tests.ps1`. They exercise the production
release functions with simulated GitHub responses, without network requests,
SDK setup, asset uploads, or changes to releases.

Release development validates the actual SDK publish path for Windows and Linux,
then packages the outputs without running tests in the workflow. Local validation
for these installer changes covered Windows publish, Linux publish, Windows NSIS
install/uninstall, Ubuntu DEB install/remove, and tarball `install.sh`/`uninstall.sh`
on Ubuntu and Fedora containers.

Package inspection checked AMD64 PE headers, matching pinned SQLite bytes,
required runtime/worker/model-license files, excluded data and non-x64 runtimes,
and the ZIP/checksum pair. Workflow structure passed actionlint; 52 local release
guard/state-machine checks used mocked API operations, not live publication.
Those are local checks, not extra CI jobs. No hosted Actions minutes, tag push,
or real GitHub Release was used during validation.

## Local package fallback

Windows fallback packaging is:

```powershell
./scripts/Release.ps1 -Phase Package -Tag vX.Y.Z -Commit <commit> `
  -PublishDirectory <win-publish> -LinuxPublishDirectory <linux-publish> `
  -AssetDirectory <assets>
```

If `makensis` is not on PATH, the script downloads a pinned portable NSIS tool
package into `.tools/nsis` and verifies SHA-256. The `.deb` is written in
PowerShell/.NET as an `ar` archive with `control.tar.gz` and `data.tar.gz`, so it
does not require `dpkg-deb`. RPM packaging uses `rpmbuild` when available (CI
installs `rpm`); local Windows packaging warns and skips the RPM if `rpmbuild` is
absent.

Linux package dependencies were selected from the .NET self-contained runtime and
native desktop/audio payload by running `ldd` in Ubuntu 24.04 against the apphost,
Skia/HarfBuzz, SQLite, ONNX/sherpa, whisper, and bundled FFmpeg/FFprobe binaries:
`libc6`, `libstdc++6`, `libgcc-s1`, ICU, `libfontconfig1`, X11/ICE/SM/Xext/Xrandr/Xi/Xcursor,
`libgomp1`, `libpulse0`, and `pulseaudio-utils`. Vulkan and PipeWire/PulseAudio are
recommendations.


