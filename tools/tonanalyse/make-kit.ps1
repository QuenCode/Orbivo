# Builds tools/tonanalyse/out/Orbivo-Tonanalyse.zip: the analysis script plus the FFmpeg command line tools from
# native/win-x64, so the script runs on a PC without the repository. Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\tonanalyse\make-kit.ps1
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$native = Join-Path $here '..\..\native\win-x64'
$out = Join-Path $here 'out'
$stage = Join-Path $out 'Orbivo-Tonanalyse'
$zip = Join-Path $out 'Orbivo-Tonanalyse.zip'

$ffmpegFiles = @('ffmpeg.exe', 'ffprobe.exe', 'avcodec-63.dll', 'avdevice-63.dll', 'avfilter-12.dll', 'avformat-63.dll',
    'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll')

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'ffmpeg') | Out-Null

foreach ($name in @('Tonanalyse.ps1', 'Tonanalyse.cmd', 'LIESMICH.txt')) {
    Copy-Item -LiteralPath (Join-Path $here $name) -Destination $stage
}
foreach ($name in $ffmpegFiles) {
    $source = Join-Path $native $name
    if (-not (Test-Path -LiteralPath $source)) { throw "$name fehlt in native\win-x64 (native\fetch-native.ps1 ausführen)." }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'ffmpeg')
}
Copy-Item -LiteralPath (Join-Path $native 'licenses\FFmpeg-LICENSE.txt') -Destination (Join-Path $stage 'ffmpeg')

if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host ('{0} ({1:0} MB)' -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
