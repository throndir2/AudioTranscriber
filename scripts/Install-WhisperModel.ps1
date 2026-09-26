[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory, ParameterSetName = 'List')][switch]$List,
    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [ValidateSet('tiny', 'base', 'small', 'large-v3', 'large-v3-turbo')][string]$Model,
    [Parameter(ParameterSetName = 'Install')][string]$ModelDirectory = '.models\whisper',
    [Parameter(ParameterSetName = 'Install')][switch]$AcceptModelLicense,
    [Parameter(ParameterSetName = 'Install')][ValidateRange(1, [long]::MaxValue)][long]$AcceptDownloadBytes
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $root '.tools\dotnet'
$dotnet = Join-Path $sdk 'dotnet.exe'
if (-not (Test-Path $dotnet)) { throw 'Pinned local SDK missing. Run the repository setup script first.' }
$env:DOTNET_ROOT = $sdk
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$project = Join-Path $root 'tools\AudioTranscriber.Benchmarks\AudioTranscriber.Benchmarks.csproj'
Push-Location $root
try {
    & $dotnet build $project --configuration Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Model installer build failed.' }
    $arguments = @('install-model')
    if ($List) { $arguments += '--list' }
    else {
        $arguments += @('--model', $Model, '--directory', $ModelDirectory)
        if ($AcceptModelLicense) { $arguments += '--accept-model-license' }
        if ($PSBoundParameters.ContainsKey('AcceptDownloadBytes')) {
            $arguments += @('--accept-download-bytes', $AcceptDownloadBytes.ToString([Globalization.CultureInfo]::InvariantCulture))
        }
    }
    & $dotnet (Join-Path $root 'tools\AudioTranscriber.Benchmarks\bin\Release\net10.0-windows\AudioTranscriber.Benchmarks.dll') @arguments
    $code = $LASTEXITCODE
} finally { Pop-Location }
exit $code
