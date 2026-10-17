[CmdletBinding()]
param(
    [string]$AppPublishDir = "",
    [switch]$KeepWorkspace
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# 自包含产物落成共享运行时布局（乙方案）的端到端验证：
#   1) 把应用按自包含发布（运行时平铺在 app\ 里）；
#   2) 按布局清单推导运行时种子（app\runtime-seed\），并强校验种子完整；
#   3) 把这份 app\ 当成安装目录下的 app\，让 RuntimeLayoutSeeder 落成 runtime\；
#   4) 断言落成的是**硬链接**（同一份磁盘数据，不额外占盘）；
#   5) 用一个框架依赖的探针进程带 DOTNET_ROOT=<安装目录>\runtime 启动，断言它真的从这份布局加载运行时
#      —— 这一步才证明"扩展能复用桌面端的运行时"。

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("rls-" + [Guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Host "[OK]   $Message" }
    else { Write-Host "[FAIL] $Message"; $failures.Add($Message) }
}

try {
    if ([string]::IsNullOrWhiteSpace($AppPublishDir)) {
        $AppPublishDir = Join-Path $workspace "publish"
        Write-Host "自包含发布应用..."
        $publishOutput = & dotnet publish (Join-Path $repoRoot "src\ExpressPackingMonitoring\ExpressPackingMonitoring.csproj") `
            -c Release --nologo -r win-x64 --self-contained true -p:PublishSingleFile=false `
            -o $AppPublishDir 2>&1
        if (-not (Test-Path -LiteralPath (Join-Path $AppPublishDir "ExpressPackingMonitoring.dll"))) {
            throw "自包含发布失败：$([Environment]::NewLine)$($publishOutput -join [Environment]::NewLine)"
        }
    }

    Assert-True (Test-Path -LiteralPath (Join-Path $AppPublishDir "coreclr.dll")) "app\ 里有 coreclr.dll（自包含发布）"
    Assert-True (Test-Path -LiteralPath (Join-Path $AppPublishDir "hostfxr.dll")) "app\ 里有 hostfxr.dll（自包含发布）"

    $layoutSourcePath = Join-Path $workspace "layout-source"
    New-SharedRuntimeLayout -AppDir $AppPublishDir -DestinationPath $layoutSourcePath | Out-Null
    Test-SharedRuntimeLayout -LayoutPath $layoutSourcePath | Out-Null
    $seed = New-RuntimeLayoutSeed -LayoutPath $layoutSourcePath -AppDir $AppPublishDir
    Write-Host ("运行时种子：布局 {0} 个文件，app\ 里同名同哈希 {1} 个，种子自带 {2} 个" -f `
            $seed.LayoutFiles, $seed.FromAppDir, $seed.FromSeed)
    Assert-True (Test-Path -LiteralPath (Join-Path $seed.SeedDirectory "layout.json")) "种子里有布局清单"
    Assert-True ($seed.FromSeed -gt 0) "种子里确实带了自包含产物缺的文件（$($seed.FromSeed) 个）"
    Assert-True ($seed.FromAppDir -gt 100) "大部分布局文件直接复用 app\ 里的同名文件（$($seed.FromAppDir) 个）"

    # 把自包含产物当成安装目录下的 app\（同卷改名，不复制）。
    $installRoot = Join-Path $workspace "install"
    [System.IO.Directory]::CreateDirectory($installRoot) | Out-Null
    $installAppDir = Join-Path $installRoot "app"
    Move-Item -LiteralPath $AppPublishDir -Destination $installAppDir

    # 落成器：起一个框架依赖的探针进程调用 RuntimeLayoutSeeder.Ensure（与主程序启动时走同一条代码）。
    $probeProjectDirectory = Join-Path $workspace "probe"
    [System.IO.Directory]::CreateDirectory($probeProjectDirectory) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $probeProjectDirectory "probe.csproj"), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="ExpressPackingMonitoring.UpdateCore">
      <HintPath>$((Join-Path $repoRoot "src\ExpressPackingMonitoring.UpdateCore\bin\Debug\net10.0\ExpressPackingMonitoring.UpdateCore.dll"))</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"@, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $probeProjectDirectory "Program.cs"), @'
using System.Runtime.InteropServices;
using ExpressPackingMonitoring.UpdateCore;

if (args.Length > 0 && args[0] == "consume")
{
    Console.WriteLine("runtime_dir=" + RuntimeEnvironment.GetRuntimeDirectory());
    return 0;
}

RuntimeLayoutSeedResult result = RuntimeLayoutSeeder.Ensure(args[0], Path.Combine(args[0], "app"));
Console.WriteLine($"materialized={result.Materialized} linked={result.LinkedFiles} copied={result.CopiedFiles} error={result.Error}");
return result.Materialized ? 0 : 1;
'@, [System.Text.UTF8Encoding]::new($false))
    $probeOutput = & dotnet run --project (Join-Path $probeProjectDirectory "probe.csproj") -c Release -- $installRoot 2>&1
    $probeLine = @($probeOutput | Where-Object { $_ -like "materialized=*" }) -join ""
    Write-Host "落成结果：$probeLine"
    Assert-True ($probeLine -like "materialized=True*") "落成器报告成功（$probeLine）"
    Assert-True ($probeLine -like "*error=") "落成器没有报错"
    Assert-True ((Test-Path -LiteralPath (Join-Path $installRoot "runtime\layout.json"))) "安装根目录出现 runtime\layout.json"
    Test-SharedRuntimeLayout -LayoutPath (Join-Path $installRoot "runtime") | Out-Null

    # 硬链接的判据：同一份数据两个名字 —— 改 app\ 里那个文件，runtime\ 里跟着变。
    if ($probeLine -match "linked=(\d+)") {
        $linked = [int]$Matches[1]
        Assert-True ($linked -gt 100) "落成的布局主要走硬链接（$linked 个）"
    }
    $probeFile = "System.Private.CoreLib.dll"
    $appSide = Join-Path $installAppDir $probeFile
    $layoutSide = Get-ChildItem -LiteralPath (Join-Path $installRoot "runtime\shared") -Recurse -File -Filter $probeFile |
        Select-Object -First 1
    if ($null -ne $layoutSide) {
        $originalSize = (Get-Item -LiteralPath $appSide).Length
        $bytes = [System.IO.File]::ReadAllBytes($appSide)
        $bytes[0] = [byte](($bytes[0] + 1) % 256)
        [System.IO.File]::WriteAllBytes($appSide, $bytes)
        $layoutFirst = [System.IO.File]::ReadAllBytes($layoutSide.FullName)[0]
        Assert-True ($layoutFirst -eq $bytes[0]) "改 app\ 里那份，runtime\ 里那份跟着变（硬链接：同一份数据）"
        $bytes[0] = [byte](($bytes[0] + 255) % 256)
        [System.IO.File]::WriteAllBytes($appSide, $bytes)
    }
    else {
        Assert-True $false "runtime\shared 里找不到 $probeFile"
    }

    # 真正证明共享：框架依赖的探针**用自己的 apphost**（扩展也是这样被拉起来的）带 DOTNET_ROOT 启动，
    # 运行时目录必须落在我们落成的布局里。不能走系统 dotnet.exe：那是 muxer，只认它自己的 dotnet root。
    $probePublishDir = Join-Path $workspace "probe-out"
    & dotnet publish (Join-Path $probeProjectDirectory "probe.csproj") -c Release --nologo --self-contained false `
        -p:PublishSingleFile=false -o $probePublishDir 2>&1 | Out-Null
    $probeExecutable = Join-Path $probePublishDir "probe.exe"
    if (-not (Test-Path -LiteralPath $probeExecutable -PathType Leaf)) {
        throw "探针没有生成 apphost：$probeExecutable"
    }
    $env:DOTNET_ROOT = Join-Path $installRoot "runtime"
    $consumerOutput = & $probeExecutable consume 2>&1
    $consumerLine = @($consumerOutput | Where-Object { $_ -like "runtime_dir=*" }) -join ""
    Write-Host "消费方：$consumerLine"
    Assert-True ($consumerLine.ToLowerInvariant().StartsWith(("runtime_dir=" + (Join-Path $installRoot "runtime")).ToLowerInvariant())) `
        "探针进程真的从落成的布局加载运行时（$consumerLine）"
    Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
}
finally {
    Remove-Item Env:\DOTNET_ROOT -ErrorAction SilentlyContinue
    if (-not $KeepWorkspace) {
        Start-Sleep -Milliseconds 300
        try { [System.IO.Directory]::Delete($workspace, $true) } catch { }
    }
    else {
        Write-Host "工作目录保留：$workspace"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "运行时布局落地验证未通过，共 $($failures.Count) 项"
    exit 1
}
Write-Host "运行时布局落地验证通过"
exit 0
