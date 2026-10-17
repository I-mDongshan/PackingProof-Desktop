# 发布版本号与产物名的统一规则。
#
# 打包脚本 tools\Publish-CleanPackage.ps1 与发布脚本 tools\Publish-Releases.ps1
# 必须用同一套归一化，否则就会出现「有时带 v、有时不带 v」的错配：
#   - tag 可能写成 v0.0.67、0.0.67，或带后缀的 v0.0.67-rc1
#   - 产物名固定是：目录、补丁包、本地校验清单用 v<纯版本号>；
#     Setup 与更新清单用不带 v 的纯版本号
# 任何一边自己用 tag 拼名字，都会在 tag 形式不同的时候找不到文件。

function Test-GiteeAttachmentFits {
    param([Parameter(Mandatory = $true)][long]$Size)

    # Gitee 单附件上限按 100MB 计算；需求是“小于 100MB 才直接传 Setup”。
    return $Size -lt 100MB
}

function Get-NormalizedReleaseVersion {
    param([string]$RawVersion)

    $value = "$RawVersion".Trim()
    if ($value.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(1)
    }

    $suffixIndex = $value.IndexOfAny(@('+', '-'))
    if ($suffixIndex -ge 0) {
        $value = $value.Substring(0, $suffixIndex)
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        return "0.0.0"
    }

    return $value
}

# 由精确 tag 推断本地产物名，返回：
#   NormalizedVersion / ReleaseTag / PackageRoot
#   SetupFileName / UpdateJsonFileName / AppPatchFileName / LauncherPatchFileName
function Get-ReleaseArtifactNames {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $normalizedVersion = Get-NormalizedReleaseVersion $Tag
    if ($normalizedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "tag $Tag 的版本号不是 X.Y.Z 形式，无法推断产物名"
    }

    $releaseTag = "v$normalizedVersion"
    return @{
        NormalizedVersion = $normalizedVersion
        ReleaseTag = $releaseTag
        PackageRoot = Join-Path $RepoRoot "package\PackingProof+$releaseTag"
        SetupFileName = "PackingProof_Setup_v$normalizedVersion.exe"
        UpdateJsonFileName = "update_v$normalizedVersion.json"
        AppPatchFileName = "PackingProof_AppPatch_$releaseTag.zip"
        LauncherPatchFileName = "PackingProof_LauncherPatch_$releaseTag.zip"
        StorePackageRoot = Join-Path $RepoRoot "package\store\PackingProof_Store_v$normalizedVersion"
        StorePackageFileName = "PackingProof_Store_v$normalizedVersion.msix"
        StoreManifestFileName = "store_package_$releaseTag.json"
    }
}

# Setup 与 Store MSIX 必须来自同一份应用负载。这里用“相对路径 + SHA256”的
# 稳定清单做指纹；pdb/dbg/xml 是 Store 打包时会主动剔除的文件，不参与比较。
function Get-AppPayloadManifestFiles {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    if (-not (Test-Path -LiteralPath $AppDir -PathType Container)) {
        throw "找不到应用负载目录：$AppDir"
    }

    return @(
        Get-ChildItem -LiteralPath $AppDir -Recurse -File |
            Where-Object { $_.Extension.ToLowerInvariant() -notin @(".pdb", ".dbg", ".xml") } |
            Sort-Object -Property FullName
    )
}

function Get-AppPayloadFileHashes {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    $map = [ordered]@{}
    foreach ($file in (Get-AppPayloadManifestFiles -AppDir $AppDir)) {
        $relativePath = [System.IO.Path]::GetRelativePath($AppDir, $file.FullName).Replace('\', '/')
        $map[$relativePath] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Get-AppPayloadFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    $map = Get-AppPayloadFileHashes -AppDir $AppDir
    $builder = [System.Text.StringBuilder]::new()
    foreach ($entry in $map.GetEnumerator()) {
        [void]$builder.Append([string]$entry.Key).Append("`n")
        [void]$builder.Append([string]$entry.Value).Append("`n")
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-ArchiveTextEntry {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName.Replace('\', '/') -eq $EntryName } |
            Select-Object -First 1
        if ($null -eq $entry) {
            throw "压缩包缺少文件：$EntryName（$ArchivePath）"
        }

        $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.Encoding]::UTF8)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-AppPayloadMatchesArchive {
    param(
        [Parameter(Mandatory = $true)][string]$AppDir,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    $expectedFiles = Get-AppPayloadFileHashes -AppDir $AppDir
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $entries[$entry.FullName.Replace('\', '/')] = $entry
        }

        foreach ($expected in $expectedFiles.GetEnumerator()) {
            $relativePath = [string]$expected.Key
            if (-not $entries.ContainsKey($relativePath)) {
                throw "MSIX 缺少 Setup 负载文件：$relativePath"
            }

            $stream = $entries[$relativePath].Open()
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $actualHash = ([System.BitConverter]::ToString(
                            $sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                }
                finally {
                    $sha.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }

            if (-not [string]::Equals(
                    $actualHash,
                    [string]$expected.Value,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "MSIX 与 Setup 负载文件不一致：$relativePath"
            }
        }

        return $expectedFiles.Count
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-StorePayloadManifest {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$AppDir
    )

    if ([string]::IsNullOrWhiteSpace([string]$Manifest.app_payload_sha256)) {
        throw "Store 包清单缺少 app_payload_sha256，无法证明与本次 Setup 同源；请重新出包"
    }

    $actualFingerprint = Get-AppPayloadFingerprint -AppDir $AppDir
    if (-not [string]::Equals(
            [string]$Manifest.app_payload_sha256,
            $actualFingerprint,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Store 包清单记录的应用负载与本次 Setup app\ 不一致（旧 MSIX 或旧清单），拒绝发布"
    }

    $fileCount = @(Get-AppPayloadManifestFiles -AppDir $AppDir).Count
    if ($null -eq $Manifest.app_payload_file_count -or
        [int]$Manifest.app_payload_file_count -ne $fileCount) {
        throw "Store 包清单记录的应用负载文件数与本次 Setup 不一致，拒绝发布"
    }

    if ([string]::IsNullOrWhiteSpace([string]$Manifest.app_release_version) -or
        [string]$Manifest.app_release_version -ne $Version) {
        throw "Store 包清单版本与发布版本不一致：$($Manifest.app_release_version) / $Version"
    }
}
