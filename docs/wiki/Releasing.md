# Releasing

The GitHub workflow `.github\workflows\release.yml` is release-only. It has one Ubuntu
24.04 job, a 15-minute timeout, and no matrix, PR/branch checks, schedules, tests, lint,
benchmarks, model downloads, or deployment.

## Release notes (CHANGELOG.md)

Release notes come from `CHANGELOG.md`. Every user-facing change adds a one-line bullet
under `## Unreleased` (`### Added`, `### Changed`, `### Fixed` or `### Removed`) in the
same pull request, ending with the PR number. To release, move those entries under a new
`## vX.Y.Z - YYYY-MM-DD` heading (leave an empty `## Unreleased` above it), merge that to
`main`, then dispatch with the same version:

```powershell
gh workflow run release.yml --ref main -f version=vX.Y.Z
```

`Release.ps1` copies that section into the GitHub Release body. The manual-run tag step
and the `Prepare` phase fail before tagging or building if the version has no non-empty
section, so a release never ships without notes.

## Triggers

The workflow runs on pushed tags matching `v*` and on manual dispatch. Release scripts
accept `vMAJOR.MINOR.PATCH` and SemVer prereleases such as `v1.2.3-rc.1`; build metadata
is rejected.

Manual release:

```powershell
gh workflow run release.yml --ref main
```

The manual workflow can take an optional version. If omitted, it bumps the patch of the
highest existing stable `vX.Y.Z` tag. It creates the tag on the selected branch's
current commit, then builds and publishes. Existing tags are reused only if already
pointing to that commit.

## What the job does

The job checks out the exact event revision, installs the packagers (`nsis`, `rpm`), sets up
the SDK from `global.json`, restores cached locked packages, runs `Publish.ps1` twice
(self-contained `win-x64` and `linux-x64` app/worker), then uses `Release.ps1` to package
and publish release assets.

It does not install or execute the app, run tests, run benchmarks, or build ordinary CI.

## Assets

Each release receives exactly these files, each with a matching `.sha256`:

- `AudioTranscriber-<tag>-win-x64.zip` (portable; also what Windows auto-update downloads)
- `AudioTranscriber-<tag>-win-x64-setup.exe` (NSIS per-user installer, `installer\windows`)
- `AudioTranscriber-<tag>-linux-x64.tar.gz` (with `install.sh`/`uninstall.sh`; Linux home-folder auto-update)
- `audiotranscriber_<version>_amd64.deb` (written by `Release.ps1` itself, no `dpkg-deb` needed)
- `audiotranscriber-<version>-1.x86_64.rpm` (needs `rpmbuild`; skipped with a warning on hosts without it)

The ZIP and tarball contain the complete distribution and `BUILD-PROVENANCE.json`. They
omit model weights, credentials, recordings, databases, tool caches, other platforms'
natives, and raw public benchmark transcripts/data. The `.deb`/`.rpm` install to
`/opt/audiotranscriber` with `/usr/bin/audiotranscriber`, a desktop entry, hicolor icons and
AppStream metadata, and declare the system libraries they need.

The in-app updater depends on these names and verifies both GitHub asset metadata and
the `.sha256` file.

## Local release tests

Run offline release script tests with:

```powershell
pwsh -NoProfile -File .\tests\Release.Tests.ps1
```

More detail is in
[docs/releases.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/releases.md).
