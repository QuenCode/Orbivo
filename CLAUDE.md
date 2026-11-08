# Orbivo – Hinweise für Claude Code

## Projekt in einem Satz
Windows-Player für 360°/180°-Videos (≥ 8K, Stereo, räumlicher Ton) auf einer **Varjo XR-3**.
Bedient wird er **ausschließlich von einem Operator am Desktop**. Die Person mit Brille interagiert nicht.

Details: `docs/SPEC.md` (Anforderungen), `docs/ARCHITECTURE.md` (Aufbau), `docs/ROADMAP.md` (Meilensteine).
Lies vor jeder Aufgabe den passenden Meilenstein in `docs/ROADMAP.md`.

## Tech-Stack (nicht ohne Rückfrage ändern)
- .NET 10 (LTS), C#, `net10.0-windows`, x64 only
- UI: WPF + CommunityToolkit.Mvvm
- XR: Silk.NET.OpenXR, Runtime = Varjo OpenXR (Varjo Base)
- Grafik: Vortice.Windows (Direct3D11, DXGI, D3DCompiler)
- Media: FFmpeg (LGPL-**Shared**-Build) über FFmpeg.AutoGen, Hardware-Decoding D3D11VA
- Audio-Ausgabe: NAudio (WASAPI, Exclusive oder Shared mit kleinem Puffer)
- Räumlicher Ton: Steam Audio (`phonon.dll`, Apache-2.0) per P/Invoke
- Tests: xUnit

Neue NuGet-Pakete oder native DLLs **nur nach Rückfrage**. Versionen in `Directory.Packages.props` pinnen.

## Hardware-Ziel
- Desktop: RTX 5080, Laptop: RTX 4080 (NVDEC, HEVC/AV1 bis 8192×8192)
- Varjo XR-3 über Varjo Base, keine Controller nötig
- Kopfhörer: neutrale, kabelgebundene Stereo-Kopfhörer. **Die gesamte Raumklang-Berechnung passiert im Player.**

## Harte Regeln
1. **Render-Thread (OpenXR-Frame-Loop) blockiert nie**: keine Locks mit Wartezeit, keine I/O, keine Allokationen pro Frame, kein `async`.
2. **Audio-Callback blockiert nie** und allokiert nicht.
3. Kommunikation UI → Player nur über Befehle (`Channel<PlayerCommand>`); Player → UI nur über Status-Snapshots/Events. Kein direkter Zugriff der UI auf Player-Interna.
4. **Audio-Clock ist Master-Clock.** Gibt es keinen Ton, übernimmt eine Systemuhr.
5. Video-Frames bleiben auf der GPU (NV12/P010-Texturen). Keine CPU-Kopie von Videoframes. Einzige Ausnahme: Software-Decoder-Fallback (siehe `docs/DECISIONS.md`).
6. 360°-Inhalt wird **nur rotatorisch** dargestellt (Kopfposition ignorieren, Inhalt im Unendlichen).
7. In der Brille wird **nie** UI angezeigt. Nur Video, Leerlauf-Ansicht (Schwarz, Kamerabild, Standbild; F7), Schwarzbild oder Fade.
8. Projektionsmathematik existiert zweimal: HLSL-Shader und C#-Referenz (`ProjectionMath`) mit Unit-Tests. **Beide immer synchron ändern.**
9. Koordinatensystem überall: OpenXR-Konvention, rechtshändig, +Y oben, −Z vorne, +X rechts. Umrechnungen nur an klar benannten Stellen.

## Code-Konventionen
- `Nullable` aktiviert, `TreatWarningsAsErrors` an, `unsafe` nur in `Orbivo.Xr`, `.Media`, `.Audio`
- Code, Bezeichner und Kommentare auf Englisch; **Operator-UI auf Deutsch**
- Native Ressourcen (COM, FFmpeg, Steam Audio) immer mit `IDisposable` und klarer Ownership
- Logging: `Microsoft.Extensions.Logging`, Datei-Log pro Sitzung im Ordner `Logs` neben der exe (älter als 30 Tage wird beim Start gelöscht, die letzten 30 Sitzungen bleiben immer)
- Keine stillen Catch-Blöcke. Fehler loggen und als Status an die UI melden.

## Build & Test
```
dotnet build Orbivo.sln -c Debug
dotnet test
dotnet run --project src/Orbivo.App -- --no-xr      # Desktop-Debug-Viewport ohne Brille
dotnet run --project src/Orbivo.App -- --no-xr --image tools/testmedia/out/equirect360_mono_4096x2048.png
dotnet run --project src/Orbivo.App -- --image tools/testmedia/out/equirect360_mono_4096x2048.png   # Testbild in der Brille (Varjo Base muss laufen)
dotnet run --project src/Orbivo.App -- --no-xr --video tools/testmedia/out/equirect360_tb_8192x8192_hevc_8bit_30fps.mp4   # Video in der Vorschau als erster Playlist-Eintrag, Play im Operatorfenster
dotnet run --project src/Orbivo.App -- --video tools/testmedia/out/equirect360_tb_8192x8192_hevc_8bit_30fps.mp4            # Video in der Brille (--stereo mono|tb|sbs erzwingt das Layout, --decoder auto|hw|sw den Decoder)
powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1   # native DLLs laden
python tools/testmedia/make_testmedia.py            # Testbilder, -videos, Ambisonics-Test
powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -PackageOnly              # Probepaket bauen und prüfen (ohne Version, Commit, Tag)
powershell -NoProfile -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 [-Publish] # Release (docs/RELEASE.md)
$env:ORBIVO_LONG_TESTS=1; dotnet test --project tests/Orbivo.Media.Tests -- --filter-class "*SoakTests"   # 30-Minuten-Dauertest mit Aufzeichnung (ORBIVO_SOAK_MINUTES kürzt ihn)
```
FFmpeg-, Steam-Audio- und OpenXR-Loader-DLLs liegen in `native/win-x64/` (nicht im Git, siehe `native/README.md`) und werden beim Build ins Ausgabeverzeichnis kopiert.
Gearbeitet wird auf dem Branch `development`.

## Was du (Claude) nicht prüfen kannst
Du siehst die Brille nicht und hörst den Ton nicht. Deshalb:
- Alles, was ohne Brille testbar ist, über `--no-xr`-Modus, Unit-Tests und Log-Ausgaben absichern.
- `Orbivo.Xr.Tests` rendert auf WARP und legt die Ansichten als PNG unter `tests/Orbivo.Xr.Tests/bin/…/rendered-views/` ab. Die kann man sich ansehen.
- `Orbivo.Media.Tests` dekodiert die Testvideos mit echter GPU und FFmpeg (8K-Benchmark in der Testausgabe) und spielt Open/Play/Pause/Stop über den Befehlskanal durch.
- Am Ende jedes Meilensteins eine kurze **Checkliste für den manuellen Test mit Brille** ausgeben (was tun, was sollte man sehen/hören).
- Bei Unsicherheit über Orientierung oder Vorzeichen (Projektion, Ambisonics-Rotation): Testmaterial aus `tools/testmedia` nutzen und den Menschen gezielt fragen, was er sieht oder hört.

## Arbeitsweise
- Ein Meilenstein nach dem anderen, kleine Commits mit aussagekräftigen Messages
- Vor größeren Strukturänderungen kurz Plan vorschlagen und auf OK warten
- Offene Punkte oder Annahmen in `docs/DECISIONS.md` festhalten (Datum, Entscheidung, Begründung)
- Ändert sich etwas, das Operatoren oder Auswertende merken (Bedienung, Aufzeichnungsformat, Sidecar), dann `docs/Anleitung.html` mitpflegen und die Änderung in `CHANGELOG.md` unter „Unveröffentlicht“ eintragen
- Releases nur über `release.ps1` und nur auf ausdrücklichen Wunsch; `-Publish` pusht und veröffentlicht
