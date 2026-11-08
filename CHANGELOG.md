# Änderungen

Was sich von Version zu Version für Operatoren und Auswertung ändert. Neue Einträge kommen unter „Unveröffentlicht“;
`release.ps1` macht daraus beim Release den Abschnitt der neuen Version und die Notizen des GitHub-Releases
(Ablauf: `docs/RELEASE.md`). Versionen nach Semantic Versioning: 1.0.**1** = Fehlerbehebung, 1.**1**.0 = neue Funktion,
**2**.0.0 = Änderung, die bestehende Abläufe oder Dateiformate bricht (z. B. andere Spalten in der Kopf-CSV).

## Unveröffentlicht

Erste ausgelieferte Version.

- Wiedergabe von 360°- und 180°-Videos bis 8K in der Varjo XR-3, Mono und Stereo (Top-Bottom, Side-by-Side), Projektionen Equirect 360°/180°, Cubemap, EAC und Fisheye; Format aus den Metadaten oder pro Video einstellbar (Sidecar `*.orbivo.json`)
- Operatorfenster mit Playlist, Zeitleiste, Tastenkürzeln, Schwarzbild, Fade, Recenter, Live-Ansicht und Status
- Ton fest am Bild: Stereo kommt binaural von zwei virtuellen Lautsprechern im Video und bleibt beim Kopfdrehen am Bild
- Leerlauf-Ansicht der Brille: Schwarz, Kamerabild (See-through) oder Standbild
- Aufzeichnung von Kopfbewegung und Bedienereignissen als CSV (Semikolon, Dezimalkomma), wahlweise ein Dateipaar je Video
- Playlist, „Automatisch weiter“, „Wiederholen“ und Fade-Dauer bleiben über einen Neustart erhalten
- Anleitung als `Anleitung.html` im Programmordner, aus der App mit „Anleitung (F1)“ zu öffnen, mit Erklärung der Aufzeichnungsdaten
- Ausgeliefert als ZIP ohne Installation (.NET, FFmpeg, Steam Audio und OpenXR-Loader enthalten)
