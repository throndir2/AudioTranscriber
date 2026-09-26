$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\scripts\Release.ps1')

$version = Get-ReleaseVersion 'v0.1.0'
$commit = 'e2150413a321fc461e00ad00cacceacc375abde9'
$names = Get-AssetNames $version.Tag
$hash = 'a' * 64
$checksumText = "$hash  $($names.Zip)`n"
$draft = [pscustomobject]@{
    id = 42; tag_name = $version.Tag; draft = $true; prerelease = $false
    body = Get-ReleaseMarker $version.Tag $commit
    html_url = 'https://github.com/example/repository/releases/tag/v0.1.0'
    assets = @(
        [pscustomobject]@{
            name = $names.Zip; state = 'uploaded'; size = 123
            digest = "sha256:$hash"; label = "sha256:$hash"
        },
        [pscustomobject]@{
            name = $names.Checksum; state = 'uploaded'
            size = [Text.Encoding]::UTF8.GetByteCount($checksumText)
            digest = 'sha256:' + (Get-TextHash $checksumText); label = "sha256:$hash"
        }
    )
}
$published = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$published.draft = $false
$tagReference = [pscustomobject]@{
    ref = 'refs/tags/v0.1.0'
    object = [pscustomobject]@{ type = 'commit'; sha = $commit }
}

function Invoke-ReleaseApi {
    param([string]$Method, [string]$Route, [object]$Body, [switch]$AllowMissing,
          [string]$UploadFile, [string]$ContentType)
    if ($script:requests.Count -eq 0) { throw "Unexpected API request: $Method $Route" }
    $request = $script:requests.Dequeue()
    if ($Method -cne $request.Method -or $Route -cne $request.Route) {
        throw "Expected $($request.Method) $($request.Route); received $Method $Route"
    }
    if ($UploadFile) { throw 'These tests must never upload assets.' }
    if ($Route.StartsWith('releases/tags/') -and -not $AllowMissing) {
        throw 'Tag lookup must allow a missing release.'
    }
    if ($Method -eq 'PATCH' -and
        ($Body.draft -ne $false -or $Body.prerelease -ne $false -or $Body.make_latest -cne 'true')) {
        throw 'Finalization changed unexpected release metadata.'
    }
    if ($request.Error) { throw $request.Error }
    return $request.Response
}

function Expect-Api([string]$Method, [string]$Route, [object]$Response, [string]$ErrorMessage) {
    $script:requests.Enqueue([pscustomobject]@{
        Method = $Method; Route = $Route; Response = $Response; Error = $ErrorMessage
    })
}

function Expect-Draft([object]$Release) {
    Expect-Api GET 'releases/tags/v0.1.0' $null
    Expect-Api GET 'releases?per_page=100&page=1' @($Release)
}

function Assert-Equal([object]$Actual, [object]$Expected) {
    if ($Actual -cne $Expected) { throw "Expected '$Expected'; received '$Actual'." }
}

function Assert-Throws([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected failure containing '$Message'."
}

function Test-Case([string]$Name, [scriptblock]$Action) {
    $script:requests = [Collections.Generic.Queue[object]]::new()
    & $Action
    Assert-Equal $script:requests.Count 0
    Write-Host "PASS $Name"
}

Test-Case 'Published release uses the tag endpoint directly' {
    Expect-Api GET 'releases/tags/v0.1.0' $published
    Assert-Equal (Get-OwnedRelease $version $commit).id 42
}

Test-Case 'Draft omitted by the tag endpoint is discovered' {
    Expect-Draft $draft
    Assert-Equal (Get-OwnedRelease $version $commit).id 42
}

Test-Case 'Draft discovery follows pagination' {
    Expect-Api GET 'releases/tags/v0.1.0' $null
    Expect-Api GET 'releases?per_page=100&page=1' (@([pscustomobject]@{ tag_name = 'v0.0.1' }) * 100)
    Expect-Api GET 'releases?per_page=100&page=2' @($draft)
    Assert-Equal (Get-OwnedRelease $version $commit).id 42
}

Test-Case 'An absent release remains absent' {
    Expect-Api GET 'releases/tags/v0.1.0' $null
    Expect-Api GET 'releases?per_page=100&page=1' @()
    Assert-Equal (Get-OwnedRelease $version $commit) $null
}

Test-Case 'Unowned draft is not adopted' {
    $unowned = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $unowned.body = 'Human-created draft'
    Expect-Draft $unowned
    Assert-Throws { Get-OwnedRelease $version $commit } 'different ownership'
}

Test-Case 'Draft source commit and prerelease status stay guarded' {
    Expect-Draft $draft
    Assert-Throws { Get-OwnedRelease $version ('b' * 40) } 'different ownership'
    $prerelease = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $prerelease.prerelease = $true
    Expect-Draft $prerelease
    Assert-Throws { Get-OwnedRelease $version $commit } 'different ownership'
}

Test-Case 'Duplicate drafts across pages are rejected' {
    Expect-Api GET 'releases/tags/v0.1.0' $null
    Expect-Api GET 'releases?per_page=100&page=1' (@($draft) + (@([pscustomobject]@{ tag_name = 'v0.0.1' }) * 99))
    Expect-Api GET 'releases?per_page=100&page=2' @($draft)
    Assert-Throws { Get-OwnedRelease $version $commit } 'Multiple releases'
}

Test-Case 'Release list failures are not treated as absence' {
    Expect-Api GET 'releases/tags/v0.1.0' $null
    Expect-Api GET 'releases?per_page=100&page=1' $null 'GitHub HTTP 403'
    Assert-Throws { Get-OwnedRelease $version $commit } 'GitHub HTTP 403'
}

Test-Case 'Complete draft is finalized without rebuilding or replacing assets' {
    Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference
    Expect-Draft $draft
    Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference
    Expect-Api PATCH 'releases/42' $published
    Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference
    Assert-Equal (Publish-OwnedRelease $version $commit '' '') "Release ready: $($published.html_url)"
}

Test-Case 'Corrupt draft assets are not finalized' {
    $corrupt = $draft | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $corrupt.assets[1].digest = 'sha256:' + ('b' * 64)
    Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference
    Expect-Draft $corrupt
    Assert-Throws { Publish-OwnedRelease $version $commit '' '' } 'exact release ZIP and checksum are required'
}

Test-Case 'Published release is a no-op' {
    Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference
    Expect-Api GET 'releases/tags/v0.1.0' $published
    Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference
    Expect-Api GET 'git/ref/tags/v0.1.0' $tagReference
    Assert-Equal (Publish-OwnedRelease $version $commit '' '') "Release ready: $($published.html_url)"
}
