[CmdletBinding()]
param(
    [string]$ProductionConfigPath,
    [switch]$ApprovePublicAudioCloud,
    [switch]$PrepareOnly,
    [string]$OutputDirectory = 'tools\AudioTranscriber.Benchmarks\artifacts',
    [ValidateRange(0, 1)][int]$MaxRetries = 0,
    [string]$FFmpeg = 'ffmpeg',
    [string]$FFprobe = 'ffprobe'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $root '.tools\dotnet'
$dotnet = Join-Path $sdk 'dotnet.exe'
if (-not (Test-Path $dotnet)) { throw 'Pinned local SDK missing. Run the repository setup script first.' }
if ($PrepareOnly -and $ProductionConfigPath) { throw 'PrepareOnly must not receive a credential path.' }
if (-not $PrepareOnly -and (-not $ApprovePublicAudioCloud -or -not $ProductionConfigPath)) {
    throw 'Use -PrepareOnly, or explicitly authorize the public comparison with -ApprovePublicAudioCloud and -ProductionConfigPath.'
}
$env:DOTNET_ROOT = $sdk
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    $env:NUGET_PACKAGES = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
$project = Join-Path $root 'tools\AudioTranscriber.Benchmarks\AudioTranscriber.Benchmarks.csproj'
Push-Location $root
try {
    & $dotnet build $project --configuration Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
    $arguments = @('--output', $OutputDirectory, '--max-retries', "$MaxRetries", '--ffmpeg', $FFmpeg, '--ffprobe', $FFprobe)
    if ($PrepareOnly) { $arguments += '--prepare-only' }
    else { $arguments += @('--approve-cloud', '--production-config', $ProductionConfigPath) }
    & $dotnet (Join-Path $root 'tools\AudioTranscriber.Benchmarks\bin\Release\net10.0-windows\AudioTranscriber.Benchmarks.dll') @arguments
    $code = $LASTEXITCODE
} finally { Pop-Location }
exit $code
