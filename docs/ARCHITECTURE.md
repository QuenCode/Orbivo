# Architecture

## Solution structure
```
Orbivo.sln
Directory.Build.props          # Nullable, TreatWarningsAsErrors, LangVersion, x64, Version (only release.ps1 changes it)
Directory.Packages.props       # central, pinned package versions
release.ps1                    # build and publish a release (docs/RELEASE.md)
CHANGELOG.md                   # changes per version, source of the release notes
build/                         # Native.targets (native DLLs into the output directory), THIRD-PARTY-NOTICES.txt (for the package), ffmpeg/ (own FFmpeg build)
src/
  Orbivo.Core/            # no UI, XR or native dependencies
    Commands/                  # PlayerCommand records (Open, Close, Play, Pause, Stop, Seek, Loop, Blackout, Fade, SetVideoFormat, Recenter)
    Playback/                  # PlaybackStateMachine, SystemPlaybackClock, FramePacer, OutputFader (blackout/fade), MediaTime
    Playlists/                 # Playlist (order, current video, next/previous, files from drag & drop)
    Formats/                   # ProjectionType, StereoLayout, CubemapLayout, FisheyeParameters, VideoFormat, FormatOverrides,
                               # EmbeddedFormatMetadata, VideoFormatResolver (precedence), SidecarFile (load/save)
    Geometry/                  # ProjectionMath (C# reference of the shader math), OrientationMath, FovAngles, RecenterControl (M8)
    Audio/                     # AudioRingBuffer, AudioConversionPlan, clock; StereoPlacement, AudioCoordinates, ListenerOrientation (M7)
    Logging/                   # SessionRecorder (head CSV F50, event log F51), HeadSampleGate, SessionEventQueue, GermanCsv
    Status/                    # PlayerStatus snapshot (immutable record)
  Orbivo.Media/           # FFmpeg: demux, decode (D3D11VA), metadata (sv3d/st3d, channel layout)
  Orbivo.Audio/           # NAudio output (Output/), Steam Audio interop (SteamAudio/), sound fixed to the picture (Spatial/)
  Orbivo.Xr/              # OpenXR session, D3D11 device, swapchains, renderer, HLSL shaders
    OpenXr/                    # XrHost (XR render thread), instance, session, swapchains, lifecycle, conversions
    Graphics/                  # D3D11 device, shader compiler
    Rendering/                 # PanoramaRenderer, IViewSource, still image texture
    Shaders/                   # *.hlsl, embedded as resources and compiled at startup
    DebugViewport/             # preview window for --no-xr (own Win32 window, own render thread)
    LiveView/                  # live view (F42): LiveViewFrames (3 slot textures), LiveViewPresenter (thread), LiveViewWindow (child window)
  Orbivo.App/             # WPF operator UI (MVVM), composition root, settings; copies docs/Anleitung.html and docs/Manual.html next to the exe
tests/
  Orbivo.Core.Tests/      # ProjectionMath, sidecar, clock/sync logic, state machine
  Orbivo.Audio.Tests/     # renderer and output with simulated devices, direction with the real phonon.dll (ear levels)
  Orbivo.Xr.Tests/        # shaders against ProjectionMath (WARP), direction test with the test image, all projections against equirect 360, mouse control
  Orbivo.Media.Tests/     # decoding with a real GPU, player through the command channel, A/V sync, 30-minute soak test (opt-in)
tools/
  testmedia/                   # scripts (ffmpeg + Python) that create test videos and test Ambisonics
  tonanalyse/                  # analysis kit for the sound track of real material (technical data only)
native/
  README.md                    # where the FFmpeg, phonon and OpenXR DLLs come from, which version
docs/                          # SPEC, ARCHITECTURE, ROADMAP, DECISIONS, RELEASE; Anleitung.html and Manual.html (operator manual, shipped)
```

## Threads
| Thread | Task | May block? |
|---|---|---|
| WPF UI | operator UI, writes commands into `Channel<PlayerCommand>` | yes (as usual for UI) |
| Player control | reads commands, controls demux/decoders, state machine, publishes `PlayerStatus` (~10 Hz). Keeps display and system awake while "playing" and "paused" (`DisplayKeepAwake`) | yes |
| Demux (`MediaDemuxer`) | reads packets and distributes them to the video and audio `PacketQueue`; performs seeks and puts a seek marker into both queues | yes |
| Video decode | decodes into GPU frames, copies them into the ring of the `VideoFrameQueue` (6 slots) and releases them through `FramePacer`. Performs seeks and waits for the next seek after the end of the file | yes |
| Audio decode (`AudioDecoder`) | decodes, converts with swresample to float32 stereo interleaved at the device sample rate, writes with media position into `AudioRingBuffer` | yes |
| Audio out (`AudioOutput`, MMCSS "Pro Audio") | waits for the WASAPI event, reads commands, device position → `AudioClockTracker` → `ClockSnapshot`; `AudioRenderer` fills the buffer from the ring buffer (with "sound fixed to the picture" through the `SpatialStage`). Opening devices and retries happen between periods, never inside one | **no** within the period (waits only for the device event) |
| XR render (`XrHost`) | OpenXR frame loop, picks the frame by clock, renders both eyes, reads the head pose. Without a running session: connecting and retrying; meanwhile it collects due video frames so that the decoder keeps pace with the clock | **no** in the frame loop (waits only in `xrWaitFrame`); without a session it may wait |
| Preview render (only `--no-xr`) | window messages and mouse, renders one eye into the preview window | **no** (waits only for the frame pacing of the swapchain, like `xrWaitFrame`) |
| Live view (`LiveViewPresenter`, only when switched on) | copies the newest live view image into the child window in the operator window and presents it | yes (waits for the frame pacing of its swapchain); the render thread never waits for it |
| Recording (`SessionRecorder`) | collects head samples (`HeadSampleGate`) and events (`SessionEventQueue`) and writes the two CSV files; opens and closes them at start and stop | yes (file I/O); render and player threads never wait for it |

Shared data between non-blocking threads: lock-free structures (SPSC queues, `Volatile`/`Interlocked`). The XR render
or preview thread publishes the current head orientation in the picture frame (with orientation offset) every frame
as `ListenerOrientation` (`SnapshotCell`, quaternion and timestamp); the audio thread reads it once per block.

## Video data flow
1. One `ID3D11Device` with `ID3D11Multithread::SetMultithreadProtected(true)` is shared by renderer and FFmpeg (set
   the `AVHWDeviceContext` to this device).
2. The decoder delivers `AV_PIX_FMT_D3D11` frames (texture array and index).
3. Per frame `CopySubresourceRegion` into an own ring texture (NV12 or P010). This decouples from the decoder pool,
   and the frame can be released at once. The copy runs on the shared immediate context (multithread lock), so on the
   GPU it comes before every draw that reads the slot.
4. `FramePacer` (Core) hands over slots lock-free: one SPSC queue of free slots (decoder → render) and one of ready
   slots with pts (render ← decoder). The render thread picks the newest frame with `pts ≤ target time` and returns
   older ones at once.
5. `VideoPlayer` (Media) is the only interface for the render threads (`IVideoFrameSource.TryGetFrame(display time)`
   and `GetOutputBrightness(display time)` for blackout/fade). On open/stop the control thread swaps the queue; a
   hazard pointer prevents it from releasing a queue the render thread is using.
5a. **Seek** stays in the same queue: the decode thread starts a new generation in the `FramePacer`. The render thread
   returns slots of older generations but keeps showing its current frame until a frame of the new generation is due.
6. The shader samples the luma SRV (R8/R16) and the chroma SRV (R8G8/R16G16) and converts YUV→RGB (matrix and range
   from the stream: BT.601/BT.709/BT.2020, limited/full; `color.hlsl`, C# reference `VideoColor`). BT.2020 is
   converted to BT.709 in linear light.
6a. **Software fallback** (`DECISIONS.md`, 2026-11-07): if the GPU cannot decode the video (`HardwareDecodeSupport`
   asks `ID3D11VideoDevice` before opening), FFmpeg decodes on the CPU (4:2:0, 8/10 bit). swscale writes each picture
   as NV12/P010 into a mapped staging texture (`Map` with `DoNotWait`, two alternating), then `CopySubresourceRegion`
   into the slot as in step 3. If the hardware still fails before the first picture, the player reopens the file in
   software. The only place where frames go from the CPU to the GPU (exception to rule 5).
7. **Gamma trap:** with a `*_SRGB` swapchain format the shader must output linear values. Decided in M1
   (`DECISIONS.md`): sRGB views throughout, the shader computes and writes linear. Since M3 the shader linearizes the
   video after YUV→RGB with the sRGB curve, so the code values arrive unchanged.

## Rendering
- One fullscreen triangle per eye, no sphere mesh.
- The pixel shader reconstructs the viewing direction: `dir = normalize(mul(invViewRotProj, ndc))`. The view matrix
  contains **only rotation** (head orientation × recenter offset × video offset). Projection matrix from the
  asymmetric `XrFovf`.
- The orientation offset of the video (F14) turns the viewing direction before the projection:
  `view' = conj(R) · view` with `R = yaw · pitch · roll` (`OrientationOffset.ApplyToView`). This turns the picture by
  R: yaw +30° moves the former picture center 30° to the left.
- Implemented as three vectors instead of a matrix (`ViewRayBasis`): `dir = normalize(ndc.x·R + ndc.y·U + C)`. C#
  computes R, U and C, the shader gets them in the constant buffer. So C# and HLSL share no matrix layout.
- Sampling without mipmaps (`SampleLevel 0`), U wrapping, V clamped at the edge. This avoids a seam at ±180° behind.
- `dir → (u,v)` per `ProjectionType` (`ProjectedEyeUv` in `projection.hlsl`, `ProjectionMath.EyeUv` in C#). C#
  computes the projection parameters (cube layout as a table per face, fisheye circle) once per frame from the
  `VideoFormat` (`ProjectionMath.CreateProjectionParameters`) and puts them into the constant buffer. Then the stereo
  layout is applied (half picture per eye): `SourceUv = clamp(uv, min, max) · Scale + Offset` (`EyeSourceRect`).
  Top-bottom: top = left eye; side-by-side: left = left eye. Clamped to half a texel before the half-picture border,
  for all projections except equirect 360 also left and right.
- **Swap eyes** (F11): `VideoFormat.SourceEye` takes the right half picture for the left eye and vice versa.
- Outside the valid range (e.g. the back side with VR180, outside the fisheye circle) the output is black.
- Fade and blackout are a multiplier in the shader (`Brightness`); at 0 the target is only cleared. The factor comes
  per frame from `OutputFader` (through the player) and is `level^2.2`, so that the fade runs evenly in perceived
  brightness.
- Varjo-specific: standard stereo (`PRIMARY_STEREO`) first. Quad views (`XR_VARJO_quad_views`) are a later
  optimization.

## Live view (F42, M6.5)
- **Point of view:** in the headset `CombinedView` (Core) computes one view from both eyes: direction = middle of the
  eyes (head), field of view from the left edge of the left eye to the right edge of the right eye, top and bottom the
  larger of the two; every edge at most 80° from the axis. The left half picture is shown (with stereo). In
  `--no-xr` it shows the preview picture.
- **Render thread:** `LiveViewFrames.RenderIfDue` draws after the eyes, at most every 30 ms (90 Hz: every 3rd frame),
  with fade and blackout, into one of three slot textures (1920 × 1080, B8G8R8A8, sRGB view). Size: the aspect ratio of
  the field of view, fitted into the area in the operator window. The handover runs through `TripleBufferIndex`
  (Core, lock-free, the newest wins). With the live view off, the render thread draws nothing extra.
- **Display thread:** `LiveViewPresenter` copies the newest image centered into the swapchain of a Win32 child window
  (WPF `HwndHost`, `LiveViewHost`) and presents it. Flip model with frame latency handle, otherwise the older copy
  model.
- **Shared immediate context:** the display thread only uses commands that do not change pipeline state (clear,
  copy, present, flush; no `ClearState`). An own draw would change shaders and targets of the render thread in the
  middle of a frame. Because both threads write into the same context, the copy of a slot always reaches the GPU after
  the drawing.
- The child window passes mouse clicks through to the operator window (`HTTRANSPARENT`) and never takes the keyboard
  focus. It is created on the UI thread; the display thread is stopped before it is destroyed.

## Projection math (reference)
Convention: +Y up, −Z forward, +X right. `dir` normalized.
- **Equirect 360:** `lon = atan2(dir.x, -dir.z)`, `lat = asin(dir.y)`, `u = lon/(2π) + 0.5`, `v = 0.5 − lat/π`
- **Equirect 180:** as above, but `u = lon/π + 0.5`, valid for `|lon| ≤ π/2`
- **Cubemap:** the major axis determines the face (on a tie X before Y before Z). On the face `(s, t) ∈ [−1, 1]²`,
  s to the right, t downwards, as the face is seen from the center of the cube. Unrotated faces: front, back, left,
  right upright; UP with its top edge to the back (+Z), DOWN with its top edge to the front (like ffmpeg `v360`).
  `CubemapLayout` describes the grid (columns × rows), the faces per cell (`rludfb`) and the rotation per cell in
  quarter turns clockwise, in the notation of `v360` (`in_forder`, `in_frot`). Presets: 3×2 and 6×1 and 1×6
  (`rludfb`, `000000`), EAC (`lfrdbu`, `000313`). Optional border per face (`cbmp` padding) in pixels.
- **EAC:** cubemap with the angle correction `s' = (4/π)·atan(s)` per face axis (`s ∈ [−1, 1]`; 45° lie at the face
  edge, 22.5° halfway), layout as on YouTube (see above). Checked against ffmpeg `v360` (`eac`), against real YouTube
  material still open.
- **Fisheye (equidistant):** `θ = atan2(|dir.xy|, −dir.z)` (angle to the forward viewing axis), distance from the
  circle center `r = θ / (fov/2)` in circle radii, direction in the circle from `dir.xy` (right = +x, up = +y).
  `u = centerX + r·(dir.x/|dir.xy|)·radius·H/W`, `v = centerY − r·(dir.y/|dir.xy|)·radius`; center as fractions of
  the half-picture width and height, radius as a fraction of the half-picture height. Valid for `θ ≤ fov/2`,
  otherwise black.

All formulas are in `ProjectionMath.cs` and in `projection.hlsl`. The unit tests check known directions (front, left,
up, back) against expected UVs. `ProjectionFormatShaderTests` compares shader and C# pixel by pixel for all formats,
`ProjectionImageRenderTests` renders the test images of all projections and compares every view with the same view of
the equirect 360 test image.

## Format path (M6)
1. On open (`OpenMediaCommand`) the player control thread reads the sidecar (`SidecarFile.Load`). The operator's
   choice (`FormatOverrides`) then applies until the next video, also across stop and decoder restarts.
2. When the decoder opens, `MediaDemuxer.ReadFormatMetadata` reads the spherical video metadata from the side data of
   the video stream (`AV_PKT_DATA_SPHERICAL`, `AV_PKT_DATA_STEREO3D`; MP4 `sv3d`/`st3d`, Matroska, frame packing
   SEI).
3. `VideoFormatResolver.Resolve` determines each part of the format separately: `--stereo` (session only) before
   sidecar before metadata before a guess from the picture size. Result: the format used, the automatic format (for
   "Automatic (…)" in the operator window), the source of each part and hints.
4. The format goes as `VideoFormat` to the `VideoFrameQueue`; every `VideoFrame` carries it to the render thread. A
   change (`SetVideoFormatCommand`) sets `VideoFrameQueue.PictureFormat` (a reference swap, lock-free); the render
   thread sees it with the next frame, without reopening.
5. The player saves every change in the sidecar at once and reports the format with sources, hints and sidecar state
   as `VideoFormatStatus` in the `PlayerStatus`.

Still images (`--image`) remain a test tool for equirect 360 without a sidecar.

## Audio
As of M10: stereo direct or "fixed to the picture" (two virtual speakers, binaural with Steam Audio, M7) and
Ambisonics orders 1–3 binaural (M10). Surround follows later.

### Sound data flow
1. `MediaDemuxer` picks the sound track (`av_find_best_stream`, matching the video stream) and fills the audio
   `PacketQueue`. It reads the channel layout of every sound track **before** `avformat_find_stream_info`
   (`EmbeddedAudioLayout`): afterwards FFmpeg reports only "4.0" instead of "ambisonic 1" for AAC with `SA3D`.
2. `AudioDecoder` decodes; `AudioModeResolver` (Core) determines the audio mode (see "Ambisonics"),
   `AudioConversionPlan` sets the conversion: pass stereo through, mono to both sides, mix multichannel to stereo,
   Ambisonics unmixed (all channels, `AV_CHANNEL_ORDER_UNSPEC` on both sides of swresample), resampling to the sample
   rate of the output device. Result: float32 interleaved with media position, 2 channels or 4/9/16 with Ambisonics.
3. `AudioRingBuffer` (Core, SPSC, 2¹⁷ frames ≈ 2.7 s at 48 kHz) is addressed by media time: the reader fetches "data
   from media time t", late data is skipped, gaps become silence. Flush (seek, stop) as a handshake: the old data
   stays readable until the output has faded out and confirmed. Each slot holds `AmbisonicsLayout.MaxChannels` = 16
   channels (8 MB); every generation (`Start`) states its channel count (`FrameChannels`); the renderer reads it with
   every block.
4. `AudioOutput` (Orbivo.Audio) owns the audio out thread and the device. The player talks to it only through
   `IAudioOutput` (Core): commands (attach, play, pause, hold, volume, mute, device) through a lock-free queue, clock
   and status through snapshots.
5. `AudioRenderer` (states silent / playing / fading to pause / fading to hold) fetches per period the frames for the
   media time being heard, applies the transport ramp (5 ms smoothstep) and gain, and counts dropouts. `SampleWriter`
   writes in the device format (float32, int16/24/32; stereo to the first two channels, a mono device gets the
   average).
6. Gain = `level³(volume)` × (muted ? 0 : 1) × `level³(fade/blackout)` at the time the first frame of the period is
   heard, smoothed over 5 ms (`GainSmoother`).

### Devices
- `IAudioEndpoint` wraps a device: `WasapiAudioEndpoint` (shared mode, event-driven, 20 ms buffer, position through
  `IAudioClock` with QPC timestamp), `NullAudioEndpoint` (silent stand-in with system clock) and
  `SimulatedAudioEndpoint` (for tests: drift, latency, capture, tone detection).
- `WasapiEndpointProvider` lists devices and reports changes (`IMMNotificationClient`). "Windows default" follows the
  default device; a fixed device is never switched automatically.
- If the device is missing or fails (error or 500 ms without a period), `NullAudioEndpoint` takes over; a new attempt
  every 2 s and on every device change.
- `settings.json` (`%LOCALAPPDATA%\Orbivo`) stores device, volume and the audio lock, not mute (`AppSettings`, Core).
  Also idle view and recording (M8), playlist, "auto-advance", "loop" and fade duration (M9, see "Delivery") and the
  language (M11, see "Languages").

### Sound fixed to the picture (M7)
- **Choice:** `StereoPlacement` (Core) = fixed to the picture yes/no and width (30–90°, on by default, 75°), from the
  sidecar (`audioFixedToPicture`, `stereoWidthDeg`). The player sends it on open, after every reopening of the sound
  decoder and on every change in the operator window through `IAudioOutput.SetStereoPlacement` (lock-free command
  queue). Mono tracks always get "direct".
- **Head direction:** `ListenerOrientation` (see threads) = head in the picture frame,
  `OrientationOffset.ApplyToView(head)`, the same computation as for the picture. Recenter (M8) must be included there.
- **Speakers:** `AudioCoordinates.StereoSpeakers(width, head)` returns the directions of the two speakers (yaw ±width
  on the horizon of the picture) relative to the head: `normalize(conj(head)·direction)`. Steam Audio uses the same
  axes as OpenXR (+X right, +Y up, −Z forward); there is no further conversion.
- **Processing:** `SpatialStage` (Orbivo.Audio, owned by the audio thread) works in blocks of 256 frames: read the
  head direction, `SteamStereoSpatializer` (one `iplBinauralEffect` per channel with the built-in HRTF, bilinear, ear
  signals added × 0.6). The stage delays by exactly one block; `AudioRenderer` reads the ring buffer one block ahead so
  that what is heard matches the media position (clock and A/V unchanged). After start, seek, flush and switching, the
  renderer refills the delay line. Switching during playback goes through the 5 ms transport ramp.
- **Creation:** the audio thread creates context, HRTF and effects when it opens the device (between periods),
  matching the sample rate. If that fails (e.g. `phonon.dll` missing), stereo stays direct and the status reports the
  reason.
- **Status:** `AudioOutputStatus.Spatial` (available, active, choice, peak load per block, problem); the operator
  window shows the mode in the "Sound" group and the load in "Audio output".

### Ambisonics (M10)
- **Detection:** `AudioModeResolver.Resolve(layout, choice)` (Core): sidecar `audioMode` before metadata before
  channel count. Metadata = FFmpeg reports `AV_CHANNEL_ORDER_AMBISONIC` (from `SA3D` in MP4, order through
  `av_channel_layout_ambisonic_order`). Without metadata, 4, 9 or 16 channels without named speakers count as AmbiX
  of order 1–3, everything else as "channels". Result `ResolvedAudioMode` with source, automatic mode (for
  "Automatic (…)") and a hint (e.g. higher-order FuMa, too few channels).
- **Conversion in the decoder thread:** `AmbisonicsFrames.ToAmbiX` copies the first (order+1)² channels into the
  ring; further channels are dropped. First-order FuMa (W X Y Z, W −3 dB) becomes AmbiX: W·√2, order W Y Z X. When
  the choice changes, the player reopens the sound decoder at the same position (`RestartAudioDecoder`).
- **Axes:** AmbiX: X forward, Y left, Z up. In the picture frame (OpenXR) this is X = −Z, Y = −X, Z = +Y
  (`AudioCoordinates`). Steam Audio reads ACN 1 = left, 2 = up, 3 = forward (checked against its own encoder,
  `SteamAmbisonicsSpatializerTests`), so the same order as AmbiX.
- **Normalization:** Steam Audio expects orthonormalized channels (N3D/√(4π)). `SteamAmbisonicsSpatializer` scales
  every AmbiX channel by √((2l+1)/4π) (l = degree of the channel) and evens out the loudness per order (×2.5 / ×2.1 /
  ×1.8, measured with noise against stereo direct).
- **Processing:** `AmbisonicsStage` (like `SpatialStage` a `BlockStage`, 256 frames, one block of delay, read-ahead
  in the renderer) passes every block to `iplAmbisonicsDecodeEffect` (created for order 3, built-in HRTF, stereo
  speaker layout, binaural). The head direction goes in as listener axes (right, up, forward) in the picture frame
  (`AudioCoordinates.ListenerAxes`). "Sound fixed to the picture" off = the listener always looks forward, the field
  turns with the head.
- **Fallback:** if Steam Audio is missing or creation fails, the renderer plays two cardioids to the left and right,
  0.5·(W ± Y), head-locked. The status names the reason (`SpatialAudioStatus.AmbisonicsProblem`).
- **Status:** `AudioConversionPlan.Mode` in the sound status, `VideoFormatStatus.AudioMode` for the selection in the
  operator window, `SpatialAudioStatus.FieldBinaural` for "binaural".

### Later (planned)
- **Surround:** virtual speakers as with stereo (Steam Audio `iplBinauralEffect` per channel).
- **External audio file** (if the recordings come with separate sound): sidecar field `externalAudio` with `path` and
  `offsetMs`. Own demux/decoder for audio, same clock.

## Recenter, idle view and recording (M8)
- **Recenter (F30/F31):** `RecenterControl` (Core) accepts requests from any thread (`RequestRecenter`,
  `RequestReset`). They are carried out in the render thread with the head pose of the next frame (`Update`), which
  yields a pure yaw rotation `C = Ry(−yaw)`. It applies as `C·view` before orientation offset and projection to both
  eyes, the live view and `ListenerOrientation`; picture and sound thus stay aligned. In the preview it turns the
  mouse view. The value applies across video changes and reconnects; it is not saved.
- **Idle view (F7):** if the player delivers no frame (no video, ready after stop), the render thread shows
  `PanoramaSource.ForIdle(…)`: black, still image (with its own stereo layout, `StillImageTexture.PictureFormat`) or
  see-through. See-through: blend mode `ALPHA_BLEND` and `BlendTextureSourceAlphaBit` in the layer; the eyes are
  cleared with `(0, 0, 0, 1 − brightness)` (premultiplied alpha), so blackout and fade cover the camera picture. If
  the runtime does not offer `ALPHA_BLEND` (`xrEnumerateEnvironmentBlendModes`), it stays black and the status says
  so. Instead of the camera picture the preview shows a dark area (`PanoramaRenderer.SeeThroughStandIn`).
- **Recording (F50–F52):** `SessionRecorder` (Core) owns a thread of its own. Every frame the render thread passes a
  `HeadSample` to `HeadSampleGate` (lock-free SPSC queue, 8192 slots, thins out to the chosen rate, does not allocate;
  full = row lost and counted). Player control, app and `XrHost` (connection, session, errors) report events to
  `ISessionEventLog` (`SessionEventQueue`, at most 10 000 waiting). The recorder writes every 50 ms and flushes every
  second. Times: one anchor (UTC and stopwatch) per file pair, every row = anchor + stopwatch time, hence monotonic;
  written as the local time of the PC. In the headset the time of a head row is the predicted display time of the
  frame.
- **Only while a video runs:** the recorder is switched on but opens no files. The player reports state changes typed
  (`ISessionEventLog.RecordState`, `SessionEvent.PlaybackState`). When the state changes to "playing", a file pair
  begins, anchored at the Play command before it (events and head samples of the last 5 s are kept for this). Pause
  stays in the file pair; every other state (opening, ready, end, no video, error) ends it; head samples after this
  event no longer belong to it. If a video is already playing (or paused) when recording is switched on, the file pair
  starts at once (`RecordingOptions.PlaybackAtStart`).
- **Files:** per recording `YYYY-MM-DD_hhmmss[_session]_kopf.csv` and `…_ereignisse.csv` (with "Nur bei laufendem
  Video" per video `…[_session]_videoname_kopf.csv`) in the chosen folder (default `Dokumente\Orbivo\Aufzeichnungen`),
  semicolon, decimal comma, UTF-8 with BOM. Head CSV:
  `Uhrzeit;Sekunden;Video;Videozeit_s;Status;Inhalt;Helligkeit;Tracking;Kopf_qw…qz;Kopf_Yaw/Pitch/Roll_Grad;Blick_Video_Yaw/Pitch_Grad;Recenter_Yaw_Grad`.
  Event CSV: `Uhrzeit;Sekunden;Ereignis;Video;Videozeit_s;Details`. If the recorder cannot write, it ends the
  recording and reports the reason in the operator window.

## Languages (M11)
- **Texts:** English is the neutral language, German the satellite `de\*.resources.dll`.
  `Orbivo.Core/Localization/CoreText.resx` (+ `.de.resx`) holds the texts of the lower layers (format and sidecar
  hints, errors of player and decoders, audio output, native libraries), `Orbivo.App/Localization/UiText.resx`
  (+ `.de.resx`) those of the window. The build generates the classes `CoreText` and `UiText` (`StronglyTyped…` in
  the csproj). For `UiText`, `Orbivo.App.csproj` generates it before the XAML step, because WPF compiles a temporary
  assembly (`*_wpftmp.csproj`) before the resources would otherwise exist. In XAML: `{x:Static loc:UiText.Key}`.
- **Choosing the language:** `AppSettings.Language` (`"en"`/`"de"`). Without `settings.json` English applies; if only
  the field is missing (file from before M11), German applies. `--lang` overrides for one session. `App.OnStartup`
  loads the settings first and sets culture and UI culture for all threads with `Loc.Apply`, before windows, checks of
  the native libraries or threads are created, plus `FrameworkElement.Language` (numbers in bindings).
- **Switching:** menu Options → Language (`ChooseLanguageCommand`); `MainViewModel.SelectedLanguage` saves the choice
  and raises `LanguageChangeRequested`; `App` asks in both languages for a restart, starts `Orbivo.exe` with the same
  arguments (without `--lang`) plus `--wait-for-pid <own id>` and exits. The new instance waits at most 30 s until the
  old one has released the headset, the sound device and `settings.json`. No switching at runtime: status lines,
  selection lists and error texts are created in many places already at startup.
- **Recording stays German:** event texts (`SessionEvents`, `XrEventText`, commands in `VideoPlayer`,
  `App.DescribeIdleView`) are fixed German, numbers with `Loc.RecordingCulture`. Texts the operator sees **and** the
  event log gets (errors when opening, decoding, software reason) are `OperatorText` (resource name plus arguments):
  `ToString()` in the UI language, `ForRecording()` German, `ForLog()` English.
- **Tests:** `tests/xunit.runner.json` fixes the culture of all test runs to `de-DE`, independent of the Windows
  language. `LocalizationTests` checks every `.resx` pair (same keys, same placeholders, not empty) and `OperatorText`
  in all three languages.

## Delivery (M9)
- **Package:** `release.ps1` publishes `Orbivo.App` self-contained for win-x64 (`dotnet publish -c Release
  --self-contained`, `-p:OrbivoRelease=true`) and adds `licenses\`, `LICENSE.txt`, `NOTICE.txt` and
  `build/THIRD-PARTY-NOTICES.txt`. The native DLLs come through `build/Native.targets` (`CopyToPublishDirectory`), the
  manuals through `Orbivo.App.csproj`. Procedure, checks and package contents: `docs/RELEASE.md`.
- **Version:** `<Version>` in `Directory.Build.props`. Without `OrbivoRelease=true` the `InformationalVersion` becomes
  `X.Y.Z-dev`; the SDK appends the commit (`+<sha>`). The app shows the version without the commit in the title bar;
  log and recording get it in full.
- **`--check`:** like at every start, the app checks FFmpeg, Steam Audio and the OpenXR loader, plus both manuals,
  writes the result to the log and exits without a window (exit code 0 or 1). The release script checks the finished
  package with it.
- **Manual:** `docs/Anleitung.html` and the English version `docs/Manual.html` (one file each without external
  dependencies, linked to each other) are copied next to the exe in every build; F1 opens the one of the selected
  language. Help → Manual or F1 opens it through the shell in the default browser (`App.OpenManual`); if it is
  missing, a message box says where it should be.
- **Playlist at startup:** `MainViewModel` restores the saved paths (missing ones are left out and named in the
  "Playlist" status) and selects the last loaded video without loading it. Every change to playlist, "auto-advance",
  "loop" and fade duration saves the settings. With `--video` the saved playlist stays untouched.

## Synchronization
- **Media time** is the same for picture and sound: ticks from the start of the video stream
  (`MediaDemuxer.OriginTicks`). In the ring buffer: frame = ticks × sample rate / 10⁷.
- **Audio clock (master, rule 4):** the frames that go to the device are numbered from the start of the stream
  ("stream frames"). A segment maps a stream frame to a media time (play, seek). The device reports which stream frame
  is heard at which QPC time. `AudioClockTracker` follows this position with a PI controller and learns the drift of
  the device against QPC; errors above 30 ms reset the clock. Before the first heard frame of a segment the clock
  stands at its beginning, after its end (pause) at its end.
- The audio thread publishes a `ClockSnapshot` through `AudioPlaybackClock` (lock-free). Every thread computes the
  media time for any QPC timestamp from it without waiting for the audio thread. A hold (seek, stop) from the control
  thread applies at once, until the audio thread confirms it with its sequence number.
- `TransportClock` (Media) passes transport commands to the system clock and the audio output and reads the audio
  clock as long as the video has sound; otherwise the system clock (`SystemPlaybackClock`, stopwatch/QPC).
- The render thread picks the newest frame with `pts ≤ clock(display time)`. The predicted display time of the
  headset is converted into a QPC timestamp with `XR_KHR_win32_convert_performance_counter_time`; the preview takes
  "now". Older frames are dropped and counted.
- **A/V measurement** (`AvSyncMeter`): at the first display of every frame `clock(display time) − pts`, as long as the
  clock runs. Status "A/V" and log every 10 s; limit ±20 ms.
- **Start and seek:** the clock stands at the target. The demuxer jumps to the keyframe before it (`av_seek_frame`,
  `AVSEEK_FLAG_BACKWARD`) and puts a seek marker into both queues. The video decoder starts a new frame generation,
  flushes the decoder and decodes forward; frames that end before the target (1 ms tolerance) are dropped without a
  copy. The sound decoder flushes the ring buffer (after fading out) and drops sound before the target sample-exactly.
  Once 4 frames and enough sound are ready (the sound at most 2 s after the picture), the player sets the clock to
  `max(target, first frame)` and lets it run when "playing"; the sound fades in over 5 ms. During the seek the headset
  holds the last picture. Further seeks replace the running one.
- **Loop:** at the end a seek to 0 in the "playing" state.
- **Pause:** the sound fades out over 5 ms, the clock stands at the end of the ramp. The headset keeps rendering the
  current frame with head tracking.
- **Fade and blackout** apply to picture and sound (`OutputFader`), each at the time it is seen or heard.

## OpenXR lifecycle
- Loader (`openxr_loader.dll`, Khronos) → instance with `XR_KHR_D3D11_enable` → system (HMD) → graphics requirements
  (adapter LUID) → D3D11 device on this adapter (once per app run) → session (D3D11 binding) → reference space `LOCAL`
  → one swapchain per eye (sRGB, recommended size).
- Session state events run through `XrSessionLifecycle` (`READY` → begin, `STOPPING` → end,
  `LOSS_PENDING`/`EXITING` → clean up and reconnect after 2 s). When the app exits: `xrRequestExitSession`, wait at
  most 2 s.
- If the runtime is missing (`XR_ERROR_RUNTIME_UNAVAILABLE`) or the headset (`XR_ERROR_FORM_FACTOR_UNAVAILABLE`):
  report the status, retry every 2 s, the app keeps running. Without a headset only `xrGetSystem` is retried, the
  instance stays.
- Reconnecting (N3): device, still image, live view, recenter and idle view belong to the `XrHost`, not to the
  session; they still apply after reconnecting. Video and sound keep running while the headset is missing. If a
  headset was connected before, the status reports "headset disconnected since …" or "Varjo Base is no longer
  running". Connection and session changes and `XrEventDataReferenceSpaceChangePending` (e.g. recenter in Varjo Base)
  appear in the log and the event log.
- Per frame: `xrWaitFrame` → `xrBeginFrame` → `xrLocateViews` → per eye acquire the image, render, release →
  `xrEndFrame` with one projection layer. Rendering uses only the orientation; the layer carries the measured pose.

## `--no-xr` debug mode
- Instead of OpenXR one eye is rendered into an own preview window (Win32, own render thread, flip model swapchain
  with frame latency handle). The window has no close button and closes with the operator window.
- Mouse: dragging grabs the picture (drag to the right = look to the left), mouse wheel = vertical field of view
  30–120° (start 75°), Home = reset.
- Still image via `--image <path>` (M1: equirect 360 mono). Decoded with WIC through WPF in `Orbivo.App`; the texture
  is created on a worker thread and handed to the render thread.
- Same shaders and pipeline (`PanoramaRenderer`), only another view/projection source: in the preview window
  `MouseViewSource` (`IViewSource`); in the headset `xrLocateViews` delivers the views directly in the frame loop
  (they depend on the predicted display time).
- Video via `--video <path>` (M3), in preview and headset. The format comes from metadata and sidecar (format path,
  M6); `--stereo mono|tb|sbs` sets the stereo layout for the session. The preview shows the left eye.
- This allows developing decoding, formats, sync and UI without a headset.

## Sidecar format (`<file name with extension>.orbivo.json`, e.g. `clip.mp4.orbivo.json`)
Sign of `orientationOffsetDeg`: right-hand rule as everywhere, yaw positive = turn to the left. The picture turns: yaw
+30° moves the former picture center 30° to the left, pitch + raises it, roll + tilts the picture counterclockwise (as
seen by the viewer).
```json
{
  "version": 1,
  "projection": "Cubemap",
  "stereoLayout": "TopBottom",
  "swapEyes": null,
  "fisheye": null,
  "cubemapLayout": "6x1",
  "orientationOffsetDeg": { "yaw": 30, "pitch": 0, "roll": 0 },
  "audioFixedToPicture": null,
  "stereoWidthDeg": 60,
  "audioMode": "AmbiX"
}
```
- **`null` or missing = automatic** (metadata, otherwise a guess). All format fields are always written.
- `projection`: `Equirect360`, `Equirect180`, `Cubemap`, `Eac`, `Fisheye`. `stereoLayout`: `Mono`, `TopBottom`,
  `SideBySide`. Case does not matter.
- `cubemapLayout`: name of a preset (`3x2`, `6x1`, `1x6`, `eac`) or an object
  `{ "columns": 3, "rows": 2, "faceOrder": "rludfb", "faceRotation": "000000", "padding": 0 }`.
- `fisheye`: `{ "fovDeg": 180, "centerX": 0.5, "centerY": 0.5, "radius": 0.5 }` (missing fields: these defaults).
- `orientationOffsetDeg`: at most ±360° per axis; `null` = no rotation.
- `audioFixedToPicture` (M7): `true`/`false`, `null` = on (sound fixed to the picture). `stereoWidthDeg`: 30–90,
  `null` = 75.
- `audioMode` (M10): `Channels` (mono, stereo, multichannel mixed to stereo), `AmbiX` (order from the channel count),
  `FuMa` (first order only); `null` = automatic (metadata, otherwise channel count).
- The planned field `externalAudio` and all unknown fields are kept when saving.
- Saving: on every change in the operator window, through a `.tmp` file. If nothing is set any more and there are no
  foreign fields, the file is deleted.
- Invalid fields are ignored with a hint. A file that is not JSON is reported, ignored and backed up as
  `.orbivo.json.bak` before it is first overwritten. A file with `version` > 1 is read but never overwritten.
