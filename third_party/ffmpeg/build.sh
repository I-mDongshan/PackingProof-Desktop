#!/usr/bin/env bash
set -euo pipefail

export PATH=/mingw64/bin:$PATH

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT_DIR="$REPO_ROOT/third_party/ffmpeg"
BUILD_ROOT="$REPO_ROOT/package/ffmpeg-build"
VERSION="4.4.1"
TARBALL="$BUILD_ROOT/ffmpeg-$VERSION.tar.xz"
SOURCE_DIR="$BUILD_ROOT/ffmpeg-$VERSION"
PREFIX="/packingproof-ffmpeg-minimal"
OUTPUT="$BUILD_ROOT/ffmpeg.exe"
NVCODEC_VERSION="11.1.5.2"
NVCODEC_ROOT="$BUILD_ROOT/nvcodec"
LOCAL_PKGCONFIG="$BUILD_ROOT/pkgconfig"

mkdir -p "$BUILD_ROOT"

if [ ! -f "$TARBALL" ]; then
  echo "Downloading FFmpeg $VERSION source..."
  curl -fL "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz" -o "$TARBALL"
fi

echo "Verifying source hash..."
echo "eadbad9e9ab30b25f5520fbfde99fae4a92a1ae3c0257a8d68569a4651e30e02  $TARBALL" | sha256sum -c -

rm -rf "$SOURCE_DIR" "$PREFIX"
tar -xf "$TARBALL" -C "$BUILD_ROOT"

# FFmpeg 4.4.1 的 x86 内联汇编用负数移位立即数（shrl $-1），
# 新版 binutils 会直接报 operand type mismatch。改成等价的 32 - s 后
# 仍是同一条 x86 移位指令（CPU 只取低 5 位），不改变运行结果。
sed -i 's/(uint8_t)(-s)/(uint8_t)(32 - s)/g' "$SOURCE_DIR/libavcodec/x86/mathops.h"

# FFmpeg 4.4.1 仍引用 NV_ENC_PRESET_HQ_GUID，MSYS2 当前的 13.x 头文件已移除该常量。
# 这里单独安装 11.1.5.2 头文件到本地前缀，并通过 PKG_CONFIG_PATH 优先使用。
if [ ! -f "$NVCODEC_ROOT/include/ffnvcodec/nvEncodeAPI.h" ]; then
  NVCODEC_TARBALL="$BUILD_ROOT/nv-codec-headers-$NVCODEC_VERSION.tar.gz"
  if [ ! -f "$NVCODEC_TARBALL" ]; then
    curl -fL "https://github.com/FFmpeg/nv-codec-headers/archive/refs/tags/n$NVCODEC_VERSION.tar.gz" -o "$NVCODEC_TARBALL"
  fi
  NVCODEC_DIR="$BUILD_ROOT/nv-codec-headers-n$NVCODEC_VERSION"
  rm -rf "$NVCODEC_DIR"
  tar -xf "$NVCODEC_TARBALL" -C "$BUILD_ROOT"
  make -C "$NVCODEC_DIR" install PREFIX="$NVCODEC_ROOT"
fi
# MSYS2 的 x265.pc 在 Libs.private 里显式写了 -lgcc_s -lgcc。显式库名会绕过
# gcc 的 -static-libgcc，最终让 ffmpeg.exe 仍然依赖 libgcc_s_seh-1.dll
# （该 DLL 又依赖 libwinpthread-1.dll）。这里只覆盖本机构建用的 x265.pc，
# 保留编译参数并去掉动态运行库，交给 gcc 的 -static-libgcc 处理。
rm -rf "$LOCAL_PKGCONFIG"
mkdir -p "$LOCAL_PKGCONFIG"
sed -E 's/^Libs\.private:.*/Libs.private: -lstdc++/' \
  /mingw64/lib/pkgconfig/x265.pc > "$LOCAL_PKGCONFIG/x265.pc"
export PKG_CONFIG_PATH="$NVCODEC_ROOT/lib/pkgconfig:$LOCAL_PKGCONFIG:${PKG_CONFIG_PATH:-}"

mapfile -t CONFIGURE_FLAGS < <(tr -d '\r' < "$SCRIPT_DIR/configure-flags.txt")

cd "$SOURCE_DIR"
./configure \
  --prefix="$PREFIX" \
  --extra-version=packingproof-ffmpeg-minimal-V01 \
  "${CONFIGURE_FLAGS[@]}"

make -j"$(nproc)"
make install

cp "$PREFIX/bin/ffmpeg.exe" "$OUTPUT"
(cd "$BUILD_ROOT" && sha256sum "ffmpeg.exe" > "ffmpeg.exe.sha256")

echo
echo "Built: $OUTPUT"
ls -lh "$OUTPUT"
cat "$OUTPUT.sha256"
