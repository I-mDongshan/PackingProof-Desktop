Set-StrictMode -Version Latest

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-FFmpegBaselineProperty {
    param($Object, [Parameter(Mandatory = $true)][string]$Name)

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-FFmpegBaselineExecutableIdentity {
    param([Parameter(Mandatory = $true)]$Baseline)

    # 首选身份：local_build（自建精简构建）优先，其次 package（上游包）。
    # 出包路径与缓存钉入都以首选身份为准；CI 回退到 package 时按 package 身份核对。
    $localBuild = Get-FFmpegBaselineProperty $Baseline 'local_build'
    if ($null -ne $localBuild) {
        return @{
            Size   = [long]$localBuild.executable_size
            Sha256 = [string]$localBuild.executable_sha256
        }
    }

    return @{
        Size   = [long]$Baseline.package.executable_size
        Sha256 = [string]$Baseline.package.executable_sha256
    }
}

function Get-FFmpegRegisteredIdentities {
    param([Parameter(Mandatory = $true)]$Baseline)

    # 已登记的全部可执行文件身份：local_build、package 与 app_patch_compatible_executables
    # （后两者覆盖远程 CI 回退用的上游包、以及老用户机器上历史随包版本）。
    # Assert-FFmpegExecutable 按"命中任一登记身份"放行——我们放置的每一个二进制都必须在这里有登记。
    $identities = New-Object "System.Collections.Generic.List[object]"
    $localBuild = Get-FFmpegBaselineProperty $Baseline 'local_build'
    if ($null -ne $localBuild) {
        $identities.Add(@{ Size = [long]$localBuild.executable_size; Sha256 = [string]$localBuild.executable_sha256 })
    }
    $package = Get-FFmpegBaselineProperty $Baseline 'package'
    if ($null -ne $package) {
        $identities.Add(@{ Size = [long]$package.executable_size; Sha256 = [string]$package.executable_sha256 })
    }
    foreach ($compatible in @($Baseline.app_patch_compatible_executables)) {
        $identities.Add(@{ Size = [long]$compatible.size; Sha256 = [string]$compatible.sha256 })
    }
    return $identities
}

function Read-FFmpegBaselineManifest {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "FFmpeg baseline manifest not found: $ManifestPath"
    }

    $baseline = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($baseline.schema_version -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$baseline.version) -or
        -not [string]::Equals([string]$baseline.runtime, "win-x64", [StringComparison]::OrdinalIgnoreCase) -or
        @($baseline.app_patch_compatible_executables).Count -lt 1) {
        throw "FFmpeg baseline manifest is incomplete or unsupported: $ManifestPath"
    }

    # 基线二选一：local_build（仓库 third_party/ffmpeg 的自建产物）或 package（上游可下载压缩包）
    $localBuild = Get-FFmpegBaselineProperty $baseline 'local_build'
    $package = Get-FFmpegBaselineProperty $baseline 'package'
    if ($null -eq $localBuild -and $null -eq $package) {
        throw "FFmpeg baseline manifest must declare either local_build or package: $ManifestPath"
    }

    if ($null -ne $package) {
        if ([string]::IsNullOrWhiteSpace([string]$package.file) -or
            [string]::IsNullOrWhiteSpace([string]$package.entry) -or
            [long]$package.size -le 0 -or
            [long]$package.executable_size -le 0 -or
            [string]$package.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            [string]$package.executable_sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            @($package.urls).Count -lt 1) {
            throw "FFmpeg baseline manifest is incomplete or unsupported: $ManifestPath"
        }
    }

    if ($null -ne $localBuild) {
        if ([string]::IsNullOrWhiteSpace([string]$localBuild.script) -or
            [string]::IsNullOrWhiteSpace([string]$localBuild.artifact) -or
            [long]$localBuild.executable_size -le 0 -or
            [string]$localBuild.executable_sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "FFmpeg baseline local_build entry is invalid: $ManifestPath"
        }

        foreach ($relativePath in @([string]$localBuild.script, [string]$localBuild.artifact)) {
            $normalized = $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
            if ([IO.Path]::IsPathRooted($normalized) -or
                $normalized.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0 -or
                $normalized.Split([IO.Path]::DirectorySeparatorChar) -contains '..') {
                throw "FFmpeg baseline local_build path must be relative to the repository root: $relativePath"
            }
        }
    }

    foreach ($compatibleExecutable in @($baseline.app_patch_compatible_executables)) {
        if ([string]::IsNullOrWhiteSpace([string]$compatibleExecutable.version) -or
            [long]$compatibleExecutable.size -le 0 -or
            [string]$compatibleExecutable.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "FFmpeg AppPatch compatibility entry is invalid: $ManifestPath"
        }
    }

    # 文件名与来源白名单只对 package（上游下载）基线有意义；local_build 的路径安全性在上面已按仓库相对路径校验
    if ($null -ne $package) {
        $packageFile = [string]$package.file
        if (-not [string]::Equals($packageFile, [IO.Path]::GetFileName($packageFile), [StringComparison]::Ordinal) -or
            $packageFile.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "FFmpeg baseline package file name is unsafe: $packageFile"
        }

        foreach ($urlText in @($package.urls)) {
            $url = $null
            if (-not [Uri]::TryCreate([string]$urlText, [UriKind]::Absolute, [ref]$url) -or
                -not [string]::Equals($url.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase) -or
                @('www.gyan.dev', 'github.com') -notcontains $url.Host.ToLowerInvariant()) {
                throw "FFmpeg baseline URL is not an approved HTTPS source: $urlText"
            }
        }
    }

    return $baseline
}

function Assert-FFmpegExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)]$Baseline
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "FFmpeg executable not found: $ExecutablePath"
    }

    $file = Get-Item -LiteralPath $ExecutablePath
    $actualHash = Get-Sha256Lower -Path $ExecutablePath
    foreach ($identity in (Get-FFmpegRegisteredIdentities -Baseline $Baseline)) {
        if ($file.Length -eq $identity.Size -and
            [string]::Equals($actualHash, [string]$identity.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    throw "FFmpeg executable size/SHA256 mismatch: $ExecutablePath"
}

function Get-SafeArchiveEntries {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable
    )

    $listing = @(& $SevenZipExecutable l -slt -- $PackagePath)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect FFmpeg package: $PackagePath"
    }

    $entries = New-Object System.Collections.Generic.List[string]
    $inEntries = $false
    foreach ($line in $listing) {
        if (-not $inEntries) {
            if ($line -eq "----------") { $inEntries = $true }
            continue
        }
        if ($line -match '^Path = (.+)$') {
            $entry = $Matches[1].Replace('/', '\')
            if ([IO.Path]::IsPathRooted($entry) -or
                $entry.Split('\', [StringSplitOptions]::RemoveEmptyEntries) -contains '..') {
                throw "FFmpeg package contains an unsafe path: $entry"
            }
            $entries.Add($entry)
        }
    }

    if ($entries.Count -eq 0) {
        throw "FFmpeg package does not contain any entries: $PackagePath"
    }
    return $entries.ToArray()
}

function Assert-FFmpegPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "FFmpeg package not found: $PackagePath"
    }
    $package = Get-Item -LiteralPath $PackagePath
    if ($package.Length -ne [long]$Baseline.package.size) {
        throw "FFmpeg package size mismatch: $PackagePath"
    }
    $actualHash = Get-Sha256Lower -Path $PackagePath
    if (-not [string]::Equals(
            $actualHash,
            [string]$Baseline.package.sha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "FFmpeg package SHA256 mismatch: $PackagePath"
    }

    $entries = @(Get-SafeArchiveEntries -PackagePath $PackagePath -SevenZipExecutable $SevenZipExecutable)
    $expectedEntry = ([string]$Baseline.package.entry).Replace('/', '\')
    $matches = @($entries | Where-Object {
        [string]::Equals($_, $expectedEntry, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1) {
        throw "FFmpeg package must contain exactly one pinned executable entry: $expectedEntry"
    }
}

function Expand-FFmpegBaselinePackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable
    )

    Assert-FFmpegPackage -PackagePath $PackagePath -Baseline $Baseline -SevenZipExecutable $SevenZipExecutable
    $destinationFullPath = [IO.Path]::GetFullPath($DestinationPath)
    $destinationDirectory = Split-Path -Parent $destinationFullPath
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $temporaryDirectory = Join-Path $destinationDirectory ('.ffmpeg-extract-' + [Guid]::NewGuid().ToString('N'))
    $temporaryDestination = "$destinationFullPath.tmp"
    New-Item -ItemType Directory -Force -Path $temporaryDirectory | Out-Null
    try {
        $entry = [string]$Baseline.package.entry
        & $SevenZipExecutable e -y -bso0 -bsp0 "-o$temporaryDirectory" -- $PackagePath $entry
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to extract FFmpeg from package: $PackagePath"
        }
        $extractedFiles = @(Get-ChildItem -LiteralPath $temporaryDirectory -File)
        if ($extractedFiles.Count -ne 1 -or
            -not [string]::Equals($extractedFiles[0].Name, 'ffmpeg.exe', [StringComparison]::OrdinalIgnoreCase)) {
            throw "FFmpeg extraction produced unexpected files"
        }
        Assert-FFmpegExecutable -ExecutablePath $extractedFiles[0].FullName -Baseline $Baseline

        Copy-Item -LiteralPath $extractedFiles[0].FullName -Destination $temporaryDestination -Force
        Assert-FFmpegExecutable -ExecutablePath $temporaryDestination -Baseline $Baseline
        Move-Item -LiteralPath $temporaryDestination -Destination $destinationFullPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDestination) {
            Remove-Item -LiteralPath $temporaryDestination -Force
        }
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
}

function Resolve-FFmpegBaselineExecutable {
    param(
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)][string]$CacheDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$SevenZipExecutable,
        [string]$RepositoryRoot = '',
        [switch]$RequireLocalBuild,
        [scriptblock]$DownloadFile,
        [int]$MaxAttemptsPerUrl = 3,
        [int]$RetryDelaySeconds = 2
    )

    $cacheFullPath = [IO.Path]::GetFullPath($CacheDirectory)
    New-Item -ItemType Directory -Force -Path $cacheFullPath | Out-Null
    $cachedExecutable = Join-Path $cacheFullPath 'ffmpeg.exe'
    $primaryIdentity = Get-FFmpegBaselineExecutableIdentity -Baseline $Baseline
    $cacheHoldsPrimary = $false
    try {
        Assert-FFmpegExecutable -ExecutablePath $cachedExecutable -Baseline $Baseline
        $cacheHoldsPrimary = ($null -ne $primaryIdentity) -and
            ((Get-Item -LiteralPath $cachedExecutable).Length -eq $primaryIdentity.Size) -and
            [string]::Equals((Get-Sha256Lower -Path $cachedExecutable), [string]$primaryIdentity.Sha256, [StringComparison]::OrdinalIgnoreCase)
        if (-not $RequireLocalBuild -or $cacheHoldsPrimary) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
            Copy-Item -LiteralPath $cachedExecutable -Destination $DestinationPath -Force
            Assert-FFmpegExecutable -ExecutablePath $DestinationPath -Baseline $Baseline
            Write-Host "Pinned FFmpeg reused from verified cache: $cachedExecutable"
            return
        }
    }
    catch {
        if (Test-Path -LiteralPath $cachedExecutable -PathType Leaf) {
            Remove-Item -LiteralPath $cachedExecutable -Force
        }
    }

    # 自建精简构建：优先用仓库构建产物（third_party/ffmpeg/build.sh 的输出）钉入缓存。
    # 产物缺失（典型：远程 CI、未构建的第三方开发者）时落到下面的 package 回退。
    # 只有显式传 -RequireLocalBuild 时才禁止回退；正常发布允许使用按哈希校验的上游包。
    $localBuild = Get-FFmpegBaselineProperty $Baseline 'local_build'
    if ($null -ne $localBuild) {
        if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
            throw "Resolving a local_build FFmpeg baseline requires -RepositoryRoot"
        }

        $identity = Get-FFmpegBaselineExecutableIdentity -Baseline $Baseline
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
        $artifactPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$localBuild.artifact).Replace('/', [IO.Path]::DirectorySeparatorChar)))
        if (Test-Path -LiteralPath $artifactPath -PathType Leaf) {
            if ((Get-Item -LiteralPath $artifactPath).Length -ne $identity.Size) {
                throw "PackingProof minimal FFmpeg artifact size mismatch: $artifactPath"
            }
            if (-not [string]::Equals((Get-Sha256Lower -Path $artifactPath), $identity.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "PackingProof minimal FFmpeg artifact SHA256 mismatch: $artifactPath"
            }

            Copy-Item -LiteralPath $artifactPath -Destination $cachedExecutable -Force
            Assert-FFmpegExecutable -ExecutablePath $cachedExecutable -Baseline $Baseline
            Copy-Item -LiteralPath $cachedExecutable -Destination $DestinationPath -Force
            Assert-FFmpegExecutable -ExecutablePath $DestinationPath -Baseline $Baseline
            Write-Host "Pinned FFmpeg prepared from local build: $artifactPath"
            return
        }

    }

    $cachedPackage = Join-Path $cacheFullPath ([string]$Baseline.package.file)
    $packageReady = $false
    try {
        Assert-FFmpegPackage -PackagePath $cachedPackage -Baseline $Baseline -SevenZipExecutable $SevenZipExecutable
        $packageReady = $true
    }
    catch {
        if (Test-Path -LiteralPath $cachedPackage -PathType Leaf) {
            Remove-Item -LiteralPath $cachedPackage -Force
        }
    }

    if (-not $packageReady) {
        if ($null -eq $DownloadFile) {
            $DownloadFile = { param($Url, $Path) Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing -TimeoutSec 120 }
        }
        $lastError = $null
        foreach ($url in @($Baseline.package.urls)) {
            $attempt = 0
            while ($attempt -lt $MaxAttemptsPerUrl) {
                $attempt++
                $temporaryPackage = "$cachedPackage.download"
                try {
                    if (Test-Path -LiteralPath $temporaryPackage) {
                        Remove-Item -LiteralPath $temporaryPackage -Force
                    }
                    & $DownloadFile ([string]$url) $temporaryPackage
                    Assert-FFmpegPackage -PackagePath $temporaryPackage -Baseline $Baseline -SevenZipExecutable $SevenZipExecutable
                    Move-Item -LiteralPath $temporaryPackage -Destination $cachedPackage -Force
                    $packageReady = $true
                    break
                }
                catch {
                    $lastError = $_
                    if (Test-Path -LiteralPath $temporaryPackage) {
                        Remove-Item -LiteralPath $temporaryPackage -Force
                    }
                    if ($attempt -lt $MaxAttemptsPerUrl) {
                        Write-Warning "FFmpeg dependency download failed (attempt $attempt/$MaxAttemptsPerUrl), retrying: $url"
                        if ($RetryDelaySeconds -gt 0) {
                            Start-Sleep -Seconds $RetryDelaySeconds
                        }
                    }
                    else {
                        Write-Warning "FFmpeg dependency download failed after $MaxAttemptsPerUrl attempts, trying next source: $url"
                    }
                }
            }
            if ($packageReady) {
                break
            }
        }
        if (-not $packageReady) {
            throw "Unable to obtain verified FFmpeg dependency: $lastError"
        }
    }

    Expand-FFmpegBaselinePackage `
        -PackagePath $cachedPackage `
        -DestinationPath $cachedExecutable `
        -Baseline $Baseline `
        -SevenZipExecutable $SevenZipExecutable

    # 可选的严格体积优化模式：只有调用方显式要求时才拒绝上游包回退。
    if ($RequireLocalBuild) {
        throw "已启用 -RequireLocalBuild，但本机既没有精简构建产物也没有含精简身份的缓存；请运行 third_party/ffmpeg/build.sh 或去掉该可选门禁: $cachedExecutable"
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DestinationPath) | Out-Null
    Copy-Item -LiteralPath $cachedExecutable -Destination $DestinationPath -Force
    Assert-FFmpegExecutable -ExecutablePath $DestinationPath -Baseline $Baseline
    Write-Host "Pinned FFmpeg prepared: $DestinationPath"
}
