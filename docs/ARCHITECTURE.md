# Architektur

## Solution-Struktur
```
Orbivo.sln
Directory.Build.props          # Nullable, TreatWarningsAsErrors, LangVersion, x64
Directory.Packages.props       # zentrale, gepinnte Paketversionen
src/
  Orbivo.Core/            # keine UI-, XR- oder Native-Abhängigkeiten
    Commands/                  # PlayerCommand-Records (Open, Play, Pause, Stop, Seek, Loop, Blackout, Fade; später SetFormat, Recenter …)
    Playback/                  # PlaybackStateMachine, SystemPlaybackClock, FramePacer, OutputFader (Blackout/Fade), MediaTime
    Playlists/                 # Playlist (Reihenfolge, aktuelles Video, Weiter/Zurück, Dateien aus Drag & Drop)
    Formats/                   # ProjectionType, StereoLayout, AudioMode, VideoFormatSettings, Sidecar-Laden/Speichern
    Geometry/                  # ProjectionMath (C#-Referenz der Shader-Mathematik), OrientationMath, FovAngles
    Logging/                   # SessionLogger (CSV), EventLog
    Status/                    # PlayerStatus-Snapshot (immutable record)
  Orbivo.Media/           # FFmpeg: Demux, Decode (D3D11VA), Metadaten (sv3d/st3d, Kanal-Layout)
  Orbivo.Audio/           # NAudio-Ausgabe, Ringpuffer, AmbisonicsProcessor (Steam Audio), Kanal-Konvertierung
  Orbivo.Xr/              # OpenXR-Session, D3D11-Device, Swapchains, Renderer, HLSL-Shader
    OpenXr/                    # XrHost (XR-Render-Thread), Instance, Session, Swapchains, Lifecycle, Umrechnungen
    Graphics/                  # D3D11-Device, Shader-Compiler
    Rendering/                 # PanoramaRenderer, IViewSource, Standbild-Textur
    Shaders/                   # *.hlsl, als Ressource eingebettet und beim Start kompiliert
    DebugViewport/             # Vorschaufenster für --no-xr (eigenes Win32-Fenster, eigener Render-Thread)
  Orbivo.App/             # WPF-Operator-UI (MVVM), Composition Root, Settings
tests/
  Orbivo.Core.Tests/      # ProjectionMath, Sidecar, Clock/Sync-Logik, Zustandsautomat
  Orbivo.Audio.Tests/     # Ambisonics-Konvertierungen (FuMa→AmbiX), Kanalreihenfolgen
  Orbivo.Xr.Tests/        # Shader gegen ProjectionMath (WARP), Richtungstest mit dem Testbild, Maus-Steuerung
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
| Player-Control | Liest Befehle, steuert Demux/Decoder, Zustandsautomat, publiziert `PlayerStatus` (~10 Hz). Hält bei „Läuft“ und „Pause“ Bildschirm und System wach (`DisplayKeepAwake`) | ja |
| Demux (`MediaDemuxer`) | liest Pakete und verteilt sie auf Video- und Audio-`PacketQueue`; führt Seeks aus und legt eine Seek-Marke in beide Queues | ja |
| Video-Decode | dekodiert in GPU-Frames, kopiert sie in den Ring der `VideoFrameQueue` (6 Slots) und gibt sie über `FramePacer` frei. Führt Seeks aus und wartet nach dem Dateiende auf den nächsten Seek | ja |
| Audio-Decode (`AudioDecoder`) | dekodiert, konvertiert mit swresample nach float32 Stereo interleaved in der Geräte-Samplerate, schreibt mit Medienposition in `AudioRingBuffer` | ja |
| Audio-Out (`AudioOutput`, MMCSS „Pro Audio“) | wartet auf das WASAPI-Ereignis, liest Befehle, Geräteposition → `AudioClockTracker` → `ClockSnapshot`; `AudioRenderer` füllt den Puffer aus dem Ringpuffer (ab M7 über den AmbisonicsProcessor). Geräte öffnen und Wiederholung laufen zwischen den Perioden, nie darin | **nein** in der Periode (wartet nur auf das Geräte-Ereignis) |
| XR-Render (`XrHost`) | OpenXR-Frame-Loop, wählt Frame nach Clock, rendert beide Augen, liest Head-Pose. Ohne laufende Session: Verbindungsaufbau und Wiederholung; holt dabei fällige Video-Frames ab, damit der Decoder mit der Uhr mitläuft | **nein** im Frame-Loop (wartet nur in `xrWaitFrame`); ohne Session darf er warten |
| Vorschau-Render (nur `--no-xr`) | Fensternachrichten und Maus, rendert ein Auge ins Vorschaufenster | **nein** (wartet nur auf den Frame-Takt der Swapchain, wie `xrWaitFrame`) |

Geteilte Daten zwischen nicht blockierenden Threads: lock-freie Strukturen (SPSC-Queues, `Volatile`/`Interlocked`). Die aktuelle Kopforientierung wird vom Render-Thread als atomarer Snapshot (Quaternion und Zeitstempel) publiziert und vom Audio-Callback gelesen.

## Datenfluss Video
1. Ein `ID3D11Device` mit `ID3D11Multithread::SetMultithreadProtected(true)` wird von Renderer und FFmpeg gemeinsam genutzt (`AVHWDeviceContext` auf dieses Device setzen).
2. Der Decoder liefert `AV_PIX_FMT_D3D11`-Frames (Texture-Array und Index).
3. Pro Frame `CopySubresourceRegion` in eine eigene Ring-Textur (NV12 oder P010). Das entkoppelt vom Decoder-Pool, und das Frame kann sofort freigegeben werden. Die Kopie läuft auf dem geteilten Immediate Context (Multithread-Lock), damit liegt sie auf der GPU vor jedem Draw, der das Slot liest.
4. `FramePacer` (Core) übergibt Slots lock-frei: eine SPSC-Queue freier Slots (Decoder → Render) und eine fertiger Slots mit pts (Render ← Decoder). Der Render-Thread wählt das jüngste Frame mit `pts ≤ Zielzeit` und gibt ältere sofort zurück.
5. `VideoPlayer` (Media) ist die einzige Schnittstelle für die Render-Threads (`IVideoFrameSource.TryGetFrame(Anzeigezeitpunkt)` und `GetOutputBrightness(Anzeigezeitpunkt)` für Blackout/Fade). Beim Öffnen/Stoppen tauscht der Control-Thread die Queue; ein Hazard-Pointer verhindert, dass er eine Queue freigibt, die der Render-Thread gerade benutzt.
5a. **Seek** bleibt in derselben Queue: Der Decode-Thread startet eine neue Generation im `FramePacer`. Der Render-Thread gibt Slots älterer Generationen zurück, zeigt aber sein aktuelles Frame weiter, bis ein Frame der neuen Generation fällig ist.
6. Der Shader sampelt Luma-SRV (R8/R16) und Chroma-SRV (R8G8/R16G16) und konvertiert YUV→RGB (Matrix und Range aus dem Stream: BT.601/BT.709/BT.2020, limited/full; `color.hlsl`, C#-Referenz `VideoColor`). BT.2020 wird in linearem Licht auf BT.709 umgerechnet.
7. **Gamma-Falle:** Bei einem `*_SRGB`-Swapchain-Format muss der Shader linearisiert ausgeben. Entschieden in M1 (`DECISIONS.md`): durchgehend sRGB-Views, der Shader rechnet und schreibt linear. Seit M3 linearisiert der Shader das Video nach YUV→RGB mit der sRGB-Kurve, die Code-Werte kommen also unverändert an.

## Rendering
- Pro Auge ein Fullscreen-Triangle, kein Kugel-Mesh.
- Der Pixel-Shader rekonstruiert die Blickrichtung: `dir = normalize(mul(invViewRotProj, ndc))`. Die View-Matrix enthält **nur Rotation** (Kopf-Orientierung × Recenter-Offset × Video-Offset). Projektionsmatrix aus dem asymmetrischen `XrFovf`.
- Umgesetzt als drei Vektoren statt Matrix (`ViewRayBasis`): `dir = normalize(ndc.x·R + ndc.y·U + C)`. C# berechnet R, U und C, der Shader bekommt sie im Constant Buffer. So teilen C# und HLSL kein Matrix-Layout.
- Abtastung ohne Mipmaps (`SampleLevel 0`), U wiederholend, V am Rand begrenzt. Dadurch gibt es hinten bei ±180° keine Naht.
- `dir → (u,v)` je nach `ProjectionType`. Danach Stereo-Layout anwenden (Halbbild je Auge wählen): `SourceUv = clamp(uv, min, max) · Scale + Offset` (`EyeSourceRect`). Top-Bottom: oben = linkes Auge; Side-by-Side: links = linkes Auge. Begrenzt wird auf ein halbes Texel vor der Halbbildgrenze.
- Außerhalb des gültigen Bereichs (z. B. Rückseite bei VR180, außerhalb des Fisheye-Kreises) wird Schwarz ausgegeben.
- Fade und Blackout sind ein Multiplikator im Shader (`Brightness`), bei 0 wird nur gelöscht. Der Faktor kommt pro Frame aus `OutputFader` (über den Player) und ist `level^2,2`, damit der Fade in wahrgenommener Helligkeit gleichmäßig läuft.
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
Stand M5: nur Stereo-Durchleitung. Räumlicher Ton folgt in M7.

### Datenfluss Ton
1. `MediaDemuxer` wählt die Tonspur (`av_find_best_stream`, passend zum Videostream) und füllt die Audio-`PacketQueue`.
2. `AudioDecoder` dekodiert; `AudioConversionPlan` legt die Umrechnung fest: Stereo durchreichen, Mono auf beide Seiten, Mehrkanal bis M7 auf Stereo mischen, Resampling auf die Samplerate des Ausgabegeräts. Ergebnis: float32 Stereo interleaved mit Medienposition.
3. `AudioRingBuffer` (Core, SPSC, 2¹⁷ Frames ≈ 2,7 s bei 48 kHz) ist medienadressiert: Der Leser holt „Daten ab Medienzeit t“, zu späte Daten werden übersprungen, Lücken werden Stille. Flush (Seek, Stop) als Handshake: Die alten Daten bleiben lesbar, bis die Ausgabe ausgeblendet und bestätigt hat.
4. `AudioOutput` (Orbivo.Audio) besitzt den Audio-Out-Thread und das Gerät. Der Player spricht nur über `IAudioOutput` (Core) mit ihr: Befehle (Attach, Play, Pause, Hold, Lautstärke, Stumm, Gerät) über eine lock-freie Queue, Uhr und Status über Snapshots.
5. `AudioRenderer` (Zustände Still / Läuft / Blendet zur Pause aus / Blendet zum Halt aus) holt pro Periode die Frames zur gehörten Medienzeit, wendet Transport-Rampe (5 ms Smoothstep) und Verstärkung an und zählt Aussetzer. `SampleWriter` schreibt in das Geräteformat (float32, int16/24/32; Stereo auf die ersten zwei Kanäle, ein Mono-Gerät bekommt den Mittelwert).
6. Verstärkung = `level³(Lautstärke)` × (stumm ? 0 : 1) × `level³(Fade/Schwarzbild)` zu dem Zeitpunkt, an dem der erste Frame der Periode gehört wird, geglättet mit 5 ms (`GainSmoother`).

### Geräte
- `IAudioEndpoint` kapselt ein Gerät: `WasapiAudioEndpoint` (Shared Mode, ereignisgesteuert, 20 ms Puffer, Position über `IAudioClock` mit QPC-Zeitstempel), `NullAudioEndpoint` (stiller Ersatz mit Systemtakt) und `SimulatedAudioEndpoint` (für Tests: Drift, Latenz, Aufnahme, Erkennung von Tönen).
- `WasapiEndpointProvider` listet Geräte und meldet Änderungen (`IMMNotificationClient`). „Windows-Standard“ folgt dem Standardgerät; ein fest gewähltes Gerät wird nie automatisch gewechselt.
- Fehlt das Gerät oder fällt es aus (Fehler oder 500 ms ohne Periode), läuft `NullAudioEndpoint` weiter; neuer Versuch alle 2 s und bei jeder Geräteänderung.
- `settings.json` (`%LOCALAPPDATA%\Orbivo`) speichert Gerät und Lautstärke, nicht Stumm (`AppSettings`, Core).

### Ab M7 (geplant)
- Die Kanal-Layout-Erkennung bestimmt den `AudioMode`: `Stereo`, `Surround`, `Ambisonics(order, convention)`, `Binaural`.
- **Ambisonics:** FuMa wird beim Dekodieren nach AmbiX (ACN/SN3D) konvertiert. Steam Audio rechnet intern in N3D, daher vor der Verarbeitung SN3D → N3D (`iplAudioBufferConvertAmbisonics`). Danach Rotation mit der **inversen** Kopforientierung (inkl. Recenter und Video-Yaw-Offset) und binaurales Decoding über Steam Audio (`iplAmbisonicsBinauralEffect` oder Rotation und Decode).
- **Surround:** virtuelle Lautsprecher (Steam Audio `iplBinauralEffect` pro Kanal, oder Ambisonics-Encode und Decode).
- **Binaural/Stereo:** durchreichen.
- Koordinatenumrechnung OpenXR ↔ Steam Audio an genau einer Stelle (`AudioCoordinates.cs`), mit Unit-Test.
- Die Blockgröße des Prozessors passt zur Periode des Geräts (z. B. 480 Samples bei 48 kHz).
- **Externe Audiodatei** (falls die Aufnahmen getrennt geliefert werden): Sidecar-Feld `externalAudio` mit `path` und `offsetMs`. Eigener Demux/Decoder für Audio, gleiche Clock.

## Synchronisation
- **Medienzeit** ist für Bild und Ton gleich: Ticks ab dem Beginn des Videostreams (`MediaDemuxer.OriginTicks`). Im Ringpuffer: Frame = Ticks × Samplerate / 10⁷.
- **Audio-Clock (Master, Regel 4):** Die Frames, die an das Gerät gehen, sind ab dem Start des Streams nummeriert („Stream-Frames“). Ein Segment ordnet einem Stream-Frame eine Medienzeit zu (Play, Seek). Das Gerät meldet, welcher Stream-Frame zu welchem QPC-Zeitpunkt gehört wird. `AudioClockTracker` folgt dieser Position mit einem PI-Regler und lernt dabei die Drift des Geräts gegen QPC; Fehler über 30 ms setzen die Uhr neu. Vor dem ersten gehörten Frame eines Segments steht die Uhr am Anfang, nach dem Ende (Pause) am Ende.
- Der Audio-Thread publiziert einen `ClockSnapshot` über `AudioPlaybackClock` (lock-frei). Jeder Thread rechnet daraus die Medienzeit zu einem beliebigen QPC-Zeitstempel, ohne auf den Audio-Thread zu warten. Ein Halt (Seek, Stop) vom Control-Thread gilt sofort, bis der Audio-Thread ihn mit seiner Sequenznummer bestätigt.
- `TransportClock` (Media) gibt Transportbefehle an Systemuhr und Tonausgabe und liest die Ton-Uhr, solange das Video Ton hat; sonst die Systemuhr (`SystemPlaybackClock`, Stopwatch/QPC).
- Der Render-Thread wählt das jüngste Frame mit `pts ≤ Uhr(Anzeigezeitpunkt)`. Die vorhergesagte Anzeigezeit der Brille wird mit `XR_KHR_win32_convert_performance_counter_time` in einen QPC-Zeitstempel umgerechnet; die Vorschau nimmt „jetzt“. Ältere Frames werden verworfen und gezählt.
- **A/V-Messung** (`AvSyncMeter`): bei der ersten Anzeige jedes Frames `Uhr(Anzeigezeit) − pts`, solange die Uhr läuft. Status „A/V“ und Log alle 10 s; Grenze ±20 ms.
- **Start und Seek:** Die Uhr steht am Ziel. Der Demuxer springt auf den Keyframe davor (`av_seek_frame`, `AVSEEK_FLAG_BACKWARD`) und legt eine Seek-Marke in beide Queues. Der Video-Decoder beginnt eine neue Frame-Generation, leert den Decoder und dekodiert vorwärts; Frames, die vor dem Ziel enden (1 ms Toleranz), werden ohne Kopie verworfen. Der Ton-Decoder leert den Ringpuffer (nach dem Ausblenden) und verwirft Ton vor dem Ziel samplegenau. Sind 4 Frames und genug Ton bereit (der Ton höchstens 2 s nach dem Bild), setzt der Player die Uhr auf `max(Ziel, erstes Frame)` und lässt sie bei „Läuft“ weiterlaufen; der Ton blendet in 5 ms ein. Während des Seeks hält die Brille das letzte Bild. Weitere Seeks ersetzen den laufenden.
- **Loop:** Am Ende Seek auf 0 im Zustand „Läuft“.
- **Pause:** Der Ton blendet in 5 ms aus, die Uhr steht am Ende der Rampe. Die Brille rendert weiter das aktuelle Frame mit Kopf-Tracking.
- **Fade und Schwarzbild** wirken auf Bild und Ton (`OutputFader`), jeweils zu dem Zeitpunkt, an dem es gesehen bzw. gehört wird.

## OpenXR-Lebenszyklus
- Loader (`openxr_loader.dll`, Khronos) → Instance mit `XR_KHR_D3D11_enable` → System (HMD) → Grafik-Anforderungen (Adapter-LUID) → D3D11-Device auf diesem Adapter (einmal je App-Lauf) → Session (D3D11-Binding) → Reference Space `LOCAL` → Swapchain je Auge (sRGB, empfohlene Größe).
- Session-State-Events laufen durch `XrSessionLifecycle` (`READY` → Begin, `STOPPING` → End, `LOSS_PENDING`/`EXITING` → Aufräumen und nach 2 s neu verbinden). Beim Beenden der App: `xrRequestExitSession`, höchstens 2 s warten.
- Fehlt das Runtime (`XR_ERROR_RUNTIME_UNAVAILABLE`) oder die Brille (`XR_ERROR_FORM_FACTOR_UNAVAILABLE`): Status melden, alle 2 s erneut versuchen, die App läuft weiter. Ohne Brille wird nur `xrGetSystem` wiederholt, die Instance bleibt.
- Pro Frame: `xrWaitFrame` → `xrBeginFrame` → `xrLocateViews` → je Auge Bild holen, rendern, freigeben → `xrEndFrame` mit einem Projection-Layer. Gerendert wird nur mit der Orientierung, im Layer steht die gemessene Pose.

## `--no-xr` Debug-Modus
- Statt OpenXR wird ein Auge in ein eigenes Vorschaufenster gerendert (Win32, eigener Render-Thread, Flip-Model-Swapchain mit Frame-Latency-Handle). Das Fenster hat keinen Schließen-Knopf und schließt mit dem Operatorfenster.
- Maus: Ziehen greift das Bild (Ziehen nach rechts = Blick nach links), Mausrad = vertikales Sichtfeld 30–120° (Start 75°), Pos1 = zurücksetzen.
- Standbild per `--image <Pfad>` (M1: Equirect 360 Mono). Dekodiert wird mit WIC über WPF in `Orbivo.App`, die Textur entsteht auf einem Worker-Thread und wird dem Render-Thread übergeben.
- Gleiche Shader und Pipeline (`PanoramaRenderer`), nur andere View/Projection-Quelle: im Vorschaufenster `MouseViewSource` (`IViewSource`), in der Brille liefert `xrLocateViews` die Ansichten direkt im Frame-Loop (sie hängen an der vorhergesagten Anzeigezeit).
- Video per `--video <Pfad>` (M3), in Vorschau und Brille. Stereo-Layout wird aus der Größe geraten (quadratisch = Top-Bottom) oder mit `--stereo mono|tb|sbs` festgelegt; die Vorschau zeigt das linke Auge.
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
