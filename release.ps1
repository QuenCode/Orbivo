<#
.SYNOPSIS
    Builds an Orbivo release: tests, self-contained package (ZIP), version commit and git tag; optionally publishes it.

.DESCRIPTION
    The release workflow is described in docs/RELEASE.md. In short:

      1. Write the changes under "## Unveröffentlicht" in CHANGELOG.md and commit them on 'development'.
      2. .\release.ps1 -Version 1.1.0
         Checks branch and working tree, runs all tests, sets the version, publishes self-contained for win-x64,
         adds licenses and notices, checks the package with "Orbivo.exe --check", zips it to
         artifacts\release\Orbivo-1.1.0-win-x64.zip, commits "Release 1.1.0" and tags v1.1.0. Nothing is pushed.
      3. Test the ZIP (ideally with the headset).
      4. .\release.ps1 -Version 1.1.0 -Publish
         Pushes 'development' and the tag, moves 'main' forward to the release and creates the GitHub release with
         the ZIP (private repository, needs the GitHub CLI 'gh').
         Without a prior step 2 it does both in one go.

    Works with Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Version
    Version of the release, Semantic Versioning: MAJOR.MINOR.PATCH (e.g. 1.0.0, 1.1.0, 1.0.1).
    Not needed with -PackageOnly (then the current version is used, marked "-dev").

.PARAMETER Publish
    Push and create the GitHub release (after building it, if the tag does not exist yet).

.PARAMETER PackageOnly
    Only build and check the package (artifacts\release\Orbivo-<version>-dev-win-x64.zip). No version change, no
    commit, no tag, works with uncommitted changes. For trying out the package.

.PARAMETER SkipTests
    Leave out 'dotnet test'. Only for emergencies; the release notes do not say so, the console does.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -Publish

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -PackageOnly
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $Publish,
    [switch] $PackageOnly,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$changelogPath = Join-Path $root 'CHANGELOG.md'
$appProject = Join-Path $root 'src\Orbivo.App\Orbivo.App.csproj'
$nativeDir = Join-Path $root 'native\win-x64'
$noticesPath = Join-Path $root 'build\THIRD-PARTY-NOTICES.txt'
$releaseDir = Join-Path $root 'artifacts\release'
$releaseBranch = 'development'
$stableBranch = 'main'
$unreleasedHeading = '## Unveröffentlicht'
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Must match build/Native.targets (what the app needs) and native/README.md.
$nativeLibraries = @('avcodec-63.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll', 'phonon.dll', 'openxr_loader.dll')
# Manuals and German texts in both languages (M11); English is built into the exe.
$requiredInPackage = @('Orbivo.exe', 'Anleitung.html', 'Manual.html', 'de\Orbivo.resources.dll', 'de\Orbivo.Core.resources.dll',
    'THIRD-PARTY-NOTICES.txt', 'licenses\FFmpeg-LICENSE.txt',
    'licenses\SteamAudio-LICENSE.md', 'licenses\SteamAudio-THIRDPARTY.md', 'licenses\OpenXR-LICENSE.txt') + $nativeLibraries
# Tools of the FFmpeg build that are not part of the app (native/README.md).
$forbiddenInPackage = @('ffmpeg.exe', 'ffprobe.exe', 'avfilter-12.dll', 'avdevice-63.dll')

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host "== $text" -ForegroundColor Cyan
}

function Fail([string] $text) {
    throw "release.ps1: $text"
}

# Runs a tool with its output on the console (not into the function's return value).
function Invoke-Native([string] $file, [string[]] $arguments) {
    & $file @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Fail "'$file $($arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Get-GitOutput([string[]] $arguments) {
    $output = & git @arguments
    if ($LASTEXITCODE -ne 0) {
        Fail "'git $($arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
    return $output
}

function Read-Text([string] $path) {
    return [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}

function Write-Text([string] $path, [string] $text) {
    [IO.File]::WriteAllText($path, $text, $utf8)
}

function Get-CurrentVersion {
    $match = [regex]::Match((Read-Text $propsPath), '<Version>([^<]+)</Version>')
    if (-not $match.Success) {
        Fail "No <Version> in Directory.Build.props."
    }
    return $match.Groups[1].Value
}

function Set-ProjectVersion([string] $newVersion) {
    $text = Read-Text $propsPath
    $updated = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$newVersion</Version>")
    Write-Text $propsPath $updated
}

# The lines under "## Unveröffentlicht" up to the next "## " heading.
function Get-UnreleasedNotes {
    $lines = (Read-Text $changelogPath) -split "`r?`n"
    $start = [Array]::IndexOf($lines, $unreleasedHeading)
    if ($start -lt 0) {
        Fail "CHANGELOG.md has no line '$unreleasedHeading'."
    }
    $notes = New-Object System.Collections.Generic.List[string]
    for ($i = $start + 1; $i -lt $lines.Length; $i++) {
        if ($lines[$i].StartsWith('## ')) { break }
        $notes.Add($lines[$i])
    }
    return ($notes -join "`n").Trim()
}

# "## Unveröffentlicht" stays (empty) for the next release; the notes move under "## <version> – <date>".
function Set-ChangelogRelease([string] $newVersion) {
    $text = (Read-Text $changelogPath) -replace "`r`n", "`n"
    $date = Get-Date -Format 'yyyy-MM-dd'
    $heading = "## $newVersion " + [char]0x2013 + " $date"
    $index = $text.IndexOf("$unreleasedHeading`n")
    $updated = $text.Substring(0, $index) + "$unreleasedHeading`n`n$heading`n" + $text.Substring($index + $unreleasedHeading.Length + 1)
    Write-Text $changelogPath $updated
}

# The notes of an already released version (for -Publish after a local release).
function Get-ReleaseNotes([string] $releaseVersion) {
    $lines = (Read-Text $changelogPath) -split "`r?`n"
    $start = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match ('^## ' + [regex]::Escape($releaseVersion) + '( |$)')) { $start = $i; break }
    }
    if ($start -lt 0) {
        Fail "CHANGELOG.md has no section '## $releaseVersion'."
    }
    $notes = New-Object System.Collections.Generic.List[string]
    for ($i = $start + 1; $i -lt $lines.Length; $i++) {
        if ($lines[$i].StartsWith('## ')) { break }
        $notes.Add($lines[$i])
    }
    return ($notes -join "`n").Trim()
}

function Test-Tag([string] $tag) {
    & git rev-parse -q --verify "refs/tags/$tag" | Out-Null
    return $LASTEXITCODE -eq 0
}

function Assert-ReleaseBranchReady([string] $tag) {
    $branch = (Get-GitOutput @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
    if ($branch -ne $releaseBranch) {
        Fail "Releases are made on '$releaseBranch', the current branch is '$branch'."
    }
    $changes = Get-GitOutput @('status', '--porcelain', '--untracked-files=no')
    if ($changes) {
        Fail "There are uncommitted changes. Commit them first (CHANGELOG.md included):`n$($changes -join "`n")"
    }
    if (Test-Tag $tag) {
        Fail "The tag $tag exists already. Choose a new version."
    }
    Invoke-Native 'git' @('fetch', '--quiet', 'origin')
    $behind = (Get-GitOutput @('rev-list', '--count', "HEAD..origin/$releaseBranch")).Trim()
    if ($behind -ne '0') {
        Fail "origin/$releaseBranch has $behind commit(s) that are not here. Pull them first."
    }
}

function Assert-NativeLibraries {
    foreach ($file in $nativeLibraries + @('licenses\FFmpeg-LICENSE.txt', 'licenses\SteamAudio-LICENSE.md', 'licenses\SteamAudio-THIRDPARTY.md', 'licenses\OpenXR-LICENSE.txt')) {
        if (-not (Test-Path (Join-Path $nativeDir $file))) {
            Fail "native\win-x64\$file is missing. Run native\fetch-native.ps1 first (native/README.md)."
        }
    }
}

function Invoke-Tests {
    if ($SkipTests) {
        Write-Host 'Tests skipped (-SkipTests).' -ForegroundColor Yellow
        return
    }
    # Debug, as in daily work: the allocation tests of the audio path are written for it. In Release, the runtime
    # recompiles hot methods during the measurement now and then (docs/DECISIONS.md, M9). One test project at a time,
    # so the timing tests do not compete for the CPU.
    Write-Step 'Running all tests (dotnet test, one project at a time)'
    Push-Location $root
    try {
        Invoke-Native 'dotnet' @('test', '-c', 'Debug', '--max-parallel-test-modules', '1')
    }
    finally {
        Pop-Location
    }
}

# Self-contained publish plus licenses and notices, checked with "Orbivo.exe --check". Returns the ZIP path.
function New-Package([string] $packageName, [bool] $isRelease) {
    $stage = Join-Path $releaseDir $packageName
    $zip = Join-Path $releaseDir "$packageName-win-x64.zip"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    if (Test-Path $zip) { Remove-Item $zip -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null

    Write-Step "Publishing self-contained for win-x64 to $stage"
    $releaseFlag = if ($isRelease) { 'true' } else { 'false' }
    Invoke-Native 'dotnet' @('publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-o', $stage, "-p:OrbivoRelease=$releaseFlag")

    Copy-Item (Join-Path $nativeDir 'licenses') (Join-Path $stage 'licenses') -Recurse
    Copy-Item $noticesPath (Join-Path $stage 'THIRD-PARTY-NOTICES.txt')

    Write-Step 'Checking the package'
    foreach ($file in $requiredInPackage) {
        if (-not (Test-Path (Join-Path $stage $file))) {
            Fail "The package lacks $file."
        }
    }
    foreach ($file in $forbiddenInPackage) {
        if (Test-Path (Join-Path $stage $file)) {
            Fail "The package must not contain $file."
        }
    }

    # Starts the packaged exe without a window: native libraries load, both manuals are there (StartupOptions --check).
    $process = Start-Process -FilePath (Join-Path $stage 'Orbivo.exe') -ArgumentList '--check' -WorkingDirectory $stage -PassThru -Wait
    $logs = Join-Path $stage 'Logs'
    if ($process.ExitCode -ne 0) {
        $log = Get-ChildItem $logs -Filter 'orbivo_*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($log) { Get-Content $log.FullName | Write-Host }
        Fail "'Orbivo.exe --check' failed with exit code $($process.ExitCode) (log above)."
    }
    if (Test-Path $logs) { Remove-Item $logs -Recurse -Force }
    Write-Host 'Orbivo.exe --check: ok'

    Write-Step "Zipping $zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Text "$zip.sha256" "$hash  $([IO.Path]::GetFileName($zip))`n"
    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "$([IO.Path]::GetFileName($zip)): $sizeMb MB, SHA256 $hash"
    return $zip
}

function New-LocalRelease([string] $newVersion, [string] $tag) {
    Assert-ReleaseBranchReady $tag
    Assert-NativeLibraries

    $current = Get-CurrentVersion
    if ([version] $newVersion -le [version] $current) {
        Fail "The new version $newVersion must be higher than the current $current."
    }
    $notes = Get-UnreleasedNotes
    if (-not $notes) {
        Fail "Nothing under '$unreleasedHeading' in CHANGELOG.md. Write down what changed first."
    }

    Invoke-Tests

    Write-Step "Setting the version $current -> $newVersion"
    Set-ProjectVersion $newVersion
    Set-ChangelogRelease $newVersion
    try {
        $zip = New-Package "Orbivo-$newVersion" $true
    }
    catch {
        Write-Host 'Building the package failed; the version change is taken back.' -ForegroundColor Yellow
        Invoke-Native 'git' @('checkout', '--', $propsPath, $changelogPath)
        throw
    }

    Write-Step "Committing and tagging $tag"
    Invoke-Native 'git' @('add', '--', $propsPath, $changelogPath)
    Invoke-Native 'git' @('commit', '--quiet', '-m', "Release $newVersion")
    Invoke-Native 'git' @('tag', '-a', $tag, '-m', "Orbivo $newVersion")
    return $zip
}

function Publish-Release([string] $releaseVersion, [string] $tag, [string] $zip) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail "The GitHub CLI 'gh' is missing (https://cli.github.com), or publish by hand (docs/RELEASE.md)."
    }
    $branch = (Get-GitOutput @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
    if ($branch -ne $releaseBranch) {
        Fail "Publish from '$releaseBranch', the current branch is '$branch'."
    }
    $tagCommit = (Get-GitOutput @('rev-list', '-n', '1', $tag)).Trim()
    $head = (Get-GitOutput @('rev-parse', 'HEAD')).Trim()
    if ($tagCommit -ne $head) {
        Fail "$tag is not the current commit. Publish right after the release, before further commits."
    }
    if (-not (Test-Path $zip)) {
        Fail "$zip is missing. Delete the tag ('git tag -d $tag') and run the release again."
    }

    $notesFile = Join-Path $releaseDir "Orbivo-$releaseVersion-notes.md"
    $notes = Get-ReleaseNotes $releaseVersion
    $hash = (Get-Content "$zip.sha256" -Raw).Trim()
    $body = "$notes`n`n---`nZIP entpacken (nicht unter C:\Programme), Orbivo.exe starten. Anleitung: Anleitung.html im Programmordner oder F1 in der App.`n`nUnpack the ZIP (not under C:\Program Files), start Orbivo.exe. Manual: Manual.html in the program folder or F1 in the app.`n`nSHA256: ``$hash```n"
    Write-Text $notesFile $body

    Write-Step "Pushing $releaseBranch, $stableBranch and $tag"
    Invoke-Native 'git' @('push', 'origin', $releaseBranch)
    Invoke-Native 'git' @('push', 'origin', $tag)
    # main follows the releases; this fails (and changes nothing) if main has commits that development lacks.
    Invoke-Native 'git' @('push', 'origin', "refs/heads/${releaseBranch}:refs/heads/$stableBranch")
    Invoke-Native 'git' @('fetch', '--quiet', 'origin', "${stableBranch}:${stableBranch}")

    Write-Step "Creating the GitHub release $tag"
    Invoke-Native 'gh' @('release', 'create', $tag, $zip, "$zip.sha256", '--verify-tag', '--title', "Orbivo $releaseVersion", '--notes-file', $notesFile)
}

# --- main ---
Push-Location $root
try {
    if ($PackageOnly) {
        if ($Publish) { Fail '-PackageOnly and -Publish do not go together.' }
        Assert-NativeLibraries
        Invoke-Tests
        $current = Get-CurrentVersion
        $zip = New-Package "Orbivo-$current-dev" $false
        Write-Step 'Done'
        Write-Host "Test package: $zip (marked '-dev', no commit, no tag)."
        return
    }

    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        Fail "Give the version as MAJOR.MINOR.PATCH, e.g. -Version 1.1.0 (current: $(Get-CurrentVersion))."
    }
    $tag = "v$Version"
    $zip = Join-Path $releaseDir "Orbivo-$Version-win-x64.zip"

    if (-not (Test-Tag $tag)) {
        $zip = New-LocalRelease $Version $tag
        Write-Step 'Done'
        Write-Host "Release $Version is built and tagged locally ($tag): $zip"
    }
    elseif (-not $Publish) {
        Fail "The tag $tag exists already. To publish it: .\release.ps1 -Version $Version -Publish"
    }

    if ($Publish) {
        Publish-Release $Version $tag $zip
        Write-Step 'Published'
        Write-Host "GitHub release $tag is online: $(& gh release view $tag --json url --jq .url)"
    }
    else {
        Write-Host "Nothing is pushed yet. Test the ZIP, then: .\release.ps1 -Version $Version -Publish"
    }
}
finally {
    Pop-Location
}
