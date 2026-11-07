# Testmedien

Erzeugt Testmaterial, mit dem sich Projektion, Stereo, Bildrate, A/V-Sync und Raumklang ohne echte Aufnahmen prüfen lassen:

- Equirect-360-Testbilder (PNG) in Mono, Top-Bottom und Side-by-Side, jeweils 4K und 8K
- dieselbe Szene in den anderen Projektionen (M6): VR180, Cubemap 3×2 / 6×1 / 1×6, EAC und Fisheye
- daraus 20-Sekunden-Testvideos (HEVC) mit Timecode, bewegtem Marker und Sync-Blitz mit Piepton, mit Projektions-Metadaten (`sv3d`/`st3d`) und teils mit Sidecar (`<Video>.orbivo.json`)
- einen Ambisonics-Richtungstest 1. Ordnung (AmbiX und FuMa) mit deutschen Ansagen

Die Ergebnisse landen in `tools/testmedia/out/` und sind nicht im Git.

## Voraussetzungen

- Python 3.12 und die Pakete aus `requirements.txt`:
  ```
  python -m pip install -r tools/testmedia/requirements.txt
  ```
- `ffmpeg.exe` aus `native/win-x64/` (Einrichtung siehe `native/README.md`) oder `--ffmpeg <Pfad>`
- NVIDIA-GPU für den Standard-Encoder `hevc_nvenc`. Ohne NVIDIA: `--codec libsvtav1` (AV1, Software, deutlich langsamer)
- Windows-Sprachausgabe mit deutscher Stimme (z. B. „Microsoft Hedda“). Fehlt sie, enthält der Audiotest nur Rauschimpulse und das Skript gibt eine Warnung aus.

## Aufruf

```
python tools/testmedia/make_testmedia.py                                  # alles: 4K + 8K, Videos, Audio
python tools/testmedia/make_testmedia.py --sizes 4k --layouts tb          # nur 4K Top-Bottom
python tools/testmedia/make_testmedia.py --formats cubemap,eac            # nur Cubemap und EAC
python tools/testmedia/make_testmedia.py --what audio                     # nur der Ambisonics-Test
python tools/testmedia/make_testmedia.py --what decoder                   # nur die Videos für den Software-Decoder
python tools/testmedia/make_testmedia.py --bit-depth 10 --fps 60          # HEVC Main10, 60 fps
python tools/testmedia/make_testmedia.py --long                           # nur das 10-Minuten-Video für den A/V-Langtest (M5)
```

`--long` erzeugt `equirect360_mono_4096x2048_hevc_8bit_30fps_600s.mp4` (etwa 180 MB) neben den kurzen Videos. Andere Längen als 20 s bekommen allgemein die Endung `_<Sekunden>s`. Vorhandene Dateien und ihre Einträge in `manifest.json` bleiben erhalten. Der Langtest läuft nur mit `ORBIVO_LONG_TESTS=1`:

```
$env:ORBIVO_LONG_TESTS = '1'; dotnet test --project tests/Orbivo.Media.Tests
```

| Option | Standard | Bedeutung |
|---|---|---|
| `--what` | `images,videos,audio,decoder` | Was erzeugt wird |
| `--sizes` | `4k,8k` | Größenklassen (siehe Tabelle unten) |
| `--layouts` | `mono,tb,sbs` | Stereo-Layouts (nur Equirect 360) |
| `--formats` | alle | Projektionen: `equirect360`, `equirect180`, `cubemap`, `eac`, `fisheye` |
| `--codec` | `hevc_nvenc` | `hevc_nvenc`, `av1_nvenc` (ab RTX 40), `libsvtav1`, `h264_nvenc` (nur bis 4096 px) |
| `--bit-depth` | `8` | `8` oder `10` |
| `--fps` | `30` | Bildrate der Videos |
| `--duration` | `20` | Länge der Videos in Sekunden |
| `--out` | `tools/testmedia/out` | Zielordner |

Der komplette Satz braucht auf einer RTX 3070 etwa 11 Minuten und belegt etwa 350 MB (ohne das 10-Minuten-Video).

## Ergebnis

| Datei | Größe | Augen |
|---|---|---|
| `equirect360_mono_4096x2048` | 4096×2048 | ein Bild |
| `equirect360_tb_4096x4096` | 4096×4096 | je 4096×2048, linkes Auge oben |
| `equirect360_sbs_4096x2048` | 4096×2048 | je 2048×2048 (horizontal gestaucht), linkes Auge links |
| `equirect360_mono_8192x4096` | 8192×4096 | ein Bild |
| `equirect360_tb_8192x8192` | 8192×8192 | je 8192×4096, linkes Auge oben |
| `equirect360_sbs_8192x4096` | 8192×4096 | je 4096×4096 (horizontal gestaucht), linkes Auge links |

Jeweils als `.png` und als `…_hevc_8bit_30fps.mp4`. Dazu `ambix_foa_richtungstest.wav`, `fuma_foa_richtungstest.wav` und `manifest.json` mit allen Parametern und den Zeitpunkten der Audio-Ereignisse.

360°-Side-by-Side in voller Auflösung (16384 px breit) könnte kein Hardware-Decoder verarbeiten (Grenze 8192). Deshalb sind die SBS-Augen horizontal auf halbe Breite gestaucht, wie bei Kameras üblich.

Die Equirect-360-Videos tragen `sv3d` (equirectangular) und `st3d` (mono, Top-Bottom oder Side-by-Side). Für die Erkennung ohne Metadaten gibt es zwei Kopien des 4K-Top-Bottom-Videos: `…_nometa.mp4` (gar keine Metadaten, Layout aus der Größe geschätzt) und `…_sv3d_only.mp4` (nur `sv3d`, der Player schätzt das Layout und warnt).

### Andere Projektionen (M6)

Jedes Auge wird aus einem Equirect-360-Bild derselben Szene umgerechnet (Würfelflächen, Gitter, Beschriftung, Tafel TIEFE). In der Brille muss also jedes Video genauso aussehen wie das Equirect-360-Testbild, nur bei VR180 und Fisheye 180° fehlt die Rückseite (schwarz). Die Metadaten sind so verteilt, dass jeder Weg der Formaterkennung einmal vorkommt:

| Datei | Größen | Metadaten | Sidecar | Inhalt |
|---|---|---|---|---|
| `equirect180_sbs` | 4096×2048, 8192×4096 | `sv3d` Equirect-Ausschnitt (VR180) + `st3d` SBS | – | je Auge 180°×180°, linkes Auge links |
| `cubemap3x2_mono` | 3072×2048, 6144×4096 | `sv3d` Cubemap + `st3d` mono | – | 3×2: rechts, links, oben / unten, vorne, hinten |
| `cubemap6x1_mono` | 6144×1024 | `sv3d` Cubemap + `st3d` mono | `cubemapLayout: "6x1"` | Metadaten sagen Cubemap, das Sidecar setzt nur das Layout |
| `cubemap1x6_mono` | 1024×6144 | keine | Projektion, Layout, `1x6` | Format nur aus dem Sidecar |
| `cubemap3x2_tb` | 3072×4096 | `sv3d` Cubemap + `st3d` TB | – | Cubemap 3×2 Top-Bottom, linkes Auge oben |
| `eac_mono` | 3072×2048, 6144×4096 | nur `st3d` mono | `projection: "Eac"` | EAC im YouTube-Layout: links, vorne, rechts / unten, hinten, oben (untere Reihe gedreht) |
| `eac_tb` | 3072×4096, 6144×8192 | nur `st3d` TB | `projection: "Eac"` | EAC Top-Bottom |
| `fisheye180_mono` | 4096×4096 | nur `st3d` mono | Fisheye, 180°, mittig, Radius 0,5 | Kreis bildhoch und mittig |
| `fisheye200_sbs` | 4096×2048, 8192×4096 | nur `st3d` SBS | Fisheye, 200°, Mitte 0,48/0,51, Radius 0,46 | Dual-Fisheye, Kreis bewusst versetzt |

Jeweils als `.png` und als `…_hevc_8bit_30fps.mp4`, das Sidecar heißt wie das Video plus `.orbivo.json`. Bilder haben keine Metadaten.

Würfelflächen ohne Drehung sehen aus wie von der Würfelmitte aus gesehen: vorne, hinten, links und rechts aufrecht, bei OBEN zeigt die Oberkante nach hinten, bei UNTEN nach vorne (wie ffmpeg `v360`). Die Layouts folgen der Schreibweise von `v360` (`in_forder`, `in_frot`). Jedes Mono-Bild wird beim Erzeugen mit `ffmpeg -vf v360` aus demselben Equirect-Bild verglichen (mittlere Abweichung im Log, Grenze 6 von 255; gemessen 0,3–1,9).

**Metadaten schreiben:** ffmpeg kann `sv3d`/`st3d` beim Kodieren nicht setzen (der Filter `sidedata` kann sie nur löschen). `orbivo_testmedia/spherical.py` hängt die Boxen deshalb nach dem Kodieren selbst an den Video-Sample-Eintrag (Spherical Video V2, mit `prhd`, `equi`/`cbmp` und `st3d`) und korrigiert Box-Größen und Chunk-Offsets. Geprüft mit `ffprobe` (zeigt „spherical“ und „stereo3d“) und mit den Tests in `Orbivo.Media.Tests`.

### Videos für den Software-Decoder

`--what decoder` erzeugt zwei kurze Equirect-360-Mono-Videos (4 s, 120 Frames, `sv3d`/`st3d` wie oben):

| Datei | Inhalt | Wofür |
|---|---|---|
| `equirect360_mono_4320x2160_h264_8bit_30fps_4s.mp4` | H.264 (Constrained Baseline, `libopenh264`) | Größer, als NVDEC H.264 dekodiert (4096): Der Player muss auf Software ausweichen |
| `equirect360_mono_4096x2048_hevc_10bit_30fps_4s.mp4` | HEVC Main 10 (`hevc_nvenc`) | P010-Pfad; mit `--decoder sw` in Software, Bild bitgenau wie in Hardware |

## Was auf dem Testbild zu sehen ist

- **Sechs farbige Würfelflächen** mit großer Beschriftung: VORNE (grün), RECHTS (orange), HINTEN (rot), LINKS (blau), OBEN (türkis), UNTEN (violett). Die Texte werden auf Würfelflächen gezeichnet und dann umgerechnet. In der Brille stehen sie deshalb unverzerrt und aufrecht da, auch OBEN und UNTEN.
- **Hinweise, wo VORNE liegt:** auf LINKS „VORNE →“, auf RECHTS „← VORNE“, auf OBEN „↓ VORNE“, auf UNTEN „↑ VORNE“, auf HINTEN „VORNE ist hinter dir“. Damit fallen auch Spiegelungen und Rollfehler auf.
- **Gitter** alle 15°, dicker bei 0°, ±90°, 180° und am Horizont. Yaw-Beschriftung bei −37,5° Höhe (positiv = links), Pitch-Beschriftung am Meridian vor dem Betrachter.
- **Auflösungsfeld** unter VORNE: Streifen mit 1, 2 und 4 Pixel Periode (senkrecht und waagerecht) und ein 1-Pixel-Schachbrett, gezeichnet in Quellpixeln. Bleiben die feinsten Streifen sichtbar, kommt die volle Auflösung an.
- **Tafel TIEFE** rechts oberhalb von VORNE. In Mono liegt sie auf der Kugel. In Stereo hat sie 1° gekreuzte Disparität und erscheint bei richtiger Zuordnung vor dem Hintergrund (etwa 3,7 m entfernt). Bei vertauschten Augen liegt sie dahinter oder lässt sich kaum fusionieren.
- **Nur Stereo:** „LINKES AUGE“ / „RECHTES AUGE“ (mit einem geschlossenen Auge lesbar).

## Video-Overlays

- **Timecode und Framenummer** unter VORNE: Sprünge zeigen verlorene oder doppelte Frames.
- **Gelber Marker** auf +25° Höhe: läuft in der Cliplänge einmal im Uhrzeigersinn (von oben gesehen) um den Betrachter und macht Ruckeln sichtbar.
- **Sync-Blitz:** Zu jeder vollen Sekunde leuchtet unter VORNE (−42°) zwei Frames lang ein weißes Quadrat auf. Gleichzeitig ertönt ein 1-kHz-Piepton auf der Stereospur. Für die A/V-Sync-Prüfung ab M5.
- Keyframe jede Sekunde, Farbraum BT.709 limited range, ausdrücklich so konvertiert und gekennzeichnet (sonst rechnet ffmpeg RGB→YUV mit BT.601).

## Ambisonics-Richtungstest

48 kHz, 4 Kanäle, 24 Bit, WAV mit Kanalmaske 0 (keine Lautsprecher-Zuordnung), etwa 25 s.

| # | Ansage | Yaw | Pitch |
|---|---|---|---|
| 1 | vorne | 0° | 0° |
| 2 | rechts | −90° | 0° |
| 3 | hinten | 180° | 0° |
| 4 | links | +90° | 0° |
| 5 | oben | 0° | +90° |
| 6 | unten | 0° | −90° |
| 7 | vorne links | +45° | 0° |
| 8 | vorne rechts | −45° | 0° |

Je Richtung kommt erst die Ansage, dann drei Rauschimpulse aus derselben Richtung, dann eine Pause. Die genauen Zeiten stehen in `manifest.json`.

- `ambix_foa_richtungstest.wav`: AmbiX, Kanäle W, Y, Z, X (ACN), SN3D
- `fuma_foa_richtungstest.wav`: FuMa, Kanäle W, X, Y, Z, W um 3 dB abgesenkt

Ab M7 soll jeder Ton aus der angesagten Richtung kommen und beim Kopfdrehen im Raum stehen bleiben.

## Konventionen

Alle Vorzeichen stehen an einer Stelle: `orbivo_testmedia/conventions.py`. Kurzfassung:

- Welt wie OpenXR: +X rechts, +Y oben, −Z vorne
- Yaw positiv = Drehung nach **links**, Pitch positiv = nach oben
- Equirect: Bildmitte = vorne, links im Bild = links, oben im Bild = oben
- Top-Bottom: linkes Auge oben; Side-by-Side: linkes Auge links
- Ambisonics: X vorne, Y links, Z oben, Azimut gegen den Uhrzeigersinn (= Yaw)

## Grenzen

- EAC-Videos tragen keine EAC-Projektion in den Metadaten: YouTube kennzeichnet EAC nach unserem Wissen mit einer `mshp`-Box (Mesh), die FFmpeg nicht auswertet. Die Projektion steht deshalb im Sidecar (mit echtem Material prüfen, siehe docs/DECISIONS.md)
- Die Testvideos haben keine Grundausrichtung (`prhd` = 0); der Player würde sie nur anzeigen, nicht anwenden
- 8192×8192 HEVC liegt über HEVC-Level 6.2. NVDEC dekodiert es trotzdem (RTX 3070, reine Dekodierung: etwa 54 fps)
- Ambisonics nur 1. Ordnung; höhere Ordnungen kommen mit M7
- Noch kein Video mit eingebettetem Ambisonics-Ton (folgt mit M7)
