param(
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'T3.Voice\Assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-RoundedPath([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius) {
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = 2 * $Radius
    $shape.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $shape.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $shape.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $shape.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $shape.CloseFigure()
    return $shape
}

function New-VoiceBitmap([int]$Size) {
    $supersampling = 8
    $canvas = [System.Drawing.Bitmap]::new($Size * $supersampling, $Size * $supersampling, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $units = if ($Size -eq 16) { 16 } else { 24 }
    $scale = $Size * $supersampling / $units
    $graphics.ScaleTransform($scale, $scale)

    $tile = if ($Size -eq 16) { New-RoundedPath 1 1 14 14 4 } else { New-RoundedPath 1 1 22 22 6 }
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new(1, 1, $units - 2, $units - 2),
        [System.Drawing.Color]::FromArgb(255, 34, 154, 245),
        [System.Drawing.Color]::FromArgb(255, 20, 89, 217),
        [single]45
    )
    $graphics.FillPath($gradient, $tile)
    $microphone = if ($Size -eq 16) { New-RoundedPath 6 3 4 6 2 } else { New-RoundedPath 9 4.5 6 9 3 }
    $graphics.FillPath([System.Drawing.Brushes]::White, $microphone)
    $penWidth = if ($Size -eq 16) { 1.35 } else { 1.8 }
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single]$penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stand = [System.Drawing.Drawing2D.GraphicsPath]::new()
    if ($Size -eq 16) {
        $stand.AddLine([single]4, [single]7, [single]4, [single]8)
        $stand.AddArc(4, 4, 8, 8, 180, -180)
        $stand.AddLine([single]12, [single]8, [single]12, [single]7)
        $graphics.DrawPath($pen, $stand)
        $graphics.DrawLine($pen, [single]8, [single]12, [single]8, [single]13.5)
        $graphics.DrawLine($pen, [single]6.25, [single]13.5, [single]9.75, [single]13.5)
    }
    else {
        $stand.AddLine([single]6.5, [single]10.5, [single]6.5, [single]12.5)
        $stand.AddArc(6.5, 7, 11, 11, 180, -180)
        $stand.AddLine([single]17.5, [single]12.5, [single]17.5, [single]10.5)
        $graphics.DrawPath($pen, $stand)
        $graphics.DrawLine($pen, [single]12, [single]18, [single]12, [single]20.5)
        $graphics.DrawLine($pen, [single]9.5, [single]20.5, [single]14.5, [single]20.5)
    }
    $stand.Dispose()
    $pen.Dispose()
    $microphone.Dispose()
    $gradient.Dispose()
    $tile.Dispose()
    $graphics.Dispose()

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $downscale = [System.Drawing.Graphics]::FromImage($bitmap)
    $downscale.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $downscale.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $downscale.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $downscale.DrawImage($canvas, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
    $downscale.Dispose()
    $canvas.Dispose()
    return $bitmap
}

$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 24 24" fill="none">
  <defs>
    <linearGradient id="blue" x1="1" y1="1" x2="23" y2="23" gradientUnits="userSpaceOnUse">
      <stop stop-color="#229AF5"/>
      <stop offset="1" stop-color="#1459D9"/>
    </linearGradient>
  </defs>
  <rect x="1" y="1" width="22" height="22" rx="6" fill="url(#blue)"/>
  <rect x="9" y="4.5" width="6" height="9" rx="3" fill="white"/>
  <path d="M6.5 10.5V12.5a5.5 5.5 0 0 0 11 0V10.5M12 18v2.5m-2.5 0h5" stroke="white" stroke-width="1.8" stroke-linecap="round"/>
</svg>
'@
[System.IO.File]::WriteAllText((Join-Path $assetDirectory 't3-voice.svg'), $svg + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$images = @()
foreach ($size in $iconSizes) {
    $bitmap = New-VoiceBitmap $size
    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,$stream.ToArray()
        if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 't3-voice.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    }
    finally { $stream.Dispose(); $bitmap.Dispose() }
}

$file = [System.IO.File]::Create((Join-Path $assetDirectory 't3-voice.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$iconSizes.Count)
    $offset = 6 + 16 * $iconSizes.Count
    for ($index = 0; $index -lt $iconSizes.Count; $index++) {
        $sizeByte = if ($iconSizes[$index] -eq 256) { 0 } else { $iconSizes[$index] }
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
}
finally { $writer.Dispose() }

if ($PreviewPath) {
    $preview = [System.Drawing.Bitmap]::new(720, 260)
    $surface = [System.Drawing.Graphics]::FromImage($preview)
    $surface.Clear([System.Drawing.Color]::FromArgb(245, 245, 245))
    $surface.FillRectangle([System.Drawing.Brushes]::Black, 360, 0, 360, 260)
    $font = [System.Drawing.Font]::new('Segoe UI', 9)
    foreach ($origin in @(0, 360)) {
        $brush = if ($origin -eq 0) { [System.Drawing.Brushes]::DimGray } else { [System.Drawing.Brushes]::Silver }
        $position = $origin + 24
        foreach ($size in @(16, 20, 24, 32, 48)) {
            $bitmap = New-VoiceBitmap $size
            $surface.DrawImageUnscaled($bitmap, $position, 36 + (48 - $size) / 2)
            $surface.DrawString("$size px", $font, $brush, [single]$position, [single]96)
            $bitmap.Dispose()
            $position += 64
        }
        $large = New-VoiceBitmap 96
        $surface.DrawImageUnscaled($large, $origin + 132, 140)
        $large.Dispose()
    }
    $font.Dispose()
    $surface.Dispose()
    $preview.Save([System.IO.Path]::GetFullPath($PreviewPath), [System.Drawing.Imaging.ImageFormat]::Png)
    $preview.Dispose()
}

Write-Output "Exported t3-voice.svg, t3-voice.png and t3-voice.ico ($($iconSizes -join ', ') px)."
