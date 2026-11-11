<#
.SYNOPSIS
    Downloads the pinned native dependencies (Orbivo's FFmpeg build, Steam Audio, OpenXR loader) into native/win-x64,
    and the full FFmpeg build for test media into native/ffmpeg-full.

.DESCRIPTION
    Versions, URLs and SHA256 checksums are pinned below and documented in native/README.md.
    Archives are cached in native/_downloads. Only the files Orbivo needs are extracted.
    Works with Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Force
    Download the archives again even if a cached copy exists.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1
#>
[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar in PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$downloadDir = Join-Path $PSScriptRoot '_downloads'
$targetDir = Join-Path $PSScriptRoot 'win-x64'
$fullFFmpegDir = Join-Path $PSScriptRoot 'ffmpeg-full'

# Orbivo's own FFmpeg build (build/ffmpeg): a GitHub release of this repository.
$ffmpegRepo = 'QuenCode/Orbivo'
$ffmpegTag = 'ffmpeg-9.0.2-1'
$ffmpegBase = "https://github.com/$ffmpegRepo/releases/download/$ffmpegTag"

# Each rule: regex on the zip entry path -> sub folder below the package target (and optional new file name).
$packages = @(
    @{
        Name   = 'FFmpeg 9.0.2 for Orbivo (LGPL-2.1-or-later, build 1)'
        Url    = "$ffmpegBase/orbivo-ffmpeg-9.0.2-1-win64-lgpl-shared.zip"
        Sha256 = '3224ff4e073f75da98c1c6c599ccefd9792e61f91d47357f81d4bd4e262954c0'
        Target = $targetDir
        Rules  = @(
            @{ Pattern = '^[^/]+/[^/]+\.(dll|exe)$'; Folder = '' }
            @{ Pattern = '^[^/]+/licenses/[^/]+$'; Folder = 'licenses' }
            @{ Pattern = '^[^/]+/BUILD-INFO\.txt$'; Folder = 'licenses'; Rename = 'FFmpeg-BUILD-INFO.txt' }
        )
        Required = @('avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll',
                     'avfilter-12.dll', 'ffmpeg.exe', 'ffprobe.exe',
                     'licenses\FFmpeg-COPYING.LGPLv2.1.txt', 'licenses\FFmpeg-LICENSE.md', 'licenses\FFmpeg-BUILD-INFO.txt',
                     'licenses\dav1d-COPYING.txt', 'licenses\zlib-LICENSE.txt')
    }
    @{
        Name   = 'Steam Audio 4.8.1'
        Url    = 'https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip'
        Sha256 = '4a0aa5ec1176f38f0b0993a37c2259d9e86f27e22d5e24f83ec4c3cb9a1d5449'
        Target = $targetDir
        Rules  = @(
            @{ Pattern = '^steamaudio/lib/windows-x64/phonon\.dll$'; Folder = '' }
            @{ Pattern = '^steamaudio/THIRDPARTY\.md$'; Folder = 'licenses'; Rename = 'SteamAudio-THIRDPARTY.md' }
        )
        Required = @('phonon.dll')
    }
    @{
        Name   = 'OpenXR loader 1.1.65 (Khronos OpenXR-SDK-Source, release-1.1.65)'
        Url    = 'https://github.com/KhronosGroup/OpenXR-SDK-Source/releases/download/release-1.1.65/openxr_loader_windows-1.1.65.zip'
        Sha256 = '859d7de35b7a968112db770b09ed8bddeed3cb16fd8ae070643a857676bea6fd'
        Target = $targetDir
        Rules  = @(
            @{ Pattern = '^x64/bin/openxr_loader\.dll$'; Folder = '' }
            @{ Pattern = '^x64/bin/(hello_xr|openxr_runtime_list)\.exe$'; Folder = 'openxr-tools' }
            @{ Pattern = '^share/doc/openxr/LICENSE$'; Folder = 'licenses'; Rename = 'OpenXR-LICENSE.txt' }
        )
        Required = @('openxr_loader.dll', 'openxr-tools\hello_xr.exe', 'openxr-tools\openxr_runtime_list.exe')
    }
    @{
        # Full build with encoders and drawtext, only for tools/testmedia. Never copied into the app or a package.
        Name   = 'FFmpeg 9.0.2 full build for test media (BtbN autobuild-2026-10-31-12-59, LGPL shared)'
        Url    = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-shared-9.0.zip'
        Sha256 = 'e907b01fa4783913151237e37df2a10fa97d99a1598a021bf59d4ae62ffed4c0'
        Target = $fullFFmpegDir
        Rules  = @(
            @{ Pattern = '^[^/]+/bin/[^/]+\.dll$'; Folder = '' }
            @{ Pattern = '^[^/]+/bin/(ffmpeg|ffprobe)\.exe$'; Folder = '' }
            @{ Pattern = '^[^/]+/LICENSE\.txt$'; Folder = ''; Rename = 'FFmpeg-LICENSE.txt' }
        )
        Required = @('ffmpeg.exe', 'ffprobe.exe', 'avcodec-63.dll', 'avfilter-12.dll')
    }
)

# Single files that are not part of an archive. Target $null: the file stays in native/_downloads.
$singleFiles = @(
    @{
        # The Steam Audio SDK zip contains no license text.
        Name   = 'Steam Audio license (Apache-2.0), tag v4.8.1'
        Url    = 'https://raw.githubusercontent.com/ValveSoftware/steam-audio/v4.8.1/LICENSE.md'
        Sha256 = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
        Target = 'licenses\SteamAudio-LICENSE.md'
    }
    @{
        # Complete source of the FFmpeg build; release.ps1 attaches it to every Orbivo release (LGPL).
        Name   = 'FFmpeg 9.0.2 for Orbivo, source code'
        Url    = "$ffmpegBase/orbivo-ffmpeg-9.0.2-1-source.tar"
        Sha256 = 'd84883e01256b7ebd81853eb80e8dd45aa786a28a10540f33733f84c4bc9b19d'
        Target = $null
    }
)

# Left over from the BtbN build that native/win-x64 held up to 1.1.0.
$obsolete = @('avdevice-63.dll', 'licenses\FFmpeg-LICENSE.txt')

function Save-Download([hashtable] $package, [string] $path) {
    try {
        Invoke-WebRequest -Uri $package.Url -OutFile $path -UseBasicParsing
    }
    catch {
        # Releases of a private repository need a login; the GitHub CLI has one.
        if (-not $package.Url.StartsWith($ffmpegBase) -or -not (Get-Command gh -ErrorAction SilentlyContinue)) {
            throw
        }
        Write-Host "  direct download failed ($($_.Exception.Message)), trying the GitHub CLI"
        & gh release download $ffmpegTag --repo $ffmpegRepo --pattern ([IO.Path]::GetFileName($path)) --dir $downloadDir --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release download failed with exit code $LASTEXITCODE." }
    }
}

function Get-VerifiedArchive([hashtable] $package, [string] $fileName = [IO.Path]::GetFileName($package.Url)) {
    $path = Join-Path $downloadDir $fileName
    if ((Test-Path $path) -and -not $Force) {
        Write-Host "Using cached $fileName"
    }
    else {
        Write-Host "Downloading $fileName ..."
        Save-Download $package $path
    }

    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash
    if ($hash -ne $package.Sha256) {
        Remove-Item -LiteralPath $path
        throw "SHA256 mismatch for $fileName (expected $($package.Sha256), got $hash). The download was deleted."
    }
    Write-Host "SHA256 ok: $fileName"
    return $path
}

function Expand-SelectedEntries([string] $archivePath, [array] $rules, [string] $target) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $zip.Entries) {
            foreach ($rule in $rules) {
                if ($entry.FullName -match $rule.Pattern) {
                    $folder = if ($rule.Folder) { Join-Path $target $rule.Folder } else { $target }
                    $name = if ($rule.Rename) { $rule.Rename } else { $entry.Name }
                    New-Item -ItemType Directory -Force -Path $folder | Out-Null
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $folder $name), $true)
                    Write-Host "  extracted $name"
                    break
                }
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $downloadDir, $targetDir, $fullFFmpegDir | Out-Null

foreach ($file in $obsolete) {
    $path = Join-Path $targetDir $file
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
        Write-Host "Removed obsolete $file"
    }
}

foreach ($package in $packages) {
    Write-Host ''
    Write-Host "== $($package.Name)"
    $archive = Get-VerifiedArchive $package
    Expand-SelectedEntries $archive $package.Rules $package.Target

    $missing = $package.Required | Where-Object { -not (Test-Path (Join-Path $package.Target $_)) }
    if ($missing) {
        throw "Missing after extraction: $($missing -join ', ')"
    }
}

foreach ($file in $singleFiles) {
    Write-Host ''
    Write-Host "== $($file.Name)"
    $fileName = if ($file.Target) { [IO.Path]::GetFileName($file.Target) } else { [IO.Path]::GetFileName($file.Url) }
    $downloaded = Get-VerifiedArchive $file $fileName
    if ($file.Target) {
        $destination = Join-Path $targetDir $file.Target
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $downloaded -Destination $destination -Force
        Write-Host "  copied $($file.Target)"
    }
}

Write-Host ''
Write-Host "Done. Native files are in $targetDir, the full FFmpeg for test media in $fullFFmpegDir."
Write-Host "Run 'dotnet build' to copy the native files into the app output."
