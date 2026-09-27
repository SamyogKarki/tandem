# Draws the Tandem icon (a phone in front of a monitor on a gradient tile) and writes
# windows\Tandem.App\Assets\tandem.ico (16–256 px, PNG-compressed) and tandem-32.png.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'windows\Tandem.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Tile
    $tile = New-RoundedPath 8 8 240 240 56
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 8, 8), (New-Object System.Drawing.PointF 248, 248), ([System.Drawing.Color]::FromArgb(255, 79, 70, 229)), ([System.Drawing.Color]::FromArgb(255, 6, 182, 212))
    $g.FillPath($grad, $tile)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $screenBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 49, 46, 129))

    # Monitor (back)
    $monitor = New-RoundedPath 36 60 150 104 14
    $g.FillPath($white, $monitor)
    $inner = New-RoundedPath 48 72 126 80 6
    $g.FillPath($screenBrush, $inner)
    $g.FillRectangle($white, 98, 164, 26, 18)
    $stand = New-RoundedPath 76 180 70 12 6
    $g.FillPath($white, $stand)

    # Phone (front), with a thin tile-coloured gap so it reads as "in front"
    $gap = New-RoundedPath 140 92 88 140 20
    $gapBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 140, 215))
    $g.FillPath($gapBrush, $gap)
    $phone = New-RoundedPath 148 100 72 124 14
    $g.FillPath($white, $phone)
    $phoneScreen = New-RoundedPath 156 110 56 98 7
    $phoneGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 156, 110), (New-Object System.Drawing.PointF 212, 208), ([System.Drawing.Color]::FromArgb(255, 99, 102, 241)), ([System.Drawing.Color]::FromArgb(255, 34, 211, 238))
    $g.FillPath($phoneGrad, $phoneScreen)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 32) { $bmp.Save((Join-Path $assets 'tandem-32.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($size -eq 256) { $bmp.Save((Join-Path $assets 'tandem-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'tandem.ico'), $out.ToArray())
Write-Host "Wrote tandem.ico, tandem-32.png and tandem-256.png to $assets"
