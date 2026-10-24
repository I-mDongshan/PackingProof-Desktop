# 对 FFmpeg 产物做能力矩阵核对：确认应用实际用到的编码器/封装/滤镜/协议都在。
# 切换随包二进制或重建精简版（third_party/ffmpeg/build.sh）后必须跑一遍。
# 用法：pwsh -NoProfile -File tools/ffmpeg-capability-check.ps1 [-FFmpegPath <ffmpeg.exe>]
param(
    [string]$FFmpegPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($FFmpegPath)) {
    $FFmpegPath = Join-Path $repoRoot "package\ffmpeg-build\ffmpeg.exe"
}
if (-not (Test-Path -LiteralPath $FFmpegPath -PathType Leaf)) {
    throw "FFmpeg not found: $FFmpegPath"
}

# 应用参数模板的出处：
# - 录制：MainViewModel.Ffmpeg.cs BuildFFmpegEncoderArgs（硬件三系 + libx264/x265 + AAC）
# - 网络摄像头：NetworkCameraSource.BuildArguments（RTSP 输入 + rawvideo 输出）
# - 网页转码/重封装：WebServer + WebTranscodeEncoderPolicy（mp4 +faststart、scale 滤镜）
# - 剪辑/封面：VideoClipService（-ss/-t -c copy、-frames:v 1 出 JPEG）
# - 探测/基准：RecordingProfileDetector、MainViewModel.Encoder.cs（lavfi color 源、null 输出）
$requiredEncoders = @(
    "libx264", "libx265",
    "h264_nvenc", "hevc_nvenc",
    "h264_amf", "hevc_amf",
    "h264_qsv", "hevc_qsv",
    "aac"
)
$requiredMuxers = @("mp4", "matroska", "image2", "null", "rawvideo")
$requiredFilters = @("aresample", "apad", "scale")
# tcp/tls/http/crypto 是真正的文件协议；rtsp/lavfi 在 ffmpeg 里是 demuxer，归到下面校验
# 应用允许的网络地址协议（NetworkCameraUrlPolicy）：rtsp/rtmp/rtmps/http/https
$requiredProtocols = @("tcp", "udp", "pipe", "file", "http", "https", "crypto", "tls", "rtmp", "rtmps")
# mp4/matroska 的 demuxer 是逗号组合名（mov,mp4,m4a... / matroska,webm），解析时按逗号拆开
$requiredDemuxers = @("h264", "hevc", "s16le", "mp4", "matroska", "image2", "lavfi", "rawvideo", "rtsp")
# AV1 硬件编码按能力探测可选提供；libsvtav1 与上游 Gyan essentials 一样不随包，CPU AV1 由应用隐藏
$optionalEncoders = @("av1_nvenc", "av1_amf", "av1_qsv")

function Invoke-FFmpegList {
    param([string[]]$FFmpegArguments)
    $output = & $FFmpegPath -hide_banner @FFmpegArguments 2>&1 | Out-String
    return $output
}

function Get-ListedNames {
    param([string]$ListOutput)

    # -encoders/-decoders/-filters：行首是 flags token（如 "V....D"/"A....."/"T.."），随后是名字
    # -muxers/-demuxers：行首是 1~2 个 "D"/"E" 标记（如 " E 3dostr"、" D E 3g2"），随后是名字
    # 其余行（标题、空行、"--" 分隔）忽略
    $names = New-Object "System.Collections.Generic.HashSet[string]"
    foreach ($line in ($ListOutput -split "`r?`n")) {
        $name = $null
        if ($line -match '^\s*[A-Z.]{2,8}\s+(\S+)\s') {
            $name = $Matches[1]
        }
        elseif ($line -match '^\s*(?:[DE]\s+){1,2}(\S+)\s') {
            $name = $Matches[1]
        }
        if (-not [string]::IsNullOrEmpty($name) -and $name -notmatch '^[-]+$') {
            # 逗号组合名（mov,mp4,m4a,... / matroska,webm）拆成独立条目
            foreach ($piece in $name.Split(',')) {
                if ($piece.Length -gt 0) {
                    [void]$names.Add($piece)
                }
            }
        }
    }
    return $names
}

$encoders = Get-ListedNames (Invoke-FFmpegList @("-encoders"))
$decoders = Get-ListedNames (Invoke-FFmpegList @("-decoders"))
$muxers = Get-ListedNames (Invoke-FFmpegList @("-muxers"))
$demuxers = Get-ListedNames (Invoke-FFmpegList @("-demuxers"))
$filters = Get-ListedNames (Invoke-FFmpegList @("-filters"))
$protocolsOutput = Invoke-FFmpegList @("-protocols")
$protocols = New-Object "System.Collections.Generic.HashSet[string]"
foreach ($line in ($protocolsOutput -split "`r?`n")) {
    if ($line -match '^\s*([a-z0-9]+)\s*$') {
        [void]$protocols.Add($Matches[1])
    }
}

$missing = New-Object "System.Collections.Generic.List[string]"
function Test-Set {
    param(
        [string]$SetName,
        [System.Collections.Generic.HashSet[string]]$Present,
        [string[]]$Required,
        [string[]]$Optional = @()
    )

    foreach ($name in $Required) {
        if ($Present.Contains($name)) {
            Write-Host "  [必需] $SetName ${name}: OK"
        }
        else {
            Write-Host "  [必需] $SetName ${name}: 缺失" -ForegroundColor Red
            $missing.Add("$SetName/$name")
        }
    }
    foreach ($name in $Optional) {
        $state = if ($Present.Contains($name)) { "存在" } else { "不存在（可选，应用会回退）" }
        Write-Host "  [可选] $SetName ${name}: $state" -ForegroundColor DarkGray
    }
}

Write-Host "FFmpeg 能力矩阵核对：$FFmpegPath"
& $FFmpegPath -hide_banner -version | Select-Object -First 2 | ForEach-Object { Write-Host "  $_" }
Write-Host ""

Test-Set "encoder" $encoders $requiredEncoders $optionalEncoders
Test-Set "muxer" $muxers $requiredMuxers
Test-Set "filter" $filters $requiredFilters
Test-Set "protocol" $protocols $requiredProtocols
Test-Set "demuxer" $demuxers $requiredDemuxers

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "能力矩阵不完整，缺少：$($missing -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "能力矩阵完整：应用用到的编码器/封装/滤镜/协议全部在位"
exit 0
