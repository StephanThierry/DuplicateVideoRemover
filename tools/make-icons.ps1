# Makes the app icons from a square-ish picture: a centred square crop with rounded corners.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make-icons.ps1 -Source ressource/appicon.png -OutDir <folder>
# Then copy appicon.ico and appicon-32.png to apps/Desktop/Assets, appicon.ico to apps/Web,
# and favicon.ico plus appicon-32/180/192/512.png to apps/Web/wwwroot.
param([string]$Source, [string]$OutDir)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($Source)
$side = [Math]::Min($src.Width, $src.Height)
$cropX = [int](($src.Width - $side) / 2); $cropY = [int](($src.Height - $side) / 2)
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    # Scale the centred square crop to this size, then paint it through a rounded square
    $scaled = New-Object System.Drawing.Bitmap($size, $size)
    $gs = [System.Drawing.Graphics]::FromImage($scaled)
    $gs.InterpolationMode = 'HighQualityBicubic'; $gs.PixelOffsetMode = 'HighQuality'; $gs.CompositingQuality = 'HighQuality'
    $gs.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)), $cropX, $cropY, $side, $side, [System.Drawing.GraphicsUnit]::Pixel)
    $gs.Dispose()
    $r = [Math]::Max(2.0, $size * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r; $e = $size - 0.5
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($e - $d, 0, $d, $d, 270, 90)
    $path.AddArc($e - $d, $e - $d, $d, $d, 0, 90); $path.AddArc(0, $e - $d, $d, $d, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.TextureBrush($scaled)
    $g.FillPath($brush, $path)
    $brush.Dispose(); $scaled.Dispose(); $g.Dispose()
    return $bmp
}

function PngBytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return ,$ms.ToArray() }

# Multi-size .ico with PNG-compressed entries (Windows Vista and later)
function WriteIco([int[]]$sizes, [string]$file) {
    $images = @(); foreach ($s in $sizes) { $b = Render $s; $images += ,(PngBytes $b); $b.Dispose() }
    $fs = [System.IO.File]::Create($file); $w = New-Object System.IO.BinaryWriter($fs)
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Close()
}

WriteIco @(16, 20, 24, 32, 40, 48, 64, 128, 256) (Join-Path $OutDir 'appicon.ico')
WriteIco @(16, 32, 48) (Join-Path $OutDir 'favicon.ico')
foreach ($s in 32, 180, 192, 512) { $b = Render $s; $b.Save((Join-Path $OutDir "appicon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose() }
$src.Dispose()
Get-ChildItem $OutDir | Select-Object Name, Length
