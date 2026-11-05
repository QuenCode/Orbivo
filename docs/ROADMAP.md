# Roadmap

Jeder Meilenstein endet mit: grünem Build, grünen Tests, Commit und der **Checkliste für den manuellen Brillen-Test**.

## M0 – Gerüst
- Solution und Projekte laut `ARCHITECTURE.md`, `Directory.Build.props`, `Directory.Packages.props`
- WPF-Fenster mit leerem Operator-Layout (Platzhalter für Playlist, Transport, Status, Format)
- `native/README.md` mit Bezugsquellen und Versionen für FFmpeg (LGPL shared) und phonon.dll
- `tools/testmedia`: Skript erzeugt
  - Equirect-Testbild/Video mit beschrifteten Richtungen (VORNE, HINTEN, LINKS, RECHTS, OBEN, UNTEN) und Gitter, in Mono, SBS und TB, in 4K und 8K
  - Ambisonics-FOA-Testdatei (AmbiX) mit Klick bzw. Sprache aus bekannten Richtungen
- **Fertig, wenn:** `dotnet build` und `dotnet test` laufen, die App startet

## M1 – Renderer und Debug-Viewport (ohne Brille)
- D3D11-Device, Fullscreen-Triangle, Projektions-Shader (zunächst nur Equirect 360 Mono)
- `ProjectionMath.cs` und Unit-Tests
- `--no-xr`: Testbild (PNG) im Desktop-Fenster mit Maus-Look
- **Fertig, wenn:** Mit Blick nach vorne steht „VORNE“, mit Blick nach oben „OBEN“, und es gibt keine Spiegelung

## M2 – OpenXR mit Standbild
- Silk.NET.OpenXR-Session mit Varjo-Runtime, Swapchains, Frame-Loop, Session-Lifecycle
- Gleiches Testbild in der Brille, nur Rotation
- Status im UI: Runtime-Name, Session-State, fps
- **Brillen-Test:** Bild steht stabil im Raum, „VORNE“ vorne, oben/unten korrekt, keine Doppelbilder, 90 fps

## M3 – Video-Decoding
- FFmpeg-Demux und D3D11VA-Decode auf dem geteilten Device, NV12/P010 → Shader-YUV-Konvertierung
- Frame-Queue, Wiedergabe mit Systemuhr (noch ohne Audio)
- Play/Pause/Stop über Befehlskanal aus dem UI
- **Fertig, wenn:** 8K-HEVC-Testvideo läuft flüssig im Debug-Viewport, im Log steht „hardware decode“
- **Brillen-Test:** 8K-TB-Video ruckelfrei bei 90 fps auf beiden Rechnertypen

## M4 – Operator-UI
- Playlist (Datei-Dialog, Drag & Drop, Reihenfolge), Timeline, Seek, Zeiteingabe, Auto-Advance, Loop
- Tastaturkürzel, Statusanzeige (F40, F41)
- Blackout und Fade (F5), Startzustand Schwarz (F6)
- **Fertig, wenn:** Alle Transportfunktionen funktionieren im `--no-xr`-Modus, Seek ist präzise (±1 Frame)

## M5 – Audio und Synchronisation
- Audio-Decode, Ringpuffer, NAudio-WASAPI, Gerätewahl (F24), Lautstärke
- Audio-Clock als Master, Frame-Auswahl nach `targetPts`, A/V-Abweichung in Status und Log
- Zunächst nur Stereo durchreichen
- **Fertig, wenn:** A/V-Abweichung bleibt über 10 Minuten bei ±20 ms. Seek und Pause sind sauber, ohne Knacken.

## M6 – Alle Formate
- Equirect 180, Cubemap (Layouts), EAC, Fisheye; SBS/TB/Mono, Augen tauschen
- Metadaten-Erkennung (`sv3d`/`st3d`), UI-Übersteuerung, Sidecar speichern und laden, Orientierungs-Offset
- Unit-Tests für alle Projektionen
- **Brillen-Test:** Jedes Testvideo korrekt orientiert, Stereo-Tiefe korrekt (nicht vertauscht)

## M7 – Räumlicher Ton
- Steam-Audio-Interop, AmbisonicsProcessor (Ordnung 1–3), FuMa→AmbiX, Surround über virtuelle Lautsprecher, Binaural-Passthrough
- Head-Pose-Snapshot vom Render-Thread, Rotation im Audio-Callback
- `AudioCoordinates` mit Unit-Tests
- **Hör-Test:** Ambisonics-Testdatei: Quelle „vorne“ bleibt beim Kopfdrehen räumlich stehen. Links und rechts, oben und unten sind korrekt. Keine hörbare Verzögerung.

## M8 – Recenter, Logging, Robustheit
- Recenter (F30/F31) für Bild und Ton
- SessionLogger (CSV, F50) und EventLog (F51), im UI steuerbar
- Brille trennen und neu verbinden, Varjo Base neu starten: App erholt sich (N3)
- **Fertig, wenn:** Ein 30-minütiger Durchlauf mit mehreren Videos läuft ohne Fehler, die Logs sind vollständig

## M9 – Feinschliff und Auslieferung
- Self-contained Publish (`dotnet publish -r win-x64 --self-contained`), native DLLs im Paket
- Einstellungen persistieren (Ausgabegerät, Logging-Ordner, letzte Playlist)
- Optional: Mirror-Vorschau im UI (F42)
- Kurze Bedienungsanleitung für Operatoren (`docs/OPERATOR.md`)
