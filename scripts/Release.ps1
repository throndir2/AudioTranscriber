[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Package', 'Publish')][string]$Phase,
    [string]$Tag,
    [string]$Commit,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$EventSha = $env:RELEASE_EVENT_SHA,
    [string]$PublishDirectory,
    [string]$AssetDirectory,
    [string]$ZipPath,
    [string]$ChecksumPath
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseVersion([string]$Value) {
    $identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    $pattern = "\Av(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?<pre>$identifier(?:\.$identifier)*))?\z"
    if ([string]::IsNullOrEmpty($Value) -or $Value.Length -gt 128 -or $Value -cnotmatch $pattern) {
        throw 'A release tag must be vMAJOR.MINOR.PATCH, optionally followed by valid SemVer prerelease identifiers. Build metadata is not supported.'
    }
    return [pscustomobject]@{ Tag = $Value; Prerelease = $Matches.pre.Length -gt 0 }
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

function Get-ReleaseMarker([string]$VersionTag, [string]$SourceCommit) {
    return "<!-- audiotranscriber-release:v1 tag=$VersionTag commit=$SourceCommit -->"
}

function Get-OwnedRelease([object]$Version, [string]$SourceCommit) {
    $release = Invoke-ReleaseApi -Method GET -Route ("releases/tags/" + [Uri]::EscapeDataString($Version.Tag)) -AllowMissing
    if ($null -eq $release) {
        # GitHub's tag endpoint omits drafts, including the draft we just uploaded.
        $candidates = [Collections.Generic.List[object]]::new()
        for ($page = 1; ; $page++) {
            $releases = @(Invoke-ReleaseApi -Method GET -Route "releases?per_page=100&page=$page")
            foreach ($candidate in $releases) {
                if ($candidate.tag_name -ceq $Version.Tag) { $candidates.Add($candidate) }
            }
            if ($releases.Count -lt 100) { break }
        }
        if ($candidates.Count -gt 1) { throw 'Multiple releases use this tag. Refusing ambiguous draft ownership.' }
        if ($candidates.Count -eq 0) { return $null }
        $release = $candidates[0]
    }
    $marker = Get-ReleaseMarker $Version.Tag $SourceCommit
    if ($release.tag_name -cne $Version.Tag -or -not ([string]$release.body).Contains($marker) -or
        [bool]$release.prerelease -ne $Version.Prerelease) {
        throw 'An existing release has different ownership, commit, tag, or prerelease metadata. It will not be replaced or repointed.'
    }
    return $release
}

function Get-AssetNames([string]$VersionTag) {
    $name = "AudioTranscriber-$VersionTag-win-x64.zip"
    return [pscustomobject]@{ Zip = $name; Checksum = "$name.sha256" }
}

function Get-TextHash([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function Test-CompleteReleaseAssets([object]$Release, [object]$Names) {
    if ($null -eq $Release) { return $false }
    $zip = @($Release.assets | Where-Object name -CEQ $Names.Zip)
    $checksum = @($Release.assets | Where-Object name -CEQ $Names.Checksum)
    if ($zip.Count -ne 1 -or $checksum.Count -ne 1) { return $false }
    if ($zip[0].state -ne 'uploaded' -or $checksum[0].state -ne 'uploaded' -or $zip[0].size -le 0 -or
        $zip[0].digest -cnotmatch '^sha256:(?<hash>[0-9a-f]{64})$') { return $false }
    $hash = $Matches.hash
    $text = "$hash  $($Names.Zip)`n"
    return $zip[0].label -ceq "sha256:$hash" -and $checksum[0].label -ceq "sha256:$hash" -and
        $checksum[0].digest -ceq ("sha256:" + (Get-TextHash $text)) -and
        $checksum[0].size -eq [Text.Encoding]::UTF8.GetByteCount($text)
}

function Write-ReleaseOutput([string]$Name, [string]$Value) {
    if ($Value.Contains("`n") -or $Value.Contains("`r")) { throw 'Invalid multiline workflow output.' }
    if ($env:GITHUB_OUTPUT) { "$Name=$Value" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}

function Get-ReleasePackageFiles([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { throw 'Publish directory is missing.' }
    $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($base, $file.FullName).Replace('\', '/')
        # Keep documentation, not the public benchmark's verbatim transcripts; ship only the requested native architecture.
        if ($relative.StartsWith('docs/', [StringComparison]::Ordinal) -and $file.Extension -ne '.md') { continue }
        if ($relative.StartsWith('runtimes/', [StringComparison]::Ordinal) -and
            -not $relative.StartsWith('runtimes/win-x64/', [StringComparison]::Ordinal)) { continue }
        if ($relative -match '(^|/)(\.tools|\.models|\.git|obj|bin|sessions)(/|$)' -or
            $file.Name -match '^\.env|^appsettings.*\.json$|^transcript[._-]|^(ffmpeg|ffprobe)(\.exe)?$' -or
            $file.Extension -match '^\.(onnx|bin|gguf|ggml|pt|pth|safetensors|wav|w64|rf64|flac|mp3|aac|aiff?|wma|pcm|pcm16|ogg|m4a|m4b|mp4|mkv|webm|srt|vtt|sqlite3?|db|dpapi|key|pfx|p12|csv)$' -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected private data, model, tool, or non-release file in publish output: $relative"
        }
        $files.Add($relative, $file.FullName)
    }
    foreach ($required in @(
        'AudioTranscriber.App.exe','AudioTranscriber.App.dll','AudioTranscriber.App.deps.json','AudioTranscriber.App.runtimeconfig.json',
        'AudioTranscriber.Worker.exe','AudioTranscriber.Worker.dll','AudioTranscriber.Worker.deps.json','AudioTranscriber.Worker.runtimeconfig.json',
        'coreclr.dll','hostpolicy.dll','wpfgfx_cor3.dll','e_sqlite3.dll','sherpa-onnx-c-api.dll','onnxruntime.dll',
        'runtimes/win-x64/whisper.dll','README.md','licenses/PACKAGE-INVENTORY.txt','licenses/SQLite-provenance.md',
        'licenses/models/Segmentation-original-MIT.txt','licenses/models/WeSpeaker-NOTICE.txt'
    )) {
        if (-not $files.ContainsKey($required)) { throw "Incomplete Windows package: $required is missing." }
    }
    $sourceSqlite = Join-Path $PSScriptRoot '..\src\AudioTranscriber.Storage\NativeSqlite\e_sqlite3.dll'
    if ((Get-FileHash $sourceSqlite).Hash -ne (Get-FileHash $files['e_sqlite3.dll']).Hash) {
        throw 'Published SQLite does not match the pinned repository binary.'
    }
    return ,$files
}

function New-ReleasePackage([object]$Version, [string]$SourceCommit, [string]$Directory, [string]$Destination) {
    $files = Get-ReleasePackageFiles $Directory
    $names = Get-AssetNames $Version.Tag
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    [void][IO.Directory]::CreateDirectory($destinationPath)
    $zip = Join-Path $destinationPath $names.Zip
    $checksum = Join-Path $destinationPath $names.Checksum
    if ((Test-Path $zip) -or (Test-Path $checksum)) { throw 'Release assets already exist locally; refusing to overwrite them.' }
    $timestampText = & git show -s --format=%cI $SourceCommit
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the source commit timestamp.' }
    $timestamp = [DateTimeOffset]::Parse($timestampText).ToUniversalTime()
    $provenance = [ordered]@{
        schemaVersion = 1; tag = $Version.Tag; commit = $SourceCommit; runtime = 'win-x64'
        sdk = (Get-Content (Join-Path $PSScriptRoot '..\global.json') -Raw | ConvertFrom-Json).sdk.version
    } | ConvertTo-Json -Compress
    $partial = "$zip.partial"
    $stream = [IO.File]::Open($partial, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in ($files.Keys | Sort-Object -CaseSensitive)) {
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $inputFile = [IO.File]::OpenRead($files[$name])
                try {
                    $outputFile = $entry.Open()
                    try { $inputFile.CopyTo($outputFile) } finally { $outputFile.Dispose() }
                } finally { $inputFile.Dispose() }
            }
            $entry = $archive.CreateEntry('BUILD-PROVENANCE.json')
            $entry.LastWriteTime = $timestamp
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($provenance) } finally { $writer.Dispose() }
        } finally { $archive.Dispose() }
        $stream.Dispose()
        [IO.File]::Move($partial, $zip)
    } finally {
        $stream.Dispose()
        if ([IO.File]::Exists($partial)) { [IO.File]::Delete($partial) }
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksum, "$hash  $($names.Zip)`n", [Text.UTF8Encoding]::new($false))
    Write-ReleaseOutput 'zip' $zip
    Write-ReleaseOutput 'checksum' $checksum
    return [pscustomobject]@{ Zip = $zip; Checksum = $checksum; Sha256 = $hash }
}

function Publish-OwnedRelease([object]$Version, [string]$SourceCommit, [string]$ArchivePath, [string]$HashPath) {
    Assert-RemoteReleaseTag $Version.Tag $SourceCommit ''
    $names = Get-AssetNames $Version.Tag
    $release = Get-OwnedRelease $Version $SourceCommit
    if (-not (Test-CompleteReleaseAssets $release $names)) {
        if ($null -ne $release -and -not $release.draft) {
            throw 'The published release has incomplete or inconsistent assets. Published assets will not be replaced.'
        }
        if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf) -or -not (Test-Path -LiteralPath $HashPath -PathType Leaf) -or
            [IO.Path]::GetFileName($ArchivePath) -cne $names.Zip -or [IO.Path]::GetFileName($HashPath) -cne $names.Checksum) {
            throw 'The exact release ZIP and checksum are required.'
        }
        $hash = (Get-FileHash $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([IO.File]::ReadAllText($HashPath) -cne "$hash  $($names.Zip)`n") { throw 'Release checksum does not match the ZIP.' }
        $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        try {
            $entry = $archive.GetEntry('BUILD-PROVENANCE.json')
            if ($null -eq $entry) { throw 'Release ZIP has no build provenance.' }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $provenance = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($provenance.tag -cne $Version.Tag -or $provenance.commit -cne $SourceCommit -or $provenance.runtime -cne 'win-x64') {
                throw 'Release ZIP identifies a different tag, commit, or platform.'
            }
        } finally { $archive.Dispose() }
        if ($null -eq $release) {
            $release = Invoke-ReleaseApi -Method POST -Route 'releases' -Body @{
                tag_name = $Version.Tag; target_commitish = $SourceCommit; name = "AudioTranscriber $($Version.Tag)"
                draft = $true; prerelease = $Version.Prerelease; make_latest = 'false'
                body = "$(Get-ReleaseMarker $Version.Tag $SourceCommit)`n`nWindows x64 self-contained application and speaker worker.`n`nSource commit: $SourceCommit`n`nFFmpeg and optional model weights are not bundled. See the included README and licenses."
            }
        }
        # Only these two named assets of our matching draft may be replaced after an interrupted upload.
        foreach ($asset in @($release.assets | Where-Object { $_.name -ceq $names.Zip -or $_.name -ceq $names.Checksum })) {
            Invoke-ReleaseApi -Method DELETE -Route "releases/assets/$([long]$asset.id)" | Out-Null
        }
        foreach ($file in @($ArchivePath, $HashPath)) {
            $name = [Uri]::EscapeDataString([IO.Path]::GetFileName($file))
            $label = [Uri]::EscapeDataString("sha256:$hash")
            $contentType = if ($file -eq $ArchivePath) { 'application/zip' } else { 'text/plain' }
            Invoke-ReleaseApi -Method POST -Route "releases/$([long]$release.id)/assets?name=$name&label=$label" `
                -UploadFile $file -ContentType $contentType | Out-Null
        }
        $release = Get-OwnedRelease $Version $SourceCommit
        if (-not (Test-CompleteReleaseAssets $release $names)) { throw 'GitHub did not confirm both uploaded asset digests. The draft is retained for a rerun.' }
    }
    Assert-RemoteReleaseTag $Version.Tag $SourceCommit ''
    if ($release.draft) {
        $release = Invoke-ReleaseApi -Method PATCH -Route "releases/$([long]$release.id)" -Body @{
            draft = $false; prerelease = $Version.Prerelease; make_latest = 'false'
        }
    }
    Assert-RemoteReleaseTag $Version.Tag $SourceCommit ''
    Write-Output "Release ready: $($release.html_url)"
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $Phase) { throw 'Choose Prepare, Package, or Publish.' }
$version = Get-ReleaseVersion $Tag
$sourceCommit = Get-CheckedOutCommit $Commit
switch ($Phase) {
    'Prepare' {
        Assert-RemoteReleaseTag $version.Tag $sourceCommit $EventSha
        $release = Get-OwnedRelease $version $sourceCommit
        $complete = Test-CompleteReleaseAssets $release (Get-AssetNames $version.Tag)
        if ($null -ne $release -and -not $release.draft -and -not $complete) {
            throw 'An inconsistent published release requires explicit intervention; it will not be rebuilt over.'
        }
        Write-ReleaseOutput 'commit' $sourceCommit
        Write-ReleaseOutput 'needs-build' (-not $complete).ToString().ToLowerInvariant()
        Write-ReleaseOutput 'needs-publish' ($null -eq $release -or [bool]$release.draft).ToString().ToLowerInvariant()
        if ($complete) { Write-Output 'Matching release assets already verified; SDK setup and build are skipped.' }
    }
    'Package' { New-ReleasePackage $version $sourceCommit $PublishDirectory $AssetDirectory }
    'Publish' { Publish-OwnedRelease $version $sourceCommit $ZipPath $ChecksumPath }
}
