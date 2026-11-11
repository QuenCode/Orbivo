# Requirements

## Purpose
Playback of 360°/180° videos with spatial sound on a Varjo XR-3 in controlled viewing sessions. An operator controls
playback at the desktop. The person wearing the headset only sees the video and does not interact.

## Roles
- **Operator:** sits at the desktop monitor and controls everything with mouse and keyboard
- **Viewer:** wears headset and headphones, sees no UI

## Functional requirements

### Playback
- F1 Load videos (file dialog and drag & drop) and a playlist with several videos
- F2 Play, pause, stop, seek (timeline slider and direct time input), jump to the next or previous video
- F3 Optional loop of the single video, auto-advance in the playlist on/off
- F4 Volume (viewer) and mute
- F5 **Blackout** and **fade** from/to black with adjustable duration, independent of the playback state
- F6 Start state of the headset: idle view (F7), black by default. No video until the operator starts it.
- F7 **Idle view:** what the headset shows while no video runs (before Play, after Stop, without a video): black,
  see-through (camera picture of the XR-3) or a still image chosen by the operator (equirect 360). Selectable in the
  UI and saved. Blackout and fade (F5) also apply to the idle view; blackout always stays black

### Formats
- F10 Projections: equirectangular 360°, equirectangular 180° (VR180), cubemap (configurable layout), equi-angular
  cubemap (EAC), fisheye (field of view, center and radius configurable)
- F11 Stereo layouts: mono, side-by-side, top-bottom, each with the option "swap eyes"
- F12 Automatic detection from spherical video metadata (`sv3d`/`st3d`) if present
- F13 Manual override in the UI, saved as a **sidecar file** next to the video (`<file name with extension>.orbivo.json`,
  e.g. `clip.mp4.orbivo.json`), which takes precedence over the metadata the next time the video is loaded
- F14 Orientation offset (yaw/pitch/roll) per video, also in the sidecar
- F15 Video: at least 8K, H.264/HEVC/AV1, 8 and 10 bit, color spaces BT.709 and BT.2020 (SDR)

### Sound
- F20 **Stereo "sound fixed to the picture"** (M7): the left and right channels come binaurally from two virtual
  speakers in the video (by default 75° left and right of the picture center), **head-tracked**: when the viewer turns
  the head, the sound moves with the picture. Can be switched off per video in the UI and sidecar, width adjustable.
  Mono stays without direction, multichannel is mixed down to stereo
- F21 **Ambisonics** (M10): orders 1 to 3 as AmbiX (ACN/SN3D), FuMa first order only; binaural and head-tracked,
  "sound fixed to the picture" applies to the whole sound field. *(later)* 5.1/7.1 via virtual speakers
- F22 Material that is already binaural: passed through without processing (manually selectable: "sound fixed to the
  picture" off)
- F23 The audio mode is detected automatically (Ambisonics metadata `SA3D`, otherwise the channel count: 4, 9 or 16
  channels without speaker positions count as AmbiX) and can be overridden in the UI ("audio format") and the sidecar
  (`audioMode`)
- F24 Output device for the viewer selectable in the UI and saved
- F25 Latency from head rotation to sound change ≤ 30 ms targeted

### Orientation
- F30 **Recenter:** the current viewing direction of the viewer becomes the forward direction of the video (yaw only)
- F31 Recenter affects picture and sound alike

### Status and monitoring (operator)
- F40 Display: headset connected, OpenXR session state, tracking ok, render fps, dropped frames, decoder
  (hardware/software), video/audio time and A/V offset
- F41 Current viewing direction of the viewer (yaw/pitch) as a simple display
- F42 Live view: what the viewer sees, from their point of view, in the operator window. Can be switched on and off
  (off by default); a reduced resolution is enough

### Logging
- F50 One CSV file per session: timestamp (local time of the PC, monotonic), video file, video time, playback state,
  head orientation (quaternion and yaw/pitch/roll), at 90 Hz or a configurable lower rate
- F51 Event log: load, play, pause, seek, blackout, fade, recenter, format change, errors
- F52 Logging on/off in the UI, target folder selectable, optionally only while a video runs (one file pair per video)

### Operation
- F60 Keyboard shortcuts for all core functions (space = play/pause, B = blackout, R = recenter, arrow keys =
  seek ±5 s/±30 s)
- F61 The UI stays usable regardless of the focus of the headset
- F62 Operator UI and manual in English and German, switchable in the operator window (takes effect after a restart,
  which the app offers). English on the very first start. Recordings stay German in every language (M11)

## Non-functional requirements
- N1 Constant 90 fps in the headset with 8K stereo video on an RTX 4080 laptop
- N2 A/V offset ≤ ±20 ms in continuous operation
- N3 Robust when the headset is disconnected or Varjo Base is closed: show the error, reconnect without restarting
  the app
- N4 Self-contained deployment, no installation of FFmpeg or similar needed
- N5 Only LGPL or permissively licensed dependencies (FFmpeg as an LGPL shared build)

## Deliberately out of scope
- Interaction in VR (controllers, hand tracking, in-VR menus)
- Streaming and network sources
- 6DoF and volumetric videos
- Eye tracking (maybe later, the XR-3 would support it)

## Open points
- Actual video format of the recordings: as of 2026-11-09 one example with HEVC 5760 × 2880, 59.94 fps, mono,
  equirect 360. Whether all recordings are like this is open
- Audio format: as of 2026-11-09 first-order Ambisonics, AmbiX with `SA3D`, AAC 4 channels, 48 kHz, embedded in the
  video (M10)
- If audio comes as a separate file: sidecar field for an external audio file with offset → see `ARCHITECTURE.md`
