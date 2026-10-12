<#
.SYNOPSIS
    Dibuja el icono de sOC ScreenShare a partir de assets/appicon.svg (mismas medidas): appicon.ico
    con 16, 20, 24, 32, 40, 48, 64, 128 y 256 px, Assets/logo.png (256) y Assets/tray-recording.png
    (32, con el punto de grabar en rojo para la bandeja mientras graba).
.EXAMPLE
    pwsh -File tools\generar-icono.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$app = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ScreenShare.App'

function Draw([int]$size, [bool]$recording) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $r = 56 * $s
    $w = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($w-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($w-2*$r, $w-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $w-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0,0)), ([System.Drawing.PointF]::new($size,$size)), ([System.Drawing.Color]::FromArgb(0x63,0x5B,0xF2)), ([System.Drawing.Color]::FromArgb(0x2A,0x1C,0xB8))
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 14*$s))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $sx = 40*$s; $sy = 52*$s; $sw = 176*$s; $sh = 118*$s; $sr = 16*$s
    $scr = New-Object System.Drawing.Drawing2D.GraphicsPath
    $scr.AddArc($sx, $sy, 2*$sr, 2*$sr, 180, 90); $scr.AddArc($sx+$sw-2*$sr, $sy, 2*$sr, 2*$sr, 270, 90)
    $scr.AddArc($sx+$sw-2*$sr, $sy+$sh-2*$sr, 2*$sr, 2*$sr, 0, 90); $scr.AddArc($sx, $sy+$sh-2*$sr, 2*$sr, 2*$sr, 90, 90); $scr.CloseFigure()
    $g.DrawPath($pen, $scr)
    $g.DrawLine($pen, [float](128*$s), [float]($sy+$sh), [float](128*$s), [float](198*$s))
    $g.DrawLine($pen, [float](92*$s), [float](204*$s), [float](164*$s), [float](204*$s))
    # El punto de grabar: indigo claro; rojo de grabacion (Danger #BA1A1A, mas vivo para que se vea pequeño) mientras graba.
    $color = if ($recording) { [System.Drawing.Color]::FromArgb(0xE5,0x39,0x35) } else { [System.Drawing.Color]::FromArgb(0xC3,0xC0,0xFF) }
    $dot = New-Object System.Drawing.SolidBrush $color
    $d = $(if ($recording) { 80 } else { 54 }) * $s
    $g.FillEllipse($dot, [float](128*$s-$d/2), [float](111*$s-$d/2), [float]$d, [float]$d)
    $g.Dispose()
    return $bmp
}

function Png($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); return ,$ms.ToArray() }

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @(); foreach ($z in $sizes) { $pngs += ,(Png (Draw $z $false)) }
[IO.File]::WriteAllBytes((Join-Path $app 'Assets\logo.png'), (Png (Draw 256 $false)))
[IO.File]::WriteAllBytes((Join-Path $app 'Assets\tray-recording.png'), (Png (Draw 32 $true)))

$fs = [IO.File]::Create((Join-Path $app 'appicon.ico')); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $z = $sizes[$i]; $b = if ($z -ge 256) { 0 } else { $z }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"appicon.ico, Assets\logo.png y Assets\tray-recording.png generados en $app"
