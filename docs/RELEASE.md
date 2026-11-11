# Releases

A release is a ZIP that is unpacked and started on the target computer (self-contained, without installation).
It is built locally with `release.ps1`, because the tests need a real GPU and the native DLLs only exist here
(`docs/DECISIONS.md`, M9).

## Versions
- **Semantic Versioning** `MAJOR.MINOR.PATCH`: 1.0.**1** = bug fix, 1.**1**.0 = new feature, **2**.0.0 = breaks
  existing workflows or file formats (e.g. other columns in the head CSV, another sidecar format).
- The version lives only in `Directory.Build.props` (`<Version>`), and only `release.ps1` changes it (together with
  version and date in `CITATION.cff`).
- Every release has a Git tag `vX.Y.Z` and a section in `CHANGELOG.md`.
- Builds outside `release.ps1` are called `X.Y.Z-dev` (title bar, log, start event of the recording). This shows in
  recordings whether a development build was in use.

## One-time setup
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1   # native DLLs (native/README.md)
gh auth login                                                                 # GitHub CLI, only for -Publish
```

## Procedure
1. **Note the changes:** while working on `development`, add every change visible to operators or analysts to
   `CHANGELOG.md` under `## Unveröffentlicht` (short, in German, from the operator's point of view). Commit.
2. **Build the release** (on `development`, everything committed):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0
   ```
   Takes about 3 minutes. Result: `artifacts\release\Orbivo-1.1.0-win-x64.zip` (+ `.sha256`), commit
   "Release 1.1.0", tag `v1.1.0`. Nothing is pushed.
3. **Check the package:** unpack the ZIP on the headset computer, start it, short headset test (see below).
4. **Publish:**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -Publish
   ```
   Pushes `development` and the tag, moves `main` to the release and creates the GitHub release with ZIP, checksum,
   the FFmpeg source archive (`native\_downloads\orbivo-ffmpeg-…-source.tar`, required by the LGPL) and the notes from
   `CHANGELOG.md`. Steps 2 and 4 can be done in one go: call it with `-Publish` right away.

## What `release.ps1` does
| Step | Stops if … |
|---|---|
| Checks branch `development`, no uncommitted changes, tag free, nothing new on `origin/development` | any of these is not true |
| Checks the native DLLs and license texts in `native/win-x64` | something is missing |
| Checks that the new version is higher and that there is something under "Unveröffentlicht" | not |
| `dotnet test` (Debug, one test project after the other) | a test fails |
| Sets the version (also in `CITATION.cff`), turns "Unveröffentlicht" into the section `## X.Y.Z – date` | – |
| `dotnet publish` self-contained win-x64 (Release) to `artifacts\release\Orbivo-X.Y.Z\`, plus `licenses\` and `THIRD-PARTY-NOTICES.txt` | the build fails (version and changelog are reset) |
| Checks the package: required files present, `ffmpeg.exe` & co. absent, `Orbivo.exe --check` loads FFmpeg, Steam Audio and the OpenXR loader and finds both manuals | something is missing |
| ZIP with the folder `Orbivo-X.Y.Z` inside, SHA256 file | – |
| Commit "Release X.Y.Z" (only `Directory.Build.props`, `CHANGELOG.md` and `CITATION.cff`), annotated tag `vX.Y.Z` | – |
| Only `-Publish`: `git push` of `development` and the tag, `main` forward to the release (`git push origin development:main`), `gh release create` | `main` has commits that `development` lacks; `gh` is missing |

Further switches:
- `-PackageOnly`: only build and check the package (`Orbivo-X.Y.Z-dev-win-x64.zip`), without version change, commit
  and tag; also with uncommitted changes. For trying things out.
- `-SkipTests`: skip the tests. Emergencies only.

## Package contents
```
Orbivo-1.1.0/
  Orbivo.exe, *.dll           app and .NET runtime (self-contained)
  avcodec-63.dll …            FFmpeg (LGPL, replaceable), phonon.dll, openxr_loader.dll
  Anleitung.html, Manual.html operator manual in German and English (F1 in the app)
  de/*.resources.dll          German texts of the app (English is inside the DLLs themselves)
  LICENSE.txt, NOTICE.txt     license of Orbivo (Apache-2.0) and copyright notice
  THIRD-PARTY-NOTICES.txt     third-party components and licenses
  licenses/                   license texts of FFmpeg, dav1d, zlib, Steam Audio, OpenXR
  *.pdb                       symbols, so that errors in the log have line numbers
```
At the first start Orbivo creates the folder `Logs` next to it. Settings live in `%LOCALAPPDATA%\Orbivo`, so an
update means: replace the old folder.

## Short headset test before publishing
1. Unpack the ZIP (not under "Program Files"), start `Orbivo.exe`. Title bar: `Orbivo X.Y.Z – Operator` (without
   "-dev").
2. Status: FFmpeg, Steam Audio, OpenXR green; headset connected, about 90 fps.
3. F1 or Help → Manual opens the manual of the selected language in the browser; switch Options → "Sprache /
   Language", confirm the restart, the texts change.
4. Play an 8K test video: picture in the headset, sound fixed to the picture, A/V in the green range.
5. Try out specifically what changed in this release (`CHANGELOG.md`).

## When something goes wrong
- **Discard a release locally** (before `-Publish`): `git tag -d vX.Y.Z` and `git reset --keep HEAD~1`, delete the
  ZIP, fix the error, build again.
- **`-Publish` stopped halfway:** fix the cause and call `-Publish` again; parts already pushed stay as they are. If
  the GitHub release already exists, do only the missing steps by hand.
- **Error in a published release:** fix it on `development`, add it to the changelog, new release with a higher PATCH
  version (e.g. 1.1.1). Published tags are never moved.
- **Publish by hand** (without `gh`): push tag and branches, on GitHub under "Releases" create a release for the tag,
  attach the ZIP, the `.sha256` and the FFmpeg source archive from `native\_downloads`, text from
  `artifacts\release\Orbivo-X.Y.Z-notes.md` or the changelog.
