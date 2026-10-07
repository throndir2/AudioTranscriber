[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Package', 'Publish')][string]$Phase,
    [string]$Tag,
    [string]$Commit,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$EventSha = $env:RELEASE_EVENT_SHA,
    [string]$PublishDirectory,
    [string]$LinuxPublishDirectory,
    [string]$AssetDirectory,
    [string]$ZipPath,
    [string]$ChecksumPath
)

$ErrorActionPreference = 'Stop'
$script:TarAssemblyLoaded = $false

function Get-ReleaseVersion([string]$Value) {
    $identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    $pattern = "\Av(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?<pre>$identifier(?:\.$identifier)*))?\z"
    if ([string]::IsNullOrEmpty($Value) -or $Value.Length -gt 128 -or $Value -cnotmatch $pattern) {
        throw 'A release tag must be vMAJOR.MINOR.PATCH, optionally followed by valid SemVer prerelease identifiers. Build metadata is not supported.'
    }
    return [pscustomobject]@{ Tag = $Value; SemVer = $Value.Substring(1); Prerelease = $Matches.pre.Length -gt 0 }
}

function Get-ReleaseNotes([string]$VersionTag, [string]$Path = (Join-Path $PSScriptRoot '..\CHANGELOG.md')) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "CHANGELOG.md not found at $Path." }
    $lines = [IO.File]::ReadAllLines($Path)
    $start = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -cmatch "^## $([regex]::Escape($VersionTag))(\s|$)") { $start = $i + 1; break }
    }
    if ($start -lt 0) {
        throw "CHANGELOG.md has no '## $VersionTag - YYYY-MM-DD' section. Move the Unreleased entries under that heading and merge it before releasing."
    }
    $section = for ($i = $start; $i -lt $lines.Length -and $lines[$i] -notmatch '^## '; $i++) { $lines[$i] }
    $notes = ($section -join "`n").Trim()
    if (-not $notes) { throw "CHANGELOG.md section for $VersionTag is empty." }
    return $notes
}

function Get-ReleaseBody([string]$VersionTag, [string]$SourceCommit) {
    $semver = $VersionTag.Substring(1)
    return "$(Get-ReleaseMarker $VersionTag $SourceCommit)`n`n$(Get-ReleaseNotes $VersionTag)`n`n---`n`n" +
        "### Install`n`n" +
        "- **Windows x64:** run ``AudioTranscriber-$VersionTag-win-x64-setup.exe`` (per-user, no admin; unsigned, so SmartScreen may ask you to confirm), " +
        "or extract ``AudioTranscriber-$VersionTag-win-x64.zip`` and run ``AudioTranscriber.App.exe``.`n" +
        "- **Ubuntu / Debian:** ``sudo apt install ./audiotranscriber_${semver}_amd64.deb`` (prerequisites are installed automatically).`n" +
        "- **Fedora / openSUSE:** ``sudo dnf install ./audiotranscriber-$semver-1.x86_64.rpm``.`n" +
        "- **Other Linux:** extract ``AudioTranscriber-$VersionTag-linux-x64.tar.gz`` and run ``./install.sh`` (offers to install prerequisites).`n`n" +
        "Installed copies update themselves from inside the app. FFmpeg/FFprobe (LGPL build) are bundled. Optional model weights are downloaded " +
        "from inside the app. See the included README and licenses.`n`nSource commit: $SourceCommit"
}

function Get-CheckedOutCommit([string]$Expected) {
    $head = & git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^[0-9a-f]{40}$') { throw 'Checkout does not identify a commit.' }
    if ($Expected -and $Expected -cne $head) { throw 'Checkout differs from the requested release commit.' }
    return $head
}

function Invoke-ReleaseApi {
    param([string]$Method, [string]$Route, [object]$Body, [switch]$AllowMissing,
          [string]$UploadFile, [string]$ContentType = 'application/json')
    if ($Repository -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9_.-]+\z' -or
        $Repository.Split('/')[1] -in @('.', '..') -or
        [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'A repository and its GITHUB_TOKEN are required.' }
    $authority = if ($UploadFile) { 'uploads.github.com' } else { 'api.github.com' }
    $parameters = @{
        Uri = "https://$authority/repos/$Repository/$Route"
        Method = $Method
        Headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
        UserAgent = 'AudioTranscriber-release'
        MaximumRedirection = 0
        SkipHttpErrorCheck = $true
        TimeoutSec = 300
    }
    if ($UploadFile) { $parameters.InFile = $UploadFile; $parameters.ContentType = $ContentType }
    elseif ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress; $parameters.ContentType = 'application/json' }
    $response = Invoke-WebRequest @parameters
    if ($AllowMissing -and [int]$response.StatusCode -eq 404) { return $null }
    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
        throw "GitHub $Method $Route returned HTTP $([int]$response.StatusCode). No tag or unrelated release was changed."
    }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }
}

function Assert-RemoteReleaseTag([string]$VersionTag, [string]$ExpectedCommit, [string]$ExpectedEventSha) {
    $reference = Invoke-ReleaseApi -Method GET -Route ("git/ref/tags/" + [Uri]::EscapeDataString($VersionTag))
    if ($reference.ref -cne "refs/tags/$VersionTag") { throw 'GitHub returned a different tag reference.' }
    $originalSha = $reference.object.sha
    $target = $reference.object
    for ($depth = 0; $target.type -eq 'tag'; $depth++) {
        if ($depth -ge 8 -or $target.sha -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid or excessively nested annotated tag.' }
        $annotation = Invoke-ReleaseApi -Method GET -Route "git/tags/$($target.sha)"
        $target = $annotation.object
    }
    if ($target.type -ne 'commit' -or $target.sha -cne $ExpectedCommit) {
        throw 'The release tag is missing, moved, or does not point to the checked-out commit.'
    }
    if ($ExpectedEventSha -and $ExpectedEventSha -cne $originalSha -and $ExpectedEventSha -cne $ExpectedCommit) {
        throw 'The tag changed after the push event. Refusing to build or publish a different revision.'
    }
}

function Get-ReleaseMarker([string]$VersionTag, [string]$SourceCommit) { "<!-- audiotranscriber-release:v1 tag=$VersionTag commit=$SourceCommit -->" }

function Get-OwnedRelease([object]$Version, [string]$SourceCommit) {
    $release = Invoke-ReleaseApi -Method GET -Route ("releases/tags/" + [Uri]::EscapeDataString($Version.Tag)) -AllowMissing
    if ($null -eq $release) {
        $candidates = [Collections.Generic.List[object]]::new()
        for ($page = 1; ; $page++) {
            $releases = @(Invoke-ReleaseApi -Method GET -Route "releases?per_page=100&page=$page")
            foreach ($candidate in $releases) { if ($candidate.tag_name -ceq $Version.Tag) { $candidates.Add($candidate) } }
            if ($releases.Count -lt 100) { break }
        }
        if ($candidates.Count -gt 1) { throw 'Multiple releases use this tag. Refusing ambiguous draft ownership.' }
        if ($candidates.Count -eq 0) { return $null }
        $release = $candidates[0]
    }
    $marker = Get-ReleaseMarker $Version.Tag $SourceCommit
    if ($release.tag_name -cne $Version.Tag -or -not ([string]$release.body).Contains($marker) -or [bool]$release.prerelease -ne $Version.Prerelease) {
        throw 'An existing release has different ownership, commit, tag, or prerelease metadata. It will not be replaced or repointed.'
    }
    return $release
}

function Get-AssetNames([string]$VersionTag) {
    $semver = $VersionTag.Substring(1)
    $names = [ordered]@{
        WinZip = "AudioTranscriber-$VersionTag-win-x64.zip"
        WinSetup = "AudioTranscriber-$VersionTag-win-x64-setup.exe"
        LinuxTar = "AudioTranscriber-$VersionTag-linux-x64.tar.gz"
        Deb = "audiotranscriber_${semver}_amd64.deb"
        Rpm = "audiotranscriber-$semver-1.x86_64.rpm"
    }
    [pscustomobject]@{ Zip=$names.WinZip; Checksum="$($names.WinZip).sha256"; WinZip=$names.WinZip; WinSetup=$names.WinSetup; LinuxTar=$names.LinuxTar; Deb=$names.Deb; Rpm=$names.Rpm; Files=@($names.Values + @($names.Values | ForEach-Object { "$_.sha256" })) }
}

function Get-TextHash([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }

function Test-CompleteReleaseAssets([object]$Release, [object]$Names) {
    if ($null -eq $Release) { return $false }
    foreach ($name in $Names.Files) {
        $matches = @($Release.assets | Where-Object name -CEQ $name)
        if ($matches.Count -ne 1 -or $matches[0].state -ne 'uploaded' -or $matches[0].size -le 0) { return $false }
    }
    foreach ($assetName in @($Names.WinZip, $Names.WinSetup, $Names.LinuxTar, $Names.Deb, $Names.Rpm)) {
        $asset = @($Release.assets | Where-Object name -CEQ $assetName)[0]
        $checksum = @($Release.assets | Where-Object name -CEQ "$assetName.sha256")[0]
        if ($asset.digest -cnotmatch '^sha256:(?<hash>[0-9a-f]{64})$') { return $false }
        $hash = $Matches.hash
        $text = "$hash  $assetName`n"
        if ($asset.label -cne "sha256:$hash" -or $checksum.label -cne "sha256:$hash" -or $checksum.digest -cne ("sha256:" + (Get-TextHash $text)) -or $checksum.size -ne [Text.Encoding]::UTF8.GetByteCount($text)) { return $false }
    }
    return $true
}

function Write-ReleaseOutput([string]$Name, [string]$Value) {
    if ($Value.Contains("`n") -or $Value.Contains("`r")) { throw 'Invalid multiline workflow output.' }
    if ($env:GITHUB_OUTPUT) { "$Name=$Value" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}

function Get-ReleasePackageFiles([string]$Directory, [ValidateSet('win-x64','linux-x64')][string]$Runtime) {
    $base = [IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { throw 'Publish directory is missing.' }
    $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($base, $file.FullName).Replace('\', '/')
        if ($relative.StartsWith('docs/', [StringComparison]::Ordinal) -and $file.Extension -ne '.md') { continue }
        if ($Runtime -eq 'win-x64' -and $relative.StartsWith('runtimes/', [StringComparison]::Ordinal) -and -not $relative.StartsWith('runtimes/win-x64/', [StringComparison]::Ordinal) -and -not $relative.StartsWith('runtimes/vulkan/win-x64/', [StringComparison]::Ordinal)) { continue }
        if ($Runtime -eq 'linux-x64' -and $relative.StartsWith('runtimes/', [StringComparison]::Ordinal) -and -not $relative.StartsWith('runtimes/linux-x64/', [StringComparison]::Ordinal) -and -not $relative.StartsWith('runtimes/vulkan/linux-x64/', [StringComparison]::Ordinal)) { continue }
        $bundledFFmpeg = if ($Runtime -eq 'win-x64') { $relative -cmatch '^ffmpeg/(ffmpeg\.exe|ffprobe\.exe|[A-Za-z0-9_.-]+\.dll)$' } else { $relative -cmatch '^ffmpeg/(ffmpeg|ffprobe)$' }
        if ($relative -match '(^|/)(\.tools|\.models|\.git|obj|bin|sessions)(/|$)' -or $file.Name -match '^\.env|^appsettings.*\.json$|^transcript[._-]' -or ($file.Name -match '^(ffmpeg|ffprobe)(\.exe)?$' -and -not $bundledFFmpeg) -or $file.Extension -match '^\.(onnx|bin|gguf|ggml|pt|pth|safetensors|wav|w64|rf64|flac|mp3|aac|aiff?|wma|pcm|pcm16|ogg|m4a|m4b|mp4|mkv|webm|srt|vtt|sqlite3?|db|dpapi|key|pfx|p12|csv)$' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected private data, model, tool, or non-release file in publish output: $relative"
        }
        $files.Add($relative, $file.FullName)
    }
    $required = if ($Runtime -eq 'win-x64') { @('AudioTranscriber.App.exe','AudioTranscriber.App.dll','AudioTranscriber.App.deps.json','AudioTranscriber.App.runtimeconfig.json','AudioTranscriber.Worker.exe','AudioTranscriber.Worker.dll','AudioTranscriber.Worker.deps.json','AudioTranscriber.Worker.runtimeconfig.json','coreclr.dll','hostpolicy.dll','e_sqlite3.dll','sherpa-onnx-c-api.dll','onnxruntime.dll','ffmpeg/ffmpeg.exe','ffmpeg/ffprobe.exe','licenses/FFmpeg-LICENSE.txt','licenses/FFmpeg-provenance.md','README.md','licenses/PACKAGE-INVENTORY.txt','licenses/SQLite-provenance.md','licenses/models/Segmentation-original-MIT.txt','licenses/models/WeSpeaker-NOTICE.txt') } else { @('AudioTranscriber.App','AudioTranscriber.App.dll','AudioTranscriber.App.deps.json','AudioTranscriber.App.runtimeconfig.json','AudioTranscriber.Worker','AudioTranscriber.Worker.dll','AudioTranscriber.Worker.deps.json','AudioTranscriber.Worker.runtimeconfig.json','libcoreclr.so','libhostpolicy.so','libe_sqlite3.so','libsherpa-onnx-c-api.so','libonnxruntime.so','ffmpeg/ffmpeg','ffmpeg/ffprobe','licenses/FFmpeg-LICENSE.txt','licenses/FFmpeg-provenance.md','README.md','licenses/PACKAGE-INVENTORY.txt','licenses/SQLite-provenance.md','licenses/models/Segmentation-original-MIT.txt','licenses/models/WeSpeaker-NOTICE.txt') }
    foreach ($requiredFile in $required) { if (-not $files.ContainsKey($requiredFile)) { throw "Incomplete $Runtime package: $requiredFile is missing." } }
    return ,$files
}

function Add-ZipEntry([IO.Compression.ZipArchive]$Archive, [string]$Name, [string]$Path, [DateTimeOffset]$Timestamp) {
    $entry = $Archive.CreateEntry($Name, [IO.Compression.CompressionLevel]::Optimal); $entry.LastWriteTime = $Timestamp
    $inputFile = [IO.File]::OpenRead($Path)
    try { $outputFile = $entry.Open(); try { $inputFile.CopyTo($outputFile) } finally { $outputFile.Dispose() } } finally { $inputFile.Dispose() }
}
function Add-Checksum([string]$File) {
    $hash = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$File.sha256", "$hash  $([IO.Path]::GetFileName($File))`n", [Text.UTF8Encoding]::new($false)); $hash
}
function New-WindowsZipPackage([object]$Version, [string]$SourceCommit, [string]$Directory, [string]$Destination, [DateTimeOffset]$Timestamp) {
    $files = Get-ReleasePackageFiles $Directory 'win-x64'; $names = Get-AssetNames $Version.Tag; $zip = Join-Path $Destination $names.WinZip; $partial = "$zip.partial"
    $provenance = [ordered]@{ schemaVersion = 1; tag = $Version.Tag; commit = $SourceCommit; runtime = 'win-x64'; sdk = (Get-Content (Join-Path $PSScriptRoot '..\global.json') -Raw | ConvertFrom-Json).sdk.version } | ConvertTo-Json -Compress
    $stream = [IO.File]::Open($partial, [IO.FileMode]::CreateNew)
    try { $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true); try { foreach ($name in ($files.Keys | Sort-Object -CaseSensitive)) { Add-ZipEntry $archive $name $files[$name] $Timestamp }; $entry=$archive.CreateEntry('BUILD-PROVENANCE.json'); $entry.LastWriteTime=$Timestamp; $writer=[IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false)); try { $writer.Write($provenance) } finally { $writer.Dispose() } } finally { $archive.Dispose() }; $stream.Dispose(); [IO.File]::Move($partial, $zip) } finally { $stream.Dispose(); if ([IO.File]::Exists($partial)) { [IO.File]::Delete($partial) } }
    [void](Add-Checksum $zip); $zip
}
function Get-NsisPath([string]$ToolsRoot) {
    $command = Get-Command makensis -ErrorAction SilentlyContinue; if ($command) { return $command.Source }
    $nsisRoot = Join-Path $ToolsRoot 'nsis'; $makensis = Join-Path $nsisRoot 'tools\Bin\makensis.exe'; if (Test-Path -LiteralPath $makensis) { return $makensis }
    $url = 'https://api.nuget.org/v3-flatcontainer/nsis-tool/3.13.0/nsis-tool.3.13.0.nupkg'; $sha256 = '11b9880836e6d25389a63d2d3f547fd8638c094e5bed4b7ae436e252f8511e3c'
    $downloads = Join-Path $ToolsRoot 'downloads'; New-Item -ItemType Directory -Force $downloads | Out-Null; $package = Join-Path $downloads 'nsis-tool.3.13.0.nupkg'
    $needsDownload = -not (Test-Path -LiteralPath $package); if (-not $needsDownload) { $needsDownload = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $sha256 }
    if ($needsDownload) { Write-Host 'Downloading pinned portable NSIS tool package.'; $previousProgress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'; try { Invoke-WebRequest -Uri $url -OutFile $package } finally { $ProgressPreference = $previousProgress } }
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $sha256) { throw 'Downloaded NSIS package does not match its pinned SHA-256.' }
    if (Test-Path -LiteralPath $nsisRoot) { Remove-Item -LiteralPath $nsisRoot -Recurse -Force }; New-Item -ItemType Directory -Force $nsisRoot | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $nsisRoot); if (-not (Test-Path -LiteralPath $makensis)) { throw 'The portable NSIS package did not contain makensis.exe.' }; $makensis
}
function New-WindowsSetupPackage([object]$Version, [string]$SourceDirectory, [string]$Destination, [DateTimeOffset]$Timestamp) {
    $names = Get-AssetNames $Version.Tag; $installer = Join-Path $Destination $names.WinSetup; $nsis = Get-NsisPath (Join-Path $PSScriptRoot '..\.tools')
    $source = [IO.Path]::GetFullPath($SourceDirectory); $sizeKb = [math]::Max(1, [math]::Ceiling(((Get-ChildItem -LiteralPath $source -Recurse -File | Measure-Object Length -Sum).Sum) / 1kb))
    $script = Join-Path $PSScriptRoot '..\installer\windows\AudioTranscriber.nsi'
    # makensis on Linux only accepts "-" switches ("/" is read as a path); Windows accepts both.
    & $nsis -V2 "-DAPP_VERSION=$($Version.SemVer)" "-DSOURCE_DIR=$source" "-DOUT_FILE=$installer" "-DICON_FILE=$(Join-Path $PSScriptRoot '..\src\AudioTranscriber.App\Assets\AppIcon.ico')" "-DEST_SIZE_KB=$sizeKb" $script
    if ($LASTEXITCODE -ne 0) { throw 'NSIS failed to build the Windows setup executable.' }
    (Get-Item -LiteralPath $installer).LastWriteTimeUtc = $Timestamp.UtcDateTime; [void](Add-Checksum $installer); $installer
}
function Ensure-TarAssembly { if (-not $script:TarAssemblyLoaded) { Add-Type -AssemblyName System.Formats.Tar; $script:TarAssemblyLoaded = $true } }
function New-TarEntry([System.Formats.Tar.TarWriter]$Writer, [string]$Name, [System.Formats.Tar.TarEntryType]$Type, [int]$Mode, [DateTimeOffset]$Timestamp, [string]$Source, [string]$LinkName) {
    $entry = [System.Formats.Tar.GnuTarEntry]::new($Type, $Name); $entry.Mode = $Mode; $entry.Uid = 0; $entry.Gid = 0; $entry.ModificationTime = $Timestamp; if ($LinkName) { $entry.LinkName = $LinkName }; if ($Source) { $entry.DataStream = [IO.File]::OpenRead($Source) }
    try { $Writer.WriteEntry($entry) } finally { if ($entry.DataStream) { $entry.DataStream.Dispose() } }
}
function New-GzipTar([string]$Path, [scriptblock]$Content) {
    Ensure-TarAssembly; $file = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try { $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal, $true); try { $writer = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Gnu, $true); try { & $Content $writer } finally { $writer.Dispose() } } finally { $gzip.Dispose() } } finally { $file.Dispose() }
}
function Get-LinuxMode([string]$Relative) { if ($Relative -in @('AudioTranscriber.App','AudioTranscriber.Worker','createdump','ffmpeg/ffmpeg','ffmpeg/ffprobe') -or $Relative.EndsWith('.sh')) { 493 } else { 420 } }
function Write-DesktopFile([string]$Path, [string]$Exec, [string]$Icon) {
    $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\installer\linux\audiotranscriber.desktop') -Raw
    $template = $template -replace '(?m)^Exec=.*$', "Exec=$Exec" -replace '(?m)^Icon=.*$', "Icon=$Icon"
    Set-Content -LiteralPath $Path -Value $template -Encoding utf8NoBOM
}
function Write-LinuxMetainfo([string]$Path, [object]$Version, [DateTimeOffset]$Timestamp) {
    $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\installer\linux\io.github.throndir2.AudioTranscriber.metainfo.xml.in') -Raw
    $text = $template.Replace('@VERSION@', $Version.SemVer).Replace('@DATE@', $Timestamp.ToString('yyyy-MM-dd'))
    Set-Content -LiteralPath $Path -Value $text -Encoding utf8NoBOM
}
function Copy-LinuxIcons([string]$RootFs) {
    foreach ($icon in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\installer\linux\icons') -File -Filter 'audiotranscriber-*.png') {
        if ($icon.BaseName -notmatch 'audiotranscriber-(\d+)$') { continue }
        $size = $Matches[1]
        $targetDir = Join-Path $RootFs "usr\share\icons\hicolor\${size}x${size}\apps"
        New-Item -ItemType Directory -Force $targetDir | Out-Null
        Copy-Item -LiteralPath $icon.FullName -Destination (Join-Path $targetDir 'audiotranscriber.png') -Force
    }
}
function Add-LinuxDesktopMetadata([string]$RootFs, [object]$Version, [DateTimeOffset]$Timestamp) {
    New-Item -ItemType Directory -Force (Join-Path $RootFs 'usr\share\applications') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $RootFs 'usr\share\metainfo') | Out-Null
    Write-DesktopFile (Join-Path $RootFs 'usr\share\applications\audiotranscriber.desktop') 'audiotranscriber %U' 'audiotranscriber'
    Write-LinuxMetainfo (Join-Path $RootFs 'usr\share\metainfo\io.github.throndir2.AudioTranscriber.metainfo.xml') $Version $Timestamp
    Copy-LinuxIcons $RootFs
}
function Copy-LinuxPayload([string]$PublishDirectory, [string]$Destination, [string[]]$ExtraFiles) {
    $files = Get-ReleasePackageFiles $PublishDirectory 'linux-x64'; if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }; New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($name in $files.Keys) { $target = Join-Path $Destination ($name -replace '/', [IO.Path]::DirectorySeparatorChar); New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null; Copy-Item -LiteralPath $files[$name] -Destination $target -Force }
    foreach ($extra in $ExtraFiles) { if (Test-Path -LiteralPath $extra -PathType Container) { Copy-Item -LiteralPath $extra -Destination (Join-Path $Destination (Split-Path $extra -Leaf)) -Recurse -Force } else { Copy-Item -LiteralPath $extra -Destination (Join-Path $Destination (Split-Path $extra -Leaf)) -Force } }
    foreach ($rel in @('AudioTranscriber.App','AudioTranscriber.Worker','createdump','ffmpeg/ffmpeg','ffmpeg/ffprobe','install.sh','uninstall.sh')) { $path = Join-Path $Destination ($rel -replace '/', [IO.Path]::DirectorySeparatorChar); if ((Test-Path -LiteralPath $path -PathType Leaf) -and -not $IsWindows) { chmod 755 $path } }
    return ,$files
}
function New-LinuxTarPackage([object]$Version, [string]$SourceCommit, [string]$Directory, [string]$Destination, [DateTimeOffset]$Timestamp) {
    $names = Get-AssetNames $Version.Tag; $tarPath = Join-Path $Destination $names.LinuxTar; $installer = Join-Path $PSScriptRoot '..\installer\linux\install.sh'; $uninstaller = Join-Path $PSScriptRoot '..\installer\linux\uninstall.sh'; $desktop = Join-Path $PSScriptRoot '..\installer\linux\audiotranscriber.desktop'; $icons = Join-Path $PSScriptRoot '..\installer\linux\icons'
    $work = Join-Path $Destination '.linux-tar-root'; New-Item -ItemType Directory -Force $work | Out-Null; $metainfo = Join-Path $work 'io.github.throndir2.AudioTranscriber.metainfo.xml'; Write-LinuxMetainfo $metainfo $Version $Timestamp; $payload = Join-Path $work 'AudioTranscriber'; [void](Copy-LinuxPayload $Directory $payload @($installer, $uninstaller, $desktop, $metainfo, $icons))
    [IO.File]::WriteAllText((Join-Path $payload 'BUILD-PROVENANCE.json'), ([ordered]@{ schemaVersion=1; tag=$Version.Tag; commit=$SourceCommit; runtime='linux-x64'; sdk=(Get-Content (Join-Path $PSScriptRoot '..\global.json') -Raw | ConvertFrom-Json).sdk.version } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    New-GzipTar $tarPath { param($writer) New-TarEntry $writer 'AudioTranscriber/' ([System.Formats.Tar.TarEntryType]::Directory) 493 $Timestamp $null $null; foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) { $rel = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/'); New-TarEntry $writer "AudioTranscriber/$rel" ([System.Formats.Tar.TarEntryType]::RegularFile) (Get-LinuxMode $rel) $Timestamp $file.FullName $null } }
    Remove-Item -LiteralPath $work -Recurse -Force; [void](Add-Checksum $tarPath); $tarPath
}

function New-ArArchive([string]$Path, [string[]]$Files) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try { $header = [Text.Encoding]::ASCII.GetBytes("!<arch>`n"); $stream.Write($header, 0, $header.Length); foreach ($file in $Files) { $name = [IO.Path]::GetFileName($file) + '/'; if ($name.Length -gt 16) { throw "ar member name is too long: $name" }; $bytes = [IO.File]::ReadAllBytes($file); $member = ("{0,-16}{1,-12}{2,-6}{3,-6}{4,-8}{5,-10}```n" -f $name, 0, 0, 0, '100644', $bytes.Length); $memberBytes = [Text.Encoding]::ASCII.GetBytes($member); if ($memberBytes.Length -ne 60) { throw 'Invalid ar header length.' }; $stream.Write($memberBytes, 0, $memberBytes.Length); $stream.Write($bytes, 0, $bytes.Length); if (($bytes.Length % 2) -eq 1) { $stream.WriteByte(10) } } } finally { $stream.Dispose() }
}
function New-DebianPackage([object]$Version, [string]$SourceCommit, [string]$Directory, [string]$Destination, [DateTimeOffset]$Timestamp) {
    $names = Get-AssetNames $Version.Tag; $deb = Join-Path $Destination $names.Deb; $work = Join-Path $Destination '.deb-work'; $rootfs = Join-Path $work 'rootfs'
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }; New-Item -ItemType Directory -Force $rootfs | Out-Null
    $payloadDir = Join-Path $rootfs 'opt\audiotranscriber'; [void](Copy-LinuxPayload $Directory $payloadDir @())
    [IO.File]::WriteAllText((Join-Path $payloadDir 'BUILD-PROVENANCE.json'), ([ordered]@{ schemaVersion=1; tag=$Version.Tag; commit=$SourceCommit; runtime='linux-x64'; sdk=(Get-Content (Join-Path $PSScriptRoot '..\global.json') -Raw | ConvertFrom-Json).sdk.version } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Force (Join-Path $rootfs 'usr\bin') | Out-Null; Add-LinuxDesktopMetadata $rootfs $Version $Timestamp
    $installedSize = [math]::Max(1, [math]::Ceiling(((Get-ChildItem -LiteralPath $rootfs -Recurse -File | Measure-Object Length -Sum).Sum) / 1kb))
    $depends = 'libc6, libstdc++6, libgcc-s1, libicu78 | libicu76 | libicu74 | libicu72 | libicu70 | libicu-dev, libfontconfig1, libx11-6, libice6, libsm6, libxext6, libxrandr2, libxi6, libxcursor1, libgomp1, libpulse0, pulseaudio-utils'
    $controlDir = Join-Path $work 'control'; New-Item -ItemType Directory -Force $controlDir | Out-Null
    @('Package: audiotranscriber',"Version: $(($Version.SemVer -replace '\+.*$', '') -replace '-', '~')",'Architecture: amd64','Maintainer: throndir2 <throndir2@users.noreply.github.com>',"Installed-Size: $installedSize",'Section: sound','Priority: optional','Homepage: https://github.com/throndir2/AudioTranscriber',"Depends: $depends",'Recommends: libvulkan1, pipewire-pulse | pulseaudio','Description: local audio recording and transcription desktop app',' AudioTranscriber records local audio and produces searchable transcripts with local models.') | Set-Content -LiteralPath (Join-Path $controlDir 'control') -Encoding utf8NoBOM
    foreach ($scriptName in @('postinst','postrm')) { [IO.File]::WriteAllText((Join-Path $controlDir $scriptName), "#!/bin/sh`nset -e`nif command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi`nif command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true; fi`nexit 0`n", [Text.UTF8Encoding]::new($false)) }
    $controlTar = Join-Path $work 'control.tar.gz'; New-GzipTar $controlTar { param($writer) foreach ($name in @('control','postinst','postrm')) { $mode = if ($name -eq 'control') { 420 } else { 493 }; New-TarEntry $writer "./$name" ([System.Formats.Tar.TarEntryType]::RegularFile) $mode $Timestamp (Join-Path $controlDir $name) $null } }
    $dataTar = Join-Path $work 'data.tar.gz'; New-GzipTar $dataTar { param($writer) $dirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal); foreach ($dir in @('./opt/','./opt/audiotranscriber/','./usr/','./usr/bin/','./usr/share/','./usr/share/applications/','./usr/share/icons/','./usr/share/icons/hicolor/','./usr/share/icons/hicolor/256x256/','./usr/share/icons/hicolor/256x256/apps/')) { if ($dirs.Add($dir)) { New-TarEntry $writer $dir ([System.Formats.Tar.TarEntryType]::Directory) 493 $Timestamp $null $null } }; foreach ($directory in Get-ChildItem -LiteralPath $rootfs -Recurse -Directory | Sort-Object FullName) { $dirRel = './' + [IO.Path]::GetRelativePath($rootfs, $directory.FullName).Replace('\','/') + '/'; if ($dirs.Add($dirRel)) { New-TarEntry $writer $dirRel ([System.Formats.Tar.TarEntryType]::Directory) 493 $Timestamp $null $null } }; foreach ($file in Get-ChildItem -LiteralPath $rootfs -Recurse -File | Sort-Object FullName) { $rel = './' + [IO.Path]::GetRelativePath($rootfs, $file.FullName).Replace('\','/'); $payloadRel = if ($rel.StartsWith('./opt/audiotranscriber/')) { $rel.Substring('./opt/audiotranscriber/'.Length) } else { $rel }; New-TarEntry $writer $rel ([System.Formats.Tar.TarEntryType]::RegularFile) (Get-LinuxMode $payloadRel) $Timestamp $file.FullName $null }; New-TarEntry $writer './usr/bin/audiotranscriber' ([System.Formats.Tar.TarEntryType]::SymbolicLink) 511 $Timestamp $null '/opt/audiotranscriber/AudioTranscriber.App' }
    $debianBinary = Join-Path $work 'debian-binary'; [IO.File]::WriteAllText($debianBinary, "2.0`n", [Text.UTF8Encoding]::new($false)); New-ArArchive $deb @($debianBinary, $controlTar, $dataTar); Remove-Item -LiteralPath $work -Recurse -Force; [void](Add-Checksum $deb); $deb
}
function New-RpmPackage([object]$Version, [string]$SourceCommit, [string]$Directory, [string]$Destination) {
    $rpmbuild = Get-Command rpmbuild -ErrorAction SilentlyContinue; if (-not $rpmbuild) { Write-Warning 'rpmbuild was not found; skipping RPM package on this host.'; return $null }
    $names = Get-AssetNames $Version.Tag; $top = Join-Path $Destination '.rpmbuild'; $staging = Join-Path $top 'staging'; if (Test-Path -LiteralPath $top) { Remove-Item -LiteralPath $top -Recurse -Force }
    foreach ($dir in @('BUILD','BUILDROOT','RPMS','SOURCES','SPECS','SRPMS')) { New-Item -ItemType Directory -Force (Join-Path $top $dir) | Out-Null }
    $rootfs = Join-Path $staging 'rootfs'; $payloadDir = Join-Path $rootfs 'opt\audiotranscriber'; [void](Copy-LinuxPayload $Directory $payloadDir @())
    [IO.File]::WriteAllText((Join-Path $payloadDir 'BUILD-PROVENANCE.json'), ([ordered]@{ schemaVersion=1; tag=$Version.Tag; commit=$SourceCommit; runtime='linux-x64'; sdk=(Get-Content (Join-Path $PSScriptRoot '..\global.json') -Raw | ConvertFrom-Json).sdk.version } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Force (Join-Path $rootfs 'usr\bin') | Out-Null; Add-LinuxDesktopMetadata $rootfs $Version ([DateTimeOffset]::UtcNow)
    [IO.File]::WriteAllText((Join-Path $rootfs 'usr\bin\audiotranscriber'), ('#!/bin/sh' + "`n" + 'exec /opt/audiotranscriber/AudioTranscriber.App "$@"' + "`n"), [Text.UTF8Encoding]::new($false))
    $spec = Join-Path $top 'SPECS\audiotranscriber.spec'; $rootForSpec = $rootfs.Replace('\','/')
    @"
%define debug_package %{nil}
%define _build_id_links none
%global __os_install_post %{nil}
Name: audiotranscriber
Version: $(($Version.SemVer -replace '\+.*$', '') -replace '-', '~')
Release: 1
Summary: Local audio recording and transcription desktop app
License: Proprietary and third-party notices
URL: https://github.com/throndir2/AudioTranscriber
BuildArch: x86_64
# The bundled .NET runtime, ONNX Runtime, FFmpeg etc. are private to /opt; only the system libraries below are required.
AutoReqProv: no
Requires: glibc, libstdc++, libgcc, libicu, fontconfig, libX11, libICE, libSM, libXext, libXrandr, libXi, libXcursor, pulseaudio-libs, pulseaudio-utils, libgomp
Recommends: vulkan-loader, pipewire-pulseaudio

%description
AudioTranscriber records local audio and produces searchable transcripts with local models.

%prep
%build
%install
mkdir -p %{buildroot}
cp -a "$rootForSpec"/. %{buildroot}/
chmod 755 %{buildroot}/opt/audiotranscriber/AudioTranscriber.App %{buildroot}/opt/audiotranscriber/AudioTranscriber.Worker || true
chmod 755 %{buildroot}/opt/audiotranscriber/ffmpeg/ffmpeg %{buildroot}/opt/audiotranscriber/ffmpeg/ffprobe || true
chmod 755 %{buildroot}/usr/bin/audiotranscriber

%post
if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true; fi

%postun
if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true; fi

%files
/opt/audiotranscriber
/usr/bin/audiotranscriber
/usr/share/applications/audiotranscriber.desktop
/usr/share/icons/hicolor
/usr/share/metainfo/io.github.throndir2.AudioTranscriber.metainfo.xml
"@ | Set-Content -LiteralPath $spec -Encoding utf8NoBOM
    & $rpmbuild.Source -bb --define "_topdir $($top.Replace('\','/'))" $spec; if ($LASTEXITCODE -ne 0) { throw 'rpmbuild failed.' }
    $built = Get-ChildItem -LiteralPath (Join-Path $top 'RPMS') -Recurse -File -Filter '*.rpm' | Select-Object -First 1; if (-not $built) { throw 'rpmbuild did not produce an RPM.' }
    $rpm = Join-Path $Destination $names.Rpm; Copy-Item -LiteralPath $built.FullName -Destination $rpm -Force; Remove-Item -LiteralPath $top -Recurse -Force; [void](Add-Checksum $rpm); $rpm
}

function New-ReleasePackage([object]$Version, [string]$SourceCommit, [string]$WindowsDirectory, [string]$LinuxDirectory, [string]$Destination) {
    $destinationPath = [IO.Path]::GetFullPath($Destination); [void][IO.Directory]::CreateDirectory($destinationPath); $names = Get-AssetNames $Version.Tag
    foreach ($name in $names.Files) { if (Test-Path -LiteralPath (Join-Path $destinationPath $name)) { throw 'Release assets already exist locally; refusing to overwrite them.' } }
    $timestampText = & git show -s --format=%cI $SourceCommit; if ($LASTEXITCODE -ne 0) { throw 'Could not read the source commit timestamp.' }; $timestamp = [DateTimeOffset]::Parse($timestampText).ToUniversalTime()
    [void](New-WindowsZipPackage $Version $SourceCommit $WindowsDirectory $destinationPath $timestamp)
    [void](New-WindowsSetupPackage $Version $WindowsDirectory $destinationPath $timestamp)
    if ([string]::IsNullOrWhiteSpace($LinuxDirectory)) { $LinuxDirectory = $WindowsDirectory }
    [void](New-LinuxTarPackage $Version $SourceCommit $LinuxDirectory $destinationPath $timestamp)
    [void](New-DebianPackage $Version $SourceCommit $LinuxDirectory $destinationPath $timestamp)
    [void](New-RpmPackage $Version $SourceCommit $LinuxDirectory $destinationPath)
    Write-ReleaseOutput 'asset-directory' $destinationPath; Write-ReleaseOutput 'asset_directory' $destinationPath; Write-ReleaseOutput 'zip' (Join-Path $destinationPath $names.WinZip); Write-ReleaseOutput 'checksum' (Join-Path $destinationPath "$($names.WinZip).sha256")
    Get-ChildItem -LiteralPath $destinationPath -File | Where-Object Name -In $names.Files | Sort-Object Name
}
function Test-LocalAssetSet([object]$Version, [string]$Directory) {
    $names = Get-AssetNames $Version.Tag
    foreach ($name in $names.Files) { $path = Join-Path $Directory $name; if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release asset is missing: $name" } }
    foreach ($assetName in @($names.WinZip, $names.WinSetup, $names.LinuxTar, $names.Deb, $names.Rpm)) { $path = Join-Path $Directory $assetName; $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); if ([IO.File]::ReadAllText("$path.sha256") -cne "$hash  $assetName`n") { throw "Checksum does not match $assetName." } }
}
function Get-AssetContentType([string]$Path) { switch -Regex ([IO.Path]::GetFileName($Path)) { '\.zip$' { 'application/zip'; break } '\.exe$' { 'application/vnd.microsoft.portable-executable'; break } '\.tar\.gz$' { 'application/gzip'; break } '\.deb$' { 'application/vnd.debian.binary-package'; break } '\.rpm$' { 'application/x-rpm'; break } '\.sha256$' { 'text/plain'; break } default { 'application/octet-stream' } } }
function Publish-OwnedRelease([object]$Version, [string]$SourceCommit, [string]$ArchivePath, [string]$HashPath, [string]$AssetDirectory) {
    Assert-RemoteReleaseTag $Version.Tag $SourceCommit ''; $names = Get-AssetNames $Version.Tag; $release = Get-OwnedRelease $Version $SourceCommit
    if (-not (Test-CompleteReleaseAssets $release $names)) {
        if ($null -ne $release -and -not $release.draft) { throw 'The published release has incomplete or inconsistent assets. Published assets will not be replaced.' }
        if ([string]::IsNullOrWhiteSpace($AssetDirectory)) { if ($ArchivePath -and $HashPath) { $AssetDirectory = Split-Path ([IO.Path]::GetFullPath($ArchivePath)) -Parent } else { throw 'The release asset directory is required.' } }
        Test-LocalAssetSet $Version $AssetDirectory
        $zipPath = Join-Path $AssetDirectory $names.WinZip; $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try { $entry = $archive.GetEntry('BUILD-PROVENANCE.json'); if ($null -eq $entry) { throw 'Release ZIP has no build provenance.' }; $reader = [IO.StreamReader]::new($entry.Open()); try { $provenance = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }; if ($provenance.tag -cne $Version.Tag -or $provenance.commit -cne $SourceCommit -or $provenance.runtime -cne 'win-x64') { throw 'Release ZIP identifies a different tag, commit, or platform.' } } finally { $archive.Dispose() }
        if ($null -eq $release) { $release = Invoke-ReleaseApi -Method POST -Route 'releases' -Body @{ tag_name = $Version.Tag; target_commitish = $SourceCommit; name = "AudioTranscriber $($Version.Tag)"; draft = $true; prerelease = $Version.Prerelease; make_latest = 'false'; body = Get-ReleaseBody $Version.Tag $SourceCommit } }
        foreach ($asset in @($release.assets | Where-Object { $_.name -in $names.Files })) { Invoke-ReleaseApi -Method DELETE -Route "releases/assets/$([long]$asset.id)" | Out-Null }
        foreach ($assetName in $names.Files) { $file = Join-Path $AssetDirectory $assetName; $labelHash = if ($assetName.EndsWith('.sha256', [StringComparison]::Ordinal)) { $target = $assetName.Substring(0, $assetName.Length - '.sha256'.Length); (Get-FileHash -LiteralPath (Join-Path $AssetDirectory $target) -Algorithm SHA256).Hash.ToLowerInvariant() } else { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }; $name = [Uri]::EscapeDataString($assetName); $label = [Uri]::EscapeDataString("sha256:$labelHash"); Invoke-ReleaseApi -Method POST -Route "releases/$([long]$release.id)/assets?name=$name&label=$label" -UploadFile $file -ContentType (Get-AssetContentType $file) | Out-Null }
        $release = Get-OwnedRelease $Version $SourceCommit; if (-not (Test-CompleteReleaseAssets $release $names)) { throw 'GitHub did not confirm all uploaded asset digests. The draft is retained for a rerun.' }
    }
    Assert-RemoteReleaseTag $Version.Tag $SourceCommit ''
    if ($release.draft) { $release = Invoke-ReleaseApi -Method PATCH -Route "releases/$([long]$release.id)" -Body @{ draft = $false; prerelease = $Version.Prerelease; make_latest = (-not $Version.Prerelease).ToString().ToLowerInvariant() } }
    Assert-RemoteReleaseTag $Version.Tag $SourceCommit ''; Write-Output "Release ready: $($release.html_url)"
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $Phase) { throw 'Choose Prepare, Package, or Publish.' }
$version = Get-ReleaseVersion $Tag; $sourceCommit = Get-CheckedOutCommit $Commit
switch ($Phase) {
    'Prepare' { [void](Get-ReleaseNotes $version.Tag); Assert-RemoteReleaseTag $version.Tag $sourceCommit $EventSha; $release = Get-OwnedRelease $version $sourceCommit; $complete = Test-CompleteReleaseAssets $release (Get-AssetNames $version.Tag); if ($null -ne $release -and -not $release.draft -and -not $complete) { throw 'An inconsistent published release requires explicit intervention; it will not be rebuilt over.' }; Write-ReleaseOutput 'commit' $sourceCommit; Write-ReleaseOutput 'needs-build' (-not $complete).ToString().ToLowerInvariant(); Write-ReleaseOutput 'needs-publish' ($null -eq $release -or [bool]$release.draft).ToString().ToLowerInvariant(); if ($complete) { Write-Output 'Matching release assets already verified; SDK setup and build are skipped.' } }
    'Package' { New-ReleasePackage $version $sourceCommit $PublishDirectory $LinuxPublishDirectory $AssetDirectory | Format-Table -AutoSize }
    'Publish' { Publish-OwnedRelease $version $sourceCommit $ZipPath $ChecksumPath $AssetDirectory }
}










