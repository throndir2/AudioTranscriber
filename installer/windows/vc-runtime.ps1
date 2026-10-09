# Run by setup: installs Microsoft's x64 Visual C++ runtime when it is missing or older than the app needs.
# Keep the file list and minimum version in step with src\AudioTranscriber.App\Prerequisites.cs.
$minimum = [version]'14.40'
$ready = $true
foreach ($file in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll') {
    $path = Join-Path ([Environment]::SystemDirectory) $file
    if (-not (Test-Path -LiteralPath $path)) { $ready = $false; break }
    $info = (Get-Item -LiteralPath $path).VersionInfo
    if ([version]::new($info.FileMajorPart, $info.FileMinorPart) -lt $minimum) { $ready = $false; break }
}
if ($ready) { exit 0 }
try {
    $dir = Join-Path $env:TEMP 'AudioTranscriber'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir 'vc_redist.x64.exe'
    Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $out -UseBasicParsing
    $process = Start-Process -FilePath $out -ArgumentList '/install', '/passive', '/norestart' -Wait -PassThru
    exit $process.ExitCode
}
catch { exit 99 }
