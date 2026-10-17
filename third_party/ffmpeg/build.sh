#!/usr/bin/env bash
set -euo pipefail

export PATH=/mingw64/bin:$PATH

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT_DIR="$REPO_ROOT/third_party/ffmpeg"
BUILD_ROOT="$REPO_ROOT/package/ffmpeg-build"
VERSION="4.4.1"
TARBALL="$BUILD_ROOT/ffmpeg-$VERSION.tar.xz"
SOURCE_DIR="$BUILD_ROOT/ffmpeg-$VERSION"
PREFIX="$BUILD_ROOT/install"
OUTPUT="$BUILD_ROOT/ffmpeg-$VERSION-minimal.exe"
NVCODEC_VERSION="11.1.5.2"
NVCODEC_ROOT="$BUILD_ROOT/nvcodec"

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
export PKG_CONFIG_PATH="$NVCODEC_ROOT/lib/pkgconfig:${PKG_CONFIG_PATH:-}"

mapfile -t CONFIGURE_FLAGS < "$SCRIPT_DIR/configure-flags.txt"

cd "$SOURCE_DIR"
./configure \
  --prefix="$PREFIX" \
  "${CONFIGURE_FLAGS[@]}"

make -j"$(nproc)"
make install

cp "$PREFIX/bin/ffmpeg.exe" "$OUTPUT"
(cd "$BUILD_ROOT" && sha256sum "ffmpeg-$VERSION-minimal.exe" > "ffmpeg-$VERSION-minimal.exe.sha256")

echo
echo "Built: $OUTPUT"
ls -lh "$OUTPUT"
cat "$OUTPUT.sha256"
