[CmdletBinding()]
param(
    [string]$LauncherPath = "",
    [string]$LayoutSourceAppDir = "",
    [switch]$KeepWorkspace
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# 共享运行时布局的端到端验证：在临时“安装目录”里放
#   根启动器 + app\（框架依赖的探针程序 + 老装机的平铺运行时副本）+ runtime\（共享布局）
# 然后真的跑一次启动器，验证：
#   1) 老装机的平铺运行时被搬进 runtime\.legacy，app\ 里的应用文件不受影响；
#   2) 主程序收到的 DOTNET_ROOT 指向安装目录里的 runtime\；
#   3) 探针程序报出的运行时目录确实落在 runtime\shared\ 里（证明真的用了布局而不是系统 .NET）。

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("srl-launch-" + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "[OK]   $Message" }
    else { Write-Host "[FAIL] $Message"; $failures.Add($Message) }
}

# 启动器启动的主程序如果崩了会弹一个阻塞的对话框，测完必须按安装目录前缀清掉，别留在机器上。
function Stop-LeftoverAppProcesses {
    param([string]$InstallRoot)

    $prefix = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='ExpressPackingMonitoring.exe'")) {
        $path = [string]$process.ExecutablePath
        if ($path.Length -eq 0) { continue }
        if (-not $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

# 没有共享布局时，框架依赖的探针只能靠机器上装的 .NET Desktop Runtime；
# 机器上也没有的话，.NET 宿主会弹“请安装 .NET”的系统对话框（测试不该在用户屏幕上弹窗），
# 这种情况直接跳过该场景并说明。
function Test-SystemDesktopRuntimeAvailable {
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $shared = Join-Path $root "dotnet\shared\Microsoft.WindowsDesktop.App"
        if (-not (Test-Path -LiteralPath $shared -PathType Container)) { continue }
        if (@(Get-ChildItem -LiteralPath $shared -Directory | Where-Object { $_.Name -match '^8\.' }).Count -gt 0) {
            return $true
        }
    }
    return $false
}

# 真实发布里根启动器是 AOT 单文件，这里按同样形态发布，既贴合现场也顺带验证新代码能过 AOT。
if ([string]::IsNullOrWhiteSpace($LauncherPath)) {
    $launcherPublishDirectory = Join-Path $workspace "launcher-out"
    $launcherPublishOutput = & dotnet publish (Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj") `
        -c Release --nologo -r win-x64 -o $launcherPublishDirectory 2>&1
    $LauncherPath = Join-Path $launcherPublishDirectory "ExpressPackingMonitoring.exe"
    if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) {
        throw "根启动器 AOT 发布失败：$([Environment]::NewLine)$($launcherPublishOutput -join [Environment]::NewLine)"
    }
}
if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) {
    throw "找不到根启动器：$LauncherPath"
}
if ([string]::IsNullOrWhiteSpace($LayoutSourceAppDir)) {
    $LayoutSourceAppDir = Join-Path $repoRoot "ExpressPackingMonitoring\bin\Debug\net8.0-windows\win-x64"
}
if (-not (Test-Path -LiteralPath $LayoutSourceAppDir -PathType Container)) {
    throw "应用发布目录不存在：$LayoutSourceAppDir（先 dotnet build ExpressPackingMonitoring.sln -c Debug）"
}

try {
    # 1. 框架依赖的探针：把启动器传进来的环境与真实运行时目录落到文件里
    $stubProjectDirectory = Join-Path $workspace "stub"
    [System.IO.Directory]::CreateDirectory($stubProjectDirectory) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $stubProjectDirectory "stub.csproj"), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>ExpressPackingMonitoring</AssemblyName>
  </PropertyGroup>
</Project>
"@, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $stubProjectDirectory "Program.cs"), @'
using System;
using System.IO;
using System.Runtime.InteropServices;

string outputPath = Environment.GetEnvironmentVariable("SRL_STUB_OUTPUT") ?? "";
if (outputPath.Length > 0)
{
    File.AppendAllText(outputPath, string.Join(Environment.NewLine, new[]
    {
        "dotnet_root=" + (Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? ""),
        "runtime_dir=" + RuntimeEnvironment.GetRuntimeDirectory(),
        "base_dir=" + AppContext.BaseDirectory,
        "args=" + string.Join(" ", args),
    }) + Environment.NewLine);
}
return 0;
'@, [System.Text.UTF8Encoding]::new($false))
    $stubPublishDirectory = Join-Path $workspace "stub-out"
    $stubPublishOutput = & dotnet publish (Join-Path $stubProjectDirectory "stub.csproj") -c Release --nologo `
        -r win-x64 --self-contained false -p:PublishSingleFile=false -o $stubPublishDirectory 2>&1
    if (-not (Test-Path -LiteralPath (Join-Path $stubPublishDirectory "ExpressPackingMonitoring.dll"))) {
        throw "探针程序发布失败：$([Environment]::NewLine)$($stubPublishOutput -join [Environment]::NewLine)"
    }

    # 2. 先准备好共享运行时布局（场景二才放进安装目录）。布局按应用发布目录的 runtimeconfig 定版本，
    #    框架依赖或自包含产物都能读。
    $layoutSourcePath = Join-Path $workspace "layout-source"
    New-SharedRuntimeLayout -AppDir $LayoutSourceAppDir -DestinationPath $layoutSourcePath | Out-Null
    Test-SharedRuntimeLayout -LayoutPath $layoutSourcePath | Out-Null
    $layoutManifest = Get-Content -Raw -Encoding UTF8 (Join-Path $layoutSourcePath "layout.json") | ConvertFrom-Json
    $legacySources = @{}
    foreach ($property in $layoutManifest.appFileMap.PSObject.Properties) {
        $legacySources[$property.Name] = Join-Path $layoutSourcePath (@($property.Value)[0] -replace '/', '\')
    }

    # 3. 组装安装目录：框架依赖的 app\ + 根启动器（老装机的平铺运行时副本到场景二再加）
    $installRoot = Join-Path $workspace "install"
    $appDirectory = Join-Path $installRoot "app"
    [System.IO.Directory]::CreateDirectory($appDirectory) | Out-Null
    Copy-Item -Path (Join-Path $stubPublishDirectory "*") -Destination $appDirectory -Recurse -Force
    Copy-Item -LiteralPath $LauncherPath -Destination (Join-Path $installRoot "ExpressPackingMonitoring.exe") -Force
    Write-Host "临时安装目录：$installRoot"

    # 4. 场景一：没有 runtime\ 时启动器必须什么都不动（灰度期不能干扰现有安装）
    $stubOutput = Join-Path $workspace "stub-output.txt"
    $env:SRL_STUB_OUTPUT = $stubOutput
    # 让启动器把状态写在临时目录里，不碰这台机器上真实的用户数据与更新缓存。
    $env:EPM_USER_DATA_DIR = Join-Path $workspace "userdata"
    $launcherLog = Join-Path $env:EPM_USER_DATA_DIR "log\launcher_update.log"
    $baselineSnapshot = (Get-ChildItem -LiteralPath $appDirectory -File | Measure-Object).Count
    $launcher = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") -PassThru -WorkingDirectory $installRoot
    $launcher | Wait-Process -Timeout 90 -ErrorAction SilentlyContinue
    if (-not $launcher.HasExited) { $launcher.Kill() }
    Stop-LeftoverAppProcesses -InstallRoot $installRoot
    if (Test-SystemDesktopRuntimeAvailable) {
        Assert-True (Test-Path -LiteralPath $stubOutput) "没有布局时主程序仍能启动"
    }
    else {
        Write-Host "[SKIP] 机器上没有系统 .NET Desktop Runtime，跳过“没有布局时启动主程序”这一步，避免弹宿主对话框"
    }
    Assert-True ((Get-ChildItem -LiteralPath $appDirectory -File | Measure-Object).Count -eq $baselineSnapshot) "没有布局时不得搬动任何文件"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot "runtime"))) "没有布局时不得凭空创建 runtime\"

    # 5. 场景二：补上老装机的平铺运行时副本与共享布局（增量升级上来的形态），验证迁移与 DOTNET_ROOT
    $copied = 0
    # 老装机的平铺运行时副本：按布局清单里的映射把同名文件铺回 app\，
    # 名字集合与真实自包含时代的 app\ 一致，用来验证迁移。
    foreach ($name in $legacySources.Keys) {
        Copy-Item -LiteralPath $legacySources[$name] -Destination (Join-Path $appDirectory $name) -Force
        $copied++
    }
    $layoutPath = Join-Path $installRoot "runtime"
    Move-Item -LiteralPath $layoutSourcePath -Destination $layoutPath
    Test-SharedRuntimeLayout -LayoutPath $layoutPath | Out-Null
    [System.IO.File]::WriteAllText($stubOutput, "", [System.Text.UTF8Encoding]::new($false))
    Write-Host "铺了 $copied 个平铺运行时文件，并把共享布局搬进安装目录"

    $beforeNames = @(Get-ChildItem -LiteralPath $appDirectory -File | Select-Object -ExpandProperty Name)
    $totalBefore = $beforeNames.Count
    $launcher = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") -PassThru -WorkingDirectory $installRoot
    $launcher | Wait-Process -Timeout 120 -ErrorAction SilentlyContinue
    if (-not $launcher.HasExited) { $launcher.Kill() }
    Stop-LeftoverAppProcesses -InstallRoot $installRoot

    $stubLines = @(Get-Content -LiteralPath $stubOutput -Encoding UTF8)
    $rootLine = @($stubLines | Where-Object { $_ -like 'dotnet_root=*' }) -join ""
    $runtimeLine = @($stubLines | Where-Object { $_ -like 'runtime_dir=*' }) -join ""
    Assert-True ($rootLine -eq ("dotnet_root=" + $layoutPath)) "主程序收到的 DOTNET_ROOT 指向安装目录里的 runtime\（实际：$rootLine）"
    Assert-True ($runtimeLine.ToLowerInvariant().StartsWith(("runtime_dir=" + $layoutPath).ToLowerInvariant())) `
        "主程序实际加载的运行时来自布局（实际：$runtimeLine）"

    $backupDirectory = Join-Path $layoutPath ".legacy"
    Assert-True (Test-Path -LiteralPath (Join-Path $backupDirectory "hostfxr.dll")) "老装机的 app\hostfxr.dll 已搬进 runtime\.legacy"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $appDirectory "hostfxr.dll"))) "app\ 里不再留 hostfxr.dll"
    Assert-True (Test-Path -LiteralPath (Join-Path $appDirectory "ExpressPackingMonitoring.dll")) "应用自身的程序集留在 app\"
    $movedCount = @(Get-ChildItem -LiteralPath $backupDirectory -File).Count
    Assert-True ($movedCount -gt 100) "迁移的平铺运行时文件数量合理（实际 $movedCount 个）"
    Assert-True ((Get-ChildItem -LiteralPath $appDirectory -File | Measure-Object).Count -lt $totalBefore) "app\ 文件数已减少"
    $logText = if (Test-Path -LiteralPath $launcherLog) { Get-Content -Raw -LiteralPath $launcherLog -Encoding UTF8 } else { "" }
    Assert-True ($logText -like "*共享运行时布局可用*") "启动器日志记录了共享运行时布局可用"
    Assert-True ($logText -like "*本次迁移旧运行时文件 $movedCount 个*") "启动器日志记录了迁移文件数（$movedCount）"

    # 5. 连续第二次启动后回滚备份应被清掉
    $launcher = Start-Process -FilePath (Join-Path $installRoot "ExpressPackingMonitoring.exe") -PassThru -WorkingDirectory $installRoot
    $launcher | Wait-Process -Timeout 120 -ErrorAction SilentlyContinue
    if (-not $launcher.HasExited) { $launcher.Kill() }
    Stop-LeftoverAppProcesses -InstallRoot $installRoot
    Assert-True (-not (Test-Path -LiteralPath $backupDirectory)) "第二次启动后回滚备份被清理"
    $logText = if (Test-Path -LiteralPath $launcherLog) { Get-Content -Raw -LiteralPath $launcherLog -Encoding UTF8 } else { "" }
    Assert-True ($logText -like "*已清理回滚备份*") "启动器日志记录了回滚备份清理"
}
finally {
    Remove-Item Env:\SRL_STUB_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item Env:\EPM_USER_DATA_DIR -ErrorAction SilentlyContinue
    if (-not $KeepWorkspace) {
        Start-Sleep -Milliseconds 300
        try { Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    }
    else {
        Write-Host "工作目录保留：$workspace"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "共享运行时启动验证未通过，共 $($failures.Count) 项"
    exit 1
}
Write-Host "共享运行时启动验证通过"
exit 0
