# Contributing to Orbivo

Thank you for helping! Orbivo is a small spare-time project, so answers may take a few days.
Issues and pull requests in English or German are both fine.

## Before you start

- **Bugs:** open an issue with the bug form. Attach the diagnostic log (`Logs` folder next to `Orbivo.exe`; the path
  is shown in the status panel) and say which version, GPU and headset you use.
- **Larger changes:** open an issue first and describe the plan, so we can agree on it before you spend time on code.
- **Security issues:** never in a public issue, see [SECURITY.md](SECURITY.md).

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Setting up

Windows 10/11 x64 with the .NET 10 SDK. The headset is not needed for most work: `--no-xr` shows the picture in a
desktop window, and the tests render on WARP.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1   # native DLLs, see native/README.md
python -m pip install -r tools/testmedia/requirements.txt
python tools/testmedia/make_testmedia.py                                     # test images and videos (NVIDIA GPU)
dotnet build Orbivo.sln -c Debug
dotnet test
```

## Pull requests

- Branch from **`development`** and open the pull request against `development`. `main` only follows releases.
- Keep pull requests small and focused, with commit messages that say why.
- `dotnet build` must have no warnings (`TreatWarningsAsErrors`) and `dotnet test` must pass.
- Say what you tested and how (with the headset, `--no-xr`, tests only).

## Rules of the code

The full list is in [CLAUDE.md](CLAUDE.md) (it guides humans and the AI alike). The most important ones:

1. The render thread (OpenXR frame loop) and the audio callback **never block** and do not allocate per frame.
2. The UI talks to the player only through commands (`Channel<PlayerCommand>`), the player answers with status
   snapshots.
3. The audio clock is the master clock. Video frames stay on the GPU.
4. The headset **never** shows user interface.
5. The projection math exists twice, in HLSL and in C# (`ProjectionMath`) with unit tests: always change both.
6. Coordinates follow the OpenXR convention everywhere: right-handed, +Y up, −Z forward, +X right.
7. Code, identifiers and comments in English. Every text of the operator window in **both** languages
   (`UiText.resx`/`UiText.de.resx`, `CoreText.resx`/`CoreText.de.resx`); `LocalizationTests` checks the keys.
   Recordings (CSV columns, event texts, file names) stay German.
8. No new NuGet packages or native DLLs without discussing them first; versions are pinned in
   `Directory.Packages.props`.

If a change is noticeable for operators or for people evaluating recordings, update both manuals
(`docs/Anleitung.html`, `docs/Manual.html`) and add a line to `CHANGELOG.md` under „Unveröffentlicht“.
Decisions and assumptions go to `docs/DECISIONS.md` (date, decision, reason).

## AI-assisted contributions

Most of Orbivo was written with an AI coding assistant, so AI-assisted contributions are welcome. You are responsible
for what you submit: read and understand it, test it, and mention the assistance in the pull request (for example
with a `Co-Authored-By` line).

## License

By contributing you agree that your contribution is licensed under the [Apache License 2.0](LICENSE), like the rest
of Orbivo (section 5 of the license). Only submit code you have the right to contribute.
