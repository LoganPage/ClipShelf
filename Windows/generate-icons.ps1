param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Technical asset conversion only: preserve all original PNG artwork. Keep this
# square crop synchronized with ThemeManager.Icon (36, 36, 440, 440). The 2 px
# transparent guard retains the original antialiased rounded corners.
$clipAssetDirectory = Join-Path $PSScriptRoot 'ClipShelf\Assets'
$clipIconDestination = Join-Path $clipAssetDirectory 'ClipShelf.ico'
$clipIconSizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$clipCrop = [Drawing.RectangleF]::new(36, 36, 440, 440)
$clipSourceBitmap = [Drawing.Bitmap]::new((Join-Path $clipAssetDirectory 'AppIcon1.png'))
$clipIconFrames = [Collections.Generic.List[byte[]]]::new()
try {
    if ($clipSourceBitmap.Width -ne 512 -or $clipSourceBitmap.Height -ne 512) {
        throw 'The original icon geometry changed; review the crop before regenerating shell icons.'
    }
    foreach ($clipIconSize in $clipIconSizes) {
        $clipFrameBitmap = [Drawing.Bitmap]::new($clipIconSize, $clipIconSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $clipFrameGraphics = [Drawing.Graphics]::FromImage($clipFrameBitmap)
        $clipFrameStream = [IO.MemoryStream]::new()
        try {
            $clipFrameGraphics.Clear([Drawing.Color]::Transparent)
            $clipFrameGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $clipFrameGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $clipFrameGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $clipFrameGraphics.DrawImage($clipSourceBitmap, [Drawing.RectangleF]::new(0, 0, $clipIconSize, $clipIconSize), $clipCrop, [Drawing.GraphicsUnit]::Pixel)
            $clipFrameBitmap.Save($clipFrameStream, [Drawing.Imaging.ImageFormat]::Png)
            $clipIconFrames.Add($clipFrameStream.ToArray())
        } finally {
            $clipFrameStream.Dispose(); $clipFrameGraphics.Dispose(); $clipFrameBitmap.Dispose()
        }
    }
} finally { $clipSourceBitmap.Dispose() }

$clipIconStream = [IO.MemoryStream]::new()
$clipIconWriter = [IO.BinaryWriter]::new($clipIconStream)
try {
    $clipIconWriter.Write([uint16]0)
    $clipIconWriter.Write([uint16]1)
    $clipIconWriter.Write([uint16]$clipIconSizes.Count)
    $clipFrameOffset = 6 + 16 * $clipIconSizes.Count
    for ($clipFrameIndex = 0; $clipFrameIndex -lt $clipIconSizes.Count; $clipFrameIndex++) {
        $clipStoredSize = if ($clipIconSizes[$clipFrameIndex] -eq 256) { 0 } else { $clipIconSizes[$clipFrameIndex] }
        $clipIconWriter.Write([byte]$clipStoredSize)
        $clipIconWriter.Write([byte]$clipStoredSize)
        $clipIconWriter.Write([byte]0)
        $clipIconWriter.Write([byte]0)
        $clipIconWriter.Write([uint16]1)
        $clipIconWriter.Write([uint16]32)
        $clipIconWriter.Write([uint32]$clipIconFrames[$clipFrameIndex].Length)
        $clipIconWriter.Write([uint32]$clipFrameOffset)
        $clipFrameOffset += $clipIconFrames[$clipFrameIndex].Length
    }
    foreach ($clipIconFrame in $clipIconFrames) { $clipIconWriter.Write([byte[]]$clipIconFrame) }
    $clipIconWriter.Flush()
    $clipNewIconBytes = $clipIconStream.ToArray()
    # Avoid changing the file timestamp when a build regenerates identical data.
    $clipExistingBytes = if (Test-Path -LiteralPath $clipIconDestination) { [IO.File]::ReadAllBytes($clipIconDestination) } else { [byte[]]@() }
    if ([Convert]::ToBase64String($clipExistingBytes) -ne [Convert]::ToBase64String($clipNewIconBytes)) {
        [IO.File]::WriteAllBytes($clipIconDestination, $clipNewIconBytes)
    }
} finally { $clipIconWriter.Dispose(); $clipIconStream.Dispose() }
Write-Output "Shell icon: 10 native sizes (16-256 px), normalized rounded-square artwork."
