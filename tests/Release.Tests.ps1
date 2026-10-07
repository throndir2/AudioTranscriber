$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\Release.ps1')

$version = Get-ReleaseVersion 'v0.1.0'
$commit = 'e2150413a321fc461e00ad00cacceacc375abde9'
$names = Get-AssetNames $version.Tag
$hashes = @{}
$i = 0
foreach ($assetName in @($names.WinZip, $names.WinSetup, $names.LinuxTar, $names.Deb, $names.Rpm)) {
    $hashes[$assetName] = ([char]([int][char]'a' + $i)).ToString() * 64
    $i++
}
function New-MockAssets([object]$Names) {
    $assets = @()
    foreach ($assetName in @($Names.WinZip, $Names.WinSetup, $Names.LinuxTar, $Names.Deb, $Names.Rpm)) {
        $hash = $script:hashes[$assetName]
        $text = "$hash  $assetName`n"
        $assets += [pscustomobject]@{ name = $assetName; state = 'uploaded'; size = 123; digest = "sha256:$hash"; label = "sha256:$hash" }
        $assets += [pscustomobject]@{ name = "$assetName.sha256"; state = 'uploaded'; size = [Text.Encoding]::UTF8.GetByteCount($text); digest = 'sha256:' + (Get-TextHash $text); label = "sha256:$hash" }
    }
    return $assets
}
$draft = [pscustomobject]@{
    id = 42; tag_name = $version.Tag; draft = $true; prerelease = $false
    body = Get-ReleaseMarker $version.Tag $commit
    html_url = 'https://github.com/example/repository/releases/tag/v0.1.0'
    assets = @(New-MockAssets $names)
}
$published = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$published.draft = $false
$tagReference = [pscustomobject]@{ ref = 'refs/tags/v0.1.0'; object = [pscustomobject]@{ type = 'commit'; sha = $commit } }

function Invoke-ReleaseApi {
    param([string]$Method, [string]$Route, [object]$Body, [switch]$AllowMissing,
          [string]$UploadFile, [string]$ContentType)
    if ($script:requests.Count -eq 0) { throw "Unexpected API request: $Method $Route" }
    $request = $script:requests.Dequeue()
    if ($Method -cne $request.Method -or $Route -cne $request.Route) { throw "Expected $($request.Method) $($request.Route); received $Method $Route" }
    if ($UploadFile) { throw 'These tests must never upload assets.' }
    if ($Route.StartsWith('releases/tags/') -and -not $AllowMissing) { throw 'Tag lookup must allow a missing release.' }
    if ($Method -eq 'PATCH' -and ($Body.draft -ne $false -or $Body.prerelease -ne $false -or $Body.make_latest -cne 'true')) { throw 'Finalization changed unexpected release metadata.' }
    if ($request.Error) { throw $request.Error }
    return $request.Response
}
function Expect-Api([string]$Method, [string]$Route, [object]$Response, [string]$ErrorMessage) { $script:requests.Enqueue([pscustomobject]@{ Method = $Method; Route = $Route; Response = $Response; Error = $ErrorMessage }) }
function Expect-Draft([object]$Release) { Expect-Api GET 'releases/tags/v0.1.0' $null; Expect-Api GET 'releases?per_page=100&page=1' @($Release) }
function Assert-Equal([object]$Actual, [object]$Expected) { if ($Actual -cne $Expected) { throw "Expected '$Expected'; received '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Message) { try { & $Action | Out-Null } catch { if ($_.Exception.Message -notlike "*$Message*") { throw }; return }; throw "Expected failure containing '$Message'." }
function Test-Case([string]$Name, [scriptblock]$Action) { $script:requests = [Collections.Generic.Queue[object]]::new(); & $Action; Assert-Equal $script:requests.Count 0; Write-Host "PASS $Name" }

Test-Case 'Published release uses the tag endpoint directly' { Expect-Api GET 'releases/tags/v0.1.0' $published; Assert-Equal (Get-OwnedRelease $version $commit).id 42 }
Test-Case 'Draft omitted by the tag endpoint is discovered' { Expect-Draft $draft; Assert-Equal (Get-OwnedRelease $version $commit).id 42 }
Test-Case 'Draft discovery follows pagination' { Expect-Api GET 'releases/tags/v0.1.0' $null; Expect-Api GET 'releases?per_page=100&page=1' (@([pscustomobject]@{ tag_name = 'v0.0.1' }) * 100); Expect-Api GET 'releases?per_page=100&page=2' @($draft); Assert-Equal (Get-OwnedRelease $version $commit).id 42 }
Test-Case 'An absent release remains absent' { Expect-Api GET 'releases/tags/v0.1.0' $null; Expect-Api GET 'releases?per_page=100&page=1' @(); Assert-Equal (Get-OwnedRelease $version $commit) $null }
Test-Case 'Unowned draft is not adopted' { $unowned = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json; $unowned.body = 'Human-created draft'; Expect-Draft $unowned; Assert-Throws { Get-OwnedRelease $version $commit } 'different ownership' }
Test-Case 'Draft source commit and prerelease status stay guarded' { Expect-Draft $draft; Assert-Throws { Get-OwnedRelease $version ('b' * 40) } 'different ownership'; $prerelease = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json; $prerelease.prerelease = $true; Expect-Draft $prerelease; Assert-Throws { Get-OwnedRelease $version $commit } 'different ownership' }
Test-Case 'Duplicate drafts across pages are rejected' { Expect-Api GET 'releases/tags/v0.1.0' $null; Expect-Api GET 'releases?per_page=100&page=1' (@($draft) + (@([pscustomobject]@{ tag_name = 'v0.0.1' }) * 99)); Expect-Api GET 'releases?per_page=100&page=2' @($draft); Assert-Throws { Get-OwnedRelease $version $commit } 'Multiple releases' }
Test-Case 'Release list failures are not treated as absence' { Expect-Api GET 'releases/tags/v0.1.0' $null; Expect-Api GET 'releases?per_page=100&page=1' $null 'GitHub HTTP 403'; Assert-Throws { Get-OwnedRelease $version $commit } 'GitHub HTTP 403' }
Test-Case 'Complete draft is finalized without rebuilding or replacing assets' { Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference; Expect-Draft $draft; Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference; Expect-Api PATCH 'releases/42' $published; Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference; Assert-Equal (Publish-OwnedRelease $version $commit '' '' '') "Release ready: $($published.html_url)" }
Test-Case 'Corrupt draft assets are not finalized' { $corrupt = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json; $corrupt.assets[1].digest = 'sha256:' + ('f' * 64); Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference; Expect-Draft $corrupt; Assert-Throws { Publish-OwnedRelease $version $commit '' '' '' } 'release asset directory is required' }
Test-Case 'Published release is a no-op' { Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference; Expect-Api GET 'releases/tags/v0.1.0' $published; Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference; Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference; Assert-Equal (Publish-OwnedRelease $version $commit '' '' '') "Release ready: $($published.html_url)" }

Test-Case 'Debian package writer emits ar package metadata' {
    $base = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\release-tests'
    $publish = Join-Path $base 'linux-publish'
    $assets = Join-Path $base 'assets'
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($dir in @($publish, (Join-Path $publish 'ffmpeg'), (Join-Path $publish 'licenses\models'))) { New-Item -ItemType Directory -Force $dir | Out-Null }
    foreach ($file in @('AudioTranscriber.App','AudioTranscriber.App.dll','AudioTranscriber.App.deps.json','AudioTranscriber.App.runtimeconfig.json','AudioTranscriber.Worker','AudioTranscriber.Worker.dll','AudioTranscriber.Worker.deps.json','AudioTranscriber.Worker.runtimeconfig.json','libcoreclr.so','libhostpolicy.so','libe_sqlite3.so','libsherpa-onnx-c-api.so','libonnxruntime.so','ffmpeg\ffmpeg','ffmpeg\ffprobe','licenses\FFmpeg-LICENSE.txt','licenses\FFmpeg-provenance.md','README.md','licenses\PACKAGE-INVENTORY.txt','licenses\SQLite-provenance.md','licenses\models\Segmentation-original-MIT.txt','licenses\models\WeSpeaker-NOTICE.txt')) {
        $path = Join-Path $publish $file; New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null; Set-Content -LiteralPath $path -Value '{}' -Encoding utf8NoBOM
    }
    New-Item -ItemType Directory -Force $assets | Out-Null
    $deb = New-DebianPackage $version $commit $publish $assets ([DateTimeOffset]'2026-01-01T00:00:00Z')
    $bytes = [IO.File]::ReadAllBytes($deb)
    Assert-Equal ([Text.Encoding]::ASCII.GetString($bytes,0,8)) "!<arch>`n"
    if (-not (Test-Path -LiteralPath "$deb.sha256")) { throw 'Missing checksum file.' }
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
