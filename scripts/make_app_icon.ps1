# Regenerates the Ru app icon and the packaged-app logos from the wordmark's
# block 'r' (docs/assets/wordmark.svg): 4 columns x 6 half-cells, aurora green
# on a transparent background. Half-cells are kept on whole pixels, so the
# small sizes stay crisp without anti-aliasing.
#
# Usage (from the repository root, Windows PowerShell, no extra dependencies):
#   powershell -ExecutionPolicy Bypass -File scripts\make_app_icon.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\make_app_icon.ps1 -Verify
#
# AppIcon.ico carries 16/24/32/48/64/128 as 32bpp BMP frames and 256 as a PNG
# frame (the standard split: every shell and tool reads BMP frames, and the
# 256 PNG keeps the file small); the PNG set next to it is what a packaged
# MSIX build would use.

param(
    [switch]$Verify,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$assets = Join-Path $RepoRoot 'gui-winui3\Assets'
$iconPath = Join-Path $assets 'AppIcon.ico'
$green = [System.Drawing.Color]::FromArgb(255, 0, 230, 118)
$splashBackground = '#141414'

# Half-cells of the 'r' (column, half-row): a full stem, the top arms and the
# right shoulder, exactly as the wordmark rasterises:
#   #...
#   #.##
#   #..#  (rendered as 6 half-rows)
$halves = @(
    @(0, 0), @(0, 1), @(0, 2), @(0, 3), @(0, 4), @(0, 5),
    @(1, 0), @(1, 2),
    @(2, 0), @(2, 2),
    @(3, 0), @(3, 1), @(3, 3), @(3, 4), @(3, 5)
)

function New-MarkBitmap([int]$Width, [int]$Height, [string]$Fill) {
    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None

    if (-not [string]::IsNullOrEmpty($Fill)) {
        $background = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($Fill))
        $graphics.FillRectangle($background, 0, 0, $Width, $Height)
        $background.Dispose()
    }

    # An even cell size keeps every half-cell a whole pixel at every size.
    $cell = 2 * [Math]::Floor([Math]::Min($Width, $Height) / 8)
    if ($cell -lt 2) {
        $cell = 2
    }

    $originX = [int](($Width - $cell * 4) / 2)
    $originY = [int](($Height - $cell * 3) / 2)
    $brush = New-Object System.Drawing.SolidBrush($green)
    foreach ($half in $halves) {
        $x = $originX + $half[0] * $cell
        $y = $originY + $half[1] * ($cell / 2)
        $graphics.FillRectangle($brush, $x, $y, $cell, $cell / 2)
    }

    $brush.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Get-BmpFrame([System.Drawing.Bitmap]$Bitmap) {
    $width = $Bitmap.Width
    $height = $Bitmap.Height
    $rectangle = New-Object System.Drawing.Rectangle 0, 0, $width, $height
    $locked = $Bitmap.LockBits($rectangle, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $locked.Stride
        $pixels = New-Object byte[] ($stride * $height)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    } finally {
        $Bitmap.UnlockBits($locked)
    }

    # The AND mask marks fully transparent pixels, one bit per pixel, rows
    # padded to four bytes; modern Windows uses the alpha channel, the mask
    # keeps legacy consumers honest.
    $maskStride = [int]([Math]::Ceiling($width / 32.0) * 4)
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([int]40)
    $writer.Write([int]$width)
    $writer.Write([int]($height * 2))
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]0)
    $writer.Write([int](($width * $height * 4) + ($maskStride * $height)))
    $writer.Write([int]0)
    $writer.Write([int]0)
    $writer.Write([int]0)
    $writer.Write([int]0)

    for ($row = $height - 1; $row -ge 0; $row--) {
        for ($column = 0; $column -lt $width; $column++) {
            $at = $row * $stride + $column * 4
            $writer.Write($pixels[$at])      # blue
            $writer.Write($pixels[$at + 1])  # green
            $writer.Write($pixels[$at + 2])  # red
            $writer.Write($pixels[$at + 3])  # alpha
        }
    }

    for ($row = $height - 1; $row -ge 0; $row--) {
        $mask = New-Object byte[] $maskStride
        for ($column = 0; $column -lt $width; $column++) {
            if ($pixels[$row * $stride + $column * 4 + 3] -eq 0) {
                $index = [int][Math]::Floor($column / 8)
                $mask[$index] = $mask[$index] -bor (0x80 -shr ($column % 8))
            }
        }

        $writer.Write($mask)
    }

    $writer.Flush()
    $result = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
    # The leading comma keeps the byte[] intact: without it PowerShell
    # unrolls the array through the pipeline and the caller sees objects.
    return ,$result
}

function Write-Ico([string]$Path, $Frames) {
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([int16]0)
    $writer.Write([int16]1)
    $writer.Write([int16]$Frames.Count)
    $offset = 6 + ($Frames.Count * 16)
    foreach ($frame in $Frames) {
        $dimension = $frame.Size
        $writer.Write([byte]$(if ($dimension -ge 256) { 0 } else { $dimension }))
        $writer.Write([byte]$(if ($dimension -ge 256) { 0 } else { $dimension }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([int16]1)
        $writer.Write([int16]32)
        $writer.Write([int]$frame.Data.Length)
        $writer.Write([int]$offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $Frames) {
        $writer.Write($frame.Data)
    }

    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Get-PngSize([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $width = ([int]$bytes[16] * 16777216) + ([int]$bytes[17] * 65536) + ([int]$bytes[18] * 256) + [int]$bytes[19]
    $height = ([int]$bytes[20] * 16777216) + ([int]$bytes[21] * 65536) + ([int]$bytes[22] * 256) + [int]$bytes[23]
    return @($width, $height)
}

$logos = @(
    @{ Name = 'Square44x44Logo.scale-200.png'; Width = 88; Height = 88; Fill = $null },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Width = 24; Height = 24; Fill = $null },
    @{ Name = 'Square44x44Logo.targetsize-48_altform-lightunplated.png'; Width = 48; Height = 48; Fill = $null },
    @{ Name = 'Square150x150Logo.scale-200.png'; Width = 300; Height = 300; Fill = $null },
    @{ Name = 'Wide310x150Logo.scale-200.png'; Width = 620; Height = 300; Fill = $null },
    @{ Name = 'StoreLogo.png'; Width = 50; Height = 50; Fill = $null },
    @{ Name = 'LockScreenLogo.scale-200.png'; Width = 48; Height = 48; Fill = $null },
    @{ Name = 'SplashScreen.scale-200.png'; Width = 620; Height = 300; Fill = $splashBackground }
)

if ($Verify) {
    $bytes = [System.IO.File]::ReadAllBytes($iconPath)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    Write-Output "AppIcon.ico: $count frames"
    for ($index = 0; $index -lt $count; $index++) {
        $entry = 6 + ($index * 16)
        $width = $bytes[$entry]
        if ($width -eq 0) { $width = 256 }
        $height = $bytes[$entry + 1]
        if ($height -eq 0) { $height = 256 }
        $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
        $image = [BitConverter]::ToUInt32($bytes, $entry + 12)
        $kind = 'bmp'
        if ($bytes[$image] -eq 0x89 -and $bytes[$image + 1] -eq 0x50) { $kind = 'png' }
        Write-Output ("  {0}x{1} {2} {3} bytes" -f $width, $height, $kind, $length)
    }

    foreach ($logo in $logos) {
        $path = Join-Path $assets $logo.Name
        $size = Get-PngSize $path
        Write-Output ("  {0} {1}x{2}" -f $logo.Name, $size[0], $size[1])
    }

    exit 0
}

$frames = @()
foreach ($size in 16, 24, 32, 48, 64, 128) {
    $bitmap = New-MarkBitmap $size $size $null
    $frames += [pscustomobject]@{ Size = $size; Data = (Get-BmpFrame $bitmap) }
    $bitmap.Dispose()
}

foreach ($size in 256) {
    $bitmap = New-MarkBitmap $size $size $null
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }
    $stream.Dispose()
    $bitmap.Dispose()
}

Write-Ico $iconPath $frames
Write-Output "wrote AppIcon.ico with $($frames.Count) frames"

foreach ($logo in $logos) {
    $bitmap = New-MarkBitmap $logo.Width $logo.Height $logo.Fill
    $path = Join-Path $assets $logo.Name
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output ("wrote {0} {1}x{2}" -f $logo.Name, $logo.Width, $logo.Height)
}
