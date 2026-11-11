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
- F6 Startzustand der Brille: Leerlauf-Ansicht (F7), Standard Schwarz. Kein Video, bis der Operator startet.
- F7 **Leerlauf-Ansicht:** Was die Brille zeigt, wenn kein Video läuft (vor Play, nach Stop, ohne Video): Schwarz, See-through (Kamerabild der XR-3) oder ein vom Operator gewähltes Standbild (Equirect 360). Im UI wählbar und gespeichert. Schwarzbild und Fade (F5) wirken auch auf die Leerlauf-Ansicht, Schwarzbild bleibt immer schwarz

### Formate
- F10 Projektionen: Equirectangular 360°, Equirectangular 180° (VR180), Cubemap (Layout konfigurierbar), Equi-Angular Cubemap (EAC), Fisheye (Sichtfeld, Mittelpunkt und Radius konfigurierbar)
- F11 Stereo-Layouts: Mono, Side-by-Side, Top-Bottom, jeweils mit Option „Augen tauschen“
- F12 Automatische Erkennung über Spherical-Video-Metadaten (`sv3d`/`st3d`), falls vorhanden
- F13 Manuelle Übersteuerung im UI, die als **Sidecar-Datei** neben dem Video gespeichert wird (`<Dateiname mit Endung>.orbivo.json`, z. B. `clip.mp4.orbivo.json`) und beim nächsten Laden Vorrang vor den Metadaten hat
- F14 Ausrichtungs-Offset (Yaw/Pitch/Roll) pro Video, ebenfalls im Sidecar
- F15 Video: mindestens 8K, H.264/HEVC/AV1, 8 und 10 Bit, Farbräume BT.709 und BT.2020 (SDR)

### Ton
- F20 **Stereo „Ton fest am Bild“** (M7): Linker und rechter Kanal kommen binaural von zwei virtuellen Lautsprechern im Video (Standard 75° links und rechts der Bildmitte), **kopfgetrackt**: Dreht der Proband den Kopf, wandert der Ton mit dem Bild. Pro Video im UI und Sidecar abschaltbar, Breite einstellbar. Mono bleibt ohne Richtung, Mehrkanal wird auf Stereo gemischt
- F21 **Ambisonics** (M10): Ordnung 1 bis 3 als AmbiX (ACN/SN3D), FuMa nur 1. Ordnung; binaural und kopfgetrackt, „Ton fest am Bild“ gilt für das ganze Klangfeld. *(später)* 5.1/7.1 über virtuelle Lautsprecher
- F22 Bereits binaurales Material: Durchreichen ohne Verarbeitung (manuell wählbar: „Ton fest am Bild“ aus)
- F23 Audio-Modus wird automatisch erkannt (Ambisonics-Metadaten `SA3D`, sonst Kanalzahl: 4, 9 oder 16 Kanäle ohne Lautsprecher-Angabe gelten als AmbiX) und ist im UI („Tonformat“) und Sidecar (`audioMode`) übersteuerbar
- F24 Ausgabegerät für den Probanden im UI wählbar und wird gespeichert
- F25 Latenz von Kopfdrehung bis Tonänderung ≤ 30 ms angestrebt

### Orientierung
- F30 **Recenter:** Die aktuelle Blickrichtung des Probanden wird zur Video-Vorwärtsrichtung (nur Yaw)
- F31 Recenter beeinflusst Bild und Ton gleichermaßen

### Status und Monitoring (Operator)
- F40 Anzeige: Brille verbunden, OpenXR-Session-Zustand, Tracking ok, Render-fps, verworfene Frames, Decoder (Hardware/Software), Video-/Audio-Zeit und A/V-Abweichung
- F41 Aktuelle Blickrichtung des Probanden (Yaw/Pitch) als einfache Anzeige
- F42 Live-Ansicht: was der Proband sieht, aus seinem Blickwinkel, im Operatorfenster. An- und abschaltbar (Standard aus), reduzierte Auflösung genügt

### Logging
- F50 Pro Sitzung eine CSV-Datei: Zeitstempel (Ortszeit des PCs, monoton), Videodatei, Video-Zeit, Wiedergabestatus, Kopforientierung (Quaternion und Yaw/Pitch/Roll), mit 90 Hz oder konfigurierbar reduziert
- F51 Event-Log: Laden, Play, Pause, Seek, Blackout, Fade, Recenter, Formatwechsel, Fehler
- F52 Logging im UI ein/aus, Zielordner wählbar, wahlweise nur bei laufendem Video (ein Dateipaar je Video)

### Bedienung
- F60 Tastaturkürzel für alle Kernfunktionen (Leertaste = Play/Pause, B = Blackout, R = Recenter, Pfeiltasten = Seek ±5 s/±30 s)
- F61 Das UI bleibt bedienbar, unabhängig vom Fokus der Brille
- F62 Operator-UI und Anleitung auf Englisch und Deutsch, umschaltbar im Operatorfenster (wirkt nach einem Neustart, den die App anbietet). Beim allerersten Start Englisch. Aufzeichnungen bleiben in jeder Sprache deutsch (M11)

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
- Konkretes Video-Format der Aufnahmen: Stand 2026-11-09 ein Beispiel mit HEVC 5760 × 2880, 59,94 fps, Mono, Equirect 360. Ob alle Aufnahmen so sind, ist offen
- Audio-Format: Stand 2026-11-09 Ambisonics 1. Ordnung, AmbiX mit `SA3D`, AAC 4 Kanäle, 48 kHz, im Video eingebettet (M10)
- Falls Audio als separate Datei: Sidecar-Feld für externe Audiodatei mit Offset → siehe `ARCHITECTURE.md`
