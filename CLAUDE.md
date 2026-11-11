# Orbivo – notes for Claude Code

## The project in one sentence
Windows player for 360°/180° videos (≥ 8K, stereo, spatial sound) on a **Varjo XR-3**.
It is operated **only by an operator at the desktop**. The person wearing the headset does not interact.

Details: `docs/SPEC.md` (requirements), `docs/ARCHITECTURE.md` (structure), `docs/ROADMAP.md` (milestones).
Before every task, read the matching milestone in `docs/ROADMAP.md`.

## Tech stack (do not change without asking)
- .NET 10 (LTS), C#, `net10.0-windows`, x64 only
- UI: WPF + CommunityToolkit.Mvvm
- XR: Silk.NET.OpenXR, runtime = Varjo OpenXR (Varjo Base)
- Graphics: Vortice.Windows (Direct3D11, DXGI, D3DCompiler)
- Media: FFmpeg (LGPL **shared** build) via FFmpeg.AutoGen, hardware decoding D3D11VA
- Audio output: NAudio (WASAPI, exclusive or shared with a small buffer)
- Spatial sound: Steam Audio (`phonon.dll`, Apache-2.0) via P/Invoke
- Tests: xUnit

New NuGet packages or native DLLs **only after asking**. Pin versions in `Directory.Packages.props`.

## Target hardware
- Desktop: RTX 5080, laptop: RTX 4080 (NVDEC, HEVC/AV1 up to 8192×8192)
- Varjo XR-3 via Varjo Base, no controllers needed
- Headphones: neutral, wired stereo headphones. **All spatial sound processing happens in the player.**

## Hard rules
1. **The render thread (OpenXR frame loop) never blocks**: no locks with waiting, no I/O, no allocations per frame,
   no `async`.
2. **The audio callback never blocks** and does not allocate.
3. Communication UI → player only through commands (`Channel<PlayerCommand>`); player → UI only through status
   snapshots/events. The UI has no direct access to player internals.
4. **The audio clock is the master clock.** Without sound, a system clock takes over.
5. Video frames stay on the GPU (NV12/P010 textures). No CPU copy of video frames. Only exception: the software
   decoder fallback (see `docs/DECISIONS.md`).
6. 360° content is shown **rotation only** (head position ignored, content at infinity).
7. The headset **never** shows UI. Only video, the idle view (black, camera picture, still image; F7), blackout or
   fade.
8. The projection math exists twice: HLSL shader and C# reference (`ProjectionMath`) with unit tests. **Always change
   both together.**
9. Coordinate system everywhere: OpenXR convention, right-handed, +Y up, −Z forward, +X right. Conversions only at
   clearly named places.

## Code conventions
- `Nullable` enabled, `TreatWarningsAsErrors` on, `unsafe` only in `Orbivo.Xr`, `.Media`, `.Audio`
- Code, identifiers, comments and the documentation in `docs/` in English; **operator UI in English and German**
  (`UiText.resx`/`UiText.de.resx` in `Orbivo.App`, `CoreText.resx`/`CoreText.de.resx` in `Orbivo.Core`). Every new or
  changed text in **both** languages; the test `LocalizationTests` checks for equal keys and placeholders. Recordings
  (CSV columns, event texts, file names) always stay German, the diagnostic log English. German counterparts exist
  only for user-facing documents: `docs/Anleitung.html` (to `docs/Manual.html`) and `README.de.md` (to `README.md`)
- Native resources (COM, FFmpeg, Steam Audio) always with `IDisposable` and clear ownership
- Logging: `Microsoft.Extensions.Logging`, one file log per session in the `Logs` folder next to the exe (older than
  30 days is deleted at startup, the last 30 sessions always stay)
- No silent catch blocks. Log errors and report them to the UI as status.

## Build & test
```
dotnet build Orbivo.sln -c Debug
dotnet test
dotnet run --project src/Orbivo.App -- --no-xr      # desktop debug viewport without headset (--lang en|de picks the language for this start only)
dotnet run --project src/Orbivo.App -- --no-xr --image tools/testmedia/out/equirect360_mono_4096x2048.png
dotnet run --project src/Orbivo.App -- --image tools/testmedia/out/equirect360_mono_4096x2048.png   # test image in the headset (Varjo Base must be running)
dotnet run --project src/Orbivo.App -- --no-xr --video tools/testmedia/out/equirect360_tb_8192x8192_hevc_8bit_30fps.mp4   # video in the preview as the first playlist entry, Play in the operator window
dotnet run --project src/Orbivo.App -- --video tools/testmedia/out/equirect360_tb_8192x8192_hevc_8bit_30fps.mp4            # video in the headset (--stereo mono|tb|sbs forces the layout, --decoder auto|hw|sw the decoder)
powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1   # load the native DLLs
python tools/testmedia/make_testmedia.py            # test images, test videos, Ambisonics test
powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -PackageOnly              # build and check a trial package (no version, commit, tag)
powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 [-Publish] # release (docs/RELEASE.md)
$env:ORBIVO_LONG_TESTS=1; dotnet test --project tests/Orbivo.Media.Tests -- --filter-class "*SoakTests"   # 30-minute soak test with recording (ORBIVO_SOAK_MINUTES shortens it)
```
FFmpeg, Steam Audio and OpenXR loader DLLs live in `native/win-x64/` (not in Git, see `native/README.md`) and are
copied to the output directory during the build.
Work happens on the branch `development`.

## What you (Claude) cannot check
You do not see the headset and do not hear the sound. Therefore:
- Cover everything that can be tested without a headset through `--no-xr` mode, unit tests and log output.
- `Orbivo.Xr.Tests` renders on WARP and stores the views as PNG under `tests/Orbivo.Xr.Tests/bin/…/rendered-views/`.
  They can be looked at.
- `Orbivo.Media.Tests` decodes the test videos with a real GPU and FFmpeg (8K benchmark in the test output) and runs
  open/play/pause/stop through the command channel.
- At the end of every milestone, output a short **checklist for the manual test with the headset** (what to do, what
  one should see/hear).
- When unsure about orientation or signs (projection, Ambisonics rotation): use test material from `tools/testmedia`
  and ask the human specifically what they see or hear.

## Way of working
- One milestone after the other, small commits with meaningful messages
- Before larger structural changes, briefly propose a plan and wait for an OK
- Record open points or assumptions in `docs/DECISIONS.md` (date, decision, reason)
- If something changes that operators or analysts notice (operation, recording format, sidecar), update
  `docs/Anleitung.html` **and** `docs/Manual.html` (English) and add the change to `CHANGELOG.md` under
  "Unveröffentlicht"
- Releases only through `release.ps1` and only when explicitly requested; `-Publish` pushes and publishes
