# 本机验证商店 MSIX：导入测试证书、安装、按包身份启动，或卸载清理。
# 需要管理员权限（证书写入 LocalMachine\TrustedPeople）。
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$CertificatePath = "",
    [string]$PackageName = "PackingProof.PackingProof",
    [string]$LogPath = "",
    [switch]$Launch,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    Start-Transcript -Path $LogPath -Force | Out-Null
}

if ($Uninstall) {
    $subjects = @()
    foreach ($installed in @(Get-AppxPackage -Name $PackageName | Where-Object { $null -ne $_ })) {
        if (-not [string]::IsNullOrWhiteSpace($installed.Publisher)) {
            $subjects += [string]$installed.Publisher
        }
        Remove-AppxPackage -Package $installed.PackageFullName
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and
        (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        try {
            $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                (Resolve-Path -LiteralPath $CertificatePath).Path)
            $subjects += $certificate.Subject
        }
        catch {
            Write-Warning "读取测试证书失败，跳过按证书主题清理：$($_.Exception.Message)"
        }
    }
    # 兼容旧版脚本固定使用 CN=PackingProof 的测试证书。
    $subjects += "CN=PackingProof"
    $subjects = @($subjects | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique)
    Get-ChildItem "Cert:\LocalMachine\TrustedPeople" |
        Where-Object { $subjects -contains $_.Subject } |
        Remove-Item -Force
    Write-Host "已卸载测试包并清理测试证书"
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) { Stop-Transcript | Out-Null }
    return
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $resolvedCertificate = (Resolve-Path -LiteralPath $CertificatePath).Path
    $imported = Import-Certificate `
        -FilePath $resolvedCertificate `
        -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"
    Write-Host "测试证书已导入本机受信任人：$($imported.Thumbprint)"
}

Add-AppxPackage -Path $resolvedPackage -ForceApplicationShutdown
$package = Get-AppxPackage -Name $PackageName | Select-Object -First 1
if ($null -eq $package) {
    throw "安装后找不到包：$PackageName"
}

Write-Host "已安装：$($package.PackageFullName)"
Write-Host "安装目录：$($package.InstallLocation)"
if ($Launch) {
    Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
    Write-Host "已请求启动：$($package.PackageFamilyName)!App"
}
if (-not [string]::IsNullOrWhiteSpace($LogPath)) { Stop-Transcript | Out-Null }
