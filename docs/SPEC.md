# Anforderungen

## Zweck
Wiedergabe von 360°/180°-Videos mit räumlichem Ton auf einer Varjo XR-3 für den Einsatz in Test- und Assessment-Situationen. Ein Operator steuert die Wiedergabe am Desktop. Die Person mit Brille sieht nur das Video und interagiert nicht.

## Rollen
- **Operator:** sitzt am Desktop-Monitor, bedient alles mit Maus und Tastatur
- **Proband:** trägt Brille und Kopfhörer, sieht keine UI

## Funktionale Anforderungen

### Wiedergabe
- F1 Video laden (Datei-Dialog und Drag & Drop) und Playlist mit mehreren Videos
- F2 Play, Pause, Stop, Seek (Timeline-Slider und direkte Zeiteingabe), Sprung zum nächsten oder vorherigen Video
- F3 Optional Loop für das einzelne Video, Auto-Advance in der Playlist ein/aus
- F4 Lautstärke (Proband) und Mute
- F5 **Schwarzbild** und **Fade** von/zu Schwarz mit einstellbarer Dauer, unabhängig vom Playback-Zustand
- F6 Startzustand der Brille: Schwarz. Kein Inhalt, bis der Operator startet.

### Formate
- F10 Projektionen: Equirectangular 360°, Equirectangular 180° (VR180), Cubemap (Layout konfigurierbar), Equi-Angular Cubemap (EAC), Fisheye (Sichtfeld, Mittelpunkt und Radius konfigurierbar)
- F11 Stereo-Layouts: Mono, Side-by-Side, Top-Bottom, jeweils mit Option „Augen tauschen“
- F12 Automatische Erkennung über Spherical-Video-Metadaten (`sv3d`/`st3d`), falls vorhanden
- F13 Manuelle Übersteuerung im UI, die als **Sidecar-Datei** neben dem Video gespeichert wird (`<video>.orbivo.json`) und beim nächsten Laden Vorrang vor den Metadaten hat
- F14 Ausrichtungs-Offset (Yaw/Pitch/Roll) pro Video, ebenfalls im Sidecar
- F15 Video: mindestens 8K, H.264/HEVC/AV1, 8 und 10 Bit, Farbräume BT.709 und BT.2020 (SDR)

### Ton
- F20 Stereo- und Mehrkanal-Ton (5.1/7.1) binaural über virtuelle Lautsprecher
- F21 Ambisonics, Ordnung 1 bis 3, AmbiX (ACN/SN3D) und FuMa, **kopfgetrackt** binaural
- F22 Bereits binaurales Material: Durchreichen ohne Verarbeitung (manuell wählbar)
- F23 Audio-Modus wird automatisch erkannt (Kanalzahl und Metadaten) und ist im UI und Sidecar übersteuerbar
- F24 Ausgabegerät für den Probanden im UI wählbar und wird gespeichert
- F25 Latenz von Kopfdrehung bis Tonänderung ≤ 30 ms angestrebt

### Orientierung
- F30 **Recenter:** Die aktuelle Blickrichtung des Probanden wird zur Video-Vorwärtsrichtung (nur Yaw)
- F31 Recenter beeinflusst Bild und Ton gleichermaßen

### Status und Monitoring (Operator)
- F40 Anzeige: Brille verbunden, OpenXR-Session-Zustand, Tracking ok, Render-fps, verworfene Frames, Decoder (Hardware/Software), Video-/Audio-Zeit und A/V-Abweichung
- F41 Aktuelle Blickrichtung des Probanden (Yaw/Pitch) als einfache Anzeige
- F42 *(später)* Mirror-Vorschau: was der Proband sieht, als kleines Fenster

### Logging
- F50 Pro Sitzung eine CSV-Datei: Zeitstempel (UTC, monoton), Videodatei, Video-Zeit, Wiedergabestatus, Kopforientierung (Quaternion und Yaw/Pitch/Roll), mit 90 Hz oder konfigurierbar reduziert
- F51 Event-Log: Laden, Play, Pause, Seek, Blackout, Fade, Recenter, Formatwechsel, Fehler
- F52 Logging im UI ein/aus, Zielordner wählbar

### Bedienung
- F60 Tastaturkürzel für alle Kernfunktionen (Leertaste = Play/Pause, B = Blackout, R = Recenter, Pfeiltasten = Seek ±5 s/±30 s)
- F61 Das UI bleibt bedienbar, unabhängig vom Fokus der Brille
- F62 Operator-UI auf Deutsch

## Nicht-funktionale Anforderungen
- N1 Konstante 90 fps in der Brille bei 8K-Stereo-Video auf RTX 4080 Laptop
- N2 A/V-Abweichung ≤ ±20 ms im Dauerbetrieb
- N3 Robust bei getrennter Brille oder beendetem Varjo Base: Fehler anzeigen, Wiederverbindung ohne Neustart der App
- N4 Self-contained Deployment, keine Installation von FFmpeg o. Ä. nötig
- N5 Nur LGPL- oder permissiv lizenzierte Abhängigkeiten (FFmpeg als LGPL-Shared-Build)

## Bewusst nicht im Umfang
- Interaktion in VR (Controller, Hand-Tracking, In-VR-Menüs)
- Streaming und Netzwerkquellen
- 6DoF und volumetrische Videos
- Eye-Tracking (eventuell später, die XR-3 hätte es)

## Offene Punkte
- Konkretes Video-Format der Aufnahmen (Codec, Auflösung, Projektion, Stereo-Layout)
- Konkretes Audio-Format (Ambisonics-Ordnung, AmbiX/FuMa, im Video eingebettet oder separate WAV)
- Falls Audio als separate Datei: Sidecar-Feld für externe Audiodatei mit Offset → siehe `ARCHITECTURE.md`
