# GitHub release build

`.github\workflows\release.yml` builds releases only. It has **one Ubuntu 24.04
job**, a 15-minute timeout, and no matrix, PR/branch-push checks, schedules, tests,
lint, benchmarks, model downloads, or deployment.

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
prereleases; stable tags do not. The workflow does not change an existing
release's latest designation.

There is deliberately no manual branch-build trigger. Retry the original tagged
run from the Actions UI instead. A rerun revalidates the remote tag, including
annotated tags, against the exact checked-out commit and original push SHA.
Deleted, moved, missing, or non-commit tags fail instead of building `main`.
Nothing runs on an ordinary branch push.

**No tag, remote workflow run, or GitHub Release is created by adding this code.**
Pushing code and choosing/pushing a release version are separate user actions.

## Cost controls and build path

- Linux was selected only after an actual SDK 10.0.401 Linux-container
  cross-publish produced both Windows executables and the matching pinned SQLite
  binary. `EnableWindowsTargeting=true` is used on Linux; no portability changes
  to application code are required.
- Checkout is shallow and pinned to the event SHA. Official checkout and
  setup-dotnet actions are pinned to verified commit SHAs.
- setup-dotnet reads the exact `global.json` SDK and caches NuGet packages using
  that file plus checked-in source-project lockfiles. CI never downloads the
  worktree-local SDK or runs `Setup.ps1`.
- `Publish.ps1` restores **only the app/worker dependency graph once in locked
  mode**. The app publish builds the referenced worker; its subsequent publish
  uses `--no-build --no-restore`. The solution's tests and benchmark tool are not
  built by the release workflow.
- WPF prunes `System.Security.Cryptography.ProtectedData` differently during
  Linux restore. The app therefore uses `packages.linux.lock.json` on Linux and
  its existing Windows lockfile otherwise. Package versions are unchanged.
- Verified assets from a completed run skip SDK setup, cache restoration, and
  rebuilding entirely. No duplicate `upload-artifact` staging is used.

The existing local command still uses `.tools\dotnet` and the usual publish
directory. CI supplies its SDK and a fresh output directory:

```powershell
.\scripts\Publish.ps1 -DotnetPath (Get-Command dotnet).Source `
  -OutputDirectory $publishDirectory -RequireEmptyOutput
```

This publishes the self-contained Windows app and worker, framework/native
libraries, README, model-license notices, package licenses, and provenance
inventory. It does not install or execute the application.

## Release assets and reruns

Each GitHub Release receives exactly these two workflow-owned assets:

- `AudioTranscriber-<tag>-win-x64.zip`
- `AudioTranscriber-<tag>-win-x64.zip.sha256`

The ZIP contains the complete Windows x64 distribution and a
`BUILD-PROVENANCE.json` identifying its tag, commit, SDK, and platform. It omits
other-architecture Whisper runtimes and the documentation's raw public benchmark
transcripts/data. Model weights, FFmpeg, credentials, recordings, databases, and
tool caches are not release payloads. Framework/runtime JSON and documentation
remain included.

Publishing uses GitHub's supported release API with the job's `GITHUB_TOKEN`:
`contents: write` is the only permission granted. The token is supplied only to
the release-inspection/publication steps, not the build command; checkout does
not persist credentials. There is no PAT or third-party release action.

The script creates a **draft for an already-existing tag** with an ownership
marker containing that exact tag and commit. GitHub's tag lookup omits drafts,
so a missing tag lookup falls back to the authenticated, paginated release list.
Ambiguous duplicate releases for the same tag are rejected. Both asset digests
and the checksum content are verified using GitHub's SHA256 asset metadata before publishing.
The remote tag is checked again immediately before and after publication.

An interrupted upload leaves a draft. A rerun may replace only the two expected
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

Release development validated the actual SDK 10.0.401 **Ubuntu 24.04** container
path, including locked restore, both publishes, notices, and ZIP creation. The
Linux-built EXE also opened all five WPF tabs on Windows with an empty library:
no capture or uploads. The Windows publish path passed independently, and the
previous user package retained its original checksum.

Package inspection checked AMD64 PE headers, matching pinned SQLite bytes,
required runtime/worker/model-license files, excluded data and non-x64 runtimes,
and the ZIP/checksum pair. Workflow structure passed actionlint; 52 local release
guard/state-machine checks used mocked API operations, not live publication.
Those are local checks, not extra CI jobs. No hosted Actions minutes, tag push,
or real GitHub Release was used during validation.
