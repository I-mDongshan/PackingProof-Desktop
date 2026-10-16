[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LegacyLauncherPath,
    [string]$BaselineAppDir = "",
    [string]$PackageDir = "",
    [string]$Version = "0.0.83",
    [int]$AppStartupSeconds = 90,
    [switch]$SimulateSecondInstance,
    # 老启动器（0.0.83 基线之前）不认补丁的 deletes 字段：这是预期边界，不是缺陷，
    # 所以用它跑时要反过来断言"旧文件仍在"，别把边界当成 bug 报出来。
    [switch]$LauncherPredatesDeleteList,
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
    if ($SimulateSecondInstance) {
        # 现场形态：用户在安装过程中又点了一次（或主程序把直接启动重定向到根启动器），
        # 于是第二个启动器实例进来。老代码会"跳过安装、直接启动主程序"，把主程序拉到
        # 正在被覆盖的目录上 —— 0.0.83 现场就是这么起不来的。
        Start-Sleep -Milliseconds 1500
        Write-Host "再启动一次启动器，模拟安装过程中的第二次点击"
        $second = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") `
            -PassThru -WorkingDirectory $installRoot
        $second | Wait-Process -Timeout ($AppStartupSeconds + 120) -ErrorAction SilentlyContinue
        if (-not $second.HasExited) { $second.Kill() }
    }
    $launcher | Wait-Process -Timeout ($AppStartupSeconds + 60) -ErrorAction SilentlyContinue
    if (-not $launcher.HasExited) { $launcher.Kill() }
    # 主程序是被启动器拉起来的：留足时间让它跑完启动早期的布局落成。
    Start-Sleep -Seconds 5
    Stop-LeftoverProcesses -InstallRoot $installRoot

    $launcherLog = Join-Path $userDataDir "log\launcher_update.log"
    $logText = if (Test-Path -LiteralPath $launcherLog) { Get-Content -Raw -LiteralPath $launcherLog -Encoding UTF8 } else { "" }
    Assert-True ($logText -notlike "*Patch 文件不存在*") "老启动器没有报「Patch 文件不存在」（0.0.82 现场就是这么失败的）"
    Assert-True ($logText -like "*pending Patch 更新完成*") "老启动器成功应用了 pending 补丁"

    # 关键不变量：补丁装完之前不许启动主程序（装到一半启动 = 主程序拉到半新半旧的目录上起不来）。
    $logLines = @(Get-Content -LiteralPath $launcherLog -Encoding UTF8)
    $installedAt = -1
    $startedAt = -1
    for ($index = 0; $index -lt $logLines.Count; $index++) {
        if ($installedAt -lt 0 -and $logLines[$index] -like "*pending Patch 更新完成*") { $installedAt = $index }
        if ($startedAt -lt 0 -and $logLines[$index] -like "*已启动主程序*") { $startedAt = $index }
    }
    Assert-True ($installedAt -ge 0) "日志里能看到补丁安装完成"
    Assert-True ($startedAt -ge 0) "日志里能看到启动主程序"
    if ($installedAt -ge 0 -and $startedAt -ge 0) {
        Assert-True ($startedAt -gt $installedAt) "第一条「已启动主程序」在「pending Patch 更新完成」之后（没有在覆盖文件时启动主程序）"
    }

    $appDll = Join-Path $installAppDir "ExpressPackingMonitoring.dll"
    Assert-True (Test-Path -LiteralPath $appDll) "app\ExpressPackingMonitoring.dll 在"
    if (Test-Path -LiteralPath $appDll) {
        $fileVersion = (Get-Item -LiteralPath $appDll).VersionInfo.FileVersion
        Assert-True ($fileVersion -like "$Version*") "升级后 app 版本是 $Version（实际 $fileVersion）"
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $installAppDir "coreclr.dll")) "升级后 app\ 是自包含形态（有 coreclr.dll）"

    # 清理列表：补丁声明的 deletes 必须在升级后真的不在了，且不能误删别的东西。
    $patchManifestPath = Join-Path $workspace "patch_manifest.json"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $patchZip = [System.IO.Compression.ZipFile]::OpenRead(
        (Join-Path $PackageDir "PackingProof_AppPatch_$releaseTag.zip"))
    try {
        $manifestEntry = $patchZip.Entries | Where-Object { $_.FullName -eq "patch_manifest.json" }
        if ($null -eq $manifestEntry) { throw "补丁里没有 patch_manifest.json" }
        $reader = New-Object System.IO.StreamReader($manifestEntry.Open(), [System.Text.Encoding]::UTF8)
        try { $patchManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    }
    finally {
        $patchZip.Dispose()
    }
    $deletes = @()
    if ($null -ne $patchManifest.PSObject.Properties['deletes']) { $deletes = @($patchManifest.deletes) }
    Assert-True ($deletes.Count -gt 0) "补丁带上了清理列表（$($deletes.Count) 个）"
    $survivors = @()
    foreach ($entry in $deletes) {
        $relative = ([string]$entry.path) -replace '/', '\'
        if (Test-Path -LiteralPath (Join-Path $installAppDir $relative)) { $survivors += $relative }
    }
    if ($LauncherPredatesDeleteList) {
        Assert-True ($survivors.Count -eq $deletes.Count) `
            "老启动器不认清理列表：旧文件都还在（$($survivors.Count)/$($deletes.Count)），这是预期边界"
    }
    else {
        Assert-True ($survivors.Count -eq 0) "清理列表里的旧文件升级后都不在了（残留 $($survivors.Count) 个：$($survivors -join ', ')）"
        Assert-True (Test-Path -LiteralPath $appDll) "清理没有误删主程序 dll"
        Assert-True (Test-Path -LiteralPath (Join-Path $installAppDir "tools\ffmpeg.exe")) "清理走白名单：ffmpeg 仍在"
    }

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
