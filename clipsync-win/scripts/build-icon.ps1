# Convert ../AppIcon.png to ClipSync/Assets/AppIcon.ico as a multi-resolution
# ICO containing PNG-encoded entries at 256, 128, 64, 48, 32, 16 px.
# Run after AppIcon.png changes.
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Source)      { $Source      = Join-Path $root '..\..\AppIcon.png' }
if (-not $Destination) { $Destination = Join-Path $root '..\ClipSync\Assets\AppIcon.ico' }
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$destDir = Split-Path -Parent $Destination
if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

$sizes = @(256, 128, 64, 48, 32, 16)

$src = [System.Drawing.Image]::FromFile($Source)
try {
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.DrawImage($src, 0, 0, $s, $s)
        } finally { $g.Dispose() }
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngs += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    }
} finally { $src.Dispose() }

# ICO format:
#   ICONDIR     6 bytes        : reserved=0(2), type=1(2), count(2)
#   ICONDIRENTRY 16 bytes each : width(1), height(1), colors(1)=0, reserved(1)=0,
#                                planes(2)=1, bitCount(2)=32, sizeBytes(4), offset(4)
#   image data  ...            : raw PNG bytes
$count = $pngs.Count
$headerSize = 6 + 16 * $count
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
try {
    $bw.Write([uint16]0)        # reserved
    $bw.Write([uint16]1)        # type = icon
    $bw.Write([uint16]$count)
    $offset = $headerSize
    foreach ($p in $pngs) {
        $w = if ($p.Size -ge 256) { 0 } else { [byte]$p.Size }
        $h = $w
        $bw.Write([byte]$w)
        $bw.Write([byte]$h)
        $bw.Write([byte]0)      # palette colors
        $bw.Write([byte]0)      # reserved
        $bw.Write([uint16]1)    # color planes
        $bw.Write([uint16]32)   # bits per pixel
        $bw.Write([uint32]$p.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $p.Bytes.Length
    }
    foreach ($p in $pngs) { $bw.Write($p.Bytes) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Destination, $out.ToArray())
} finally {
    $bw.Dispose()
    $out.Dispose()
}

Write-Host "Wrote $Destination ($((Get-Item $Destination).Length) bytes, $count entries)"
