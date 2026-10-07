[CmdletBinding()]
param(
    [ValidateSet('headless', 'ui')][string]$Mode = 'headless',
    [string]$DataRoot,
    [string]$AppPath,
    [switch]$NoBuild
)

# Launches AudioTranscriber as a stdio MCP server. stdout carries the protocol, so everything else goes to stderr.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Write-Log([string]$Message) { [Console]::Error.WriteLine($Message) }

$arguments = @(if ($Mode -eq 'headless') { '--mcp' } else { })
if ($DataRoot) { $arguments += @('--data-root', [IO.Path]::GetFullPath($DataRoot)) }

if ($AppPath -and $Mode -eq 'headless') {
    $executable = (Resolve-Path -LiteralPath $AppPath).Path
}
else {
    $dotnetName = if ($IsLinux -or $IsMacOS) { 'dotnet' } else { 'dotnet.exe' }
    $dotnet = Join-Path $root ".tools\dotnet\$dotnetName"
    if (-not (Test-Path $dotnet)) { throw 'Run .\scripts\Setup.ps1 first to install the pinned local SDK.' }
    if ($AppPath) { $env:AUDIOTRANSCRIBER_APP = (Resolve-Path -LiteralPath $AppPath).Path }
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
    $env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $project = if ($Mode -eq 'headless') { Join-Path $root 'src\AudioTranscriber.App\AudioTranscriber.App.csproj' } else { Join-Path $root 'tools\AudioTranscriber.UiMcp\AudioTranscriber.UiMcp.csproj' }
    $appProject = Join-Path $root 'src\AudioTranscriber.App\AudioTranscriber.App.csproj'
    $assembly = if ($Mode -eq 'headless') { Join-Path $root 'src\AudioTranscriber.App\bin\Release\net10.0\AudioTranscriber.App.dll' } else { Join-Path $root 'tools\AudioTranscriber.UiMcp\bin\Release\net10.0-windows\AudioTranscriber.UiMcp.dll' }
    if (-not $NoBuild -or -not (Test-Path $assembly)) {
        if ($Mode -eq 'ui' -and -not $AppPath) {
            & $dotnet build $appProject --configuration Release --nologo -p:RestoreLockedMode=true 2>&1 | ForEach-Object { Write-Log "$_" }
            if ($LASTEXITCODE -ne 0) { throw 'Desktop app build failed; see stderr.' }
            $env:AUDIOTRANSCRIBER_APP = Join-Path $root 'src\AudioTranscriber.App\bin\Release\net10.0\AudioTranscriber.App.dll'
        }
        & $dotnet build $project --configuration Release --nologo -p:RestoreLockedMode=true 2>&1 | ForEach-Object { Write-Log "$_" }
        if ($LASTEXITCODE -ne 0) { throw 'MCP build failed; see stderr.' }
    }
    $executable = $dotnet
    $arguments = @($assembly) + $arguments
}

# Start-Process without redirection hands this process's stdin/stdout pipes straight to the server.
$quoted = $arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
$process = Start-Process -FilePath $executable -ArgumentList $quoted -NoNewWindow -Wait -PassThru
exit $process.ExitCode
