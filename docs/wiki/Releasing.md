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

The job checks out the exact event revision, sets up the SDK from `global.json`,
restores cached locked packages, runs `Publish.ps1` for a self-contained Windows x64
app/worker package, then uses `Release.ps1` to package and publish release assets.

It does not install or execute the app, run tests, run benchmarks, or build ordinary CI.

## Assets

Each release receives exactly:

- `AudioTranscriber-<tag>-win-x64.zip`
- `AudioTranscriber-<tag>-win-x64.zip.sha256`

The ZIP contains the complete Windows x64 distribution and `BUILD-PROVENANCE.json`. It
omits model weights, credentials, recordings, databases, tool caches, non-x64 Whisper
runtimes, and raw public benchmark transcripts/data.

The in-app updater depends on this layout and verifies both GitHub asset metadata and
the `.sha256` file.

## Local release tests

Run offline release script tests with:

```powershell
pwsh -NoProfile -File .\tests\Release.Tests.ps1
```

More detail is in
[docs/releases.md](https://github.com/throndir2/AudioTranscriber/blob/main/docs/releases.md).
