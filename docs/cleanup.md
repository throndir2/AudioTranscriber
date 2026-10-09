# Disk cleanup

The build, the tests, and the development app write large generated folders into each
checkout. Each Copilot session uses its own git worktree, so these folders repeat in each
worktree. This page tells you what the folders are, when to clean them, and how.

## What the repository generates

On 2026-10-09, a used worktree was 4.6–6.5 GB. 25 active worktrees used 64 GB. Folders
that archived sessions left behind used another 56 GB.

| Folder (in each checkout) | Written by | Typical size | How to get it back |
| --- | --- | --- | --- |
| `.tools\dotnet` | `Setup.ps1` (pinned .NET SDK) | 0.75 GB | Run `.\scripts\Setup.ps1` |
| `.tools\nuget` | Scripts from before the shared NuGet cache | 2.1 GB | Not used any more |
| `.tools\downloads` | `Setup.ps1` in older checkouts (SDK ZIP), `Publish.ps1` (FFmpeg archives) | 0.1–0.3 GB | Downloaded again when necessary |
| `.tools\nsis`, `.tools\cli-home` | `Release.ps1`, the .NET CLI | Small | Created again when necessary |
| `.models\diarization`, `.models\parakeet`, `.models\parakeet-gpu`, `.models\whisper` | Model setup in the development app, `Install-WhisperModel.ps1`, commands in `docs\` | 0.04–1.7 GB | Install the models again |
| `src\*\bin`, `src\*\obj`, `tests\*\bin`, `tests\*\obj`, `tools\*\bin`, `tools\*\obj` | `dotnet build`, `Test.ps1`, `Start-App.ps1`, `Start-Mcp.ps1` | 0.7–2.6 GB | Build again |
| `artifacts\TestResults`, `smoke`, `desktop-smoke`, `publish`, `release-tests`, `fixtures`, `public-fixture` | `Test.ps1`, `Start-App.ps1 -Smoke`, `Publish.ps1`, `tests\Release.Tests.ps1`, `New-SpeechFixture.ps1`, `Compare-Models.ps1` | 0–1 GB | Run the command again |

All of these folders are in `.gitignore`. Git does not track them.

## Shared caches

**NuGet packages.** All scripts use `NUGET_PACKAGES` if it is set. If it is not set, they
use the shared per-user cache `%USERPROFILE%\.nuget\packages`. All worktrees share one
copy (about 2 GB). Locked restore checks the package hashes, so a shared cache is safe.
Older worktrees still have `.tools\nuget`. `Clean.ps1` deletes it.

**Models (optional).** When the development app runs from a checkout, it keeps models in
`<checkout>\.models`. To share one model folder between all checkouts:

1. Set a user environment variable:
   `[Environment]::SetEnvironmentVariable('AUDIOTRANSCRIBER_DEV_MODELS', "$env:LOCALAPPDATA\AudioTranscriber-dev\models", 'User')`.
2. Restart the Copilot app and your terminals, so that new processes get the variable.
3. Optional: move an existing model set into the shared folder, for example
   `Move-Item .models\* "$env:LOCALAPPDATA\AudioTranscriber-dev\models"`.

The app then uses `diarization`, `parakeet`, `parakeet-gpu`, and `whisper` in that folder.
Installed (non-checkout) copies of the app ignore this variable. Do not use the shared
folder when you test a first-time model installation. Remove the variable for that test.

**SDK.** The SDK stays in each worktree at `.tools\dotnet`. The scripts, the docs, and
the worker launch of the development app use that path. `Setup.ps1` deletes the SDK ZIP
after it extracts it. `Clean.ps1 -Tools` deletes the SDK. `Setup.ps1` installs it again
in about one minute.

## When to clean

- **At the end of each session.** Clean after your last build, test, or app run.
- **When disk space is low**, or when the worktrees folder becomes large. Run
  `.\scripts\Clean.ps1 -AllWorktrees -All -DryRun` to see the size of each worktree.
- **When the archive of a session leaves its folder behind.** See [Old worktrees](#old-worktrees).

## Clean.ps1

```powershell
.\scripts\Clean.ps1 -DryRun                 # show what it would delete in this worktree
.\scripts\Clean.ps1                         # delete build output, test output, .tools\nuget, .tools\downloads
.\scripts\Clean.ps1 -All                    # also delete .tools (SDK) and .models
.\scripts\Clean.ps1 -Tools                  # also delete .tools, but keep .models
.\scripts\Clean.ps1 -AllWorktrees -All -DryRun   # sizes for each linked worktree
.\scripts\Clean.ps1 -Path <folder> -All     # clean the generated output of a leftover folder
```

What the script does:

- It deletes only the folders in the table above. It does not delete other files.
- It keeps a folder that contains tracked files (`git ls-files`).
- It does not delete `sessions\`, `models\`, other `artifacts\` subfolders (for example
  `artifacts\desktop-library`, which can hold recordings), `.env` files, local settings
  files, or `tools\AudioTranscriber.Benchmarks\artifacts`.
- It stops the .NET build servers of the worktree first, because they lock files.
- If a file is in use (for example by a running app), it shows a warning and continues.
- If a folder is a link (junction), it deletes only the link, not the target.
- `-Path` accepts only an AudioTranscriber checkout or worktree folder.
- `-AllWorktrees` cleans each linked worktree. It does not clean the main checkout.

After `-Tools` or `-All`, run `.\scripts\Setup.ps1` before you build again.

Do not use `git clean -x`, `git clean -X`, or `git reset --hard` to clean. They also
delete local settings, credentials files, recordings, or uncommitted work.

## Old worktrees

The Copilot app owns the worktrees in `C:\Repositories\copilot-worktrees\AudioTranscriber`.

- To remove the worktree of a finished session, archive the session in the app.
- Do not remove the worktree of a session that is not archived.
- Do not remove `copilot-prewarm-*` worktrees. The app keeps them ready for new sessions.

If an archive leaves a folder behind (usually because a build server or a running app
locked files), do these steps:

1. Run `git worktree list`. Find out if git still lists the folder.
2. Run `.\scripts\Clean.ps1 -Path <folder> -All` to delete the generated output.
3. If git lists the folder, run `git worktree remove <folder>`. Do not add `--force`.
   Git refuses to remove a worktree that has changes or untracked files. If git refuses,
   stop and ask the owner.
4. If git does not list the folder, look at the files that remain. Delete the folder only
   if it contains no work that someone needs.
5. Run `git worktree prune` to remove the records of worktrees whose folders are gone.
