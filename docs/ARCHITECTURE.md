# Architektur

## Solution-Struktur
```
Orbivo.sln
Directory.Build.props          # Nullable, TreatWarningsAsErrors, LangVersion, x64, Version (nur release.ps1 ändert sie)
Directory.Packages.props       # zentrale, gepinnte Paketversionen
release.ps1                    # Release bauen und veröffentlichen (docs/RELEASE.md)
CHANGELOG.md                   # Änderungen je Version, Quelle der Release-Notizen
build/                         # Native.targets (native DLLs ins Ausgabeverzeichnis), THIRD-PARTY-NOTICES.txt (fürs Paket)
src/
  Orbivo.Core/            # keine UI-, XR- oder Native-Abhängigkeiten
    Commands/                  # PlayerCommand-Records (Open, Close, Play, Pause, Stop, Seek, Loop, Blackout, Fade, SetVideoFormat, Recenter)
    Playback/                  # PlaybackStateMachine, SystemPlaybackClock, FramePacer, OutputFader (Blackout/Fade), MediaTime
    Playlists/                 # Playlist (Reihenfolge, aktuelles Video, Weiter/Zurück, Dateien aus Drag & Drop)
    Formats/                   # ProjectionType, StereoLayout, CubemapLayout, FisheyeParameters, VideoFormat, FormatOverrides,
                               # EmbeddedFormatMetadata, VideoFormatResolver (Rangfolge), SidecarFile (Laden/Speichern)
    Geometry/                  # ProjectionMath (C#-Referenz der Shader-Mathematik), OrientationMath, FovAngles, RecenterControl (M8)
    Audio/                     # AudioRingBuffer, AudioConversionPlan, Uhr; StereoPlacement, AudioCoordinates, ListenerOrientation (M7)
    Logging/                   # SessionRecorder (Kopf-CSV F50, Ereignis-Log F51), HeadSampleGate, SessionEventQueue, GermanCsv
    Status/                    # PlayerStatus-Snapshot (immutable record)
  Orbivo.Media/           # FFmpeg: Demux, Decode (D3D11VA), Metadaten (sv3d/st3d, Kanal-Layout)
  Orbivo.Audio/           # NAudio-Ausgabe (Output/), Steam-Audio-Interop (SteamAudio/), Ton fest am Bild (Spatial/)
  Orbivo.Xr/              # OpenXR-Session, D3D11-Device, Swapchains, Renderer, HLSL-Shader
    OpenXr/                    # XrHost (XR-Render-Thread), Instance, Session, Swapchains, Lifecycle, Umrechnungen
    Graphics/                  # D3D11-Device, Shader-Compiler
    Rendering/                 # PanoramaRenderer, IViewSource, Standbild-Textur
    Shaders/                   # *.hlsl, als Ressource eingebettet und beim Start kompiliert
    DebugViewport/             # Vorschaufenster für --no-xr (eigenes Win32-Fenster, eigener Render-Thread)
    LiveView/                  # Live-Ansicht (F42): LiveViewFrames (3 Slot-Texturen), LiveViewPresenter (Thread), LiveViewWindow (Kindfenster)
  Orbivo.App/             # WPF-Operator-UI (MVVM), Composition Root, Settings; kopiert docs/Anleitung.html und docs/Manual.html neben die exe
tests/
  Orbivo.Core.Tests/      # ProjectionMath, Sidecar, Clock/Sync-Logik, Zustandsautomat
  Orbivo.Audio.Tests/     # Renderer und Ausgabe mit simulierten Geräten, Richtung mit echter phonon.dll (Pegel der Ohren)
  Orbivo.Xr.Tests/        # Shader gegen ProjectionMath (WARP), Richtungstest mit dem Testbild, alle Projektionen gegen Equirect 360, Maus-Steuerung
  Orbivo.Media.Tests/     # Dekodieren mit echter GPU, Player über den Befehlskanal, A/V-Sync, 30-Minuten-Dauertest (opt-in)
tools/
  testmedia/                   # Skripte (ffmpeg + Python) zum Erzeugen von Testvideos und Test-Ambisonics
native/
  README.md                    # Woher die FFmpeg- und phonon-DLLs kommen, welche Version
docs/                          # SPEC, ARCHITECTURE, ROADMAP, DECISIONS, RELEASE; Anleitung.html (Bedienungsanleitung, wird ausgeliefert)
```

## Threads
| Thread | Aufgabe | Darf blockieren? |
|---|---|---|
| WPF-UI | Operator-UI, schreibt Befehle in `Channel<PlayerCommand>` | ja (UI-üblich) |
| Player-Control | Liest Befehle, steuert Demux/Decoder, Zustandsautomat, publiziert `PlayerStatus` (~10 Hz). Hält bei „Läuft“ und „Pause“ Bildschirm und System wach (`DisplayKeepAwake`) | ja |
| Demux (`MediaDemuxer`) | liest Pakete und verteilt sie auf Video- und Audio-`PacketQueue`; führt Seeks aus und legt eine Seek-Marke in beide Queues | ja |
| Video-Decode | dekodiert in GPU-Frames, kopiert sie in den Ring der `VideoFrameQueue` (6 Slots) und gibt sie über `FramePacer` frei. Führt Seeks aus und wartet nach dem Dateiende auf den nächsten Seek | ja |
| Audio-Decode (`AudioDecoder`) | dekodiert, konvertiert mit swresample nach float32 Stereo interleaved in der Geräte-Samplerate, schreibt mit Medienposition in `AudioRingBuffer` | ja |
| Audio-Out (`AudioOutput`, MMCSS „Pro Audio“) | wartet auf das WASAPI-Ereignis, liest Befehle, Geräteposition → `AudioClockTracker` → `ClockSnapshot`; `AudioRenderer` füllt den Puffer aus dem Ringpuffer (bei „Ton fest am Bild“ über die `SpatialStage`). Geräte öffnen und Wiederholung laufen zwischen den Perioden, nie darin | **nein** in der Periode (wartet nur auf das Geräte-Ereignis) |
| XR-Render (`XrHost`) | OpenXR-Frame-Loop, wählt Frame nach Clock, rendert beide Augen, liest Head-Pose. Ohne laufende Session: Verbindungsaufbau und Wiederholung; holt dabei fällige Video-Frames ab, damit der Decoder mit der Uhr mitläuft | **nein** im Frame-Loop (wartet nur in `xrWaitFrame`); ohne Session darf er warten |
| Vorschau-Render (nur `--no-xr`) | Fensternachrichten und Maus, rendert ein Auge ins Vorschaufenster | **nein** (wartet nur auf den Frame-Takt der Swapchain, wie `xrWaitFrame`) |
| Live-Ansicht (`LiveViewPresenter`, nur wenn eingeschaltet) | kopiert das neueste Bild der Live-Ansicht ins Kindfenster im Operatorfenster und zeigt es an | ja (wartet auf den Frame-Takt seiner Swapchain); der Render-Thread wartet nie auf ihn |
| Aufzeichnung (`SessionRecorder`) | holt Kopfproben (`HeadSampleGate`) und Ereignisse (`SessionEventQueue`) ab und schreibt die beiden CSV-Dateien; öffnet und schließt sie bei Start und Stopp | ja (Datei-I/O); Render- und Player-Thread warten nie auf ihn |

Geteilte Daten zwischen nicht blockierenden Threads: lock-freie Strukturen (SPSC-Queues, `Volatile`/`Interlocked`). Die aktuelle Kopforientierung im Bildsystem (mit Ausrichtungs-Offset) veröffentlichen XR-Render- bzw. Vorschau-Thread jedes Bild als `ListenerOrientation` (`SnapshotCell`, Quaternion und Zeitstempel); der Audio-Thread liest sie einmal je Block.

## Datenfluss Video
1. Ein `ID3D11Device` mit `ID3D11Multithread::SetMultithreadProtected(true)` wird von Renderer und FFmpeg gemeinsam genutzt (`AVHWDeviceContext` auf dieses Device setzen).
2. Der Decoder liefert `AV_PIX_FMT_D3D11`-Frames (Texture-Array und Index).
3. Pro Frame `CopySubresourceRegion` in eine eigene Ring-Textur (NV12 oder P010). Das entkoppelt vom Decoder-Pool, und das Frame kann sofort freigegeben werden. Die Kopie läuft auf dem geteilten Immediate Context (Multithread-Lock), damit liegt sie auf der GPU vor jedem Draw, der das Slot liest.
4. `FramePacer` (Core) übergibt Slots lock-frei: eine SPSC-Queue freier Slots (Decoder → Render) und eine fertiger Slots mit pts (Render ← Decoder). Der Render-Thread wählt das jüngste Frame mit `pts ≤ Zielzeit` und gibt ältere sofort zurück.
5. `VideoPlayer` (Media) ist die einzige Schnittstelle für die Render-Threads (`IVideoFrameSource.TryGetFrame(Anzeigezeitpunkt)` und `GetOutputBrightness(Anzeigezeitpunkt)` für Blackout/Fade). Beim Öffnen/Stoppen tauscht der Control-Thread die Queue; ein Hazard-Pointer verhindert, dass er eine Queue freigibt, die der Render-Thread gerade benutzt.
5a. **Seek** bleibt in derselben Queue: Der Decode-Thread startet eine neue Generation im `FramePacer`. Der Render-Thread gibt Slots älterer Generationen zurück, zeigt aber sein aktuelles Frame weiter, bis ein Frame der neuen Generation fällig ist.
6. Der Shader sampelt Luma-SRV (R8/R16) und Chroma-SRV (R8G8/R16G16) und konvertiert YUV→RGB (Matrix und Range aus dem Stream: BT.601/BT.709/BT.2020, limited/full; `color.hlsl`, C#-Referenz `VideoColor`). BT.2020 wird in linearem Licht auf BT.709 umgerechnet.
6a. **Software-Fallback** (`DECISIONS.md`, 2026-11-07): Kann die GPU das Video nicht dekodieren (`HardwareDecodeSupport` fragt vor dem Öffnen `ID3D11VideoDevice`), dekodiert FFmpeg auf der CPU (4:2:0, 8/10 Bit). swscale schreibt jedes Bild als NV12/P010 in eine gemappte Staging-Textur (`Map` mit `DoNotWait`, zwei im Wechsel), danach `CopySubresourceRegion` in den Slot wie bei Schritt 3. Scheitert die Hardware vor dem ersten Bild trotzdem, öffnet der Player die Datei neu in Software. Die einzige Stelle, an der Frames von der CPU zur GPU gehen (Ausnahme von Regel 5).
7. **Gamma-Falle:** Bei einem `*_SRGB`-Swapchain-Format muss der Shader linearisiert ausgeben. Entschieden in M1 (`DECISIONS.md`): durchgehend sRGB-Views, der Shader rechnet und schreibt linear. Seit M3 linearisiert der Shader das Video nach YUV→RGB mit der sRGB-Kurve, die Code-Werte kommen also unverändert an.

## Rendering
- Pro Auge ein Fullscreen-Triangle, kein Kugel-Mesh.
- Der Pixel-Shader rekonstruiert die Blickrichtung: `dir = normalize(mul(invViewRotProj, ndc))`. Die View-Matrix enthält **nur Rotation** (Kopf-Orientierung × Recenter-Offset × Video-Offset). Projektionsmatrix aus dem asymmetrischen `XrFovf`.
- Der Ausrichtungs-Offset des Videos (F14) dreht die Blickrichtung vor der Projektion: `view' = conj(R) · view` mit `R = yaw · pitch · roll` (`OrientationOffset.ApplyToView`). So dreht sich das Bild um R: Yaw +30° legt die bisherige Bildmitte 30° nach links.
- Umgesetzt als drei Vektoren statt Matrix (`ViewRayBasis`): `dir = normalize(ndc.x·R + ndc.y·U + C)`. C# berechnet R, U und C, der Shader bekommt sie im Constant Buffer. So teilen C# und HLSL kein Matrix-Layout.
- Abtastung ohne Mipmaps (`SampleLevel 0`), U wiederholend, V am Rand begrenzt. Dadurch gibt es hinten bei ±180° keine Naht.
- `dir → (u,v)` je nach `ProjectionType` (`ProjectedEyeUv` in `projection.hlsl`, `ProjectionMath.EyeUv` in C#). Die Parameter der Projektion (Würfel-Layout als Tabelle je Fläche, Fisheye-Kreis) rechnet C# einmal pro Frame aus dem `VideoFormat` (`ProjectionMath.CreateProjectionParameters`) und legt sie in den Constant Buffer. Danach Stereo-Layout anwenden (Halbbild je Auge wählen): `SourceUv = clamp(uv, min, max) · Scale + Offset` (`EyeSourceRect`). Top-Bottom: oben = linkes Auge; Side-by-Side: links = linkes Auge. Begrenzt wird auf ein halbes Texel vor der Halbbildgrenze, bei allen Projektionen außer Equirect 360 auch links und rechts.
- **Augen tauschen** (F11): `VideoFormat.SourceEye` nimmt für das linke Auge das rechte Halbbild und umgekehrt.
- Außerhalb des gültigen Bereichs (z. B. Rückseite bei VR180, außerhalb des Fisheye-Kreises) wird Schwarz ausgegeben.
- Fade und Blackout sind ein Multiplikator im Shader (`Brightness`), bei 0 wird nur gelöscht. Der Faktor kommt pro Frame aus `OutputFader` (über den Player) und ist `level^2,2`, damit der Fade in wahrgenommener Helligkeit gleichmäßig läuft.
- Varjo-spezifisch: Zuerst Standard-Stereo (`PRIMARY_STEREO`). Quad-Views (`XR_VARJO_quad_views`) sind eine spätere Optimierung.

## Live-Ansicht (F42, M6.5)
- **Blickwinkel:** In der Brille rechnet `CombinedView` (Core) aus beiden Augen eine Ansicht: Richtung = Mitte der Augen (Kopf), Sichtfeld vom linken Rand des linken Auges bis zum rechten Rand des rechten Auges, oben und unten jeweils das Größere; jede Kante höchstens 80° von der Achse. Gezeigt wird das linke Halbbild (bei Stereo). In `--no-xr` zeigt sie das Bild der Vorschau.
- **Render-Thread:** `LiveViewFrames.RenderIfDue` zeichnet nach den Augen, höchstens alle 30 ms (90 Hz: jedes 3. Bild), mit Fade und Schwarzbild, in eine von drei Slot-Texturen (1920 × 1080, B8G8R8A8, sRGB-View). Größe: das Seitenverhältnis des Sichtfelds, eingepasst in die Fläche im Operatorfenster. Die Übergabe läuft über `TripleBufferIndex` (Core, lock-frei, das neueste gewinnt). Ist die Live-Ansicht aus, zeichnet der Render-Thread nichts zusätzlich.
- **Anzeige-Thread:** `LiveViewPresenter` kopiert das neueste Bild mittig in die Swapchain eines Win32-Kindfensters (WPF `HwndHost`, `LiveViewHost`) und zeigt es an. Flip-Model mit Frame-Latency-Handle, sonst das ältere Copy-Model.
- **Geteilter Immediate Context:** Der Anzeige-Thread benutzt nur Befehle, die keinen Pipeline-Zustand ändern (Löschen, Kopieren, Present, Flush; kein `ClearState`). Ein eigener Draw würde dem Render-Thread mitten im Bild Shader und Ziele verstellen. Weil beide Threads in denselben Context schreiben, kommt die Kopie eines Slots auf der GPU immer nach dem Zeichnen hinein.
- Das Kindfenster lässt Mausklicks zum Operatorfenster durch (`HTTRANSPARENT`) und nimmt nie den Tastaturfokus. Es wird auf dem UI-Thread erzeugt; vor dem Zerstören wird der Anzeige-Thread beendet.

## Projektionsmathematik (Referenz)
Konvention: +Y oben, −Z vorne, +X rechts. `dir` normalisiert.
- **Equirect 360:** `lon = atan2(dir.x, -dir.z)`, `lat = asin(dir.y)`, `u = lon/(2π) + 0.5`, `v = 0.5 − lat/π`
- **Equirect 180:** wie oben, aber `u = lon/π + 0.5`, gültig für `|lon| ≤ π/2`
- **Cubemap:** Die Hauptachse bestimmt die Fläche (bei Gleichstand X vor Y vor Z). Auf der Fläche `(s, t) ∈ [−1, 1]²`, s nach rechts, t nach unten, so wie man die Fläche von der Würfelmitte aus sieht. Ungedrehte Flächen: vorne, hinten, links, rechts aufrecht; OBEN mit der Oberkante nach hinten (+Z), UNTEN mit der Oberkante nach vorne (wie ffmpeg `v360`). `CubemapLayout` beschreibt Raster (Spalten × Zeilen), Flächen je Zelle (`rludfb`) und Drehung je Zelle in Vierteldrehungen im Uhrzeigersinn, in der Schreibweise von `v360` (`in_forder`, `in_frot`). Vorbelegt: 3×2 und 6×1 und 1×6 (`rludfb`, `000000`), EAC (`lfrdbu`, `000313`). Optionaler Rand je Fläche (`cbmp`-Padding) in Pixeln.
- **EAC:** Cubemap mit Winkelkorrektur `s' = (4/π)·atan(s)` je Flächenachse (`s ∈ [−1, 1]`; 45° liegen am Flächenrand, 22,5° auf halber Strecke), Layout nach YouTube (siehe oben). Gegen ffmpeg `v360` (`eac`) geprüft, gegen echtes YouTube-Material noch offen.
- **Fisheye (äquidistant):** `θ = atan2(|dir.xy|, −dir.z)` (Winkel zur Blickachse vorne), Abstand vom Kreismittelpunkt `r = θ / (fov/2)` in Kreisradien, Richtung im Kreis aus `dir.xy` (rechts = +x, oben = +y). `u = centerX + r·(dir.x/|dir.xy|)·radius·H/W`, `v = centerY − r·(dir.y/|dir.xy|)·radius`; Mittelpunkt in Anteilen der Halbbild-Breite und -Höhe, Radius als Anteil der Halbbild-Höhe. Gültig für `θ ≤ fov/2`, sonst Schwarz.

Alle Formeln stehen in `ProjectionMath.cs` und in `projection.hlsl`. Die Unit-Tests prüfen bekannte Richtungen (vorne, links, oben, hinten) gegen erwartete UV. `ProjectionFormatShaderTests` vergleicht Shader und C# pixelweise für alle Formate, `ProjectionImageRenderTests` rendert die Testbilder aller Projektionen und vergleicht jede Ansicht mit derselben Ansicht des Equirect-360-Testbilds.

## Formatpfad (M6)
1. Beim Öffnen (`OpenMediaCommand`) liest der Player-Control-Thread das Sidecar (`SidecarFile.Load`). Die Wahl des Operators (`FormatOverrides`) gilt danach bis zum nächsten Video, auch über Stop und Neustart des Decoders.
2. Beim Öffnen des Decoders liest `MediaDemuxer.ReadFormatMetadata` die Spherical-Video-Metadaten aus den Side-Data des Videostreams (`AV_PKT_DATA_SPHERICAL`, `AV_PKT_DATA_STEREO3D`; MP4 `sv3d`/`st3d`, Matroska, Frame-Packing-SEI).
3. `VideoFormatResolver.Resolve` bestimmt jedes Teil des Formats einzeln: `--stereo` (nur Sitzung) vor Sidecar vor Metadaten vor Schätzung aus der Bildgröße. Ergebnis: verwendetes Format, das automatische Format (für „Automatisch (…)“ im Operatorfenster), die Quelle jedes Teils und deutsche Hinweise.
4. Das Format geht als `VideoFormat` an die `VideoFrameQueue`; jedes `VideoFrame` trägt es zum Render-Thread. Eine Änderung (`SetVideoFormatCommand`) setzt `VideoFrameQueue.PictureFormat` (ein Referenz-Tausch, lock-frei), der Render-Thread sieht sie mit dem nächsten Frame, ohne Neuöffnen.
5. Der Player speichert jede Änderung sofort im Sidecar und meldet das Format mit Quellen, Hinweisen und Sidecar-Zustand als `VideoFormatStatus` im `PlayerStatus`.

Standbilder (`--image`) bleiben ein Testwerkzeug für Equirect 360 ohne Sidecar.

## Audio
Stand M10: Stereo direkt oder „fest am Bild“ (zwei virtuelle Lautsprecher, binaural mit Steam Audio, M7) und Ambisonics Ordnung 1–3 binaural (M10). Surround folgt später.

### Datenfluss Ton
1. `MediaDemuxer` wählt die Tonspur (`av_find_best_stream`, passend zum Videostream) und füllt die Audio-`PacketQueue`. Das Kanal-Layout jeder Tonspur liest er **vor** `avformat_find_stream_info` (`EmbeddedAudioLayout`): Danach meldet FFmpeg für AAC mit `SA3D` nur noch „4.0“ statt „ambisonic 1“.
2. `AudioDecoder` dekodiert; `AudioModeResolver` (Core) bestimmt den Audio-Modus (siehe „Ambisonics“), `AudioConversionPlan` legt die Umrechnung fest: Stereo durchreichen, Mono auf beide Seiten, Mehrkanal auf Stereo mischen, Ambisonics ungemischt (alle Kanäle, `AV_CHANNEL_ORDER_UNSPEC` auf beiden Seiten von swresample), Resampling auf die Samplerate des Ausgabegeräts. Ergebnis: float32 interleaved mit Medienposition, 2 Kanäle bzw. 4/9/16 bei Ambisonics.
3. `AudioRingBuffer` (Core, SPSC, 2¹⁷ Frames ≈ 2,7 s bei 48 kHz) ist medienadressiert: Der Leser holt „Daten ab Medienzeit t“, zu späte Daten werden übersprungen, Lücken werden Stille. Flush (Seek, Stop) als Handshake: Die alten Daten bleiben lesbar, bis die Ausgabe ausgeblendet und bestätigt hat. Jeder Platz fasst `AmbisonicsLayout.MaxChannels` = 16 Kanäle (8 MB), jede Generation (`Start`) nennt ihre Kanalzahl (`FrameChannels`); der Renderer liest sie mit jedem Block.
4. `AudioOutput` (Orbivo.Audio) besitzt den Audio-Out-Thread und das Gerät. Der Player spricht nur über `IAudioOutput` (Core) mit ihr: Befehle (Attach, Play, Pause, Hold, Lautstärke, Stumm, Gerät) über eine lock-freie Queue, Uhr und Status über Snapshots.
5. `AudioRenderer` (Zustände Still / Läuft / Blendet zur Pause aus / Blendet zum Halt aus) holt pro Periode die Frames zur gehörten Medienzeit, wendet Transport-Rampe (5 ms Smoothstep) und Verstärkung an und zählt Aussetzer. `SampleWriter` schreibt in das Geräteformat (float32, int16/24/32; Stereo auf die ersten zwei Kanäle, ein Mono-Gerät bekommt den Mittelwert).
6. Verstärkung = `level³(Lautstärke)` × (stumm ? 0 : 1) × `level³(Fade/Schwarzbild)` zu dem Zeitpunkt, an dem der erste Frame der Periode gehört wird, geglättet mit 5 ms (`GainSmoother`).

### Geräte
- `IAudioEndpoint` kapselt ein Gerät: `WasapiAudioEndpoint` (Shared Mode, ereignisgesteuert, 20 ms Puffer, Position über `IAudioClock` mit QPC-Zeitstempel), `NullAudioEndpoint` (stiller Ersatz mit Systemtakt) und `SimulatedAudioEndpoint` (für Tests: Drift, Latenz, Aufnahme, Erkennung von Tönen).
- `WasapiEndpointProvider` listet Geräte und meldet Änderungen (`IMMNotificationClient`). „Windows-Standard“ folgt dem Standardgerät; ein fest gewähltes Gerät wird nie automatisch gewechselt.
- Fehlt das Gerät oder fällt es aus (Fehler oder 500 ms ohne Periode), läuft `NullAudioEndpoint` weiter; neuer Versuch alle 2 s und bei jeder Geräteänderung.
- `settings.json` (`%LOCALAPPDATA%\Orbivo`) speichert Gerät, Lautstärke und die Ton-Sperre, nicht Stumm (`AppSettings`, Core). Dazu Leerlauf-Ansicht und Aufzeichnung (M8) sowie Playlist, „Automatisch weiter“, „Wiederholen“ und Fade-Dauer (M9, siehe „Auslieferung“) und die Sprache (M11, siehe „Sprachen“).

### Ton fest am Bild (M7)
- **Wahl:** `StereoPlacement` (Core) = fest am Bild ja/nein und Breite (30–90°, Standard an, 75°), aus dem Sidecar (`audioFixedToPicture`, `stereoWidthDeg`). Der Player schickt sie beim Öffnen, nach jedem Neuöffnen des Ton-Decoders und bei jeder Änderung im Operatorfenster über `IAudioOutput.SetStereoPlacement` (lock-freie Befehlsqueue). Mono-Spuren bekommen immer „direkt“.
- **Kopfrichtung:** `ListenerOrientation` (siehe Threads) = Kopf im Bildsystem, `OrientationOffset.ApplyToView(Kopf)`, dieselbe Rechnung wie für das Bild. Recenter (M8) muss dort mit eingehen.
- **Lautsprecher:** `AudioCoordinates.StereoSpeakers(Breite, Kopf)` liefert die Richtungen der beiden Lautsprecher (Yaw ±Breite auf dem Horizont des Bilds) relativ zum Kopf: `normalize(conj(Kopf)·Richtung)`. Steam Audio nutzt dieselben Achsen wie OpenXR (+X rechts, +Y oben, −Z vorne), es gibt keine weitere Umrechnung.
- **Verarbeitung:** `SpatialStage` (Orbivo.Audio, gehört dem Audio-Thread) arbeitet in Blöcken zu 256 Frames: Kopfrichtung lesen, `SteamStereoSpatializer` (je Kanal ein `iplBinauralEffect` mit eingebauter HRTF, bilinear, Ohrsignale addiert × 0,6). Die Stufe verzögert um genau einen Block; `AudioRenderer` liest den Ringpuffer um diesen Block voraus, damit das Gehörte zur Medienposition passt (Uhr und A/V unverändert). Nach Start, Seek, Flush und Umschalten füllt der Renderer die Verzögerungsleitung neu. Umschalten während der Wiedergabe läuft über die 5-ms-Transportrampe.
- **Erzeugen:** Kontext, HRTF und Effekte legt der Audio-Thread beim Öffnen des Geräts an (zwischen den Perioden), passend zur Samplerate. Scheitert das (z. B. `phonon.dll` fehlt), bleibt es bei Stereo direkt, der Status meldet den Grund.
- **Status:** `AudioOutputStatus.Spatial` (verfügbar, aktiv, Wahl, Spitzenlast je Block, Problem); das Operatorfenster zeigt den Modus in der Gruppe „Ton“ und die Last in „Tonausgabe“.

### Ambisonics (M10)
- **Erkennung:** `AudioModeResolver.Resolve(Layout, Wahl)` (Core): Sidecar `audioMode` vor Metadaten vor Kanalzahl. Metadaten = FFmpeg meldet `AV_CHANNEL_ORDER_AMBISONIC` (aus `SA3D` in MP4, Ordnung über `av_channel_layout_ambisonic_order`). Ohne Metadaten gelten 4, 9 oder 16 Kanäle ohne benannte Lautsprecher als AmbiX der Ordnung 1–3, alles andere als „Kanäle“. Ergebnis `ResolvedAudioMode` mit Quelle, automatischem Modus (für „Automatisch (…)“) und deutschem Hinweis (z. B. FuMa höherer Ordnung, zu wenige Kanäle).
- **Umrechnung im Decoder-Thread:** `AmbisonicsFrames.ToAmbiX` kopiert die ersten (Ordnung+1)² Kanäle in den Ring, weitere Kanäle fallen weg. FuMa 1. Ordnung (W X Y Z, W −3 dB) wird zu AmbiX: W·√2, Reihenfolge W Y Z X. Wechselt die Wahl, öffnet der Player den Ton-Decoder an derselben Stelle neu (`RestartAudioDecoder`).
- **Achsen:** AmbiX: X vorne, Y links, Z oben. Im Bildsystem (OpenXR) ist das X = −Z, Y = −X, Z = +Y (`AudioCoordinates`). Steam Audio liest ACN 1 = links, 2 = oben, 3 = vorne (gegen den eigenen Encoder geprüft, `SteamAmbisonicsSpatializerTests`), also dieselbe Reihenfolge wie AmbiX.
- **Normierung:** Steam Audio erwartet orthonormierte Kanäle (N3D/√(4π)). `SteamAmbisonicsSpatializer` skaliert jeden AmbiX-Kanal mit √((2l+1)/4π) (l = Grad des Kanals) und gleicht die Lautheit je Ordnung aus (×2,5 / ×2,1 / ×1,8, gemessen mit Rauschen gegen Stereo direkt).
- **Verarbeitung:** `AmbisonicsStage` (wie `SpatialStage` ein `BlockStage`, 256 Frames, ein Block Verzögerung, Vorauslesen im Renderer) gibt jeden Block an `iplAmbisonicsDecodeEffect` (Ordnung 3 angelegt, eingebaute HRTF, Stereo-Lautsprecher-Layout, binaural). Die Kopfrichtung geht als Hörer-Achsen (rechts, oben, vorne) im Bildsystem hinein (`AudioCoordinates.ListenerAxes`). „Ton fest am Bild“ aus = Hörer schaut immer nach vorne, das Feld dreht mit dem Kopf.
- **Notbehelf:** Fehlt Steam Audio oder scheitert das Anlegen, spielt der Renderer zwei Nieren nach links und rechts, 0,5·(W ± Y), kopffest. Der Status nennt den Grund (`SpatialAudioStatus.AmbisonicsProblem`).
- **Status:** `AudioConversionPlan.Mode` im Ton-Status, `VideoFormatStatus.AudioMode` für die Auswahl im Operatorfenster, `SpatialAudioStatus.FieldBinaural` für „binaural“.

### Später (geplant)
- **Surround:** virtuelle Lautsprecher wie bei Stereo (Steam Audio `iplBinauralEffect` pro Kanal).
- **Externe Audiodatei** (falls die Aufnahmen getrennt geliefert werden): Sidecar-Feld `externalAudio` mit `path` und `offsetMs`. Eigener Demux/Decoder für Audio, gleiche Clock.

## Recenter, Leerlauf-Ansicht und Aufzeichnung (M8)
- **Recenter (F30/F31):** `RecenterControl` (Core) nimmt Anforderungen von jedem Thread an (`RequestRecenter`, `RequestReset`). Ausgeführt werden sie im Render-Thread mit der Kopfpose des nächsten Bilds (`Update`), daraus entsteht eine reine Yaw-Drehung `C = Ry(−Yaw)`. Sie wirkt als `C·Ansicht` vor Ausrichtungs-Offset und Projektion auf beide Augen, die Live-Ansicht und `ListenerOrientation`; Bild und Ton bleiben damit deckungsgleich. In der Vorschau dreht sie die Mausansicht. Der Wert gilt über Videowechsel und Wiederverbindung, er wird nicht gespeichert.
- **Leerlauf-Ansicht (F7):** Liefert der Player kein Frame (kein Video, Bereit nach Stop), zeigt der Render-Thread `PanoramaSource.ForIdle(…)`: Schwarz, Standbild (mit eigenem Stereo-Layout, `StillImageTexture.PictureFormat`) oder See-through. See-through: Blend-Mode `ALPHA_BLEND` und `BlendTextureSourceAlphaBit` im Layer, die Augen werden mit `(0, 0, 0, 1 − Helligkeit)` gelöscht (vormultipliziertes Alpha), Schwarzbild und Fade decken das Kamerabild also ab. Bietet die Runtime `ALPHA_BLEND` nicht an (`xrEnumerateEnvironmentBlendModes`), bleibt es schwarz und der Status sagt es. Die Vorschau zeigt statt des Kamerabilds eine dunkle Fläche (`PanoramaRenderer.SeeThroughStandIn`).
- **Aufzeichnung (F50–F52):** `SessionRecorder` (Core) besitzt einen eigenen Thread. Der Render-Thread gibt jedes Bild eine `HeadSample` an `HeadSampleGate` (lock-freie SPSC-Queue, 8192 Plätze, dünnt auf die gewählte Rate aus, allokiert nicht; voll = Zeile verloren und gezählt). Player-Control, App und `XrHost` (Verbindung, Sitzung, Fehler) melden Ereignisse an `ISessionEventLog` (`SessionEventQueue`, höchstens 10 000 wartend). Der Recorder schreibt alle 50 ms und flusht jede Sekunde. Zeiten: ein Anker (UTC und Stopwatch) je Dateipaar, jede Zeile = Anker + Stopwatch-Zeit, damit monoton; geschrieben wird sie als Ortszeit des PCs. In der Brille ist der Zeitpunkt einer Kopfzeile die vorhergesagte Anzeigezeit des Bilds.
- **Nur bei laufendem Video:** Der Recorder ist eingeschaltet, öffnet aber keine Dateien. Der Player meldet Zustandswechsel typisiert (`ISessionEventLog.RecordState`, `SessionEvent.PlaybackState`). Wechselt der Zustand auf „Läuft“, beginnt ein Dateipaar, verankert am Play-Befehl davor (Ereignisse und Kopfproben der letzten 5 s werden dafür vorgehalten). Pause bleibt im Dateipaar; jeder andere Zustand (Öffnet, Bereit, Ende, Kein Video, Fehler) beendet es, Kopfproben nach diesem Ereignis gehören nicht mehr dazu. Läuft beim Einschalten schon ein Video (oder steht es auf Pause), beginnt das Dateipaar sofort (`RecordingOptions.PlaybackAtStart`).
- **Dateien:** je Aufzeichnung `JJJJ-MM-TT_hhmmss[_Proband]_kopf.csv` und `…_ereignisse.csv` (mit „Nur bei laufendem Video“ je Video `…[_Proband]_Videoname_kopf.csv`) im gewählten Ordner (Standard `Dokumente\Orbivo\Aufzeichnungen`), Semikolon, Dezimalkomma, UTF-8 mit BOM. Kopf-CSV: `Uhrzeit;Sekunden;Video;Videozeit_s;Status;Inhalt;Helligkeit;Tracking;Kopf_qw…qz;Kopf_Yaw/Pitch/Roll_Grad;Blick_Video_Yaw/Pitch_Grad;Recenter_Yaw_Grad`. Ereignis-CSV: `Uhrzeit;Sekunden;Ereignis;Video;Videozeit_s;Details`. Kann der Recorder nicht schreiben, beendet er die Aufzeichnung und meldet den Grund im Operatorfenster.

## Sprachen (M11)
- **Texte:** Englisch ist die neutrale Sprache, Deutsch der Satellit `de\*.resources.dll`. `Orbivo.Core/Localization/CoreText.resx` (+ `.de.resx`) hält die Texte der unteren Schichten (Format- und Sidecar-Hinweise, Fehler von Player und Decodern, Tonausgabe, native Bibliotheken), `Orbivo.App/Localization/UiText.resx` (+ `.de.resx`) die des Fensters. Die Klassen `CoreText` und `UiText` erzeugt der Build (`StronglyTyped…` im csproj). Für `UiText` erzeugt `Orbivo.App.csproj` sie vor dem XAML-Schritt, weil WPF eine temporäre Assembly (`*_wpftmp.csproj`) übersetzt, bevor die Ressourcen sonst entstehen. Im XAML: `{x:Static loc:UiText.Schlüssel}`.
- **Sprache wählen:** `AppSettings.Language` (`"en"`/`"de"`). Fehlt `settings.json`, gilt Englisch; fehlt nur das Feld (Datei von vor M11), gilt Deutsch. `--lang` übersteuert für eine Sitzung. `App.OnStartup` lädt die Einstellungen zuerst und setzt mit `Loc.Apply` Culture und UI-Culture für alle Threads, bevor Fenster, Prüfungen der nativen Bibliotheken oder Threads entstehen, dazu `FrameworkElement.Language` (Zahlen in Bindings).
- **Umschalten:** Menü Optionen → Sprache (`ChooseLanguageCommand`); `MainViewModel.SelectedLanguage` speichert die Wahl und meldet `LanguageChangeRequested`; `App` fragt in beiden Sprachen nach einem Neustart, startet `Orbivo.exe` mit denselben Argumenten (ohne `--lang`) plus `--wait-for-pid <eigene Id>` und beendet sich. Die neue Instanz wartet höchstens 30 s, bis die alte Brille, Tongerät und `settings.json` freigegeben hat. Kein Umschalten zur Laufzeit: Statuszeilen, Auswahllisten und Fehlertexte entstehen an vielen Stellen schon beim Start.
- **Aufzeichnung bleibt deutsch:** Ereignistexte (`SessionEvents`, `XrEventText`, Befehle im `VideoPlayer`, `App.DescribeIdleView`) sind fest deutsch, Zahlen mit `Loc.RecordingCulture`. Texte, die der Operator sieht **und** das Ereignis-Log bekommt (Fehler beim Öffnen, Dekodieren, Software-Grund), sind `OperatorText` (Ressourcen-Name plus Argumente): `ToString()` in der UI-Sprache, `ForRecording()` deutsch, `ForLog()` englisch.
- **Tests:** `tests/xunit.runner.json` legt die Culture aller Testläufe auf `de-DE` fest, unabhängig von der Windows-Sprache. `LocalizationTests` prüft jedes `.resx`-Paar (gleiche Schlüssel, gleiche Platzhalter, nicht leer) und `OperatorText` in allen drei Sprachen.

## Auslieferung (M9)
- **Paket:** `release.ps1` veröffentlicht `Orbivo.App` self-contained für win-x64 (`dotnet publish -c Release --self-contained`, `-p:OrbivoRelease=true`) und legt `licenses\` und `build/THIRD-PARTY-NOTICES.txt` dazu. Die nativen DLLs kommen über `build/Native.targets` (`CopyToPublishDirectory`), die Anleitung über `Orbivo.App.csproj`. Ablauf, Prüfungen und Paketinhalt: `docs/RELEASE.md`.
- **Version:** `<Version>` in `Directory.Build.props`. Ohne `OrbivoRelease=true` wird die `InformationalVersion` zu `X.Y.Z-dev`; das SDK hängt den Commit an (`+<sha>`). Die App zeigt die Version ohne Commit in der Titelleiste, Log und Aufzeichnung bekommen sie ganz.
- **`--check`:** Die App prüft wie bei jedem Start FFmpeg, Steam Audio und den OpenXR-Loader, dazu beide Anleitungen, schreibt das Ergebnis ins Log und beendet sich ohne Fenster (Exit-Code 0 oder 1). Das Release-Skript prüft damit das fertige Paket.
- **Anleitung:** `docs/Anleitung.html` und die englische Fassung `docs/Manual.html` (je eine Datei ohne externe Abhängigkeiten, gegenseitig verlinkt) werden bei jedem Build neben die exe kopiert; F1 öffnet die der eingestellten Sprache. Hilfe → Anleitung bzw. F1 öffnet sie über die Shell im Standardbrowser (`App.OpenManual`); fehlt sie, sagt ein Hinweisfenster wo sie liegen müsste.
- **Playlist beim Start:** `MainViewModel` legt die gespeicherten Pfade wieder an (fehlende weglassen, im Status „Playlist“ nennen) und wählt das zuletzt geladene Video aus, ohne es zu laden. Jede Änderung an Playlist, „Automatisch weiter“, „Wiederholen“ und Fade-Dauer speichert die Einstellungen. Mit `--video` bleibt die gespeicherte Playlist unberührt.

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
- Wiederverbindung (N3): Device, Standbild, Live-Ansicht, Recenter und Leerlauf-Ansicht gehören dem `XrHost`, nicht der Session; sie gelten nach dem Neuverbinden weiter. Video und Ton laufen weiter, während die Brille fehlt. War schon einmal eine Brille verbunden, meldet der Status „Brille getrennt seit …“ bzw. „Varjo Base läuft nicht mehr“. Verbindungs- und Sitzungswechsel und `XrEventDataReferenceSpaceChangePending` (z. B. Recenter in Varjo Base) stehen im Log und im Ereignis-Log.
- Pro Frame: `xrWaitFrame` → `xrBeginFrame` → `xrLocateViews` → je Auge Bild holen, rendern, freigeben → `xrEndFrame` mit einem Projection-Layer. Gerendert wird nur mit der Orientierung, im Layer steht die gemessene Pose.

## `--no-xr` Debug-Modus
- Statt OpenXR wird ein Auge in ein eigenes Vorschaufenster gerendert (Win32, eigener Render-Thread, Flip-Model-Swapchain mit Frame-Latency-Handle). Das Fenster hat keinen Schließen-Knopf und schließt mit dem Operatorfenster.
- Maus: Ziehen greift das Bild (Ziehen nach rechts = Blick nach links), Mausrad = vertikales Sichtfeld 30–120° (Start 75°), Pos1 = zurücksetzen.
- Standbild per `--image <Pfad>` (M1: Equirect 360 Mono). Dekodiert wird mit WIC über WPF in `Orbivo.App`, die Textur entsteht auf einem Worker-Thread und wird dem Render-Thread übergeben.
- Gleiche Shader und Pipeline (`PanoramaRenderer`), nur andere View/Projection-Quelle: im Vorschaufenster `MouseViewSource` (`IViewSource`), in der Brille liefert `xrLocateViews` die Ansichten direkt im Frame-Loop (sie hängen an der vorhergesagten Anzeigezeit).
- Video per `--video <Pfad>` (M3), in Vorschau und Brille. Das Format kommt aus Metadaten und Sidecar (Formatpfad, M6); `--stereo mono|tb|sbs` legt das Stereo-Layout für die Sitzung fest. Die Vorschau zeigt das linke Auge.
- Damit lassen sich Decoding, Formate, Sync und UI ohne Brille entwickeln.

## Sidecar-Format (`<Dateiname mit Endung>.orbivo.json`, z. B. `clip.mp4.orbivo.json`)
Vorzeichen von `orientationOffsetDeg`: wie überall Rechte-Hand-Regel, Yaw positiv = Drehung nach links. Das Bild dreht sich: Yaw +30° legt die bisherige Bildmitte 30° nach links, Pitch + hebt sie an, Roll + kippt das Bild gegen den Uhrzeigersinn (vom Betrachter aus).
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
- **`null` oder fehlend = automatisch** (Metadaten, sonst Schätzung). Geschrieben werden immer alle Formatfelder.
- `projection`: `Equirect360`, `Equirect180`, `Cubemap`, `Eac`, `Fisheye`. `stereoLayout`: `Mono`, `TopBottom`, `SideBySide`. Groß-/Kleinschreibung egal.
- `cubemapLayout`: Name einer Vorgabe (`3x2`, `6x1`, `1x6`, `eac`) oder ein Objekt `{ "columns": 3, "rows": 2, "faceOrder": "rludfb", "faceRotation": "000000", "padding": 0 }`.
- `fisheye`: `{ "fovDeg": 180, "centerX": 0.5, "centerY": 0.5, "radius": 0.5 }` (fehlende Felder: diese Standardwerte).
- `orientationOffsetDeg`: je Achse höchstens ±360°; `null` = keine Drehung.
- `audioFixedToPicture` (M7): `true`/`false`, `null` = an (Ton fest am Bild). `stereoWidthDeg`: 30–90, `null` = 75.
- `audioMode` (M10): `Channels` (Mono, Stereo, Mehrkanal auf Stereo), `AmbiX` (Ordnung aus der Kanalzahl), `FuMa` (nur 1. Ordnung); `null` = automatisch (Metadaten, sonst Kanalzahl).
- Das geplante Feld `externalAudio` und alle unbekannten Felder bleiben beim Speichern erhalten.
- Speichern: bei jeder Änderung im Operatorfenster, über eine `.tmp`-Datei. Ist nichts mehr eingestellt und gibt es keine fremden Felder, wird die Datei gelöscht.
- Ungültige Felder werden mit Hinweis ignoriert. Eine Datei, die kein JSON ist, wird gemeldet, ignoriert und vor dem ersten Überschreiben als `.orbivo.json.bak` gesichert. Eine Datei mit `version` > 1 wird gelesen, aber nie überschrieben.
