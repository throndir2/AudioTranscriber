[CmdletBinding()]
param([switch]$SkipRestore)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$sdkRoot = Join-Path $root '.tools\dotnet'
$dotnet = Join-Path $sdkRoot 'dotnet.exe'
$version = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version

if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') {
    throw 'This application requires 64-bit Windows.'
}

$env:DOTNET_ROOT = $sdkRoot
$env:DOTNET_ROOT_X64 = $sdkRoot
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$installed = $false
if (Test-Path $dotnet) {
    $sdks = & $dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) { throw 'The local dotnet host could not enumerate SDKs.' }
    $installed = @($sdks | Where-Object { $_ -match "^$([regex]::Escape($version)) \[" }).Count -gt 0
}

if (-not $installed) {
    $downloadRoot = Join-Path $root '.tools\downloads'
    New-Item -ItemType Directory -Path $downloadRoot, $sdkRoot -Force | Out-Null
    $metadataUri = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    Write-Host "Looking up pinned SDK $version in Microsoft's official release metadata..."
    $metadata = Invoke-RestMethod -Uri $metadataUri -TimeoutSec 60
    $matches = @(
        foreach ($release in $metadata.releases) {
            foreach ($sdk in $release.sdks) {
                if ($sdk.version -eq $version) {
                    $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-sdk-win-x64.zip' }
                }
            }
        }
    )
    $archives = @($matches | Sort-Object -Property url -Unique)
    if ($archives.Count -ne 1) { throw "Expected exactly one official Windows x64 SDK archive for $version." }
    $archive = $archives[0]
    $uri = [Uri]$archive.url
    if ($uri.Scheme -ne 'https' -or
        $uri.Host -notin @('builds.dotnet.microsoft.com', 'dotnetcli.azureedge.net', 'dotnetcli.blob.core.windows.net') -or
        $archive.hash -notmatch '^[0-9a-fA-F]{128}$') {
        throw 'Official SDK metadata contained an unexpected download authority or SHA-512 hash.'
    }
    $zipPath = Join-Path $downloadRoot "dotnet-sdk-$version-win-x64.zip"
    $validArchive = (Test-Path $zipPath) -and
        ((Get-FileHash $zipPath -Algorithm SHA512).Hash -eq $archive.hash)
    if (-not $validArchive) {
        $partial = "$zipPath.partial"
        try {
            Write-Host "Downloading official SDK $version (worktree-local installation only)..."
            Invoke-WebRequest -Uri $uri -OutFile $partial -TimeoutSec 1200
            if ((Get-FileHash $partial -Algorithm SHA512).Hash -ne $archive.hash) {
                throw 'SDK archive SHA-512 mismatch; nothing has been installed.'
            }
            Move-Item -LiteralPath $partial -Destination $zipPath -Force
        }
        finally {
            if (Test-Path $partial) { Remove-Item -LiteralPath $partial -Force }
        }
    }
    Write-Host 'SHA-512 verified against official HTTPS release metadata. Extracting locally...'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $sdkRoot -Force
}

Push-Location $root
try {
    $actual = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actual -ne $version) {
        throw "Pinned local SDK $version could not be selected."
    }
    Write-Host "Using local SDK $actual at $sdkRoot"
    if (-not $SkipRestore) {
        & $dotnet restore (Join-Path $root 'AudioTranscriber.slnx') --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
    }
    foreach ($tool in @('ffmpeg', 'ffprobe')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            Write-Warning "$tool is not on PATH. Install a suitable trusted FFmpeg distribution before importing or normalizing audio."
        }
    }
    Write-Host 'Setup complete. No global SDK, API keys, audio capture, or model downloads were configured.'
}
finally { Pop-Location }
