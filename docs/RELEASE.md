# Releases

Ein Release ist ein ZIP, das man auf dem Zielrechner entpackt und startet (self-contained, ohne Installation).
Gebaut wird es lokal mit `release.ps1`, weil die Tests eine echte GPU brauchen und die nativen DLLs nur hier liegen
(`docs/DECISIONS.md`, M9).

## Versionen
- **Semantic Versioning** `MAJOR.MINOR.PATCH`: 1.0.**1** = Fehlerbehebung, 1.**1**.0 = neue Funktion, **2**.0.0 = bricht
  bestehende Abläufe oder Dateiformate (z. B. andere Spalten in der Kopf-CSV, anderes Sidecar-Format).
- Die Version steht nur in `Directory.Build.props` (`<Version>`), und nur `release.ps1` ändert sie.
- Jedes Release hat ein Git-Tag `vX.Y.Z` und einen Abschnitt in `CHANGELOG.md`.
- Builds außerhalb von `release.ps1` heißen `X.Y.Z-dev` (Titelleiste, Log, Start-Ereignis der Aufzeichnung). So sieht man
  in Aufzeichnungen, ob ein Entwicklungsstand im Einsatz war.

## Einmal einrichten
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1   # native DLLs (native/README.md)
gh auth login                                                                 # GitHub CLI, nur für -Publish
```

## Ablauf
1. **Änderungen notieren:** Beim Arbeiten an `development` jede für Operatoren oder Auswertung sichtbare Änderung in
   `CHANGELOG.md` unter `## Unveröffentlicht` eintragen (kurz, auf Deutsch, aus Sicht des Operators). Committen.
2. **Release bauen** (auf `development`, alles committet):
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0
   ```
   Dauer etwa 3 Minuten. Ergebnis: `artifacts\release\Orbivo-1.1.0-win-x64.zip` (+ `.sha256`), Commit „Release 1.1.0“,
   Tag `v1.1.0`. Es wird nichts gepusht.
3. **Paket prüfen:** ZIP auf dem Brillen-Rechner entpacken, starten, kurzer Brillen-Test (siehe unten).
4. **Veröffentlichen:**
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -Publish
   ```
   Pusht `development` und das Tag, setzt `main` auf das Release und legt das GitHub-Release (privates Repository) mit ZIP,
   Prüfsumme und den Notizen aus `CHANGELOG.md` an. Schritt 2 und 4 gehen auch in einem: gleich mit `-Publish` aufrufen.

## Was `release.ps1` tut
| Schritt | Bricht ab, wenn … |
|---|---|
| Prüft Branch `development`, keine uncommitteten Änderungen, Tag frei, nichts Neues auf `origin/development` | eins davon nicht stimmt |
| Prüft die nativen DLLs und Lizenztexte in `native/win-x64` | etwas fehlt |
| Prüft, dass die neue Version größer ist und unter „Unveröffentlicht“ etwas steht | nicht |
| `dotnet test` (Debug, ein Testprojekt nach dem anderen) | ein Test scheitert |
| Setzt die Version, macht aus „Unveröffentlicht“ den Abschnitt `## X.Y.Z – Datum` | – |
| `dotnet publish` self-contained win-x64 (Release) nach `artifacts\release\Orbivo-X.Y.Z\`, dazu `licenses\` und `THIRD-PARTY-NOTICES.txt` | der Build scheitert (Version und Changelog werden zurückgesetzt) |
| Prüft das Paket: Pflichtdateien da, `ffmpeg.exe` & Co. nicht da, `Orbivo.exe --check` lädt FFmpeg, Steam Audio und den OpenXR-Loader und findet beide Anleitungen | etwas fehlt |
| ZIP mit Ordner `Orbivo-X.Y.Z` darin, SHA256-Datei | – |
| Commit „Release X.Y.Z“ (nur `Directory.Build.props` und `CHANGELOG.md`), annotiertes Tag `vX.Y.Z` | – |
| Nur `-Publish`: `git push` von `development` und Tag, `main` vorwärts auf das Release (`git push origin development:main`), `gh release create` | `main` hat Commits, die `development` fehlen; `gh` fehlt |

Weitere Schalter:
- `-PackageOnly`: nur Paket bauen und prüfen (`Orbivo-X.Y.Z-dev-win-x64.zip`), ohne Versionsänderung, Commit und Tag; auch mit
  uncommitteten Änderungen. Zum Ausprobieren.
- `-SkipTests`: Tests auslassen. Nur im Notfall.

## Paketinhalt
```
Orbivo-1.1.0/
  Orbivo.exe, *.dll           App und .NET-Laufzeit (self-contained)
  avcodec-63.dll …            FFmpeg (LGPL, austauschbar), phonon.dll, openxr_loader.dll
  Anleitung.html, Manual.html Bedienungsanleitung deutsch und englisch (F1 in der App)
  de/*.resources.dll          deutsche Texte der App (Englisch steckt in den DLLs selbst)
  THIRD-PARTY-NOTICES.txt     Drittkomponenten und Lizenzen
  licenses/                   Lizenztexte von FFmpeg, Steam Audio, OpenXR
  *.pdb                       Symbole, damit Fehler im Log mit Zeilennummern stehen
```
Beim ersten Start legt Orbivo daneben den Ordner `Logs` an. Einstellungen liegen in `%LOCALAPPDATA%\Orbivo`, deshalb
ist ein Update: alten Ordner ersetzen.

## Kurzer Brillen-Test vor dem Veröffentlichen
1. ZIP entpacken (nicht unter „Programme“), `Orbivo.exe` starten. Titelleiste: `Orbivo X.Y.Z – Operator` (ohne „-dev“).
2. Status: FFmpeg, Steam Audio, OpenXR grün; Brille verbunden, etwa 90 fps.
3. F1 bzw. Hilfe → Anleitung öffnet die Anleitung der eingestellten Sprache im Browser; Optionen → „Sprache / Language“
   umstellen, Neustart bestätigen, die Texte wechseln.
4. Ein 8K-Testvideo abspielen: Bild in der Brille, Ton fest am Bild, A/V im grünen Bereich.
5. Was sich in diesem Release geändert hat, gezielt ausprobieren (`CHANGELOG.md`).

## Wenn etwas schiefgeht
- **Release lokal verwerfen** (vor `-Publish`): `git tag -d vX.Y.Z` und `git reset --keep HEAD~1`, ZIP löschen, Fehler
  beheben, neu bauen.
- **`-Publish` brach mittendrin ab:** Ursache beheben und `-Publish` erneut aufrufen; bereits gepushte Teile bleiben, wie
  sie sind. Existiert das GitHub-Release schon, nur noch fehlende Schritte von Hand.
- **Fehler in einem veröffentlichten Release:** auf `development` beheben, im Changelog eintragen, neues Release mit
  erhöhter PATCH-Version (z. B. 1.1.1). Veröffentlichte Tags werden nie verschoben.
- **Von Hand veröffentlichen** (ohne `gh`): Tag und Branches pushen, auf GitHub unter „Releases“ ein Release zum Tag
  anlegen, ZIP und `.sha256` anhängen, Text aus `artifacts\release\Orbivo-X.Y.Z-notes.md` bzw. dem Changelog.
