param(
    [string]$Root = $PSScriptRoot
)

Add-Type -AssemblyName System.Drawing

function Get-Utf8Text {
    param([string]$Base64)

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Base64))
}

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$outputPath = Join-Path $Root 'DeskBox-sspai-cover-1600x1200.png'
$screenshotPath = Join-Path $Root 'assets\deskbox-desktop-dark.png'
$logoPath = Join-Path $Root '..\images\brand\logo-200.png'

$bitmap = [System.Drawing.Bitmap]::new(1600, 1200, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

$background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F2F4F3'))
$ink = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#151917'))
$body = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#3F4743'))
$muted = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#69716D'))
$blue = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#1777D2'))
$yellow = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F4B63F'))
$line = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#CFD5D2'), 1)
$divider = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#D8DDDA'), 1)
$screenBorder = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(58, 10, 18, 14), 1)
$shadow = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(38, 16, 22, 19))
$labelFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(232, 15, 18, 16))
$white = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F7FAF8'))
$labelBorder = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(46, 255, 255, 255), 1)

$brandFont = [System.Drawing.Font]::new('Segoe UI', 36, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$smallFont = [System.Drawing.Font]::new('Segoe UI', 16, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$eyebrowFont = [System.Drawing.Font]::new('Segoe UI', 19, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$titleFont = [System.Drawing.Font]::new('Microsoft YaHei UI', 72, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$accentFont = [System.Drawing.Font]::new('Microsoft YaHei UI', 92, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subtitleFont = [System.Drawing.Font]::new('Microsoft YaHei UI', 27, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$featureFont = [System.Drawing.Font]::new('Microsoft YaHei UI', 18, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$labelFont = [System.Drawing.Font]::new('Microsoft YaHei UI', 17, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)

try {
    $graphics.FillRectangle($background, 0, 0, 1600, 1200)
    $graphics.DrawLine($line, 80, 62, 1520, 62)
    $graphics.DrawLine($divider, 650, 115, 650, 1075)

    $graphics.DrawString('DESKTOP / 01', $smallFont, $muted, [System.Drawing.PointF]::new(80, 25))
    $openSource = 'OPEN SOURCE / WINDOWS 11'
    $openSize = $graphics.MeasureString($openSource, $smallFont)
    $graphics.DrawString($openSource, $smallFont, $blue, [System.Drawing.PointF]::new(1520 - $openSize.Width, 25))

    $logo = [System.Drawing.Image]::FromFile($logoPath)
    try {
        $graphics.DrawImage($logo, 82, 112, 70, 70)
    }
    finally {
        $logo.Dispose()
    }

    $graphics.DrawString('DeskBox', $brandFont, $ink, [System.Drawing.PointF]::new(170, 125))
    $graphics.DrawString('NATIVE DESKTOP ORGANIZER', $eyebrowFont, $blue, [System.Drawing.PointF]::new(84, 266))

    $titleLine = Get-Utf8Text '57uZIFdpbmRvd3Mg5qGM6Z2i'
    $accentLine = Get-Utf8Text '5Yqg5LiA5bGC56ep5bqP'
    $subtitle = Get-Utf8Text '5Y6f55Sf6aOO5qC855qE5qGM6Z2i5pW055CG5bel5YW3'
    $features = Get-Utf8Text '5paH5Lu2ICDCtyAg5b6F5YqeICDCtyAg6ZqP6K6wICDCtyAg5aSp5rCUICDCtyAg6Z+z5LmQ'

    $graphics.DrawString($titleLine, $titleFont, $ink, [System.Drawing.PointF]::new(78, 320))
    $graphics.DrawString($accentLine, $accentFont, $blue, [System.Drawing.PointF]::new(78, 420))
    $accentSize = $graphics.MeasureString($accentLine, $accentFont)
    $graphics.FillRectangle($yellow, 84, 530, [math]::Min($accentSize.Width - 4, 490), 8)

    $graphics.DrawString($subtitle, $subtitleFont, $body, [System.Drawing.PointF]::new(84, 620))
    $graphics.DrawString($features, $featureFont, $muted, [System.Drawing.RectangleF]::new(84, 730, 500, 80))

    $shadowRectangle = [System.Drawing.RectangleF]::new(744, 168, 856, 930)
    $shadowPath = New-RoundedPath -Rectangle $shadowRectangle -Radius 24
    $graphics.FillPath($shadow, $shadowPath)

    $screenRectangle = [System.Drawing.RectangleF]::new(710, 140, 914, 930)
    $screenPath = New-RoundedPath -Rectangle $screenRectangle -Radius 24
    $state = $graphics.Save()
    $graphics.SetClip($screenPath)
    $screenshot = [System.Drawing.Image]::FromFile($screenshotPath)
    try {
        $destination = [System.Drawing.Rectangle]::new(710, 140, 914, 930)
        $source = [System.Drawing.Rectangle]::new(858, 0, 1062, 1080)
        $graphics.DrawImage($screenshot, $destination, $source, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $screenshot.Dispose()
        $graphics.Restore($state)
    }
    $graphics.DrawPath($screenBorder, $screenPath)

    $labelRectangle = [System.Drawing.RectangleF]::new(736, 990, 248, 52)
    $labelPath = New-RoundedPath -Rectangle $labelRectangle -Radius 7
    $graphics.FillPath($labelFill, $labelPath)
    $graphics.DrawPath($labelBorder, $labelPath)
    $graphics.DrawString('DESKBOX / WINUI 3', $labelFont, $white, [System.Drawing.PointF]::new(753, 1004))

    $graphics.FillRectangle($blue, 84, 1110, 12, 12)
    $graphics.DrawString('DESKBOX / WINUI 3', $smallFont, $ink, [System.Drawing.PointF]::new(108, 1105))

    $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $brandFont.Dispose()
    $smallFont.Dispose()
    $eyebrowFont.Dispose()
    $titleFont.Dispose()
    $accentFont.Dispose()
    $subtitleFont.Dispose()
    $featureFont.Dispose()
    $labelFont.Dispose()
    $background.Dispose()
    $ink.Dispose()
    $body.Dispose()
    $muted.Dispose()
    $blue.Dispose()
    $yellow.Dispose()
    $line.Dispose()
    $divider.Dispose()
    $screenBorder.Dispose()
    $shadow.Dispose()
    $labelFill.Dispose()
    $white.Dispose()
    $labelBorder.Dispose()
    $shadowPath.Dispose()
    $screenPath.Dispose()
    $labelPath.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output $outputPath
