# FFmpeg 精简构建（Windows x64）

这里保存随包分发的 FFmpeg Windows 构建配方。仓库本身不提交二进制和源码包，
构建产物放在被忽略的 `package/ffmpeg-build/` 下。

## 许可证与源码

- FFmpeg 版本：4.4.1
- 上游源码：<https://ffmpeg.org/releases/ffmpeg-4.4.1.tar.xz>
- 源码 SHA256：`eadbad9e9ab30b25f5520fbfde99fae4a92a1ae3c0257a8d68569a4651e30e02`
- 发布签名：`ffmpeg-4.4.1.tar.xz.asc`，签名密钥指纹
  `FCF9 86EA 15E6 E293 A564 4F10 B432 2F04 D676 58D8`
- 构建启用 `--enable-gpl --enable-version3`，因此二进制按 GPLv3 分发；
  完整许可证与构建说明随安装包放在 `licenses/` 下。

本目录的构建脚本和参数属于 PackingProof 项目；FFmpeg 及其依赖仍遵循各自许可证。
发布时必须同时提供本版本对应的完整源码：上游 tarball（按上面的 SHA256 校验）、
本目录的构建脚本与 `configure-flags.txt`，以及实际链接的依赖版本（见 `versions.env`）。

## 依赖

在 MSYS2 的 `mingw64` 环境里安装：

```bash
pacman -S --needed make diffutils \
  mingw-w64-x86_64-gcc mingw-w64-x86_64-make mingw-w64-x86_64-nasm \
  mingw-w64-x86_64-pkgconf mingw-w64-x86_64-zlib mingw-w64-x86_64-gnutls \
  mingw-w64-x86_64-libx264 mingw-w64-x86_64-x265 \
  mingw-w64-x86_64-ffnvcodec-headers mingw-w64-x86_64-amf-headers \
  mingw-w64-x86_64-libmfx
```

## 构建

在 PowerShell 里执行：

```powershell
C:\msys64\usr\bin\bash.exe -lc "cd /d/OpenSourceProject/PackingProof-Desktop && bash third_party/ffmpeg/build.sh"
```

产物（文件名固定不带版本，身份信息在二进制内的版本戳里，方便直接复用与打包管线钉住）：

- `package/ffmpeg-build/ffmpeg.exe`
- 同名 `.sha256` 文件

只有出正式包（Publish-CleanPackage.ps1，带 -RequireLocalBuild）需要这份产物：本机构建一次，
或把其他构建机产出的同名哈希 ffmpeg.exe 复制进 `package/dependency-cache/ffmpeg/4.4.1-minimal/`（哈希校验把关），
缺失时发布打包直接失败、绝不回退。远程 CI 与未构建的贡献者机器不走精简版：基线解析回退到
清单 `package` 段的上游 Gyan essentials（同样按哈希校验下载），只用于构建与测试，不进发布产物。
普通开发与 `dotnet build` / `dotnet test` 不需要构建 FFmpeg（开发构建回退项目根 ffmpeg.exe，运行时再回退系统 PATH）。
构建带 `--extra-version=packingproof-ffmpeg-minimal-V01` 版本戳：`ffmpeg -version` 第一行显示
`ffmpeg version 4.4.1-packingproof-ffmpeg-minimal-V01 ...`，诊断日志据此识别构建来源。
同版本 FFmpeg 重新出包时递增 V 号（V01 到 V02），`tools/ffmpeg-baseline.json` 的
`local_build` 哈希与 `app_patch_compatible_executables` 条目同步更新。
构建机无关性：configure 的 `--prefix` 使用中性路径（避免把构建机绝对路径嵌进版本信息），
读取 `configure-flags.txt` 时剥掉 CR（工作区行尾随 git autocrlf 变化，残留 CR 会让
pkg-config 收到带 CR 的 --static，现场症状是 pkgconf: unknown option -- static）。

## 精简范围

保留：H.264/HEVC 硬件与软件编码、NVENC/AMF/QSV、AAC、mp4/mkv、RTSP/RTMP/HTTP
网络摄像头输入、TLS、常用音视频滤镜和内置解码器。

移除：libass/freetype/fribidi、vmaf/zimg、webp/openjpeg、srt/ssh/zmq、
openmpt/gme/rubberband、aom/vpx/theora/xvid 以及 opus/vorbis/mp3lame 等未使用的外部库。

`libsvtav1` 不在本构建中：现有 Gyan essentials 构建同样没有它，应用会按能力检测隐藏 CPU AV1。
