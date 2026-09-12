[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
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
$destination = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root 'artifacts\publish\win-x64' }
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

Push-Location $root
try {
    $sdkVersion = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $sdkVersion) { throw "The build requires SDK $sdkVersion from global.json." }
    [string[]]$targeting = if ($IsWindows) { @() } else { @('-p:EnableWindowsTargeting=true') }
    $worker = Join-Path $root 'src\AudioTranscriber.Worker\AudioTranscriber.Worker.csproj'
    if (-not (Test-Path $worker)) { throw 'The required speaker worker project is missing.' }
    # The application already references the worker; one locked restore covers both dependency graphs.
    & $dotnet restore $project --locked-mode --nologo @targeting
    if ($LASTEXITCODE -ne 0) { throw 'Locked Windows x64 restore failed. Run Setup.ps1 after intentional dependency changes.' }
    $projects = @($project, $worker)
    foreach ($item in $projects) {
        [string[]]$reuseBuild = if ($item -eq $worker) { @('--no-build') } else { @() }
        & $dotnet publish $item --configuration $Configuration --runtime win-x64 --self-contained true `
            --no-restore --output $destination --nologo -p:PublishSingleFile=false @targeting @reuseBuild
        if ($LASTEXITCODE -ne 0) { throw "Windows x64 publish failed: $item" }
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
    Write-Host "Published Windows x64 application to $destination"
    Write-Host 'FFmpeg and optional model weights are not bundled automatically.'
}
finally { Pop-Location }
