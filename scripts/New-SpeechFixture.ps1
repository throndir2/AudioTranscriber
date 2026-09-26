[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\fixtures'),
    [string]$Name = 'two-speaker-conversation'
)

# Generates a local two-voice (David/Zira) SAPI conversation for MCP/app testing. Nothing is downloaded or uploaded.
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$directory = (Resolve-Path $OutputDirectory).Path
$wav = Join-Path $directory "$Name.wav"
$script = Join-Path $directory "$Name.txt"
$lines = @(
    @('David', 'Good morning. Thanks for joining the planning meeting about the new audio transcriber release.'),
    @('Zira', 'Happy to be here. I tested the recording feature yesterday and the microphone track worked well.'),
    @('David', 'Great. The main issue we saw was that speaker analysis failed on a clean computer.'),
    @('Zira', 'Right. The diarization worker could not find the dotnet runtime, so it exited immediately.'),
    @('David', 'We fixed the publish step so the worker is self contained, just like the desktop application.'),
    @('Zira', 'Perfect. Next week we should test a longer recording with three or four different people.'),
    @('David', 'Agreed. Let us schedule that for Tuesday afternoon and share the results with the team.'),
    @('Zira', 'Sounds good. I will send the calendar invitation after this call. Goodbye for now.')
)
$voice = New-Object -ComObject SAPI.SpVoice
$stream = New-Object -ComObject SAPI.SpFileStream
$voices = @{}
foreach ($token in $voice.GetVoices()) {
    foreach ($speaker in 'David', 'Zira') { if ($token.GetDescription() -match $speaker) { $voices[$speaker] = $token } }
}
if ($voices.Count -ne 2) { throw 'This fixture needs the built-in Microsoft David and Zira voices.' }
try {
    $stream.Format.Type = 22 # SAPI mono PCM16, 22.05 kHz.
    $stream.Open($wav, 3)
    $voice.AudioOutputStream = $stream
    [void]$voice.Speak('<silence msec="500"/>', 8)
    foreach ($line in $lines) {
        $voice.Voice = $voices[$line[0]]
        [void]$voice.Speak($line[1])
        [void]$voice.Speak('<silence msec="700"/>', 8)
    }
}
finally {
    $stream.Close()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($voice)
}
$lines | ForEach-Object { "$($_[0]): $($_[1])" } | Set-Content -LiteralPath $script -Encoding utf8
[pscustomobject]@{ Wav = $wav; Script = $script; Bytes = (Get-Item $wav).Length; Seconds = [math]::Round(((Get-Item $wav).Length - 44) / 44100, 1) }
