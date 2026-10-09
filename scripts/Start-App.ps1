[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$DataRoot,
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnetName = if ($IsLinux -or $IsMacOS) { 'dotnet' } else { 'dotnet.exe' }
$dotnet = Join-Path $root ".tools\dotnet\$dotnetName"
$project = Join-Path $root 'src\AudioTranscriber.App\AudioTranscriber.App.csproj'
if (-not (Test-Path $dotnet)) { throw 'Run .\scripts\Setup.ps1 first to install the pinned local SDK.' }
if (-not (Test-Path $project)) { throw 'The desktop application project is not present in this checkout yet.' }
if ($Smoke -and [string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $root "artifacts\smoke\$([Guid]::NewGuid().ToString('N'))"
}
if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
    if (-not [IO.Path]::IsPathRooted($DataRoot)) { $DataRoot = Join-Path $root $DataRoot }
    $DataRoot = [IO.Path]::GetFullPath($DataRoot)
    if ($Smoke -and (Test-Path -LiteralPath $DataRoot) -and
        (-not (Test-Path -LiteralPath $DataRoot -PathType Container) -or
            @(Get-ChildItem -LiteralPath $DataRoot -Force).Count -ne 0)) {
        throw 'Smoke checks require a new or empty isolated data root; existing application data will not be used.'
    }
}
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    $env:NUGET_PACKAGES = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $root
try {
    if (-not $NoBuild) {
        & $dotnet build $project --configuration $Configuration --nologo -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed. Run Setup.ps1 after intentional dependency changes.' }
    }
    $assembly = Join-Path $root "src\AudioTranscriber.App\bin\$Configuration\net10.0\AudioTranscriber.App.dll"
    if (-not (Test-Path -LiteralPath $assembly)) { throw 'The desktop assembly is missing. Build the application first.' }
    $arguments = @($assembly)
    if ($DataRoot) { $arguments += @('--data-root', $DataRoot) }
    if ($Smoke) {
        $arguments += '--smoke'
        Write-Host "Smoke-check output: $(Join-Path $DataRoot 'app-smoke.json')"
    }
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "The desktop application exited with code $LASTEXITCODE." }
}
finally { Pop-Location }
