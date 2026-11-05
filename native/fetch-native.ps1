<#
.SYNOPSIS
    Downloads the pinned native dependencies (FFmpeg LGPL shared build, Steam Audio, OpenXR loader) into native/win-x64.

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

# Each rule: regex on the zip entry path -> sub folder below win-x64 (and optional new file name).
$packages = @(
    @{
        Name   = 'FFmpeg 9.0.2 (LGPL shared, BtbN autobuild-2026-10-31-12-59)'
        Url    = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-shared-9.0.zip'
        Sha256 = 'e907b01fa4783913151237e37df2a10fa97d99a1598a021bf59d4ae62ffed4c0'
        Rules  = @(
            @{ Pattern = '^[^/]+/bin/[^/]+\.dll$'; Folder = '' }
            @{ Pattern = '^[^/]+/bin/(ffmpeg|ffprobe)\.exe$'; Folder = '' }
            @{ Pattern = '^[^/]+/LICENSE\.txt$'; Folder = 'licenses'; Rename = 'FFmpeg-LICENSE.txt' }
        )
        Required = @('avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll',
                     'avfilter-12.dll', 'avdevice-63.dll', 'ffmpeg.exe', 'ffprobe.exe')
    }
    @{
        Name   = 'Steam Audio 4.8.1'
        Url    = 'https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip'
        Sha256 = '4a0aa5ec1176f38f0b0993a37c2259d9e86f27e22d5e24f83ec4c3cb9a1d5449'
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
        Rules  = @(
            @{ Pattern = '^x64/bin/openxr_loader\.dll$'; Folder = '' }
            @{ Pattern = '^x64/bin/(hello_xr|openxr_runtime_list)\.exe$'; Folder = 'openxr-tools' }
            @{ Pattern = '^share/doc/openxr/LICENSE$'; Folder = 'licenses'; Rename = 'OpenXR-LICENSE.txt' }
        )
        Required = @('openxr_loader.dll', 'openxr-tools\hello_xr.exe', 'openxr-tools\openxr_runtime_list.exe')
    }
)

# Single files that are not part of an archive (the Steam Audio SDK zip contains no license text).
$singleFiles = @(
    @{
        Name   = 'Steam Audio license (Apache-2.0), tag v4.8.1'
        Url    = 'https://raw.githubusercontent.com/ValveSoftware/steam-audio/v4.8.1/LICENSE.md'
        Sha256 = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
        Target = 'licenses\SteamAudio-LICENSE.md'
    }
)

function Get-VerifiedArchive([hashtable] $package, [string] $fileName = [IO.Path]::GetFileName($package.Url)) {
    $path = Join-Path $downloadDir $fileName
    if ((Test-Path $path) -and -not $Force) {
        Write-Host "Using cached $fileName"
    }
    else {
        Write-Host "Downloading $fileName ..."
        Invoke-WebRequest -Uri $package.Url -OutFile $path -UseBasicParsing
    }

    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash
    if ($hash -ne $package.Sha256) {
        Remove-Item -LiteralPath $path
        throw "SHA256 mismatch for $fileName (expected $($package.Sha256), got $hash). The download was deleted."
    }
    Write-Host "SHA256 ok: $fileName"
    return $path
}

function Expand-SelectedEntries([string] $archivePath, [array] $rules) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $zip.Entries) {
            foreach ($rule in $rules) {
                if ($entry.FullName -match $rule.Pattern) {
                    $folder = if ($rule.Folder) { Join-Path $targetDir $rule.Folder } else { $targetDir }
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

New-Item -ItemType Directory -Force -Path $downloadDir, $targetDir | Out-Null

foreach ($package in $packages) {
    Write-Host ''
    Write-Host "== $($package.Name)"
    $archive = Get-VerifiedArchive $package
    Expand-SelectedEntries $archive $package.Rules

    $missing = $package.Required | Where-Object { -not (Test-Path (Join-Path $targetDir $_)) }
    if ($missing) {
        throw "Missing after extraction: $($missing -join ', ')"
    }
}

foreach ($file in $singleFiles) {
    Write-Host ''
    Write-Host "== $($file.Name)"
    $downloaded = Get-VerifiedArchive $file ([IO.Path]::GetFileName($file.Target))
    $destination = Join-Path $targetDir $file.Target
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $downloaded -Destination $destination -Force
    Write-Host "  copied $($file.Target)"
}

Write-Host ''
Write-Host "Done. Native files are in $targetDir. Run 'dotnet build' to copy them into the app output."
