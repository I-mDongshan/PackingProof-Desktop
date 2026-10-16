[CmdletBinding()]
param(
    [string]$PatchRoot = "",
    [string]$ConfigPath = "",
    [string]$AppRootPath = "",
    [switch]$SkipProcessCheck,
    [switch]$SkipVersionCheck,
    [string]$MutexName = "Local\ExpressPackingMonitoring.ManualPatch"
)

$ErrorActionPreference = "Stop"
$isWindows7 = ([Environment]::OSVersion.Version.Major -eq 6) -and
    ([Environment]::OSVersion.Version.Minor -eq 1)
if (-not $isWindows7) {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}

if ([string]::IsNullOrEmpty($PatchRoot)) {
    if ($MyInvocation.MyCommand.Path) {
        $PatchRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    else {
        throw "无法确定补丁根目录，请通过 -PatchRoot 指定"
    }
}

function Write-UpdateHost {
    param(
        [string]$Message = "",
        [System.ConsoleColor]$ForegroundColor = [System.ConsoleColor]::Gray
    )
    try {
        if ($PSBoundParameters.ContainsKey('ForegroundColor')) {
            Write-Host $Message -ForegroundColor $ForegroundColor
        }
        else {
            Write-Host $Message
        }
    }
    catch {
        # Windows 7 PowerShell 2.0 控制台偶发 0x1F（设备未就绪），
        # 输出失败不应中断更新流程；降级到标准输出尽力显示。
        try { [Console]::Out.WriteLine($Message) } catch { }
    }
}

function Test-IsNullOrWhiteSpace {
    param([string]$Value)
    return [string]::IsNullOrEmpty($Value) -or $Value.Trim().Length -eq 0
}

function ConvertFrom-Json35 {
    param([string]$Json)
    if (Get-Command ConvertFrom-Json -ErrorAction SilentlyContinue) {
        return $Json | ConvertFrom-Json
    }
    Add-Type -AssemblyName System.Web.Extensions -ErrorAction SilentlyContinue
    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $serializer.MaxJsonLength = 104857600
    return $serializer.DeserializeObject($Json)
}

function Get-JsonProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) {
        return $null
    }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.ContainsKey($Name)) {
            return $Object[$Name]
        }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }
    return $null
}

function Get-DefaultConfigPath {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (Test-IsNullOrWhiteSpace $localAppData) {
        $localAppData = $env:LOCALAPPDATA
    }
    if (Test-IsNullOrWhiteSpace $localAppData) {
        throw "无法定位 Windows 用户数据目录"
    }
    return Join-Path $localAppData "ExpressPackingMonitoring\config.json"
}

function Remove-ReadOnlyAttribute {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }

    $attributes = [System.IO.File]::GetAttributes($Path)
    if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
        [System.IO.File]::SetAttributes(
            $Path,
            ($attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)))
    }
}

function Get-NormalizedDirectory {
    param([string]$Path)

    if ((Test-IsNullOrWhiteSpace $Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "config.json 中的 AppRootDirectory 无效"
    }

    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]"\/")
}

function Get-ShortcutTargetPath {
    param([string]$ShortcutPath)

    $wscriptShell = $null
    $wscriptShortcut = $null
    try {
        $wscriptShell = New-Object -ComObject WScript.Shell
        $wscriptShortcut = $wscriptShell.CreateShortcut($ShortcutPath)
        $targetPath = ([string]$wscriptShortcut.TargetPath).Trim()
    }
    finally {
        if ($null -ne $wscriptShortcut) {
            try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($wscriptShortcut) } catch { }
        }
        if ($null -ne $wscriptShell) {
            try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($wscriptShell) } catch { }
        }
    }

    if (-not (Test-IsNullOrWhiteSpace $targetPath)) {
        return $targetPath
    }

    # Inno Setup creates valid Shell Link files that some WScript.Shell versions
    # can launch but return with an empty TargetPath. Ask the Windows Shell for
    # the same link target before treating the shortcut as invalid.
    $shellApplication = $null
    $shellFolder = $null
    $shellItem = $null
    $shellLink = $null
    try {
        $shellApplication = New-Object -ComObject Shell.Application
        $shellFolder = $shellApplication.Namespace((Split-Path -Parent $ShortcutPath))
        if ($null -ne $shellFolder) {
            $shellItem = $shellFolder.ParseName((Split-Path -Leaf $ShortcutPath))
        }
        if ($null -ne $shellItem) {
            $shellLink = $shellItem.GetLink
        }
        if ($null -ne $shellLink) {
            $targetPath = ([string]$shellLink.Path).Trim()
        }
    }
    finally {
        foreach ($comObject in @($shellLink, $shellItem, $shellFolder, $shellApplication)) {
            if ($null -ne $comObject) {
                try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) } catch { }
            }
        }
    }

    return $targetPath
}

function Resolve-AppRootCandidate {
    param([string]$CandidatePath)

    $cleanedPath = ([string]$CandidatePath).Trim()
    if ($cleanedPath.Length -ge 2) {
        $firstCharacter = $cleanedPath[0]
        $lastCharacter = $cleanedPath[$cleanedPath.Length - 1]
        if (($firstCharacter -eq '"' -and $lastCharacter -eq '"') -or
            ($firstCharacter -eq "'" -and $lastCharacter -eq "'")) {
            $cleanedPath = $cleanedPath.Substring(1, $cleanedPath.Length - 2).Trim()
        }
    }
    if (Test-IsNullOrWhiteSpace $cleanedPath) {
        throw "没有提供安装位置"
    }

    $resolvedInput = [System.IO.Path]::GetFullPath($cleanedPath)
    if ([string]::Equals([System.IO.Path]::GetExtension($resolvedInput), ".url", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "不支持网络快捷方式 .url，请拖入安装文件夹、程序 EXE 或 .lnk 快捷方式"
    }

    $visitedShortcuts = New-Object System.Collections.Generic.List[string]
    while ([string]::Equals([System.IO.Path]::GetExtension($resolvedInput), ".lnk", [System.StringComparison]::OrdinalIgnoreCase)) {
        if (-not (Test-Path -LiteralPath $resolvedInput -PathType Leaf)) {
            throw "快捷方式不存在：$resolvedInput"
        }
        $visitedKey = $resolvedInput.ToLowerInvariant()
        if ($visitedShortcuts.Contains($visitedKey)) {
            throw "快捷方式存在循环引用：$resolvedInput"
        }
        $visitedShortcuts.Add($visitedKey)

        $shortcutTarget = Get-ShortcutTargetPath -ShortcutPath $resolvedInput

        if (Test-IsNullOrWhiteSpace $shortcutTarget) {
            throw "快捷方式没有有效目标：$resolvedInput"
        }
        if (-not [System.IO.Path]::IsPathRooted($shortcutTarget)) {
            $shortcutTarget = Join-Path (Split-Path -Parent $resolvedInput) $shortcutTarget
        }
        $resolvedInput = [System.IO.Path]::GetFullPath($shortcutTarget)
        if ([string]::Equals([System.IO.Path]::GetExtension($resolvedInput), ".url", [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "快捷方式指向不支持的 .url 网络快捷方式"
        }
    }

    $candidateDirectories = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $resolvedInput -PathType Leaf) {
        $candidateDirectories.Add((Split-Path -Parent $resolvedInput))
    }
    elseif (Test-Path -LiteralPath $resolvedInput -PathType Container) {
        $candidateDirectories.Add($resolvedInput)
    }
    else {
        throw "路径不存在：$cleanedPath"
    }

    $inputDirectory = $candidateDirectories[0]
    $candidateDirectories.Add((Join-Path $inputDirectory "app"))
    foreach ($directory in $candidateDirectories) {
        $normalizedDirectory = Get-NormalizedDirectory -Path $directory
        $exePath = Join-Path $normalizedDirectory "ExpressPackingMonitoring.exe"
        $dllPath = Join-Path $normalizedDirectory "ExpressPackingMonitoring.dll"
        if ((Test-Path -LiteralPath $exePath -PathType Leaf) -and
            (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
            return $normalizedDirectory
        }
    }

    throw "无法识别安装目录，请拖入完整包根目录、app 目录或其中的 ExpressPackingMonitoring.exe"
}

function Request-AppRootDirectory {
    while ($true) {
        Write-UpdateHost ""
        Write-UpdateHost "未能从 config.json 自动找到软件安装目录。" -ForegroundColor Yellow
        Write-UpdateHost "请把安装文件夹或 ExpressPackingMonitoring.exe 拖到此窗口，然后按 Enter。"
        $draggedPath = Read-Host "安装位置"
        try {
            return Resolve-AppRootCandidate -CandidatePath $draggedPath
        }
        catch {
            Write-UpdateHost $_.Exception.Message -ForegroundColor Yellow
        }
    }
}

function Test-PathWithinDirectory {
    param(
        [string]$Path,
        [string]$Directory
    )

    $directoryPrefix = $Directory.TrimEnd([char[]]"\/") + [System.IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($directoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-VersionNumber {
    param([string]$Value)

    if ($Value -match "(\d+\.\d+\.\d+(?:\.\d+)?)") {
        return [Version]$Matches[1]
    }
    throw "无法识别版本号：$Value"
}

function Get-InstalledVersion {
    param([string]$DllPath)

    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($DllPath)
    foreach ($candidate in @($info.ProductVersion, $info.FileVersion)) {
        if ((-not (Test-IsNullOrWhiteSpace $candidate)) -and
            $candidate -match "(\d+\.\d+\.\d+(?:\.\d+)?)") {
            return [Version]$Matches[1]
        }
    }
    throw "无法读取当前程序版本：$DllPath"
}

function Get-FileSha256 {
    param([string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
    finally {
        # PowerShell 2.0 兼容：HashAlgorithm 用 Clear()，FileStream 用 Close()。
        $sha.Clear()
        $stream.Close()
    }
}

function Get-TargetProcesses {
    param([string]$TargetExePath)

    $targetFullPath = [System.IO.Path]::GetFullPath($TargetExePath)
    return @(Get-Process -Name "ExpressPackingMonitoring" -ErrorAction SilentlyContinue | Where-Object {
        try {
            (-not (Test-IsNullOrWhiteSpace $_.Path)) -and
                [string]::Equals(
                    [System.IO.Path]::GetFullPath($_.Path),
                    $targetFullPath,
                    [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    })
}

function Stop-TargetApplication {
    param([string]$TargetExePath)

    foreach ($process in @(Get-TargetProcesses -TargetExePath $TargetExePath)) {
        Write-UpdateHost "检测到程序正在运行，正在请求安全退出..." -ForegroundColor Yellow
        if (-not $process.CloseMainWindow()) {
            throw "无法请求程序退出，请手动关闭程序后重新双击更新脚本"
        }
        if (-not $process.WaitForExit(60000)) {
            throw "等待程序退出超时，请确认录像已经保存并手动关闭程序"
        }
    }
}

function Write-UpdateLog {
    param(
        [string]$LogPath,
        [string]$Message
    )

    try {
        $logDirectory = Split-Path -Parent $LogPath
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        $line = "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}" -f [DateTime]::Now, $Message
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    }
    catch {
    }
}

if (Test-IsNullOrWhiteSpace $ConfigPath) {
    $ConfigPath = Get-DefaultConfigPath
}

$ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
$PatchRoot = [System.IO.Path]::GetFullPath($PatchRoot)
$userDataDirectory = Split-Path -Parent $ConfigPath
$logPath = Join-Path $userDataDirectory "log\manual_update.log"
$mutex = New-Object System.Threading.Mutex($false, $MutexName)
$ownsMutex = $false
$exitCode = 0
$appliedFiles = New-Object System.Collections.Generic.List[object]
$backupRoot = ""

try {
    try {
        $ownsMutex = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $ownsMutex = $true
    }
    if (-not $ownsMutex) {
        throw "另一个增量更新正在执行"
    }

    Write-UpdateHost "正在检查增量更新包..." -ForegroundColor Cyan
    $configuredAppRoot = ""
    try {
        if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
            throw "config.json does not exist"
        }
        $config = ConvertFrom-Json35 -Json ([System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8))
        $appRootValue = [string](Get-JsonProperty $config "AppRootDirectory")
        if (Test-IsNullOrWhiteSpace $appRootValue) {
            throw "config.json does not contain AppRootDirectory"
        }
        $configuredAppRoot = Resolve-AppRootCandidate -CandidatePath $appRootValue
    }
    catch {
        Write-UpdateLog -LogPath $logPath -Message "Unable to read config for manual patch: $($_.Exception.Message)"
    }

    if (-not (Test-IsNullOrWhiteSpace $configuredAppRoot)) {
        $appRoot = $configuredAppRoot
    }
    elseif (-not (Test-IsNullOrWhiteSpace $AppRootPath)) {
        $appRoot = Resolve-AppRootCandidate -CandidatePath $AppRootPath
    }
    else {
        $appRoot = Request-AppRootDirectory
    }

    $targetExePath = Join-Path $appRoot "ExpressPackingMonitoring.exe"
    $targetDllPath = Join-Path $appRoot "ExpressPackingMonitoring.dll"

    $manifestPath = Join-Path $PatchRoot "patch_manifest.json"
    $patchFilesRoot = Join-Path $PatchRoot "files"
    # 过渡补丁会把共享运行时投递到安装目录的 runtime\（物理文件在 files\.runtime\ 下），
    # 老装机因此不用下载完整包；普通补丁仍然只往 app\ 里写。
    $installRoot = Split-Path -Parent ([System.IO.Path]::GetFullPath($appRoot))
    $runtimeRoot = Join-Path $installRoot "runtime"
    $runtimeFilesRoot = Join-Path $patchFilesRoot ".runtime"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "增量更新包缺少 patch_manifest.json"
    }
    if (-not (Test-Path -LiteralPath $patchFilesRoot -PathType Container)) {
        throw "增量更新包缺少 files 目录"
    }

    $manifest = ConvertFrom-Json35 -Json ([System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8))
    if (-not [string]::Equals([string](Get-JsonProperty $manifest "type"), "baseline_patch", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "不支持的增量更新包类型"
    }

    $baselineVersion = Get-VersionNumber -Value ([string](Get-JsonProperty $manifest "patch_baseline_version"))
    $latestVersion = Get-VersionNumber -Value ([string](Get-JsonProperty $manifest "latest_version"))
    if (-not $SkipVersionCheck) {
        $installedVersion = Get-InstalledVersion -DllPath $targetDllPath
        # 只拦比增量包更新的安装版本（那是降级）。同版本必须放行：
        # 现场会把「同版本 + 修复」的增量包直接发给用户双击更新，
        # 以前这里用 -ge，同版本会被判成「无需重复更新」直接退出。
        if ($installedVersion -gt $latestVersion) {
            Write-UpdateHost "当前已是 v$installedVersion，高于增量包 v$latestVersion，无需更新" -ForegroundColor Green
            Write-UpdateLog -LogPath $logPath -Message "Manual patch skipped: installed=$installedVersion, latest=$latestVersion"
            return
        }
        if ($installedVersion -lt $baselineVersion) {
            throw "当前版本 v$installedVersion 低于增量包基线 v$baselineVersion，请下载完整包更新"
        }
    }

    $manifestFiles = @(Get-JsonProperty $manifest "files")
    if ($manifestFiles.Count -eq 0) {
        throw "补丁清单没有可安装文件"
    }

    $validatedFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in $manifestFiles) {
        $relativePath = ([string](Get-JsonProperty $file "path")).Trim().Replace("/", "\")
        $segments = @($relativePath -split "[\\/]")
        if ((Test-IsNullOrWhiteSpace $relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $segments -contains "." -or
            $segments -contains "..") {
            throw "补丁清单包含不安全路径：$relativePath"
        }

        # 安装位置：缺省 app\，过渡补丁里的运行时条目写 runtime\；其它值一律拒绝。
        $targetRootName = "app"
        if ($file.PSObject.Properties["root"]) {
            $targetRootName = ([string]$file.root).Trim().ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace($targetRootName)) { $targetRootName = "app" }
        }
        if ($targetRootName -ne "app" -and $targetRootName -ne "runtime") {
            throw "补丁清单包含不支持的安装位置：$targetRootName"
        }
        $sourceBase = if ($targetRootName -eq "runtime") { $runtimeFilesRoot } else { $patchFilesRoot }
        $targetBase = if ($targetRootName -eq "runtime") { $runtimeRoot } else { $appRoot }

        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $sourceBase $relativePath))
        $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $targetBase $relativePath))
        if (-not (Test-PathWithinDirectory -Path $sourcePath -Directory $sourceBase) -or
            -not (Test-PathWithinDirectory -Path $destinationPath -Directory $targetBase)) {
            throw "补丁文件超出允许目录：$relativePath"
        }
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "补丁文件不存在：$relativePath"
        }

        $expectedSize = [long](Get-JsonProperty $file "size")
        $actualSize = (Get-Item -LiteralPath $sourcePath).Length
        if ($actualSize -ne $expectedSize) {
            throw "补丁文件大小校验失败：$relativePath"
        }

        $expectedHash = ([string](Get-JsonProperty $file "sha256")).Trim().ToLowerInvariant()
        $actualHash = Get-FileSha256 -Path $sourcePath
        if ((Test-IsNullOrWhiteSpace $expectedHash) -or $actualHash -ne $expectedHash) {
            throw "补丁文件 SHA256 校验失败：$relativePath"
        }

        $validatedFiles.Add((New-Object PSObject -Property @{
            RelativePath = $relativePath
            RootName = $targetRootName
            SourcePath = $sourcePath
            DestinationPath = $destinationPath
            ExpectedHash = $expectedHash
        }))
    }

    # 清理列表：老版本留下、新版本已不再发布的文件。路径校验、目录边界、与写入列表的冲突检查
    # 都跟 files 一条标准；实际删除前还会先备份，失败由下面的回滚把它放回去。
    $validatedDeletes = New-Object System.Collections.Generic.List[object]
    # 注意：清单里没有 deletes 时 Get-JsonProperty 返回 $null，@($null) 会变成"一个 null 元素"，
    # 必须先滤掉；同时兼容写成字符串的条目（与启动器同一套语义）。
    $manifestDeletes = @(Get-JsonProperty $manifest "deletes") | Where-Object { $null -ne $_ }
    foreach ($entry in $manifestDeletes) {
        $rawPath = if ($entry -is [string]) { [string]$entry } else { [string](Get-JsonProperty $entry "path") }
        $relativePath = $rawPath.Trim().Replace("/", "\")
        $segments = @($relativePath -split "[\\/]")
        if ((Test-IsNullOrWhiteSpace $relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $segments -contains "." -or
            $segments -contains "..") {
            throw "补丁清理列表包含不安全路径：$relativePath"
        }

        $targetRootName = "app"
        if ($null -ne $entry.PSObject.Properties["root"]) {
            $targetRootName = ([string]$entry.root).Trim().ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace($targetRootName)) { $targetRootName = "app" }
        }
        if ($targetRootName -ne "app" -and $targetRootName -ne "runtime") {
            throw "补丁清理列表包含不支持的安装位置：$targetRootName"
        }

        $conflict = @($validatedFiles | Where-Object {
                $_.RootName -eq $targetRootName -and
                [string]::Equals($_.RelativePath, $relativePath, [System.StringComparison]::OrdinalIgnoreCase)
            })
        if ($conflict.Count -gt 0) {
            throw "补丁清理列表与写入列表冲突：$relativePath"
        }

        $targetBase = if ($targetRootName -eq "runtime") { $runtimeRoot } else { $appRoot }
        $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $targetBase $relativePath))
        if (-not (Test-PathWithinDirectory -Path $destinationPath -Directory $targetBase)) {
            throw "补丁清理条目超出允许目录：$relativePath"
        }

        $validatedDeletes.Add((New-Object PSObject -Property @{
                    RelativePath    = $relativePath
                    RootName        = $targetRootName
                    DestinationPath = $destinationPath
                }))
    }

    if (-not $SkipProcessCheck) {
        Stop-TargetApplication -TargetExePath $targetExePath
    }
    if (@(Get-TargetProcesses -TargetExePath $targetExePath).Count -gt 0) {
        throw "程序仍在运行，请关闭后重试"
    }

    $backupRoot = Join-Path $userDataDirectory (
        "cache\manual_updates\backup_{0}_{1:yyyyMMdd_HHmmss}" -f $latestVersion, [DateTime]::Now)
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    Write-UpdateLog -LogPath $logPath -Message "Manual patch start: latest=$latestVersion, appRoot=$appRoot"

    foreach ($file in $validatedFiles) {
        $destinationDirectory = Split-Path -Parent $file.DestinationPath
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        $destinationExisted = Test-Path -LiteralPath $file.DestinationPath -PathType Leaf
        $originalAttributes = if ($destinationExisted) {
            [System.IO.File]::GetAttributes($file.DestinationPath)
        }
        else {
            [System.IO.FileAttributes]::Normal
        }
        $backupPath = Join-Path $backupRoot (Join-Path $file.RootName $file.RelativePath)
        if ($destinationExisted) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
            Copy-Item -LiteralPath $file.DestinationPath -Destination $backupPath -Force
            Remove-ReadOnlyAttribute -Path $backupPath
        }

        $appliedFiles.Add((New-Object PSObject -Property @{
            DestinationPath = $file.DestinationPath
            BackupPath = $backupPath
            DestinationExisted = $destinationExisted
            OriginalAttributes = $originalAttributes
        }))

        $tempPath = $file.DestinationPath + ".manual-update-" + [Guid]::NewGuid().ToString("N")
        $replaceBackupPath = $file.DestinationPath + ".manual-replace-backup-" + [Guid]::NewGuid().ToString("N")
        try {
            Copy-Item -LiteralPath $file.SourcePath -Destination $tempPath -Force
            if ($destinationExisted) {
                Remove-ReadOnlyAttribute -Path $file.DestinationPath
                [System.IO.File]::Replace($tempPath, $file.DestinationPath, $replaceBackupPath, $true)
            }
            else {
                [System.IO.File]::Move($tempPath, $file.DestinationPath)
            }
            Remove-ReadOnlyAttribute -Path $file.DestinationPath
        }
        finally {
            try {
                if (Test-Path -LiteralPath $tempPath) {
                    Remove-Item -LiteralPath $tempPath -Force
                }
                if (Test-Path -LiteralPath $replaceBackupPath) {
                    Remove-Item -LiteralPath $replaceBackupPath -Force
                }
            }
            catch {
            }
        }
    }

    $deletedCount = 0
    foreach ($entry in $validatedDeletes) {
        if (-not (Test-Path -LiteralPath $entry.DestinationPath -PathType Leaf)) { continue }
        $backupPath = Join-Path $backupRoot (Join-Path $entry.RootName $entry.RelativePath)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
        Copy-Item -LiteralPath $entry.DestinationPath -Destination $backupPath -Force
        Remove-ReadOnlyAttribute -Path $backupPath
        $appliedFiles.Add((New-Object PSObject -Property @{
                    DestinationPath      = $entry.DestinationPath
                    BackupPath           = $backupPath
                    DestinationExisted   = $true
                    OriginalAttributes   = [System.IO.File]::GetAttributes($entry.DestinationPath)
                }))
        Remove-ReadOnlyAttribute -Path $entry.DestinationPath
        Remove-Item -LiteralPath $entry.DestinationPath -Force
        $deletedCount++
    }
    if ($deletedCount -gt 0) {
        Write-UpdateLog -LogPath $logPath -Message "Manual patch cleaned $deletedCount obsolete files"
    }

    foreach ($file in $validatedFiles) {
        $installedHash = Get-FileSha256 -Path $file.DestinationPath
        if ($installedHash -ne $file.ExpectedHash) {
            throw "更新后文件校验失败：$($file.RelativePath)"
        }
    }

    if (-not $SkipVersionCheck) {
        $updatedVersion = Get-InstalledVersion -DllPath $targetDllPath
        if ($updatedVersion -ne $latestVersion) {
            throw "更新后版本校验失败：v$updatedVersion != v$latestVersion"
        }
    }

    try {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }
    catch {
        Write-UpdateLog -LogPath $logPath -Message "Backup cleanup deferred: $backupRoot, $($_.Exception.Message)"
    }
    $backupRoot = ""
    Write-UpdateLog -LogPath $logPath -Message "Manual patch completed: latest=$latestVersion"
    Write-UpdateHost ""
    Write-UpdateHost "增量更新完成，已升级到 v$latestVersion" -ForegroundColor Green
    Write-UpdateHost "请从原来的根目录启动器重新打开软件"
}
catch {
    $failure = $_.Exception.Message
    if ($appliedFiles.Count -gt 0) {
        Write-UpdateHost "更新失败，正在恢复原文件..." -ForegroundColor Yellow
        for ($index = $appliedFiles.Count - 1; $index -ge 0; $index--) {
            $applied = $appliedFiles[$index]
            try {
                if ($applied.DestinationExisted -and (Test-Path -LiteralPath $applied.BackupPath -PathType Leaf)) {
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $applied.DestinationPath) | Out-Null
                    Remove-ReadOnlyAttribute -Path $applied.DestinationPath
                    Copy-Item -LiteralPath $applied.BackupPath -Destination $applied.DestinationPath -Force
                    [System.IO.File]::SetAttributes($applied.DestinationPath, $applied.OriginalAttributes)
                }
                elseif (-not $applied.DestinationExisted -and (Test-Path -LiteralPath $applied.DestinationPath)) {
                    Remove-Item -LiteralPath $applied.DestinationPath -Force
                }
            }
            catch {
                Write-UpdateLog -LogPath $logPath -Message "Rollback failed: $($applied.DestinationPath), $($_.Exception.Message)"
            }
        }
    }

    Write-UpdateLog -LogPath $logPath -Message "Manual patch failed: $failure"
    Write-UpdateHost ""
    Write-UpdateHost "增量更新失败：$failure" -ForegroundColor Red
    Write-UpdateHost "原程序文件已尽可能恢复，详情见：$logPath"
    $exitCode = 1
}
finally {
    if ($ownsMutex) {
        $mutex.ReleaseMutex()
    }
    if ($null -ne $mutex) {
        # Windows 7 的 PowerShell 2.0 无法解析 Mutex.Dispose()，
        # 使用等价的 WaitHandle.Close() 释放互斥锁句柄。
        $mutex.Close()
    }
}

exit $exitCode
