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

## M6.5 – Live-Ansicht (F42)
- Der XR-Render-Thread rendert jedes 3. Bild zusätzlich eine kleine Mitten-Ansicht (etwa 720 px hoch) aus der Kopfdrehung beider Augen, mit Fade und Schwarzbild. Er wartet nie: Ist die Anzeige nicht bereit, wird das Bild übersprungen
- Anzeige im Operatorfenster (Platz links unter „Format“), Schalter „Live-Ansicht“, bei jedem Start aus
- Ohne Session: Hinweis „Kein Bild“. Bei `--no-xr` zeigt sie das Vorschaubild (so lässt sie sich ohne Brille prüfen)
- **Fertig, wenn:** Mit Live-Ansicht an bleibt die Brille bei 90 fps (8K-TB-Video), die Render-Schleife allokiert nicht, abgeschaltet kostet sie nichts
- **Brillen-Test:** Kopf drehen, die Live-Ansicht folgt ohne spürbare Verzögerung in die gleiche Richtung; Fade und Schwarzbild sind sichtbar

## M7 – Räumlicher Ton: Stereo fest am Bild
Neu zugeschnitten am 2026-11-08 (`DECISIONS.md`): Der Ton der Videos ist Stereo, er soll zum Bild passen. Ambisonics und Surround kommen später.
- Steam-Audio-Interop (Binaural-Effekt mit eingebauter HRTF), `AudioCoordinates` mit Unit-Tests
- Kopfrichtung im Bildsystem vom Render-Thread (Brille) bzw. von der Maus (Vorschau) als lock-freier Snapshot, Verarbeitung im Audio-Thread in 256er-Blöcken ohne Versatz zur Uhr
- Zwei virtuelle Lautsprecher bei ±Breite (Standard 75°); Schalter „Ton fest am Bild“ und Breite pro Video im Operatorfenster und Sidecar, Standard an. Mono bleibt kopffest, Mehrkanal wird auf Stereo gemischt
- Testvideo `stereo_richtungstest_…` (`make_testmedia.py --what stereo`)
- **Fertig, wenn:** Richtung (Pegelunterschied der Ohren) mit echter `phonon.dll` gemessen, keine Knackser beim Drehen, A/V unverändert, Audio-Thread allokiert nicht, Last unter 1 % ✔
- **Hör-Test:** Stereo-Richtungstest: Der Ton kommt aus dem leuchtenden Kasten, auch beim Kopfdrehen. Mitte vorne, nicht im Kopf. Schalter aus: links bleibt links. Keine Knackser, kein spürbares Nachziehen.

## M8 – Recenter, Logging, Robustheit
- Recenter (F30/F31) für Bild und Ton
- Leerlauf-Ansicht (F7, `DECISIONS.md` 2026-11-07): Schwarz, See-through (`ALPHA_BLEND`) oder Standbild, wenn kein Video läuft; im UI wählbar und gespeichert. Regel 7 in `CLAUDE.md` und F6 anpassen
- SessionLogger (CSV, F50) und EventLog (F51), im UI steuerbar
- Brille trennen und neu verbinden, Varjo Base neu starten: App erholt sich (N3)
- **Fertig, wenn:** Ein 30-minütiger Durchlauf mit mehreren Videos läuft ohne Fehler, die Logs sind vollständig
- **Brillen-Test (Leerlauf):** See-through zeigt das Kamerabild vor Play und nach Stop, „Bild“ zeigt das gewählte Standbild richtig orientiert, Play wechselt sauber ins Video, Schwarzbild (B) bleibt schwarz

## M9 – Feinschliff und Auslieferung
- Self-contained Publish (`dotnet publish -r win-x64 --self-contained`), native DLLs im Paket
- Einstellungen persistieren (Ausgabegerät, Logging-Ordner, letzte Playlist)
- Kurze Bedienungsanleitung für Operatoren (`docs/OPERATOR.md`)

## Später – Ambisonics und Surround
- Ambisonics Ordnung 1–3 (AmbiX, FuMa → AmbiX → N3D), kopfgetrackt; 5.1/7.1 über virtuelle Lautsprecher; Erkennung des Audio-Modus (F21, F23)
- Testdateien liegen bereit (`make_testmedia.py --what audio`)
