# Roadmap

Every milestone ends with: a green build, green tests, a commit and the **checklist for the manual headset test**.

## M0 – Skeleton
- Solution and projects according to `ARCHITECTURE.md`, `Directory.Build.props`, `Directory.Packages.props`
- WPF window with an empty operator layout (placeholders for playlist, transport, status, format)
- `native/README.md` with sources and versions for FFmpeg (LGPL shared) and phonon.dll
- `tools/testmedia`: a script creates
  - an equirect test image/video with labeled directions (VORNE, HINTEN, LINKS, RECHTS, OBEN, UNTEN = front, back,
    left, right, up, down) and a grid, in mono, SBS and TB, in 4K and 8K
  - an Ambisonics FOA test file (AmbiX) with clicks or speech from known directions
- **Done when:** `dotnet build` and `dotnet test` run and the app starts

## M1 – Renderer and debug viewport (without headset)
- D3D11 device, fullscreen triangle, projection shader (at first only equirect 360 mono)
- `ProjectionMath.cs` and unit tests
- `--no-xr`: test image (PNG) in the desktop window with mouse look
- **Done when:** looking ahead shows "VORNE", looking up shows "OBEN", and nothing is mirrored

## M2 – OpenXR with a still image
- Silk.NET.OpenXR session with the Varjo runtime, swapchains, frame loop, session lifecycle
- The same test image in the headset, rotation only
- Status in the UI: runtime name, session state, fps
- **Headset test:** the picture stands still in the room, "VORNE" in front, up/down correct, no double images, 90 fps

## M3 – Video decoding
- FFmpeg demux and D3D11VA decode on the shared device, NV12/P010 → YUV conversion in the shader
- Frame queue, playback with the system clock (no audio yet)
- Play/pause/stop through the command channel from the UI
- **Done when:** the 8K HEVC test video plays smoothly in the debug viewport and the log says "hardware decode"
- **Headset test:** 8K TB video without stutter at 90 fps on both computer types

## M4 – Operator UI
- Playlist (file dialog, drag & drop, order), timeline, seek, time input, auto-advance, loop
- Keyboard shortcuts, status display (F40, F41)
- Blackout and fade (F5), start state black (F6)
- **Done when:** all transport functions work in `--no-xr` mode, seek is precise (±1 frame)

## M5 – Audio and synchronization
- Audio decode, ring buffer, NAudio WASAPI, device selection (F24), volume
- Audio clock as master, frame selection by `targetPts`, A/V offset in status and log
- At first stereo pass-through only
- **Done when:** the A/V offset stays within ±20 ms over 10 minutes. Seek and pause are clean, without clicks.

## M6 – All formats
- Equirect 180, cubemap (layouts), EAC, fisheye; SBS/TB/mono, swap eyes
- Metadata detection (`sv3d`/`st3d`), UI override, saving and loading the sidecar, orientation offset
- Unit tests for all projections
- **Headset test:** every test video correctly oriented, stereo depth correct (not swapped)

## M6.5 – Live view (F42)
- Every 3rd frame the XR render thread also renders a small center view (about 720 px high) from the head rotation of
  both eyes, with fade and blackout. It never waits: if the display is not ready, the frame is skipped
- Display in the operator window (space on the left below "Format"), switch "Live view", off at every start
- Without a session: note "No picture". With `--no-xr` it shows the preview picture (so it can be checked without a
  headset)
- **Done when:** with the live view on, the headset stays at 90 fps (8K TB video), the render loop does not allocate,
  and switched off it costs nothing
- **Headset test:** turn the head; the live view follows in the same direction without noticeable delay; fade and
  blackout are visible

## M7 – Spatial sound: stereo fixed to the picture
Re-scoped on 2026-11-08 (`DECISIONS.md`): the sound of the videos is stereo and should match the picture. Ambisonics
and surround come later.
- Steam Audio interop (binaural effect with the built-in HRTF), `AudioCoordinates` with unit tests
- Head direction in the picture frame from the render thread (headset) or the mouse (preview) as a lock-free snapshot,
  processed in the audio thread in blocks of 256 without offset to the clock
- Two virtual speakers at ±width (75° by default); switch "sound fixed to the picture" and width per video in the
  operator window and the sidecar, on by default. Mono stays head-locked, multichannel is mixed down to stereo
- Test video `stereo_richtungstest_…` (`make_testmedia.py --what stereo`)
- **Done when:** direction (level difference between the ears) measured with the real `phonon.dll`, no clicks while
  turning, A/V unchanged, the audio thread does not allocate, load below 1 % ✔
- **Listening test:** stereo direction test: the sound comes from the glowing box, also while turning the head. Center
  in front, not inside the head. Switch off: left stays left. No clicks, no noticeable lag.

## M8 – Recenter, logging, robustness
- Recenter (F30/F31) for picture and sound
- Idle view (F7, `DECISIONS.md` 2026-11-07): black, see-through (`ALPHA_BLEND`) or still image while no video runs;
  selectable in the UI and saved. Adapt rule 7 in `CLAUDE.md` and F6
- SessionLogger (CSV, F50) and EventLog (F51), controlled from the UI
- Disconnect and reconnect the headset, restart Varjo Base: the app recovers (N3)
- **Done when:** a 30-minute run with several videos runs without errors and the logs are complete ✔ (`SoakTests`,
  without headset)
- **Headset test (idle):** see-through shows the camera picture before Play and after Stop, "Image" shows the chosen
  still image correctly oriented, Play switches cleanly to the video, blackout (B) stays black

## M9 – Polish and delivery
- Self-contained publish (`dotnet publish -r win-x64 --self-contained`), native DLLs and licenses in the package, ZIP
  without installation
- **Release workflow** for repeated releases (`release.ps1`, `CHANGELOG.md`, `docs/RELEASE.md`): tests, package,
  start test `Orbivo.exe --check`, version, tag; with `-Publish` a GitHub release
- Persist settings: output device and recording folder (since M5/M8), new: playlist, "auto-advance", "loop", fade
  duration
- Operator manual as HTML (`docs/Anleitung.html`, instead of `docs/OPERATOR.md`), next to the exe in every build,
  "Manual (F1)" in the operator window; with a section on the recorded data (columns, axes, angles, conversion to
  pixels)
- **Done when:** `release.ps1 -Version 1.0.0` builds a checked package, all tests green ✔ (trial run with
  `-PackageOnly`)
- **Headset test:** unpack and start the ZIP on the headset computer (`docs/RELEASE.md`, "Short headset test"),
  manual via F1, playlist back after a restart

## M10 – Ambisonics
Started on 2026-11-09 (`DECISIONS.md`): the real material has Ambisonics sound, AAC with 4 channels, 48 kHz, about
259 kb/s.
- Detection of the audio mode (F23): sidecar `audioMode` before metadata (`SA3D`) before channel count; 4 channels
  without information = first-order AmbiX
- Decode all channels without mixing (swresample only for the sample rate), first-order FuMa → AmbiX, ring buffer with
  up to 16 channels
- Binaural with the Ambisonics decode effect of Steam Audio (orders 1–3, built-in HRTF), head direction as with
  stereo; "sound fixed to the picture" off = the sound field turns with the head
- "Audio format" in the operator window (automatic, channels, AmbiX, FuMa), status with the source of the detection;
  event "Ton" in the recording
- Test videos `ambisonics_richtungstest_…` (`make_testmedia.py --what ambisonics`: with `SA3D`, without metadata,
  FuMa with sidecar, third order with 16 channels)
- Analysis tool for the real material (`tools/tonanalyse`, reports technical data only)
- **Done when:** direction measured for all test videos (the decoded field matches the source, the ear levels follow
  head rotation), no clicks while turning, loudness per order like stereo (±1.5 dB), the audio thread does not
  allocate ✔ (without headset). Real material analyzed: AmbiX with `SA3D`, detected automatically ✔
- **Listening test:** Ambisonics direction test: the sound comes from the glowing box, also above and below and while
  turning the head. Switch "sound fixed to the picture" off: the sound field turns with the head. Same result with all
  four test videos. Then with real material

## M11 – Second language (English)
- Operator UI and manual in English and German (F62): texts as `.resx` (English neutral, German as satellite
  `de\*.resources.dll`), window with `{x:Static}`
- Menu bar File (add videos, clear playlist, exit), Playback (auto-advance, loop), Options (language, audio lock),
  Help (manual, about Orbivo); language selection "Sprache / Language" under Options, saved in `settings.json`
  (`language`); takes effect after a restart, which the app offers (the playlist stays); `--lang en|de` for one
  session only
- Very first start (no `settings.json`) in English, existing installations stay German
- Recordings (columns, file names, events, decimal comma) stay German; errors that reach the operator and the event
  log run as `OperatorText` in both languages
- `docs/Manual.html` as the English version of `Anleitung.html`, both linked; F1 opens the matching one; `--check`
  and `release.ps1` require both and the German satellites
- **Done when:** both `.resx` pairs have the same keys and placeholders (test), all tests green, window in both
  languages without cut-off texts
- **Headset test:** switch the language, confirm the restart, the headset reconnects; F1 opens the manual of the
  language; a recording in English is built exactly like one in German

## Later – Surround and external sound
- 5.1/7.1 via virtual speakers (F21)
- External sound file next to the video (sidecar `externalAudio`)
- FuMa second and third order only if material requires it
