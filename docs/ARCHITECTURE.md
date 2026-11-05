# Architektur

## Solution-Struktur
```
Orbivo.sln
Directory.Build.props          # Nullable, TreatWarningsAsErrors, LangVersion, x64
Directory.Packages.props       # zentrale, gepinnte Paketversionen
src/
  Orbivo.Core/            # keine UI-, XR- oder Native-Abhängigkeiten
    Commands/                  # PlayerCommand-Records (Play, Pause, Seek, SetFormat, Blackout, Recenter …)
    Playback/                  # PlayerController (Zustandsautomat), PlaybackClock
    Formats/                   # ProjectionType, StereoLayout, AudioMode, VideoFormatSettings, Sidecar-Laden/Speichern
    Math/                      # ProjectionMath (C#-Referenz der Shader-Mathematik), Quaternion-Helfer
    Logging/                   # SessionLogger (CSV), EventLog
    Status/                    # PlayerStatus-Snapshot (immutable record)
  Orbivo.Media/           # FFmpeg: Demux, Decode (D3D11VA), Metadaten (sv3d/st3d, Kanal-Layout)
  Orbivo.Audio/           # NAudio-Ausgabe, Ringpuffer, AmbisonicsProcessor (Steam Audio), Kanal-Konvertierung
  Orbivo.Xr/              # OpenXR-Session, D3D11-Device, Swapchains, Renderer, HLSL-Shader
    Shaders/                   # *.hlsl, zur Buildzeit oder beim Start kompiliert
    DebugViewport/             # Rendering in ein Win32/WPF-Fenster für --no-xr
  Orbivo.App/             # WPF-Operator-UI (MVVM), Composition Root, Settings
tests/
  Orbivo.Core.Tests/      # ProjectionMath, Sidecar, Clock/Sync-Logik, Zustandsautomat
  Orbivo.Audio.Tests/     # Ambisonics-Konvertierungen (FuMa→AmbiX), Kanalreihenfolgen
tools/
  testmedia/                   # Skripte (ffmpeg + Python) zum Erzeugen von Testvideos und Test-Ambisonics
native/
  README.md                    # Woher die FFmpeg- und phonon-DLLs kommen, welche Version
docs/
```

## Threads
| Thread | Aufgabe | Darf blockieren? |
|---|---|---|
| WPF-UI | Operator-UI, schreibt Befehle in `Channel<PlayerCommand>` | ja (UI-üblich) |
| Player-Control | Liest Befehle, steuert Demux/Decoder, Zustandsautomat, publiziert `PlayerStatus` (~10 Hz) | ja |
| Demux | liest Pakete und verteilt sie auf Video- und Audio-Paketqueues | ja |
| Video-Decode | dekodiert in GPU-Frames, legt sie in `VideoFrameQueue` (begrenzt, z. B. 6–8 Frames) | ja |
| Audio-Decode | dekodiert, konvertiert nach float32 planar, schreibt in `AudioRingBuffer` | ja |
| Audio-Out (WASAPI) | Callback: Ringpuffer → AmbisonicsProcessor → Ausgabe; zählt ausgegebene Samples (Audio-Clock) | **nein** |
| XR-Render | OpenXR-Frame-Loop, wählt Frame nach Clock, rendert beide Augen, liest Head-Pose | **nein** |

Geteilte Daten zwischen nicht blockierenden Threads: lock-freie Strukturen (SPSC-Queues, `Volatile`/`Interlocked`). Die aktuelle Kopforientierung wird vom Render-Thread als atomarer Snapshot (Quaternion und Zeitstempel) publiziert und vom Audio-Callback gelesen.

## Datenfluss Video
1. Ein `ID3D11Device` mit `ID3D11Multithread::SetMultithreadProtected(true)` wird von Renderer und FFmpeg gemeinsam genutzt (`AVHWDeviceContext` auf dieses Device setzen).
2. Der Decoder liefert `AV_PIX_FMT_D3D11`-Frames (Texture-Array und Index).
3. Pro Frame `CopySubresourceRegion` in eine eigene Ring-Textur (NV12 oder P010). Das entkoppelt vom Decoder-Pool, und das Frame kann sofort freigegeben werden.
4. Der Shader sampelt Luma-SRV (R8/R16) und Chroma-SRV (R8G8/R16G16) und konvertiert YUV→RGB (Matrix und Range aus dem Stream: BT.709/BT.2020, limited/full).
5. **Gamma-Falle:** Bei einem `*_SRGB`-Swapchain-Format muss der Shader linearisiert ausgeben. Alternativ nicht-sRGB-Format. Die Entscheidung in `DECISIONS.md` festhalten.

## Rendering
- Pro Auge ein Fullscreen-Triangle, kein Kugel-Mesh.
- Der Pixel-Shader rekonstruiert die Blickrichtung: `dir = normalize(mul(invViewRotProj, ndc))`. Die View-Matrix enthält **nur Rotation** (Kopf-Orientierung × Recenter-Offset × Video-Offset). Projektionsmatrix aus dem asymmetrischen `XrFovf`.
- `dir → (u,v)` je nach `ProjectionType`. Danach Stereo-Layout anwenden (Halbbild je Auge wählen).
- Außerhalb des gültigen Bereichs (z. B. Rückseite bei VR180, außerhalb des Fisheye-Kreises) wird Schwarz ausgegeben.
- Fade und Blackout sind ein Multiplikator im Shader. Bei vollem Blackout kann das Video-Sampling entfallen.
- Varjo-spezifisch: Zuerst Standard-Stereo (`PRIMARY_STEREO`). Quad-Views (`XR_VARJO_quad_views`) sind eine spätere Optimierung.

## Projektionsmathematik (Referenz)
Konvention: +Y oben, −Z vorne, +X rechts. `dir` normalisiert.
- **Equirect 360:** `lon = atan2(dir.x, -dir.z)`, `lat = asin(dir.y)`, `u = lon/(2π) + 0.5`, `v = 0.5 − lat/π`
- **Equirect 180:** wie oben, aber `u = lon/π + 0.5`, gültig für `|lon| ≤ π/2`
- **Cubemap:** Hauptachse bestimmt die Fläche. Flächen-Reihenfolge und Rotation pro Fläche kommen aus einer `CubemapLayout`-Tabelle (3×2, 6×1, 1×6 vorbelegt).
- **EAC:** Cubemap mit Winkelkorrektur `uv' = (2/π)·atan(uv) `je Flächenachse (uv ∈ [−1,1]), Layout nach Google Spherical Video V2 / YouTube. **Gegen ein echtes EAC-Testvideo verifizieren.**
- **Fisheye (äquidistant):** `θ = acos(dot(dir, forward))`, `r = θ / (fov/2)`, Winkel um die Achse ergibt die Position im Kreis mit konfigurierbarem Mittelpunkt und Radius.

Alle Formeln stehen in `ProjectionMath.cs` und in `projection.hlsl`. Die Unit-Tests prüfen bekannte Richtungen (vorne, links, oben, hinten) gegen erwartete UV.

## Audio
- Der Decoder liefert Audio als float32. Die Kanal-Layout-Erkennung bestimmt den `AudioMode`: `Stereo`, `Surround`, `Ambisonics(order, convention)`, `Binaural`.
- **Ambisonics:** FuMa wird beim Dekodieren nach AmbiX (ACN/SN3D) konvertiert. Steam Audio rechnet intern in N3D, daher vor der Verarbeitung SN3D → N3D (`iplAudioBufferConvertAmbisonics`). Danach Rotation mit der **inversen** Kopforientierung (inkl. Recenter und Video-Yaw-Offset) und binaurales Decoding über Steam Audio (`iplAmbisonicsBinauralEffect` oder Rotation und Decode).
- **Surround:** virtuelle Lautsprecher (Steam Audio `iplBinauralEffect` pro Kanal, oder Ambisonics-Encode und Decode).
- **Binaural/Stereo:** durchreichen.
- Koordinatenumrechnung OpenXR ↔ Steam Audio an genau einer Stelle (`AudioCoordinates.cs`), mit Unit-Test.
- WASAPI-Puffer 10–20 ms. Die Blockgröße des Prozessors passt zur Puffergröße (z. B. 480 Samples bei 48 kHz).
- **Externe Audiodatei** (falls die Aufnahmen getrennt geliefert werden): Sidecar-Feld `externalAudio` mit `path` und `offsetMs`. Eigener Demux/Decoder für Audio, gleiche Clock.

## Synchronisation
- `PlaybackClock.Now` = (Anzahl ausgegebener Samples − Puffer-Latenz) / Samplerate + Startoffset.
- Der Render-Thread berechnet `targetPts = Clock.Now + (predictedDisplayTime − nowXr)` und wählt das jüngste Frame mit `pts ≤ targetPts`. Ältere Frames werden verworfen und gezählt.
- **Seek:** Queues leeren, Decoder flushen, bis zum Ziel dekodieren (Keyframe-Suche plus Vorwärtsdekodieren), Clock neu setzen. Während des Seeks hält die Brille das letzte Bild (oder Schwarz, konfigurierbar).
- **Pause:** Audio-Ausgabe gibt Stille aus und die Clock steht. Die Brille rendert weiter das aktuelle Frame mit Kopf-Tracking.

## OpenXR-Lebenszyklus
- Instance → System (HMD) → Session (D3D11-Binding) → Reference Space `LOCAL` → Swapchains je Auge.
- Session-State-Events sauber behandeln (`READY` → Begin, `STOPPING` → End, `LOSS_PENDING`/`EXITING` → Aufräumen und Reconnect-Versuch mit Backoff).
- Bei nicht verfügbarer Runtime oder Brille: Status melden, die App läuft weiter (Debug-Viewport möglich).

## `--no-xr` Debug-Modus
- Statt OpenXR wird ein Auge in ein Desktop-Fenster gerendert. Kopfrotation per Maus-Drag, FOV per Mausrad.
- Gleiche Shader und Pipeline, nur andere View/Projection-Quelle (`IViewSource`-Abstraktion: `OpenXrViewSource`, `MouseViewSource`).
- Damit lassen sich Decoding, Formate, Sync und UI ohne Brille entwickeln.

## Sidecar-Format (`<Dateiname mit Endung>.orbivo.json`, z. B. `clip.mp4.orbivo.json`)
Vorzeichen von `orientationOffsetDeg`: wie überall Rechte-Hand-Regel, Yaw positiv = Drehung nach links.
```json
{
  "version": 1,
  "projection": "Equirect360",
  "stereoLayout": "TopBottom",
  "swapEyes": false,
  "fisheye": null,
  "cubemapLayout": null,
  "orientationOffsetDeg": { "yaw": 0, "pitch": 0, "roll": 0 },
  "audioMode": { "type": "Ambisonics", "order": 1, "convention": "AmbiX" },
  "externalAudio": null
}
```
