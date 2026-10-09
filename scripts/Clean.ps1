[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$Tools,
    [switch]$Models,
    [switch]$All,
    [switch]$AllWorktrees,
    [string[]]$Path
)

# Deletes only generated, git-ignored folders (build and test output, SDK, downloads, model downloads).
# It never deletes tracked files or any other file. See docs\cleanup.md.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($All) { $Tools = $true; $Models = $true }
$artifactFolders = 'TestResults', 'smoke', 'desktop-smoke', 'publish', 'release-tests', 'fixtures', 'public-fixture'

function Get-Bytes([string]$Directory) {
    $bytes = 0L
    try {
        foreach ($file in [IO.DirectoryInfo]::new($Directory).EnumerateFiles('*', [IO.SearchOption]::AllDirectories)) {
            $bytes += $file.Length
        }
    }
    catch { }
    $bytes
}
function Format-Size([long]$Bytes) { '{0,7:N2} GB' -f ($Bytes / 1GB) }

$roots = @(
    if ($Path) { $Path | ForEach-Object { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($_) } }
    elseif ($AllWorktrees) {
        # The first entry is the main checkout; only linked worktrees are cleaned.
        git -C $repo worktree list --porcelain | Where-Object { $_ -like 'worktree *' } |
            ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)) } | Select-Object -Skip 1
    }
    else { $repo }
)

$grandTotal = 0L
$grandCount = 0
foreach ($root in $roots) {
    $root = $root.TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        Write-Warning "Missing folder: $root. Run 'git worktree prune' to remove its record."
        continue
    }
    $gitFile = [IO.Path]::Combine($root, '.git')
    $isCheckout = (Test-Path -LiteralPath ([IO.Path]::Combine($root, 'AudioTranscriber.slnx'))) -or
        ((Test-Path -LiteralPath $gitFile -PathType Leaf) -and ((Get-Content -LiteralPath $gitFile -Raw) -match 'AudioTranscriber'))
    if (-not $isCheckout) {
        Write-Warning "Skipped $root because it is not an AudioTranscriber checkout or worktree."
        continue
    }

    $candidates = [Collections.Generic.List[string]]::new()
    foreach ($group in 'src', 'tests', 'tools') {
        $groupPath = [IO.Path]::Combine($root, $group)
        if (-not (Test-Path -LiteralPath $groupPath -PathType Container)) { continue }
        foreach ($project in Get-ChildItem -LiteralPath $groupPath -Directory -Force) {
            $candidates.Add([IO.Path]::Combine($project.FullName, 'bin'))
            $candidates.Add([IO.Path]::Combine($project.FullName, 'obj'))
        }
    }
    foreach ($name in $artifactFolders) { $candidates.Add([IO.Path]::Combine($root, 'artifacts', $name)) }
    if ($Tools) { $candidates.Add([IO.Path]::Combine($root, '.tools')) }
    else {
        # Obsolete per-worktree NuGet cache (scripts now use the shared one) and re-downloadable archives.
        $candidates.Add([IO.Path]::Combine($root, '.tools', 'nuget'))
        $candidates.Add([IO.Path]::Combine($root, '.tools', 'downloads'))
    }
    if ($Models) { $candidates.Add([IO.Path]::Combine($root, '.models')) }
    $existing = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container })

    Write-Host "== $root"
    if ($existing.Count -eq 0) { Write-Host '   Nothing to clean.'; continue }

    $tracked = @()
    if (Test-Path -LiteralPath $gitFile) {
        $relative = @($existing | ForEach-Object { $_.Substring($root.Length + 1) })
        $tracked = @(git -C $root ls-files -- @relative 2>$null)
    }

    if (-not $DryRun) {
        # Build servers started from this worktree's SDK keep files locked; stop them first.
        $dotnet = @('dotnet.exe', 'dotnet') | ForEach-Object { [IO.Path]::Combine($root, '.tools', 'dotnet', $_) } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($dotnet) { & $dotnet build-server shutdown *> $null }
    }

    $rootTotal = 0L
    foreach ($directory in $existing) {
        $relativePath = $directory.Substring($root.Length + 1)
        $prefix = $relativePath.Replace('\', '/') + '/'
        if (@($tracked | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
            Write-Host ("   {0,-12} {1}  {2}" -f 'kept', (Format-Size 0), "$relativePath (contains tracked files)")
            continue
        }
        $isLink = [bool]((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
        $bytes = if ($isLink) { 0L } else { Get-Bytes $directory }
        $status = 'would remove'
        if (-not $DryRun) {
            try {
                # Remove only the link itself, never the shared folder it points to.
                if ($isLink) { [IO.Directory]::Delete($directory, $false) }
                else { Remove-Item -LiteralPath $directory -Recurse -Force }
                $status = 'removed'
            }
            catch {
                $status = 'failed'
                if (Test-Path -LiteralPath $directory) { $bytes -= Get-Bytes $directory }
                Write-Warning "Could not remove all of $directory (a file is probably in use): $($_.Exception.Message)"
            }
        }
        Write-Host ("   {0,-12} {1}  {2}" -f $status, (Format-Size $bytes), $relativePath)
        $rootTotal += $bytes
        $grandCount++
    }
    Write-Host ("   Total:       {0}" -f (Format-Size $rootTotal))
    $grandTotal += $rootTotal
}

$verb = if ($DryRun) { 'Would free' } else { 'Freed' }
Write-Host ("{0} {1} in {2} folder(s)." -f $verb, (Format-Size $grandTotal).Trim(), $grandCount)
if ($Tools -and -not $DryRun) { Write-Host 'Run .\scripts\Setup.ps1 again before you build in a cleaned worktree.' }
