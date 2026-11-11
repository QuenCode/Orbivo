# FFmpeg for Orbivo

Orbivo ships its own lean build of the FFmpeg libraries (LGPL-2.1-or-later). It contains FFmpeg with its own
decoders, demuxers, encoders and filters, plus two small libraries linked statically:

| Library | Version | License | Why |
|---|---|---|---|
| FFmpeg | 9.0.2 | LGPL-2.1-or-later | containers, decoders, D3D11VA, swresample, swscale |
| dav1d | 1.5.4 | BSD-2-Clause | AV1 software decoding |
| zlib | 1.4 | zlib | compressed MP4 headers, PNG |

Nothing else: autodetection is off, network protocols, avdevice and ffplay are disabled, GPL and nonfree parts are
not enabled. All sources are used unmodified.

## Building

The build runs on GitHub Actions (`.github/workflows/ffmpeg.yml`, Ubuntu 24.04, mingw-w64 cross compiler) whenever
this folder or the workflow changes, or by hand ("Run workflow"). Locally on Ubuntu 24.04:

```
sudo apt-get install mingw-w64 nasm meson ninja-build pkg-config make curl xz-utils zip
WORK=/tmp/ffmpeg bash build/ffmpeg/build-ffmpeg.sh
```

The script checks the SHA256 of every source archive, fails if GPL, nonfree or LGPL-3 parts are enabled, if one of
the D3D11VA hardware decoders Orbivo uses is missing, or if a DLL imports anything but Windows system DLLs.

Result in `$WORK/out`:

- `orbivo-ffmpeg-<version>-<build>-win64-lgpl-shared.zip`: DLLs, `ffmpeg.exe`, `ffprobe.exe`, `licenses/`, `BUILD-INFO.txt`
- `orbivo-ffmpeg-<version>-<build>-source.tar`: the complete corresponding source (original archives, this script,
  the workflow, `BUILD-INFO.txt`)
- `SHA256SUMS.txt`

## Publishing

Both files go into a GitHub release of the Orbivo repository (`ffmpeg-<version>-<build>`, marked as pre-release).
`native/fetch-native.ps1` downloads the ZIP from there, `release.ps1` attaches the source archive to every Orbivo
release. Steps for a new FFmpeg version: `native/README.md`.
