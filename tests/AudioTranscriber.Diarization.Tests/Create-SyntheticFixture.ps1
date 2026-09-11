param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\validation')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$directory = (Resolve-Path $OutputDirectory).Path
$wav = Join-Path $directory 'synthetic-speech.wav'
$pcm = Join-Path $directory 'synthetic-speech.pcm'
$voice = New-Object -ComObject SAPI.SpVoice
$stream = New-Object -ComObject SAPI.SpFileStream
$opened = $false
try {
    $stream.Format.Type = 22 # SAPI mono PCM16, 22.05 kHz; FFmpeg performs actual resampling.
    $stream.Open($wav, 3)
    $opened = $true
    $voice.AudioOutputStream = $stream
    $voice.Rate = -1
    [void]$voice.Speak('This is a synthetic speech fixture for local model loading and inference validation. The voice is generated on this computer. It does not measure recognition accuracy or the ability to identify real people. We are checking that speaker segmentation and separate clean speech embeddings can run locally.')
    $stream.Close()
    $opened = $false
    & ffmpeg -nostdin -hide_banner -loglevel error -y -i $wav -ac 1 -ar 16000 -f s16le $pcm
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture normalization failed.' }
    $bytes = (Get-Item $pcm).Length
    if ($bytes -le 0 -or $bytes -gt 60 * 32000 -or $bytes % 2 -ne 0) {
        throw 'Synthetic fixture is not bounded PCM16.'
    }
    [pscustomobject]@{
        Path = $pcm
        Voice = $voice.Voice.GetDescription()
        Bytes = $bytes
        Seconds = $bytes / 32000
        Sha256 = (Get-FileHash $pcm -Algorithm SHA256).Hash.ToLowerInvariant()
        Purpose = 'Synthetic inference smoke only; not a speaker-accuracy or DER benchmark.'
    }
}
finally {
    if ($opened) { $stream.Close() }
    if (Test-Path $wav) { Remove-Item $wav }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($voice)
}
