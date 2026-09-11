[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Project = 'AudioTranscriber.slnx',
    [string]$Filter,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { throw 'Run .\scripts\Setup.ps1 first to install the pinned local SDK.' }
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $root
try {
    if (-not (Test-Path $Project)) { throw "Test project or solution not found: $Project" }
    if (-not $NoRestore) {
        & $dotnet restore $Project --locked-mode --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed. Run Setup.ps1 after intentional dependency changes.' }
    }
    $arguments = @('test', $Project, '--configuration', $Configuration, '--no-restore', '--nologo', '-m:1',
        '--results-directory', (Join-Path $root 'artifacts\TestResults'))
    if ($Filter) { $arguments += @('--filter', $Filter) }
    $arguments += @('--', "RunConfiguration.DotNetHostPath=$dotnet")
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with code $LASTEXITCODE." }
}
finally { Pop-Location }
