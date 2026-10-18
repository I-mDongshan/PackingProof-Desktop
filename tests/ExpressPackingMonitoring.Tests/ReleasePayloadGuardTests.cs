using System.Diagnostics;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// Setup 与 Store MSIX 的同源门禁必须真的比较文件内容，
/// 不能只比较清单里的版本号和 MSIX 自身哈希。
/// </summary>
public sealed class ReleasePayloadGuardTests
{
    [Fact]
    public void AppPayloadFingerprint_TracksRuntimeFilesAndIgnoresStoreRemovedFiles()
    {
        string common = EscapePowerShell(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ReleaseVersion.Common.ps1"));
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $root = Join-Path ([IO.Path]::GetTempPath()) ('pp-payload-' + [Guid]::NewGuid().ToString('N'))
            try {
                $app = Join-Path $root 'app'
                New-Item -ItemType Directory -Path (Join-Path $app 'Web') -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $app 'ExpressPackingMonitoring.exe') -Value 'exe'
                Set-Content -LiteralPath (Join-Path $app 'Web\index.html') -Value 'v1'
                Set-Content -LiteralPath (Join-Path $app 'debug.pdb') -Value 'pdb1'
                Set-Content -LiteralPath (Join-Path $app 'docs.xml') -Value 'xml1'
                . '{{common}}'

                $before = Get-AppPayloadFingerprint -AppDir $app
                Set-Content -LiteralPath (Join-Path $app 'Web\index.html') -Value 'v2'
                $changed = Get-AppPayloadFingerprint -AppDir $app
                if ($before -eq $changed) { throw '应用文件变化后指纹没有变化' }

                Set-Content -LiteralPath (Join-Path $app 'debug.pdb') -Value 'pdb2'
                Set-Content -LiteralPath (Join-Path $app 'docs.xml') -Value 'xml2'
                $ignored = Get-AppPayloadFingerprint -AppDir $app
                if ($ignored -ne $changed) { throw 'pdb/xml 不应参与 Store 同源指纹' }
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        ProcessResult result = RunPowerShell(command);
        Assert.True(result.ExitCode == 0, result.Combined);
    }

    [Fact]
    public void AppPayloadArchiveGuard_RejectsMissingOrChangedPayloadFiles()
    {
        string common = EscapePowerShell(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ReleaseVersion.Common.ps1"));
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $root = Join-Path ([IO.Path]::GetTempPath()) ('pp-payload-' + [Guid]::NewGuid().ToString('N'))
            try {
                $app = Join-Path $root 'app'
                New-Item -ItemType Directory -Path (Join-Path $app 'Web') -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $app 'ExpressPackingMonitoring.exe') -Value 'exe'
                Set-Content -LiteralPath (Join-Path $app 'Web\index.html') -Value 'v1'
                $zip = Join-Path $root 'payload.zip'
                Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip
                . '{{common}}'

                $count = Assert-AppPayloadMatchesArchive -AppDir $app -ArchivePath $zip
                if ($count -ne 2) { throw "预期比较 2 个文件，实际 $count" }

                Set-Content -LiteralPath (Join-Path $app 'Web\index.html') -Value 'v2'
                try {
                    Assert-AppPayloadMatchesArchive -AppDir $app -ArchivePath $zip | Out-Null
                    throw '内容不一致时应当拒绝'
                }
                catch {
                    if ($_.Exception.Message -notlike '*不一致*') { throw }
                }

                Remove-Item -LiteralPath (Join-Path $app 'Web\index.html')
                try {
                    Assert-AppPayloadMatchesArchive -AppDir $app -ArchivePath $zip | Out-Null
                    throw '缺少文件时应当拒绝'
                }
                catch {
                    if ($_.Exception.Message -notlike '*缺少*' -and
                        $_.Exception.Message -notlike '*之外*') { throw }
                }
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        ProcessResult result = RunPowerShell(command);
        Assert.True(result.ExitCode == 0, result.Combined);
    }

    [Fact]
    public void AppPayloadArchiveGuard_RejectsExtraStoreFiles()
    {
        string common = EscapePowerShell(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ReleaseVersion.Common.ps1"));
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $root = Join-Path ([IO.Path]::GetTempPath()) ('pp-payload-' + [Guid]::NewGuid().ToString('N'))
            try {
                $app = Join-Path $root 'app'
                New-Item -ItemType Directory -Path $app -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $app 'ExpressPackingMonitoring.exe') -Value 'exe'
                $zip = Join-Path $root 'payload.zip'
                Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip

                Add-Type -AssemblyName System.IO.Compression.FileSystem
                $archive = [System.IO.Compression.ZipFile]::Open(
                    $zip,
                    [System.IO.Compression.ZipArchiveMode]::Update)
                try {
                    $extra = $archive.CreateEntry('old.dll')
                    $writer = [System.IO.StreamWriter]::new($extra.Open())
                    try { $writer.Write('old') } finally { $writer.Dispose() }
                }
                finally {
                    $archive.Dispose()
                }

                . '{{common}}'
                try {
                    Assert-AppPayloadMatchesArchive -AppDir $app -ArchivePath $zip | Out-Null
                    throw 'MSIX 中的额外旧文件应当被拒绝'
                }
                catch {
                    if ($_.Exception.Message -notlike '*之外*') { throw }
                }
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        ProcessResult result = RunPowerShell(command);
        Assert.True(result.ExitCode == 0, result.Combined);
    }

    [Fact]
    public void StorePayloadManifestGuard_RejectsLegacyOrStaleManifest()
    {
        string common = EscapePowerShell(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ReleaseVersion.Common.ps1"));
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $root = Join-Path ([IO.Path]::GetTempPath()) ('pp-payload-' + [Guid]::NewGuid().ToString('N'))
            try {
                $app = Join-Path $root 'app'
                New-Item -ItemType Directory -Path (Join-Path $app 'Web') -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $app 'ExpressPackingMonitoring.exe') -Value 'exe'
                Set-Content -LiteralPath (Join-Path $app 'Web\index.html') -Value 'v1'
                . '{{common}}'

                $fingerprint = Get-AppPayloadFingerprint -AppDir $app
                $good = [pscustomobject]@{
                    app_release_version = '0.0.86'
                    app_payload_sha256 = $fingerprint
                    app_payload_file_count = 2
                }
                Assert-StorePayloadManifest -Manifest $good -Version '0.0.86' -AppDir $app

                $legacy = [pscustomobject]@{
                    app_release_version = '0.0.86'
                    app_payload_file_count = 2
                }
                try {
                    Assert-StorePayloadManifest -Manifest $legacy -Version '0.0.86' -AppDir $app
                    throw '旧清单缺少应用负载指纹时应当拒绝'
                }
                catch {
                    if ($_.Exception.Message -notlike '*app_payload_sha256*') { throw }
                }

                $stale = [pscustomobject]@{
                    app_release_version = '0.0.86'
                    app_payload_sha256 = ('0' * 64)
                    app_payload_file_count = 2
                }
                try {
                    Assert-StorePayloadManifest -Manifest $stale -Version '0.0.86' -AppDir $app
                    throw '旧负载指纹应当被拒绝'
                }
                catch {
                    if ($_.Exception.Message -notlike '*不一致*') { throw }
                }
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        ProcessResult result = RunPowerShell(command);
        Assert.True(result.ExitCode == 0, result.Combined);
    }

    [Fact]
    public void StorePayloadDirectoryResolution_DoesNotFallBackToOldSibling()
    {
        string common = EscapePowerShell(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "ReleaseVersion.Common.ps1"));
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $root = Join-Path ([IO.Path]::GetTempPath()) ('pp-payload-' + [Guid]::NewGuid().ToString('N'))
            try {
                $exactApp = Join-Path $root 'PackingProof+v0.0.86\app'
                $oldApp = Join-Path $root 'PackingProof+v0.0.86-2-gold\app'
                New-Item -ItemType Directory -Path $exactApp -Force | Out-Null
                New-Item -ItemType Directory -Path $oldApp -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $exactApp 'ExpressPackingMonitoring.exe') -Value 'new'
                Set-Content -LiteralPath (Join-Path $oldApp 'ExpressPackingMonitoring.exe') -Value 'old'
                . '{{common}}'

                $oldFingerprint = Get-AppPayloadFingerprint -AppDir $oldApp
                $newFingerprint = Get-AppPayloadFingerprint -AppDir $exactApp
                try {
                    Resolve-ReleasePayloadAppDir `
                        -PackageRoot $root `
                        -Version '0.0.86' `
                        -ExpectedFingerprint $oldFingerprint | Out-Null
                    throw 'exact app 不匹配时不应回退到旧 sibling'
                }
                catch {
                    if ($_.Exception.Message -notlike '*不一致*') { throw }
                }

                $resolved = Resolve-ReleasePayloadAppDir `
                    -PackageRoot $root `
                    -Version '0.0.86' `
                    -ExpectedFingerprint $newFingerprint
                if ((Resolve-Path -LiteralPath $resolved).Path -ne
                    (Resolve-Path -LiteralPath $exactApp).Path) {
                    throw "应返回本次 exact app，实际返回：$resolved"
                }

                $missingExactRoot = Join-Path $root 'missing-exact'
                $missingExactApp = Join-Path $missingExactRoot 'PackingProof+v0.0.86-2-gold\app'
                New-Item -ItemType Directory -Path $missingExactApp -Force | Out-Null
                Set-Content -LiteralPath (Join-Path $missingExactApp 'ExpressPackingMonitoring.exe') -Value 'old'
                $missingExactFingerprint = Get-AppPayloadFingerprint -AppDir $missingExactApp
                try {
                    Resolve-ReleasePayloadAppDir `
                        -PackageRoot $missingExactRoot `
                        -Version '0.0.86' `
                        -ExpectedFingerprint $missingExactFingerprint | Out-Null
                    throw 'exact app 不存在时不应回退到 sibling'
                }
                catch {
                    if ($_.Exception.Message -notlike '*不存在*') { throw }
                }
            }
            finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;

        ProcessResult result = RunPowerShell(command);
        Assert.True(result.ExitCode == 0, result.Combined);
    }

    private static ProcessResult RunPowerShell(string command)
    {
        var startInfo = new ProcessStartInfo("pwsh.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = FindRepositoryRoot()
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start pwsh.exe");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(
            process.ExitCode,
            string.IsNullOrWhiteSpace(error) ? output : output + Environment.NewLine + error);
    }

    private static string EscapePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string Combined);
}
