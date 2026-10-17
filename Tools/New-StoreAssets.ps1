# 从 app.ico 生成 Microsoft Store 提交所需的 PNG 图标与启动画面。
# 任务栏/开始菜单使用 AppList 的 unplated 变体；小尺寸会裁掉透明边并轻微放大，
# 让圆角图标和旁边的系统图标视觉尺寸一致。
param(
    [Parameter(Mandatory = $true)][string]$IconPath,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$BackgroundColor = "#FFFFFF"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $IconPath -PathType Leaf)) {
    throw "找不到图标：$IconPath"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$tempPng = Join-Path ([System.IO.Path]::GetTempPath()) ("packingproof-icon-" + [Guid]::NewGuid().ToString("N") + ".png")

function Get-LargestIconFramePng {
    param([string]$Path, [string]$Destination)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 22) { throw "ICO 文件过短：$Path" }

    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $offset = 6 + $i * 16
        $width = if ($bytes[$offset] -eq 0) { 256 } else { [int]$bytes[$offset] }
        $height = if ($bytes[$offset + 1] -eq 0) { 256 } else { [int]$bytes[$offset + 1] }
        $length = [BitConverter]::ToUInt32($bytes, $offset + 8)
        $imageOffset = [BitConverter]::ToUInt32($bytes, $offset + 12)
        if ($null -eq $best -or ($width * $height) -gt ($best.Width * $best.Height)) {
            $best = [pscustomobject]@{
                Width = $width
                Height = $height
                Length = $length
                Offset = $imageOffset
            }
        }
    }

    if ($null -eq $best) { throw "ICO 中没有可用图标帧：$Path" }

    $frame = New-Object byte[] $best.Length
    [Array]::Copy($bytes, [int]$best.Offset, $frame, 0, [int]$best.Length)
    if ($frame.Length -ge 8 -and $frame[0] -eq 0x89 -and $frame[1] -eq 0x50 -and
        $frame[2] -eq 0x4E -and $frame[3] -eq 0x47) {
        [System.IO.File]::WriteAllBytes($Destination, $frame)
        return
    }

    $icon = [System.Drawing.Icon]::new($Path, $best.Width, $best.Height)
    try {
        $bitmap = $icon.ToBitmap()
        try { $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png) }
        finally { $bitmap.Dispose() }
    }
    finally { $icon.Dispose() }
}

function Get-CroppedIcon {
    param([System.Drawing.Image]$Source)

    $bitmap = New-Object System.Drawing.Bitmap($Source.Width, $Source.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.DrawImage($Source, 0, 0, $Source.Width, $Source.Height)
    }
    finally {
        $graphics.Dispose()
    }

    $minX = $bitmap.Width
    $minY = $bitmap.Height
    $maxX = -1
    $maxY = -1
    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            if ($bitmap.GetPixel($x, $y).A -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt 0) {
        return $bitmap
    }

    $rect = New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
    try {
        return $bitmap.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    }
    finally {
        $bitmap.Dispose()
    }
}

function New-TransparentIcon {
    param(
        [System.Drawing.Image]$SourceImage,
        [int]$Size,
        [string]$FileName
    )

    # 已经裁掉了透明边；这里按 100% 填满画布，整个图标（含 R 角）一起等比缩放。
    # 不额外 overscan，避免把圆角外沿裁掉。
    $scale = [Math]::Min($Size / $SourceImage.Width, $Size / $SourceImage.Height)
    $drawWidth = $SourceImage.Width * $scale
    $drawHeight = $SourceImage.Height * $scale
    $x = ($Size - $drawWidth) / 2.0
    $y = ($Size - $drawHeight) / 2.0

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage(
            $SourceImage,
            (New-Object System.Drawing.RectangleF([float]$x, [float]$y, [float]$drawWidth, [float]$drawHeight)))
        $bitmap.Save((Join-Path $OutputDir $FileName), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-StoreTile {
    param(
        [System.Drawing.Image]$SourceImage,
        [int]$Width,
        [int]$Height,
        [int]$IconSize,
        [string]$FileName
    )

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml($BackgroundColor))

        $scale = [Math]::Min($IconSize / $SourceImage.Width, $IconSize / $SourceImage.Height)
        $drawWidth = [int][Math]::Round($SourceImage.Width * $scale)
        $drawHeight = [int][Math]::Round($SourceImage.Height * $scale)
        $x = [int](($Width - $drawWidth) / 2)
        $y = [int](($Height - $drawHeight) / 2)
        $graphics.DrawImage($SourceImage, $x, $y, $drawWidth, $drawHeight)
        $bitmap.Save((Join-Path $OutputDir $FileName), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    Get-LargestIconFramePng -Path $IconPath -Destination $tempPng
    $source = [System.Drawing.Image]::FromFile($tempPng)
    $cropped = Get-CroppedIcon -Source $source
    try {
        New-TransparentIcon -SourceImage $cropped -Size 50 -FileName "StoreLogo.png"
        New-TransparentIcon -SourceImage $cropped -Size 44 -FileName "Square44x44Logo.png"
        New-TransparentIcon -SourceImage $cropped -Size 44 -FileName "Square44x44Logo.altform-unplated.png"
        New-TransparentIcon -SourceImage $cropped -Size 44 -FileName "Square44x44Logo.altform-lightunplated.png"
        New-TransparentIcon -SourceImage $cropped -Size 44 -FileName "Square44x44Logo.targetsize-44_altform-unplated.png"
        New-TransparentIcon -SourceImage $cropped -Size 44 -FileName "Square44x44Logo.targetsize-44_altform-lightunplated.png"
        foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)) {
            New-TransparentIcon -SourceImage $cropped -Size $size -FileName "Square44x44Logo.targetsize-$size.png"
            New-TransparentIcon -SourceImage $cropped -Size $size -FileName "Square44x44Logo.targetsize-${size}_altform-unplated.png"
            New-TransparentIcon -SourceImage $cropped -Size $size -FileName "Square44x44Logo.targetsize-${size}_altform-lightunplated.png"
        }

        # 任务栏/开始菜单的 AppList 图标。官方要求 targetsize 的 unplated 变体；
        # 缺少这些文件时 Windows 会把图标缩小并加一块主题色底板。
        foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)) {
            New-TransparentIcon -SourceImage $cropped -Size $size -FileName "AppList.targetsize-$size.png"
            New-TransparentIcon -SourceImage $cropped -Size $size -FileName "AppList.targetsize-${size}_altform-unplated.png"
            New-TransparentIcon -SourceImage $cropped -Size $size -FileName "AppList.targetsize-${size}_altform-lightunplated.png"
        }
        foreach ($scale in @(100, 125, 150, 200, 400)) {
            $appListSize = [int][Math]::Round(44 * $scale / 100)
            New-TransparentIcon -SourceImage $cropped -Size $appListSize -FileName "AppList.scale-${scale}_altform-colorful_theme-light.png"
            New-TransparentIcon -SourceImage $cropped -Size $appListSize -FileName "AppList.scale-${scale}_altform-colorful_theme-dark.png"
        }

        New-TransparentIcon -SourceImage $cropped -Size 71 -FileName "Square71x71Logo.png"
        New-StoreTile -SourceImage $cropped -Width 150 -Height 150 -IconSize 138 -FileName "Square150x150Logo.png"
        New-StoreTile -SourceImage $cropped -Width 310 -Height 150 -IconSize 138 -FileName "Wide310x150Logo.png"
        New-StoreTile -SourceImage $cropped -Width 310 -Height 310 -IconSize 270 -FileName "Square310x310Logo.png"
        New-StoreTile -SourceImage $cropped -Width 620 -Height 300 -IconSize 240 -FileName "SplashScreen.png"
    }
    finally {
        $cropped.Dispose()
        $source.Dispose()
    }
}
finally {
    Remove-Item -LiteralPath $tempPng -Force -ErrorAction SilentlyContinue
}

Write-Host "Store 图标已生成：$OutputDir"
