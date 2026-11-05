# Entscheidungen

| Datum | Entscheidung | Begründung |
|---|---|---|
| 2026-11-05 | Eigenentwicklung statt fertiger Player | Fertige Lösungen sind consumer-orientiert oder (SightLab) zu teuer und überdimensioniert; benötigt wird reine Operator-Steuerung von außen |
| 2026-11-05 | C#/.NET statt Unity oder C++ | Teamerfahrung mit C#; volle Kontrolle; keine Engine-Lizenz |
| 2026-11-05 | Räumlicher Ton im Player, Kopfhörer ohne eigene Raumsimulation | Nur der Player kennt die Kopforientierung |
| 2026-11-05 | Projektname „Orbivo“ | Eigenständige Wortneuschöpfung; „Orbis“ ist im VR-Umfeld bereits belegt. Vor externer Nutzung Markenprüfung (DPMA/EUIPO, Klasse 9) |
| 2026-11-05 | Git: privates Repository `QuenCode/Orbivo`, gearbeitet wird auf `development`, `main` nimmt stabile Stände auf. Commits mit der privaten Adresse (nur Repo-Einstellung) | Wunsch des Projektinhabers |
| 2026-11-05 | Paketversionen (Stand 05.11.2026, jeweils neueste stabile): CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.Logging 10.0.14, Silk.NET.OpenXR 2.23.0, Vortice 3.8.3, FFmpeg.AutoGen 9.0.1.1, NAudio.Wasapi 3.3.0, xunit.v3 4.0.2; SDK 10.0.401 (`latestPatch`) | Zentral in `Directory.Packages.props` gepinnt |
| 2026-11-05 | NAudio 3.3.0 statt 2.4.0 | Die Span-basierte `Read`-API passt zum allokationsfreien Audio-Callback. NAudio 3 ist jung (seit 08/2026); ein Rückfall auf 2.4.0 betrifft nur `Orbivo.Audio` |
| 2026-11-05 | Tests mit xUnit v3 4.x über Microsoft.Testing.Platform (`global.json` → `test.runner`), ohne `Microsoft.NET.Test.Sdk` | xUnit 4 unterstützt VSTest auf dem .NET-10-SDK nicht mehr im Standardpaket. Jedes Testprojekt braucht mindestens einen Test, sonst endet der Lauf mit Fehler |
| 2026-11-05 | FFmpeg 9.0.2 als LGPL-Shared-Build von BtbN (Monatsend-Build `autobuild-2026-10-31-12-59`), Lizenz **LGPL v3 oder später** | gyan.dev bietet Shared-Builds nur unter GPL an. BtbN baut mit `--enable-version3`, LGPL v3 ist mit N5 vereinbar. Monatsend-Builds bleiben zwei Jahre verfügbar. Die App lehnt Nicht-LGPL-Builds beim Start ab |
| 2026-11-05 | Steam Audio 4.8.1, ausgeliefert wird nur `phonon.dll` | `TrueAudioNext.dll`/`GPUUtilities.dll` dienen nur der Faltung auf AMD-GPUs. P/Invoke wird exakt gegen 4.8.1 geschrieben, die App prüft die Dateiversion |
| 2026-11-05 | Native DLLs per `native/fetch-native.ps1` (gepinnte URLs + SHA256), nicht im Git; Build bleibt grün, wenn sie fehlen | Reproduzierbar, Repo bleibt klein; fehlende DLLs meldet die App im Status |
| 2026-11-05 | Projektabhängigkeiten: Core ← Media, Audio; Xr → Core, Media; App → alle. Das D3D11-Device erzeugt Xr und gibt es an Media. Lock-freie Strukturen, die zwei Projekte teilen (Audio-Ringpuffer, Head-Pose-Snapshot), liegen in Core | Der Renderer liest die GPU-Frames aus Media; Core bleibt frei von nativen Abhängigkeiten |
| 2026-11-05 | Ausnahme von Regel 5: Nur der Software-Decoder-Fallback darf Frames von der CPU auf die GPU hochladen, mit Warnung im Status | F40 sieht Software-Decoding vor; ohne Upload ginge das nicht. Hardware-Decoding bleibt der Normalfall, 8K in Software ist nicht echtzeitfähig |
| 2026-11-05 | Sidecar heißt `<Dateiname mit Endung>.orbivo.json`, z. B. `clip.mp4.orbivo.json` | Keine Kollision zwischen `clip.mp4` und `clip.mkv` |
| 2026-11-05 | Solution bleibt im Format `.sln` (nicht `.slnx`, Standard ab .NET 10) | Befehle in CLAUDE.md nutzen `Orbivo.sln` |
| 2026-11-05 | NuGet-Audit-Warnungen NU1901–NU1904 werden nicht zu Fehlern | Neue Sicherheitsmeldungen sollen sichtbar sein, den Build aber nicht über Nacht brechen |
| 2026-11-05 | Eigener Datei-Logger für Microsoft.Extensions.Logging (begrenzte Queue, Schreib-Task, verwirft bei Überlauf statt zu blockieren) statt Serilog | Keine Zusatzabhängigkeit. Render-Thread und Audio-Callback loggen trotzdem nicht pro Frame/Puffer, weil das Formatieren allokiert |
| 2026-11-05 | Testmedien: „8K“ = 8192 px Breite; Top-Bottom 8192×8192; 360°-Side-by-Side mit horizontal gestauchten Augen (8192×4096) | Hardware-Decoder enden bei 8192 px; volle SBS-Breite (16384) wäre nicht dekodierbar |
| 2026-11-05 | Yaw-Vorzeichen überall: Drehung um +Y nach Rechte-Hand-Regel, **positiv = links**. Equirect: Bildmitte = vorne, links im Bild = links | Folgt direkt aus der OpenXR-Konvention; Ambisonics-Azimut ist damit identisch mit Yaw. Gilt auch für den Ausrichtungs-Offset im Sidecar (F14) |
| 2026-11-05 | Ambisonics-Kette für Steam Audio: FuMa → AmbiX (SN3D) → N3D (`iplAudioBufferConvertAmbisonics`) | Steam Audio rechnet intern in N3D (`phonon.h`). Steam Audio nutzt dasselbe Koordinatensystem wie OpenXR; die Umrechnung betrifft nur die Ambisonics-Achsen (X vorne, Y links, Z oben) |

## Offene Punkte

- **OpenXR-Loader (vor M2):** Silk.NET.OpenXR enthält keine `openxr_loader.dll`. Neue native DLL (Khronos OpenXR-SDK, Apache-2.0), Bezugsquelle und Version vor M2 abstimmen.
- **Kopf-Log mit 90 Hz (F50, M8):** Der Render-Thread darf nicht schreiben. Es braucht eine lock-freie Queue und einen eigenen Schreib-Thread; die Thread-Tabelle in ARCHITECTURE.md bekommt dann einen Logger-Thread.
- **FuMa 2. und 3. Ordnung:** selten und uneinheitlich definiert. Nur umsetzen, wenn echtes Material es verlangt.
- **Echtes Aufnahmeformat** (SPEC, offene Punkte): Auflösung, Codec, Stereo-Layout, Ambisonics-Ordnung. 8192×8192 HEVC liegt über HEVC-Level 6.2; früh mit echtem Material testen.
- **Dekodier-Leistung (N1):** 8K-TB-HEVC dekodiert auf der RTX 3070 (Entwicklungsrechner) per D3D11VA mit etwa 54 fps, 8K mono mit etwa 122 fps (reine Dekodierung, synthetisches Material). Auf RTX 4080 Laptop und RTX 5080 in M3 messen, besonders für 60-fps-Material.
