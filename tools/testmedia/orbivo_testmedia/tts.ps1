<#
.SYNOPSIS
    Speaks words into WAV files with the Windows speech synthesizer (System.Speech).

.PARAMETER JobFile
    JSON array of { "text": "...", "path": "..." }.

.NOTES
    Exit code 3: no enabled voice for the requested culture is installed.
    Prints the name of the voice used.
#>
param(
    [Parameter(Mandatory = $true)] [string] $JobFile,
    [int] $SampleRate = 48000,
    [string] $Culture = 'de-DE'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

$jobs = Get-Content -Raw -Encoding UTF8 -LiteralPath $JobFile | ConvertFrom-Json
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $voice = $synth.GetInstalledVoices() |
        Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq $Culture } |
        Select-Object -First 1
    if (-not $voice) {
        [Console]::Error.WriteLine("No enabled $Culture voice installed.")
        exit 3
    }

    $synth.SelectVoice($voice.VoiceInfo.Name)
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
        $SampleRate,
        [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
        [System.Speech.AudioFormat.AudioChannel]::Mono)

    foreach ($job in $jobs) {
        $synth.SetOutputToWaveFile($job.path, $format)
        $synth.Speak($job.text)
        $synth.SetOutputToNull()
    }
    Write-Output $voice.VoiceInfo.Name
}
finally {
    $synth.Dispose()
}
