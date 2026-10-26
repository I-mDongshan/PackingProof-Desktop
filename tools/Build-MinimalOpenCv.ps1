param(
    [string]$RepositoryRoot = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
. (Join-Path $PSScriptRoot "OpenCvBaseline.Common.ps1")

$manifestPath = Join-Path $PSScriptRoot "opencv-baseline.json"
$baseline = Read-OpenCvBaselineManifest -ManifestPath $manifestPath
$workRoot = Join-Path $RepositoryRoot "package\opencv-build"
$downloadDirectory = Join-Path $RepositoryRoot "package\dependency-cache\opencv\$($baseline.opencvsharp.files_tag)"
$cacheDirectory = Join-Path $RepositoryRoot "package\dependency-cache\opencv\$($baseline.version)-$($baseline.artifact_id)"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $cacheDirectory ([string]$baseline.native.file)
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
    try {
        Assert-OpenCvNativeDll -DllPath $OutputPath -Baseline $baseline
        Write-Host "Pinned OpenCV native DLL reused: $OutputPath"
        return
    }
    catch {
        Write-Warning "Discarding invalid OpenCV native DLL cache: $OutputPath"
        Remove-Item -LiteralPath $OutputPath -Force
    }
}

New-Item -ItemType Directory -Force -Path $workRoot, $downloadDirectory, (Split-Path -Parent $OutputPath) | Out-Null

function Assert-ChildPath {
    param([string]$Parent, [string]$Path)
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "OpenCV build path escapes work directory: $pathFull"
    }
}

$sourceCommit = [string]$baseline.opencvsharp.commit
$sourceShort = $sourceCommit.Substring(0, 12)
$sourceRoot = Join-Path $workRoot "opencvsharp-$sourceShort"
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot ".git") -PathType Container)) {
    & git clone --filter=blob:none https://github.com/shimat/opencvsharp.git $sourceRoot
    if ($LASTEXITCODE -ne 0) { throw "OpenCvSharp clone failed" }
}
$currentCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if (-not [string]::Equals($currentCommit, $sourceCommit, [StringComparison]::OrdinalIgnoreCase)) {
    & git -C $sourceRoot fetch --depth 1 origin $sourceCommit
    if ($LASTEXITCODE -ne 0) { throw "OpenCvSharp fetch failed" }
    & git -C $sourceRoot checkout --detach $sourceCommit
    if ($LASTEXITCODE -ne 0) { throw "OpenCvSharp checkout failed" }
}

$archiveName = [string]$baseline.opencvsharp.archive_file
$archivePath = Join-Path $downloadDirectory $archiveName
$archiveValid = (Test-Path -LiteralPath $archivePath -PathType Leaf) -and
    ((Get-Item -LiteralPath $archivePath).Length -eq [long]$baseline.opencvsharp.archive_size) -and
    [string]::Equals(
        (Get-OpenCvSha256Lower -Path $archivePath),
        [string]$baseline.opencvsharp.archive_sha256,
        [StringComparison]::OrdinalIgnoreCase)
if (-not $archiveValid) {
    $temporaryArchive = "$archivePath.download"
    $downloadError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if (Test-Path -LiteralPath $temporaryArchive) {
                Remove-Item -LiteralPath $temporaryArchive -Force
            }
            Invoke-WebRequest -UseBasicParsing -Uri ([string]$baseline.opencvsharp.archive_url) `
                -OutFile $temporaryArchive -TimeoutSec 1800
            $actualSize = (Get-Item -LiteralPath $temporaryArchive).Length
            $actualHash = Get-OpenCvSha256Lower -Path $temporaryArchive
            if ($actualSize -ne [long]$baseline.opencvsharp.archive_size -or
                -not [string]::Equals(
                    $actualHash,
                    [string]$baseline.opencvsharp.archive_sha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "OpenCV archive mismatch: actual=$actualSize/$actualHash expected=$($baseline.opencvsharp.archive_size)/$($baseline.opencvsharp.archive_sha256)"
            }
            Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
            $downloadError = $null
            break
        }
        catch {
            $downloadError = $_
            if ($attempt -lt 3) {
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
        finally {
            if (Test-Path -LiteralPath $temporaryArchive) {
                Remove-Item -LiteralPath $temporaryArchive -Force
            }
        }
    }
    if ($null -ne $downloadError) {
        throw "OpenCV archive download failed after 3 attempts: $($downloadError.Exception.Message)"
    }
}

$opencvRoot = Join-Path $workRoot "opencv-$($baseline.opencvsharp.files_tag)"
if (-not (Test-Path -LiteralPath (Join-Path $opencvRoot "include\opencv2\opencv.hpp") -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path $opencvRoot | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $opencvRoot -Force
}

$opencvFilesFolder = "opencv_win_x64-$($baseline.opencvsharp.files_tag)"
$bundledOpenCv = Join-Path $sourceRoot "opencv_files\$opencvFilesFolder"
if (-not (Test-Path -LiteralPath (Join-Path $bundledOpenCv "x64\vc17\staticlib\opencv_core4130.lib") -PathType Leaf)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bundledOpenCv) | Out-Null
    Copy-Item -LiteralPath $opencvRoot -Destination $bundledOpenCv -Recurse -Force
}

$modulesHeader = Join-Path $bundledOpenCv "include\opencv2\opencv_modules.hpp"
$modulesText = @"
/* Generated by PackingProof Build-MinimalOpenCv.ps1 */
#define HAVE_OPENCV_CORE
#define HAVE_OPENCV_IMGPROC
"@
[IO.File]::WriteAllText($modulesHeader, $modulesText, [Text.UTF8Encoding]::new($false))

$projectTemplate = Join-Path $sourceRoot "src\OpenCvSharpExtern\OpenCvSharpExtern.vcxproj"
$projectPath = Join-Path $sourceRoot "src\OpenCvSharpExtern\OpenCvSharpExtern.packingproof.vcxproj"
$projectText = [IO.File]::ReadAllText($projectTemplate, [Text.Encoding]::UTF8)
$defines = "WIN32;NDEBUG;_WINDOWS;_USRDLL;CVAPI_EXPORTS;NO_CONTRIB;NO_STITCHING;NO_VIDEO;NO_VIDEOIO;NO_HIGHGUI;NO_DNN;NO_ML;NO_BARCODE;NO_CALIB3D;NO_FEATURES2D;NO_FLANN;NO_OBJDETECT;NO_PHOTO;%(PreprocessorDefinitions)"
$projectText = $projectText.Replace(
    "WIN32;NDEBUG;_WINDOWS;_USRDLL;CVAPI_EXPORTS;%(PreprocessorDefinitions)",
    $defines)
$minimalDependencies = "ipphal.lib;ippicvmt.lib;ippiw.lib;ittnotify.lib;opencv_core4130.lib;opencv_imgproc4130.lib;zlib.lib;ws2_32.lib;crypt32.lib;%(AdditionalDependencies)"
$projectText = [regex]::Replace(
    $projectText,
    "<AdditionalDependencies>.*?</AdditionalDependencies>",
    "<AdditionalDependencies>$minimalDependencies</AdditionalDependencies>",
    [Text.RegularExpressions.RegexOptions]::Singleline)
$projectText = [regex]::Replace(
    $projectText,
    "\s*<ClCompile Include=""imgcodecs\.cpp"" />",
    "")
$linkerFlags = (@($baseline.toolchain.linker_flags) | ForEach-Object { [string]$_ }) -join ' '
$projectText = $projectText.Replace(
    "/IGNORE:4099 %(AdditionalOptions)",
    "/IGNORE:4099 $linkerFlags %(AdditionalOptions)")
$projectText = $projectText.Replace(
    "<GenerateDebugInformation>true</GenerateDebugInformation>",
    "<GenerateDebugInformation>false</GenerateDebugInformation>")
$projectText = $projectText.Replace(
    "<DebugInformationFormat>ProgramDatabase</DebugInformationFormat>",
    "<DebugInformationFormat>None</DebugInformationFormat>")
$projectText = $projectText.Replace(
    "\opencv_files\opencv_win_x64\",
    "\opencv_files\$opencvFilesFolder\")
[IO.File]::WriteAllText($projectPath, $projectText, [Text.UTF8Encoding]::new($false))

function Find-PinnedMsBuild {
    param([string]$ToolsetVersion)

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere.exe not found"
    }
    $candidates = @(& $vswhere -all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -find "MSBuild\**\Bin\MSBuild.exe")
    foreach ($candidate in $candidates) {
        $marker = $candidate.IndexOf("\MSBuild\", [StringComparison]::OrdinalIgnoreCase)
        if ($marker -lt 0) { continue }
        $visualStudioRoot = $candidate.Substring(0, $marker)
        $toolsetRoot = Join-Path $visualStudioRoot "VC\Tools\MSVC"
        if (-not (Test-Path -LiteralPath $toolsetRoot -PathType Container)) { continue }
        $toolset = Get-ChildItem -LiteralPath $toolsetRoot -Directory |
            Where-Object { [string]::Equals($_.Name, $ToolsetVersion, [StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($null -ne $toolset) { return $candidate }
    }
    throw "MSBuild with exact C++ toolset $ToolsetVersion was not found"
}

$msbuild = Find-PinnedMsBuild -ToolsetVersion ([string]$baseline.toolchain.msvc_version)
$outputDirectory = Join-Path $workRoot "out"
$intermediateDirectory = Join-Path $workRoot "obj"
Assert-ChildPath -Parent $workRoot -Path $outputDirectory
Assert-ChildPath -Parent $workRoot -Path $intermediateDirectory
foreach ($directory in @($outputDirectory, $intermediateDirectory)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$buildCommandPath = Join-Path $workRoot "build-opencv.cmd"
$responseFilePath = Join-Path $workRoot "build-opencv.rsp"
$responseLines = @(
    "/t:Rebuild"
    "/p:Configuration=Release"
    "/p:Platform=x64"
    "/p:TargetName=OpenCvSharpExtern"
    "/p:VCToolsVersion=$($baseline.toolchain.msvc_version)"
    "/p:WindowsTargetPlatformVersion=$($baseline.toolchain.windows_sdk_version)"
    "/p:SolutionDir=$sourceRoot\"
    "/p:OutDir=$outputDirectory\"
    "/p:IntDir=$intermediateDirectory\"
    "/p:PostBuildEventUseInBuild=false"
    "/p:RunPostBuildEvent=Never"
    "-maxcpucount"
)
[IO.File]::WriteAllText(
    $responseFilePath,
    ($responseLines -join [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))
$buildCommand = @"
@echo off
set http_proxy=
set HTTP_PROXY=
set https_proxy=
set HTTPS_PROXY=
set no_proxy=
set NO_PROXY=
"$msbuild" "$projectPath" "@$responseFilePath"
"@
[IO.File]::WriteAllText($buildCommandPath, $buildCommand, [Text.Encoding]::Default)
& cmd.exe /d /c $buildCommandPath
if ($LASTEXITCODE -ne 0) {
    throw "OpenCvSharpExtern minimal build failed with exit code $LASTEXITCODE"
}

$builtDll = Join-Path $outputDirectory ([string]$baseline.native.file)
Assert-OpenCvNativeDll -DllPath $builtDll -Baseline $baseline
Copy-Item -LiteralPath $builtDll -Destination $OutputPath -Force
Assert-OpenCvNativeDll -DllPath $OutputPath -Baseline $baseline
Write-Host "Pinned OpenCV native DLL built: $OutputPath"
