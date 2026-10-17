[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LayoutPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "SharedRuntimeLayout.Common.ps1")

$verified = Test-SharedRuntimeLayout -LayoutPath $LayoutPath
Write-Host ("共享运行时布局校验通过：{0}" -f $verified.LayoutPath)
Write-Host ("  hostfxr：{0}，文件数 {1}，迁移映射 {2} 项" -f `
        $verified.HostFxrVersion, $verified.FileCount, $verified.LegacyMappedFiles)
