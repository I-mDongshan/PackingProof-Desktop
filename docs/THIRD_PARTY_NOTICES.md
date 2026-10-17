# 第三方组件与许可证

发布包使用以下第三方组件，各组件仍遵循其自身许可证。组件版本、下载地址、文件大小与 SHA256 固定在 `tools/ffmpeg-baseline.json`、`tools/ffmpeg-macos-baseline.json` 和项目文件里，可逐项复核。

本项目自身以 AGPL-3.0 授权，全文见仓库根目录的 `LICENSE`。

## 随包分发的许可证全文

许可证全文随安装包一起分发，Windows 在安装目录的 `app\licenses\`，macOS 主机在 `PackingProofHost.app/Contents/MacOS/Host/licenses/`：

| 文件 | 对应组件 | 许可证 |
| --- | --- | --- |
| `AGPL-3.0.txt` | PackingProof 主程序（本项目自身） | AGPL-3.0 |
| `GPL-3.0.txt` | FFmpeg（Windows 与 macOS 构建） | GPL-3.0 |
| `LGPL-2.1.txt` | LibVLC 核心（`libvlc.dll` / `libvlccore.dll`） | LGPL-2.1 |
| `GPL-2.0.txt` | LibVLC 构建中含 GPL 组件的部分（VLC 3.0.x 为 GPLv2 或更高版本） | GPL-2.0 |
| `ffmpeg-build-notice.txt` | FFmpeg Windows 构建说明：版本、上游 commit、构建参数 | — |
| `THIRD_PARTY_NOTICES.md` | 本文件 | — |

全文来源：`GPL-3.0.txt` 与 `ffmpeg-build-notice.txt` 直接取自随包二进制所属的同一归档（`ffmpeg-4.4.1-essentials_build.7z` 内的 `LICENSE` 与 `README.txt`），保证与随包二进制一致；`LGPL-2.1.txt` 与 `GPL-2.0.txt` 取自 VideoLAN 上游 `vlc` 仓库 `3.0.21` 标签的 `COPYING.LIB` 与 `COPYING`。仓库内的原始副本保存在 `docs/third-party-licenses/`，打包时原样复制进安装包。

## FFmpeg

- Windows 二进制来源：[Gyan FFmpeg Builds](https://www.gyan.dev/ffmpeg/builds/)
- 发布变体：Essentials build
- 许可证：GPLv3
- 对应版本、下载地址、文件大小和 SHA256：`tools/ffmpeg-baseline.json`
- FFmpeg 源代码：[FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg)

- macOS 二进制来源：[Martin Riedl FFmpeg Build Server](https://ffmpeg.martin-riedl.de/)
- 发布变体：macOS Apple Silicon arm64 release 静态构建
- 许可证：GPLv3（该构建未启用 `--enable-nonfree`，可以随包分发）
- 对应版本、下载地址、文件大小和 SHA256：`tools/ffmpeg-macos-baseline.json`
- FFmpeg 源代码：[FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg)

发布包只携带独立的 `ffmpeg.exe`（Windows）或 `ffmpeg`（macOS），用于录像编码、封装、转码、缩略图和剪辑。macOS 主机上它位于 `PackingProofHost.app/Contents/MacOS/Host/tools/ffmpeg`。

## LibVLC

- 项目：[VideoLAN LibVLC](https://www.videolan.org/vlc/libvlc.html)
- 许可证：LGPL-2.1-or-later，部分插件可能采用兼容的其他开源许可证
- 用途：电脑端本机录像回放

发布包仅保留本地录像回放所需的运行库和插件，不包含网络串流、光盘访问或推流输出组件。
