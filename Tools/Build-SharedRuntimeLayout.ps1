[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$DotNetRoot = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# 为发布产物准备共享运行时布局（runtime\）。默认不改变任何现有安装行为：
# 这个脚本只负责生成并自检，是否把 runtime\ 放进安装包由发布流程决定。

. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")

$result = New-SharedRuntimeLayout -AppDir $AppDir -DestinationPath $OutputPath -DotNetRoot $DotNetRoot
$verified = Test-SharedRuntimeLayout -LayoutPath $result.LayoutPath

Write-Host ("共享运行时布局已生成：{0}" -f $result.LayoutPath)
Write-Host ("  hostfxr：{0}" -f $result.HostFxrVersion)
foreach ($framework in $result.Frameworks) {
    Write-Host ("  {0} {1}（要求 >= {2}）" -f $framework.name, $framework.version, $framework.requiredVersion)
}
Write-Host ("  文件数 {0}，合计 {1:N1} MB，迁移映射 {2} 项" -f `
        $result.FileCount, ($result.TotalBytes / 1MB), $result.LegacyMappedFiles)
Write-Host "自检通过"
