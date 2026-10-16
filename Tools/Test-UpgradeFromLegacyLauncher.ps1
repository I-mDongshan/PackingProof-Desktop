[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LegacyLauncherPath,
    [string]$BaselineAppDir = "",
    [string]$PackageDir = "",
    [string]$Version = "0.0.83",
    [int]$AppStartupSeconds = 90,
    [switch]$KeepWorkspace
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# 端到端验证真实升级路径：老启动器（0.0.78 时代，早于 root 落点支持）+ 补丁基线版本的 app\ +
# 新版的 pending 补丁。这正是 0.0.82 现场失败的那条路（老启动器报 "Patch 文件不存在：dotnet.exe"），
# 也是所有老装机升到自包含新版时实际会走的路。
#
# 断言：老启动器装得上、app 变成新版、主程序启动后把共享运行时布局落成到安装根目录、布局逐文件自检通过。

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("legacy-upgrade-" + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "[OK]   $Message" }
    else { Write-Host "[FAIL] $Message"; $failures.Add($Message) }
}

# 启动器崩溃时会弹阻塞对话框；测完按安装目录前缀清掉，别留在机器上。
function Stop-LeftoverProcesses {
    param([string]$InstallRoot)
    $prefix = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='ExpressPackingMonitoring.exe'")) {
        $path = [string]$process.ExecutablePath
        if ($path.Length -eq 0) { continue }
        if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

if ([string]::IsNullOrWhiteSpace($BaselineAppDir)) {
    $BaselineAppDir = Join-Path $repoRoot "package\ExpressPackingMonitoring+v0.0.18\ExpressPackingMonitoring+v0.0.18\app"
}
if ([string]::IsNullOrWhiteSpace($PackageDir)) {
    $PackageDir = Join-Path $repoRoot "package\PackingProof+v$Version"
}
$releaseTag = "v$Version"

foreach ($required in @($LegacyLauncherPath, $BaselineAppDir,
        (Join-Path $PackageDir "PackingProof_AppPatch_$releaseTag.zip"),
        (Join-Path $PackageDir "update_$releaseTag.json"))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "缺少输入：$required" }
}

$previousUserDataDir = $env:EPM_USER_DATA_DIR
try {
    $installRoot = Join-Path $workspace "install"
    $installAppDir = Join-Path $installRoot "app"
    $userDataDir = Join-Path $workspace "userdata"
    Copy-Item -LiteralPath (Join-Path $LegacyLauncherPath "..") -Destination $installRoot -Recurse -Force
    # 老启动器只留 exe，其余是手动更新脚本，不参与升级。
    Move-Item -LiteralPath (Join-Path $installRoot "ExpressPackingMonitoring.exe") -Destination (Join-Path $installRoot "ExpressPackingMonitoring.exe") -Force
    Copy-Item -LiteralPath $BaselineAppDir -Destination $installAppDir -Recurse -Force
    Write-Host "假安装目录：$installRoot（老启动器 + 基线 app\）"

    # 现场形态：pending 里放着新版的更新描述与补丁包。
    $pendingDir = Join-Path $userDataDir "cache\updates\pending"
    [System.IO.Directory]::CreateDirectory($pendingDir) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PackageDir "update_$releaseTag.json") `
        -Destination (Join-Path $pendingDir "update_manifest.json") -Force
    Copy-Item -LiteralPath (Join-Path $PackageDir "PackingProof_AppPatch_$releaseTag.zip") `
        -Destination (Join-Path $pendingDir "PackingProof_AppPatch_$releaseTag.zip") -Force
    $env:EPM_USER_DATA_DIR = $userDataDir

    $launcher = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") `
        -PassThru -WorkingDirectory $installRoot
    $launcher | Wait-Process -Timeout ($AppStartupSeconds + 60) -ErrorAction SilentlyContinue
    if (-not $launcher.HasExited) { $launcher.Kill() }
    # 主程序是被启动器拉起来的：留足时间让它跑完启动早期的布局落成。
    Start-Sleep -Seconds 5
    Stop-LeftoverProcesses -InstallRoot $installRoot

    $launcherLog = Join-Path $userDataDir "log\launcher_update.log"
    $logText = if (Test-Path -LiteralPath $launcherLog) { Get-Content -Raw -LiteralPath $launcherLog -Encoding UTF8 } else { "" }
    Assert-True ($logText -notlike "*Patch 文件不存在*") "老启动器没有报「Patch 文件不存在」（0.0.82 现场就是这么失败的）"
    Assert-True ($logText -like "*pending Patch 更新完成*") "老启动器成功应用了 pending 补丁"

    $appDll = Join-Path $installAppDir "ExpressPackingMonitoring.dll"
    Assert-True (Test-Path -LiteralPath $appDll) "app\ExpressPackingMonitoring.dll 在"
    if (Test-Path -LiteralPath $appDll) {
        $fileVersion = (Get-Item -LiteralPath $appDll).VersionInfo.FileVersion
        Assert-True ($fileVersion -like "$Version*") "升级后 app 版本是 $Version（实际 $fileVersion）"
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $installAppDir "coreclr.dll")) "升级后 app\ 是自包含形态（有 coreclr.dll）"

    $layoutPath = Join-Path $installRoot "runtime"
    Assert-True (Test-Path -LiteralPath (Join-Path $layoutPath "layout.json")) "主程序启动后落成了共享运行时布局"
    if (Test-Path -LiteralPath (Join-Path $layoutPath "layout.json")) {
        Test-SharedRuntimeLayout -LayoutPath $layoutPath | Out-Null
        Assert-True $true "落成的布局通过逐文件自检"
        $seedManifest = Get-Content -Raw -Encoding UTF8 (Join-Path $installAppDir "runtime-seed\layout.json") | ConvertFrom-Json
        $expected = @($seedManifest.files).Count
        $actual = @(Get-ChildItem -LiteralPath $layoutPath -Recurse -File).Count - 1
        Assert-True ($actual -eq $expected) "布局文件数与清单一致（$actual / $expected）"
    }

    # 布局本身就落在安装目录里（上一条已逐文件验证），不再去断言主程序日志的位置：
    # 主程序的数据目录不随 EPM_USER_DATA_DIR 走，日志可能在真实用户目录，拿它当判据不可靠。
}
finally {
    if ($null -eq $previousUserDataDir) { Remove-Item Env:\EPM_USER_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:EPM_USER_DATA_DIR = $previousUserDataDir }
    if (-not $KeepWorkspace) {
        Start-Sleep -Milliseconds 300
        try { [System.IO.Directory]::Delete($workspace, $true) } catch { }
    }
    else {
        Write-Host "工作目录保留：$workspace"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "老装机升级路径验证未通过，共 $($failures.Count) 项"
    exit 1
}
Write-Host "老装机升级路径验证通过"
exit 0
