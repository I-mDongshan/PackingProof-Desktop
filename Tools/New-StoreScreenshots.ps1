# 把 README 里实际使用的界面截图转成 Store 可上传的 PNG。
# 不叠加标题、Logo、白边或营销文案；只在低于 1366x768 时做最小比例放大。
param(
    [string]$SourceRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "Image"),
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "docs\store\Listing\Screenshots")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sources = @(
    @{ File = "软件截图.jpg"; Output = "01-main.png" },
    @{ File = "Replay.jpg"; Output = "02-replay.png" },
    @{ File = "WebService.jpg"; Output = "03-web-playback.png" },
    @{ File = "Statistics.jpg"; Output = "04-statistics.png" }
)

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($item in $sources) {
    $sourcePath = Join-Path $SourceRoot $item.File
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "找不到截图：$sourcePath"
    }

    $source = [System.Drawing.Image]::FromFile($sourcePath)
    try {
        $scale = 1.0
        if ($source.Width -lt 1366 -or $source.Height -lt 768) {
            $scale = [Math]::Max(1366.0 / $source.Width, 768.0 / $source.Height)
        }
        $width = [int][Math]::Round($source.Width * $scale)
        $height = [int][Math]::Round($source.Height * $scale)

        $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::White)
            $graphics.DrawImage($source, 0, 0, $width, $height)
            $outputPath = Join-Path $OutputDir $item.Output
            $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host "已生成：$outputPath ($width x $height)"
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}
