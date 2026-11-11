# Änderungen

Was sich von Version zu Version für Operatoren und Auswertung ändert. Neue Einträge kommen unter „Unveröffentlicht“;
`release.ps1` macht daraus beim Release den Abschnitt der neuen Version und die Notizen des GitHub-Releases
(Ablauf: `docs/RELEASE.md`). Versionen nach Semantic Versioning: 1.0.**1** = Fehlerbehebung, 1.**1**.0 = neue Funktion,
**2**.0.0 = Änderung, die bestehende Abläufe oder Dateiformate bricht (z. B. andere Spalten in der Kopf-CSV).

## Unveröffentlicht

- Neue Menüleiste oben im Operatorfenster: Datei (Videos hinzufügen, Playlist leeren, Beenden), Wiedergabe (automatisch weiter, wiederholen), Optionen (Sprache, Tonausgabe und Lautstärke sperren), Hilfe (Anleitung, Über Orbivo). „Automatisch weiter“, „Wiederholen (Loop)“, die Ton-Sperre und der Link zur Anleitung sind dorthin umgezogen
- Operatorfenster und Anleitung gibt es jetzt auch auf Englisch: Optionen → „Sprache / Language“, die neue Sprache gilt nach einem Neustart, den Orbivo gleich anbietet (die Playlist bleibt). Neue Installationen starten englisch, bestehende bleiben deutsch
- Englische Anleitung `Manual.html` neben `Anleitung.html`; F1 öffnet die der eingestellten Sprache
- Aufzeichnungen bleiben in jeder Sprache unverändert deutsch (Spalten, Dateinamen, Ereignisse, Dezimalkomma)
- Startoption `--lang en|de` für eine einzelne Sitzung
- Orbivo ist Open Source unter der Apache License 2.0: `LICENSE.txt` und `NOTICE.txt` liegen im Programmordner, „Über Orbivo“ nennt Copyright und Lizenz
- Eigener, schlanker FFmpeg-Build (FFmpeg 9.0.2 mit dav1d und zlib, LGPL-2.1-or-later) statt des BtbN-Builds: deutlich kleiner, ohne rund 85 eingebaute Fremdbibliotheken. Der vollständige Quellcode liegt jedem GitHub-Release bei, Lizenztexte und Build-Angaben im Ordner `licenses`

## 1.1.0 – 2026-11-09

- Ambisonics-Ton (1. bis 3. Ordnung, AmbiX und FuMa 1. Ordnung): Das Klangfeld wird binaural berechnet und bleibt beim Kopfdrehen am Bild, Geräusche kommen auch von oben und unten aus ihrer Richtung
- Ambisonics wird an den Metadaten des Videos erkannt; ohne Metadaten gelten 4, 9 oder 16 Kanäle als Ambisonics AmbiX
- Neue Auswahl „Tonformat“ im Operatorfenster (Automatisch, Kanäle, AmbiX, FuMa), pro Video im Sidecar gespeichert (Feld `audioMode`)
- „Ton fest am Bild“ gilt auch für Ambisonics; die Breite nur noch für Stereo
- Aufzeichnung: Ein Wechsel des Tonformats steht als Ereignis „Ton“ im Ereignis-Log (Details „Tonformat: …“)

## 1.0.0 – 2026-11-08

Erste ausgelieferte Version.

- Wiedergabe von 360°- und 180°-Videos bis 8K in der Varjo XR-3, Mono und Stereo (Top-Bottom, Side-by-Side), Projektionen Equirect 360°/180°, Cubemap, EAC und Fisheye; Format aus den Metadaten oder pro Video einstellbar (Sidecar `*.orbivo.json`)
- Operatorfenster mit Playlist, Zeitleiste, Tastenkürzeln, Schwarzbild, Fade, Recenter, Live-Ansicht und Status
- Ton fest am Bild: Stereo kommt binaural von zwei virtuellen Lautsprechern im Video und bleibt beim Kopfdrehen am Bild
- Leerlauf-Ansicht der Brille: Schwarz, Kamerabild (See-through) oder Standbild
- Aufzeichnung von Kopfbewegung und Bedienereignissen als CSV (Semikolon, Dezimalkomma), wahlweise ein Dateipaar je Video
- Playlist, „Automatisch weiter“, „Wiederholen“ und Fade-Dauer bleiben über einen Neustart erhalten
- Anleitung als `Anleitung.html` im Programmordner, aus der App mit „Anleitung (F1)“ zu öffnen, mit Erklärung der Aufzeichnungsdaten
- Ausgeliefert als ZIP ohne Installation (.NET, FFmpeg, Steam Audio und OpenXR-Loader enthalten)
