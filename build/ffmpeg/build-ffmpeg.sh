#!/usr/bin/env bash
# Builds the FFmpeg libraries that Orbivo ships: a lean LGPL-2.1-or-later build for 64-bit Windows,
# cross-compiled with mingw-w64 on Linux (GitHub Actions: .github/workflows/ffmpeg.yml).
#
# Contents: FFmpeg with all of its own components, dav1d (AV1 software decoding, BSD-2-Clause) and zlib.
# No other third-party libraries: autodetection is off, so nothing from the build machine slips in.
# Off as well: network protocols, avdevice, ffplay, everything GPL or nonfree.
#
# Output in $WORK/out:
#   orbivo-ffmpeg-<version>-<build>-win64-lgpl-shared.zip   DLLs, ffmpeg.exe, ffprobe.exe, licenses, BUILD-INFO.txt
#   orbivo-ffmpeg-<version>-<build>-source.tar              the unmodified source archives plus this script
#   SHA256SUMS.txt
#
# Needs (Ubuntu 24.04): mingw-w64 nasm meson ninja-build pkg-config make curl xz-utils
# Usage: WORK=/tmp/ffmpeg bash build/ffmpeg/build-ffmpeg.sh

set -euo pipefail

FFMPEG_VERSION=9.0.2
FFMPEG_URL="https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz"
FFMPEG_SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e

DAV1D_VERSION=1.5.4
DAV1D_URL="https://downloads.videolan.org/pub/videolan/dav1d/${DAV1D_VERSION}/dav1d-${DAV1D_VERSION}.tar.xz"
DAV1D_SHA256=686616b7c69eb88d44459391ab25cac13b6647a3b288835c5784e71c1514a5c5

ZLIB_VERSION=1.4
ZLIB_URL="https://github.com/madler/zlib/releases/download/v${ZLIB_VERSION}/zlib-${ZLIB_VERSION}.tar.xz"
ZLIB_SHA256=4f11c512053d7bab8d9e9b89bde3575add3e4c50a383b42ef6cb837035fc5a7c

# Raise when the same sources are built again with other options; part of the file names and of the FFmpeg version.
BUILD_NUMBER=1

# Hardware decoders Orbivo uses (HardwareDecodeSupport); the build fails if one of them is missing.
REQUIRED_HWACCELS="H264 HEVC VP9 AV1 MPEG2"

HOST=x86_64-w64-mingw32
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="${WORK:-$PWD/ffmpeg-build}"
DL="$WORK/dl"
SRC="$WORK/src"
PREFIX="$WORK/prefix"
STAGE="$WORK/stage"
OUT="$WORK/out"
NAME="orbivo-ffmpeg-${FFMPEG_VERSION}-${BUILD_NUMBER}"
JOBS="$(nproc)"

rm -rf "$SRC" "$PREFIX" "$STAGE" "$OUT"
mkdir -p "$DL" "$SRC" "$PREFIX/include" "$PREFIX/lib" "$STAGE" "$OUT"

log() { printf '\n== %s\n' "$*"; }

fetch() { # sha256 url [mirror ...] -> unpacks into $SRC. The checksum makes every mirror as good as the original.
    local sha="$1" url file
    shift
    file="$DL/$(basename "$1")"
    for url in "$@"; do
        [[ -f "$file" ]] && break
        echo "Downloading $url"
        if curl -fL --connect-timeout 30 --retry 5 --retry-delay 10 --retry-all-errors -o "$file.part" "$url"; then
            mv "$file.part" "$file"
        else
            rm -f "$file.part"
            echo "Download failed: $url"
        fi
    done
    [[ -f "$file" ]] || { echo "No source could download $(basename "$1")"; exit 1; }
    echo "$sha  $file" | sha256sum -c -
    tar -xJf "$file" -C "$SRC"
}

log "Sources"
fetch "$FFMPEG_SHA256" "$FFMPEG_URL" "${FFMPEG_URL/https:\/\/ffmpeg.org/https://www.ffmpeg.org}" \
    "https://deb.debian.org/debian/pool/main/f/ffmpeg/ffmpeg_${FFMPEG_VERSION}.orig.tar.xz"
fetch "$DAV1D_SHA256" "$DAV1D_URL"
fetch "$ZLIB_SHA256" "$ZLIB_URL" "https://zlib.net/zlib-${ZLIB_VERSION}.tar.xz"

log "zlib $ZLIB_VERSION (static)"
(
    cd "$SRC/zlib-$ZLIB_VERSION"
    make -f win32/Makefile.gcc PREFIX="$HOST-" CFLAGS="-O2 -Wall" -j"$JOBS" libz.a
    cp zlib.h zconf.h "$PREFIX/include/"
    cp libz.a "$PREFIX/lib/"
)

log "dav1d $DAV1D_VERSION (static)"
cat > "$WORK/cross.meson" <<EOF
[binaries]
c = '$HOST-gcc'
cpp = '$HOST-g++'
ar = '$HOST-ar'
strip = '$HOST-strip'
windres = '$HOST-windres'
pkg-config = 'pkg-config'

[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
(
    cd "$SRC/dav1d-$DAV1D_VERSION"
    meson setup build --cross-file "$WORK/cross.meson" --prefix="$PREFIX" --libdir=lib \
        --buildtype=release --default-library=static -Denable_tools=false -Denable_tests=false
    ninja -C build -j"$JOBS"
    ninja -C build install
)

log "FFmpeg $FFMPEG_VERSION (shared, LGPL-2.1-or-later)"
FFMPEG_CONFIGURE=(
    --prefix="$PREFIX/ffmpeg"
    --target-os=mingw32
    --arch=x86_64
    --cross-prefix="$HOST-"
    --pkg-config=pkg-config
    --pkg-config-flags=--static
    --extra-version="orbivo${BUILD_NUMBER}"
    --enable-shared
    --disable-static
    --disable-autodetect
    --enable-w32threads
    --enable-d3d11va
    --enable-dxva2
    --enable-zlib
    --enable-libdav1d
    --disable-avdevice
    --disable-network
    --disable-ffplay
    --disable-doc
    --disable-debug
    --extra-cflags="-I$PREFIX/include"
    --extra-ldflags="-L$PREFIX/lib -static-libgcc"
)
(
    cd "$SRC/ffmpeg-$FFMPEG_VERSION"
    PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" ./configure "${FFMPEG_CONFIGURE[@]}"

    # Fail early if the build would not do what Orbivo needs or would not be LGPL.
    grep -q '^CONFIG_GPL=yes' ffbuild/config.mak && { echo "GPL is enabled"; exit 1; }
    grep -q '^CONFIG_NONFREE=yes' ffbuild/config.mak && { echo "nonfree is enabled"; exit 1; }
    grep -q '^CONFIG_VERSION3=yes' ffbuild/config.mak && { echo "version3 (LGPL-3) is enabled"; exit 1; }
    grep -q '^CONFIG_LIBDAV1D_DECODER=yes' ffbuild/config.mak || { echo "libdav1d decoder is missing"; exit 1; }
    for codec in $REQUIRED_HWACCELS; do
        grep -q "^CONFIG_${codec}_D3D11VA2_HWACCEL=yes" ffbuild/config.mak \
            || { echo "D3D11VA hardware decoding for $codec is missing (mingw-w64 headers too old?)"; exit 1; }
    done

    make -j"$JOBS"
    make install
)

log "Package"
BIN="$STAGE/$NAME-win64-lgpl-shared"
mkdir -p "$BIN/licenses"
cp "$PREFIX"/ffmpeg/bin/*.dll "$PREFIX/ffmpeg/bin/ffmpeg.exe" "$PREFIX/ffmpeg/bin/ffprobe.exe" "$BIN/"
"$HOST-strip" --strip-unneeded "$BIN"/*.dll "$BIN"/*.exe

# Only Windows system DLLs and the FFmpeg DLLs themselves may be imported (no libgcc, no winpthreads, ...).
ALLOWED='^(kernel32|user32|gdi32|advapi32|bcrypt|ole32|oleaut32|shell32|shlwapi|psapi|ws2_32|secur32|msvcrt|api-ms-win-.*|avcodec-[0-9]+|avformat-[0-9]+|avutil-[0-9]+|avfilter-[0-9]+|swresample-[0-9]+|swscale-[0-9]+)\.dll$'
for file in "$BIN"/*.dll "$BIN"/*.exe; do
    while read -r dll; do
        if ! [[ "${dll,,}" =~ $ALLOWED ]]; then
            echo "$(basename "$file") imports $dll, which is not allowed"
            exit 1
        fi
    done < <("$HOST-objdump" -p "$file" | sed -n 's/^\s*DLL Name: //p')
done

FFSRC="$SRC/ffmpeg-$FFMPEG_VERSION"
cp "$FFSRC/COPYING.LGPLv2.1" "$BIN/licenses/FFmpeg-COPYING.LGPLv2.1.txt"
cp "$FFSRC/LICENSE.md" "$BIN/licenses/FFmpeg-LICENSE.md"
cp "$SRC/dav1d-$DAV1D_VERSION/COPYING" "$BIN/licenses/dav1d-COPYING.txt"
cp "$SRC/zlib-$ZLIB_VERSION/LICENSE" "$BIN/licenses/zlib-LICENSE.txt"

CONFIGURATION="$(sed -n 's/^#define FFMPEG_CONFIGURATION "\(.*\)"$/\1/p' "$FFSRC/config.h")"
LICENSE_TEXT="$(sed -n 's/^#define FFMPEG_LICENSE "\(.*\)"$/\1/p' "$FFSRC/config.h")"
{
    echo "FFmpeg for Orbivo - build $NAME"
    echo
    echo "FFmpeg   $FFMPEG_VERSION  $FFMPEG_URL"
    echo "         SHA256 $FFMPEG_SHA256"
    echo "dav1d    $DAV1D_VERSION  $DAV1D_URL"
    echo "         SHA256 $DAV1D_SHA256"
    echo "zlib     $ZLIB_VERSION  $ZLIB_URL"
    echo "         SHA256 $ZLIB_SHA256"
    echo
    echo "License: $LICENSE_TEXT"
    echo "Sources are unmodified. The complete source code with this build script is in $NAME-source.tar."
    echo
    echo "Orbivo commit: ${GITHUB_SHA:-local}"
    echo "Built: $(date -u +%Y-%m-%dT%H:%M:%SZ) on $(. /etc/os-release && echo "$PRETTY_NAME")"
    echo "Toolchain: $("$HOST-gcc" --version | head -1), $(nasm -v), meson $(meson --version)"
    echo
    echo "configure $CONFIGURATION"
} > "$BIN/BUILD-INFO.txt"

(cd "$STAGE" && zip -q -r -X "$OUT/$NAME-win64-lgpl-shared.zip" "$(basename "$BIN")")

# Corresponding source: the original archives, the build script and the workflow that ran it.
SOURCE="$STAGE/$NAME-source"
mkdir -p "$SOURCE"
cp "$DL/ffmpeg-$FFMPEG_VERSION.tar.xz" "$DL/dav1d-$DAV1D_VERSION.tar.xz" "$DL/zlib-$ZLIB_VERSION.tar.xz" "$SOURCE/"
cp "$SCRIPT_DIR/build-ffmpeg.sh" "$SCRIPT_DIR/README.md" "$BIN/BUILD-INFO.txt" "$SOURCE/"
if [[ -f "$SCRIPT_DIR/../../.github/workflows/ffmpeg.yml" ]]; then
    cp "$SCRIPT_DIR/../../.github/workflows/ffmpeg.yml" "$SOURCE/"
fi
(cd "$STAGE" && tar -cf "$OUT/$NAME-source.tar" "$(basename "$SOURCE")")

(cd "$OUT" && sha256sum -- *.zip *.tar > SHA256SUMS.txt && cat SHA256SUMS.txt)
ls -la "$BIN"
