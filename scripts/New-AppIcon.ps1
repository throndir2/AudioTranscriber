<#
.SYNOPSIS
Regenerates the AudioTranscriber app icon (multi-size .ico plus a 256 px PNG preview).

.DESCRIPTION
Draws the icon with WPF vector primitives: a navy-to-teal rounded tile holding
mint waveform bars that become white transcript lines ("audio to text").
Sizes 16-24 px use a hand-tuned, pixel-snapped layout so they stay crisp.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\AudioTranscriber.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

function New-Color([string]$hex, [byte]$alpha = 255) {
    $c = [Windows.Media.ColorConverter]::ConvertFromString($hex)
    [Windows.Media.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function New-Brush([string]$hex, [byte]$alpha = 255) {
    $b = [Windows.Media.SolidColorBrush]::new((New-Color $hex $alpha)); $b.Freeze(); $b
}

function New-Linear([double]$x1, [double]$y1, [double]$x2, [double]$y2, [object[]]$stops) {
    $g = [Windows.Media.LinearGradientBrush]::new()
    $g.StartPoint = [Windows.Point]::new($x1, $y1)
    $g.EndPoint = [Windows.Point]::new($x2, $y2)
    foreach ($s in $stops) { $g.GradientStops.Add([Windows.Media.GradientStop]::new((New-Color $s[1] $s[2]), $s[0])) }
    $g.Freeze(); $g
}

$tileBrush = New-Linear 0 0 1 1 @(@(0.0, '#0D1E31', 255), @(0.5, '#123E5C', 255), @(1.0, '#0CA893', 255))
$shineBrush = New-Linear 0 0 0 1 @(@(0.0, '#FFFFFF', 30), @(0.45, '#FFFFFF', 0))
$glowBrush = [Windows.Media.RadialGradientBrush]::new((New-Color '#3CF0D4' 90), (New-Color '#3CF0D4' 0))
$glowBrush.Freeze()
$barBrush = New-Linear 0 0 0 1 @(@(0.0, '#A6FFF0', 255), @(1.0, '#27D2BC', 255))
$lineBrush = New-Brush '#FFFFFF'
$rimPen = [Windows.Media.Pen]::new((New-Brush '#FFFFFF' 36), 1); $rimPen.Freeze()

function Add-Pill($dc, $brush, [double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $dc.DrawRoundedRectangle($brush, $null, [Windows.Rect]::new($x, $y, $w, $h), $r, $r)
}

# Hand-tuned pixel layouts for small sizes: bars are (x, y, height), lines are (x, y, width).
$pixelLayouts = @{
    16 = @{ Inset = 0; Radius = 3; Bar = 2; Line = 2; Bars = @(@(2, 6, 4), @(5, 3, 10), @(8, 5, 6)); Lines = @(@(11, 4, 3), @(11, 7, 3), @(11, 10, 2)) }
    20 = @{ Inset = 0; Radius = 4; Bar = 2; Line = 2; Bars = @(@(3, 8, 4), @(6, 4, 12), @(9, 6, 8)); Lines = @(@(12, 6, 5), @(12, 9, 5), @(12, 12, 3)) }
    24 = @{ Inset = 0; Radius = 5; Bar = 2; Line = 2; Bars = @(@(3, 9, 6), @(7, 4, 16), @(11, 7, 10)); Lines = @(@(14, 8, 7), @(14, 11, 7), @(14, 14, 4)) }
    32 = @{ Inset = 1; Radius = 7; Bar = 3; Line = 3; Bars = @(@(5, 12, 8), @(10, 6, 20), @(15, 10, 12)); Lines = @(@(20, 10, 7), @(20, 15, 7), @(20, 20, 4)) }
}

function Get-Frame([int]$size) {
    $root = [Windows.Media.ContainerVisual]::new()
    $back = [Windows.Media.DrawingVisual]::new()
    $fore = [Windows.Media.DrawingVisual]::new()
    $root.Children.Add($back) | Out-Null
    $root.Children.Add($fore) | Out-Null
    $bg = $back.RenderOpen()
    $fg = $fore.RenderOpen()

    if ($pixelLayouts.ContainsKey($size)) {
        $layout = $pixelLayouts[$size]
        $i = $layout.Inset
        $bg.DrawRoundedRectangle($tileBrush, $null, [Windows.Rect]::new($i, $i, $size - 2 * $i, $size - 2 * $i), $layout.Radius, $layout.Radius)
        foreach ($b in $layout.Bars) { Add-Pill $fg $barBrush $b[0] $b[1] $layout.Bar $b[2] ($layout.Bar * 0.3) }
        foreach ($l in $layout.Lines) { Add-Pill $fg $lineBrush $l[0] $l[1] $l[2] $layout.Line ($layout.Line * 0.3) }
    }
    else {
        $k = $size / 256.0
        $scale = [Windows.Media.ScaleTransform]::new($k, $k)
        $bg.PushTransform($scale); $fg.PushTransform($scale)
        $tile = [Windows.Rect]::new(12, 12, 232, 232)
        $bg.PushClip([Windows.Media.RectangleGeometry]::new($tile, 56, 56))
        $bg.DrawRectangle($tileBrush, $null, $tile)
        $bg.DrawEllipse($glowBrush, $null, [Windows.Point]::new(100, 132), 120, 120)
        $bg.DrawRectangle($shineBrush, $null, $tile)
        $bg.Pop()
        if ($size -ge 64) {
            $bg.DrawRoundedRectangle($null, $rimPen, [Windows.Rect]::new(12.5, 12.5, 231, 231), 55.5, 55.5)
        }
        # Waveform bars (x, top, height); width 20, centred on y=128.
        foreach ($b in @(@(50, 98, 60), @(80, 58, 140), @(110, 82, 92))) {
            Add-Pill $fg $barBrush $b[0] $b[1] 20 $b[2] 10
        }
        # Transcript lines: two full lines and a shorter trailing one.
        Add-Pill $fg $lineBrush 146 83 62 18 9
        Add-Pill $fg $lineBrush 146 119 62 18 9
        Add-Pill $fg $lineBrush 146 155 36 18 9
        $bg.Pop(); $fg.Pop()
        if ($size -ge 48) {
            $shadow = [Windows.Media.Effects.DropShadowEffect]::new()
            $shadow.Color = New-Color '#04101C'; $shadow.Direction = 270; $shadow.Opacity = 0.45
            $shadow.ShadowDepth = 5 * $k; $shadow.BlurRadius = 16 * $k
            $fore.Effect = $shadow
        }
    }

    $bg.Close(); $fg.Close()
    $rtb = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($root)
    $straight = [Windows.Media.Imaging.FormatConvertedBitmap]::new($rtb, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $pixels = [byte[]]::new($size * $size * 4)
    $straight.CopyPixels($pixels, $size * 4, 0)
    [pscustomobject]@{ Size = $size; Bitmap = $straight; Pixels = $pixels }
}

function ConvertTo-Png($bitmap) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = [IO.MemoryStream]::new(); $encoder.Save($ms); , $ms.ToArray()
}

function ConvertTo-Dib($frame) {
    # 32bpp BITMAPINFOHEADER + bottom-up BGRA + 1bpp AND mask (classic ICO entry).
    $s = $frame.Size
    $ms = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($ms)
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($row = $s - 1; $row -ge 0; $row--) { $w.Write($frame.Pixels, $row * $s * 4, $s * 4) }
    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write([byte[]]::new($maskStride * $s))
    $w.Flush(); , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = foreach ($s in $sizes) { Get-Frame $s }
$images = foreach ($f in $frames) { if ($f.Size -ge 256) { , (ConvertTo-Png $f.Bitmap) } else { , (ConvertTo-Dib $f) } }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$icoPath = Join-Path $OutputDirectory 'AppIcon.ico'
$ms = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($ms)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = [byte]($(if ($s -ge 256) { 0 } else { $s }))
    $w.Write($dim); $w.Write($dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[IO.File]::WriteAllBytes($icoPath, $ms.ToArray())

$pngPath = Join-Path $OutputDirectory 'AppIcon.png'
[IO.File]::WriteAllBytes($pngPath, (ConvertTo-Png ($frames | Where-Object Size -eq 256).Bitmap))
Write-Host "Wrote $icoPath and $pngPath"
