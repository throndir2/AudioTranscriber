[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64', 'linux-x64')][string]$Runtime = 'win-x64',
    [string]$DotnetPath,
    [string]$OutputDirectory,
    [switch]$RequireEmptyOutput
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$useLocalSdk = [string]::IsNullOrWhiteSpace($DotnetPath)
$dotnet = if ($useLocalSdk) { Join-Path $root '.tools\dotnet\dotnet.exe' }
    else { (Get-Command $DotnetPath -ErrorAction Stop).Source }
$project = Join-Path $root 'src\AudioTranscriber.App\AudioTranscriber.App.csproj'
$destination = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root "artifacts\publish\$Runtime" }
    else { [IO.Path]::GetFullPath($OutputDirectory) }
if (-not (Test-Path $dotnet)) { throw 'Run .\scripts\Setup.ps1 first to install the pinned local SDK.' }
if (-not (Test-Path $project)) { throw 'The desktop application project is not present in this checkout yet.' }
if ($RequireEmptyOutput -and (Test-Path -LiteralPath $destination) -and
    @(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
    throw 'Release output must be new or empty; existing artifacts will not be deleted or packaged.'
}
if ($useLocalSdk) {
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
    $env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
}
elseif ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    $env:NUGET_PACKAGES = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Save-UrlWithHash([string]$Url, [string]$Path, [string]$Sha256) {
    $needsDownload = -not (Test-Path -LiteralPath $Path)
    if (-not $needsDownload) {
        $needsDownload = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256
    }
    if ($needsDownload) {
        Write-Host "Downloading pinned asset: $Url"
        $previousProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $Url -OutFile $Path } finally { $ProgressPreference = $previousProgress }
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        throw "Downloaded asset does not match pinned SHA-256: $Url"
    }
}

function Copy-ArchiveEntry([IO.Compression.ZipArchiveEntry]$Entry, [string]$DestinationPath) {
    New-Item -ItemType Directory -Force (Split-Path $DestinationPath -Parent) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($Entry, $DestinationPath, $true)
}

function Add-WindowsFFmpeg([string]$Destination, [string]$Licenses, [string]$DownloadCache) {
    $ffmpegUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-31-13-27/ffmpeg-n9.0.1-11-ge47273f4d9-win64-lgpl-shared-9.0.zip'
    $ffmpegSha256 = '83a824f0729a69d143c9865125bb86988a11dd388325f0033711045522068aa0'
    $ffmpegZip = Join-Path $DownloadCache ([IO.Path]::GetFileName($ffmpegUrl))
    Save-UrlWithHash $ffmpegUrl $ffmpegZip $ffmpegSha256
    $ffmpegDestination = Join-Path $Destination 'ffmpeg'
    New-Item -ItemType Directory -Force $ffmpegDestination | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ffmpegZip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '^[^/]+/bin/(ffmpeg\.exe|ffprobe\.exe|[^/]+\.dll)$') {
                Copy-ArchiveEntry $entry (Join-Path $ffmpegDestination $entry.Name)
            }
            elseif ($entry.FullName -match '^[^/]+/LICENSE\.txt$') {
                Copy-ArchiveEntry $entry (Join-Path $Licenses 'FFmpeg-LICENSE.txt')
            }
        }
    }
    finally { $archive.Dispose() }
    foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $ffmpegDestination $tool))) { throw "The pinned FFmpeg archive did not contain $tool." }
    }
    @(
        '# Bundled FFmpeg'
        ''
        'The `ffmpeg` folder contains an unmodified LGPL-2.1-or-later shared Windows build of FFmpeg'
        '(ffmpeg.exe, ffprobe.exe and their libav* DLLs), launched as separate processes.'
        ''
        "- Archive: $ffmpegUrl"
        "- SHA-256: $ffmpegSha256"
        '- Build scripts: https://github.com/BtbN/FFmpeg-Builds'
        '- Corresponding FFmpeg source: https://git.ffmpeg.org/ffmpeg.git (release/9.0, commit e47273f4d9)'
        '- License text: FFmpeg-LICENSE.txt'
    ) | Set-Content -LiteralPath (Join-Path $Licenses 'FFmpeg-provenance.md') -Encoding utf8
}

function Add-LinuxFFmpeg([string]$Destination, [string]$Licenses, [string]$DownloadCache) {
    $ffmpegUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-31-13-27/ffmpeg-n9.0.1-11-ge47273f4d9-linux64-lgpl-9.0.tar.xz'
    $ffmpegSha256 = '204fc02692b11249c3e688ad18538ce2939129a1fc6abc32a6b2638a024496cf'
    $ffmpegTar = Join-Path $DownloadCache ([IO.Path]::GetFileName($ffmpegUrl))
    Save-UrlWithHash $ffmpegUrl $ffmpegTar $ffmpegSha256
    $extract = Join-Path $DownloadCache 'ffmpeg-linux-lgpl-9.0'
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    New-Item -ItemType Directory -Force $extract | Out-Null
    & tar -xJf $ffmpegTar -C $extract
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract pinned Linux FFmpeg archive with tar -xJf.' }
    $top = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
    if (-not $top) { throw 'The pinned Linux FFmpeg archive did not contain a top-level folder.' }
    $ffmpegDestination = Join-Path $Destination 'ffmpeg'
    New-Item -ItemType Directory -Force $ffmpegDestination | Out-Null
    foreach ($tool in @('ffmpeg', 'ffprobe')) {
        $source = Join-Path $top.FullName "bin\$tool"
        if (-not (Test-Path -LiteralPath $source)) { throw "The pinned Linux FFmpeg archive did not contain $tool." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $ffmpegDestination $tool) -Force
    }
    $license = Join-Path $top.FullName 'LICENSE.txt'
    if (Test-Path -LiteralPath $license) { Copy-Item -LiteralPath $license -Destination (Join-Path $Licenses 'FFmpeg-LICENSE.txt') -Force }
    @(
        '# Bundled FFmpeg'
        ''
        'The `ffmpeg` folder contains an unmodified LGPL-2.1-or-later static Linux build of FFmpeg'
        '(ffmpeg and ffprobe), launched as separate processes.'
        ''
        "- Archive: $ffmpegUrl"
        "- SHA-256: $ffmpegSha256"
        '- Build scripts: https://github.com/BtbN/FFmpeg-Builds'
        '- Corresponding FFmpeg source: https://git.ffmpeg.org/ffmpeg.git (release/9.0, commit e47273f4d9)'
        '- License text: FFmpeg-LICENSE.txt'
    ) | Set-Content -LiteralPath (Join-Path $Licenses 'FFmpeg-provenance.md') -Encoding utf8
}

Push-Location $root
try {
    $sdkVersion = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $sdkVersion) { throw "The build requires SDK $sdkVersion from global.json." }
    [string[]]$targeting = if ((-not $IsWindows) -and $Runtime -eq 'win-x64') { @('-p:EnableWindowsTargeting=true') } else { @() }
    $worker = Join-Path $root 'src\AudioTranscriber.Worker\AudioTranscriber.Worker.csproj'
    if (-not (Test-Path $worker)) { throw 'The required speaker worker project is missing.' }
    & $dotnet restore $project --locked-mode --nologo @targeting
    if ($LASTEXITCODE -ne 0) { throw "Locked $Runtime restore failed. Run Setup.ps1 after intentional dependency changes." }
    foreach ($item in @($project, $worker)) {
        & $dotnet publish $item --configuration $Configuration --runtime $Runtime --self-contained true `
            --no-restore --output $destination --nologo -p:PublishSingleFile=false @targeting
        if ($LASTEXITCODE -ne 0) { throw "$Runtime publish failed: $item" }
    }
    foreach ($name in @('AudioTranscriber.App', 'AudioTranscriber.Worker')) {
        $runtimeConfig = Get-Content (Join-Path $destination "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
        if (-not $runtimeConfig.runtimeOptions.includedFrameworks) { throw "$name was not published self-contained." }
    }
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $destination -Recurse -Force
    $licenses = Join-Path $destination 'licenses'
    New-Item -ItemType Directory -Force $licenses | Out-Null
    Get-ChildItem (Join-Path $root 'src\AudioTranscriber.Providers\Protocol') -File -Filter 'LICENSE-*' |
        Copy-Item -Destination $licenses -Force
    $modelNotices = Join-Path $licenses 'models'
    New-Item -ItemType Directory -Force $modelNotices | Out-Null
    Get-ChildItem (Join-Path $root 'src\AudioTranscriber.Diarization\Licenses') -File |
        Copy-Item -Destination $modelNotices -Force
    Copy-Item -LiteralPath (Join-Path $root 'src\AudioTranscriber.Storage\NativeSqlite\README.md') `
        -Destination (Join-Path $licenses 'SQLite-provenance.md') -Force
    $downloadCache = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { Join-Path $root '.tools\downloads' }
    New-Item -ItemType Directory -Force $downloadCache | Out-Null
    if ($Runtime -eq 'win-x64') { Add-WindowsFFmpeg $destination $licenses $downloadCache }
    else { Add-LinuxFFmpeg $destination $licenses $downloadCache }
    $inventory = [Collections.Generic.List[string]]::new()
    $packageNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($manifest in Get-ChildItem $destination -File -Filter '*.deps.json') {
        $dependencies = Get-Content $manifest.FullName -Raw | ConvertFrom-Json
        foreach ($library in $dependencies.libraries.PSObject.Properties) {
            if ($library.Value.type -notin @('package','runtimepack')) { continue }
            [void]$packageNames.Add(($library.Name -replace '^runtimepack\.', ''))
        }
    }
    foreach ($package in ($packageNames | Sort-Object)) {
        $id, $version = $package.Split('/')
        $packageDirectory = Join-Path $env:NUGET_PACKAGES "$($id.ToLowerInvariant())\$version"
        $spec = Get-ChildItem $packageDirectory -File -Filter '*.nuspec' -ErrorAction Stop | Select-Object -First 1
        if (-not $spec) { throw "Missing package provenance: $package" }
        $metadata = ([xml](Get-Content $spec.FullName -Raw)).package.metadata
        $inventory.Add("$package`nAuthors: $($metadata.authors)`nCopyright: $($metadata.copyright)`nLicense: $($metadata.license.InnerText)`nLicense URL: $($metadata.licenseUrl)`nRepository: $($metadata.repository.url)`n")
        $packageNotices = Join-Path $licenses "packages\$($id.ToLowerInvariant())\$version"
        New-Item -ItemType Directory -Force $packageNotices | Out-Null
        Copy-Item $spec.FullName -Destination $packageNotices -Force
        Get-ChildItem $packageDirectory -File |
            Where-Object Name -Match '^(license|copying|notice)|third.?party' |
            Copy-Item -Destination $packageNotices -Force
    }
    $inventory | Set-Content -LiteralPath (Join-Path $licenses 'PACKAGE-INVENTORY.txt') -Encoding utf8
    Write-Host "Published $Runtime application to $destination"
    Write-Host 'FFmpeg/FFprobe are bundled in the ffmpeg folder; optional model weights are not bundled.'
}
finally { Pop-Location }

