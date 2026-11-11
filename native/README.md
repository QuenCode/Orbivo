# Native Abhängigkeiten

Die nativen DLLs sind **nicht im Git**. Sie gehören nach `native/win-x64/` und werden beim Build ins Ausgabeverzeichnis der App kopiert (`build/Native.targets`). Fehlen sie, bleibt der Build grün und gibt nur einen Hinweis aus. Die App meldet dann beim Start im Status, was fehlt.

## Einrichten

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1
```

Das Skript lädt die gepinnten Archive nach `native/_downloads/`, prüft die SHA256-Prüfsummen und entpackt nur die benötigten Dateien. Danach `dotnet build`. Mit `-Force` werden die Archive neu geladen. Solange das Repository privat ist, lädt das Skript den FFmpeg-Build über die GitHub CLI (`gh`, angemeldet).

Ergebnis:

```
native/win-x64/                 was die App braucht (und das Tonanalyse-Kit)
  avcodec-63.dll  avformat-63.dll  avutil-61.dll  swresample-7.dll  swscale-10.dll
  avfilter-12.dll  ffmpeg.exe  ffprobe.exe
  phonon.dll
  openxr_loader.dll
  openxr-tools/  hello_xr.exe  openxr_runtime_list.exe
  licenses/  FFmpeg-COPYING.LGPLv2.1.txt  FFmpeg-LICENSE.md  FFmpeg-BUILD-INFO.txt  dav1d-COPYING.txt  zlib-LICENSE.txt
             SteamAudio-LICENSE.md  SteamAudio-THIRDPARTY.md  OpenXR-LICENSE.txt
native/ffmpeg-full/             voller FFmpeg-Build nur für tools/testmedia, wird nie ausgeliefert
native/_downloads/              Archive, dazu orbivo-ffmpeg-9.0.2-1-source.tar (für release.ps1)
```

**Archive selbst sichern:** Die Dateien aus `native/_downloads/` an einem eigenen Ort aufbewahren. GitHub-Releases können verschwinden (siehe Vorhaltezeit unten).

## FFmpeg 9.0.2 für Orbivo (eigener Build, LGPL-2.1-or-later)

Seit 1.2 liefert Orbivo einen **eigenen, schlanken FFmpeg-Build** aus (`build/ffmpeg/`, gebaut auf GitHub Actions). Er
enthält FFmpeg mit allen eigenen Komponenten, dazu fest eingebunden nur **dav1d** (AV1 in Software, BSD-2-Clause) und
**zlib**. Keine weiteren Fremdbibliotheken, kein Netzwerk, kein avdevice, nichts GPL oder nonfree.

| | |
|---|---|
| Quelle | GitHub-Release [`ffmpeg-9.0.2-1`](https://github.com/QuenCode/Orbivo/releases/tag/ffmpeg-9.0.2-1) dieses Repositorys (Vorabversion) |
| Datei | `orbivo-ffmpeg-9.0.2-1-win64-lgpl-shared.zip` |
| SHA256 | `3224ff4e073f75da98c1c6c599ccefd9792e61f91d47357f81d4bd4e262954c0` |
| Quellcode | `orbivo-ffmpeg-9.0.2-1-source.tar` (FFmpeg 9.0.2, dav1d 1.5.4, zlib 1.4 als Originalarchive, Build-Skript, Workflow), SHA256 `d84883e01256b7ebd81853eb80e8dd45aa786a28a10540f33733f84c4bc9b19d` |
| Lizenz | **LGPL-2.1-or-later**; Texte in `licenses/`, Build-Einstellungen in `licenses/FFmpeg-BUILD-INFO.txt` |

| Datei | Zweck | In der App-Ausgabe |
|---|---|---|
| `avcodec-63.dll` | Decoder | ja |
| `avformat-63.dll` | Container/Demux | ja |
| `avutil-61.dll` | Hilfsfunktionen, Hardware-Kontext | ja |
| `swresample-7.dll` | Audio-Konvertierung | ja |
| `swscale-10.dll` | Bild-Konvertierung (nur Software-Fallback) | ja |
| `avfilter-12.dll` | nur für `ffmpeg.exe` | nein |
| `ffmpeg.exe`, `ffprobe.exe` | Tonanalyse-Kit (`tools/tonanalyse`), Analyse | nein |

**Gekoppelt an FFmpeg.AutoGen 9.0.1.1** (`Directory.Packages.props`). Die Bindings laden genau diese Hauptversionen (avcodec 63, avformat 63, avutil 61, swresample 7, swscale 10). Die App prüft beim Start Version und Lizenz (`FFmpegRuntime`) und lehnt Nicht-LGPL-Builds ab.

**Warum ein eigener Build:** Der BtbN-Build `lgpl-shared` (bis 1.1) bindet rund 85 Fremdprojekte und über 600 Rust-Pakete fest in die DLLs ein, mit eigenen Hinweis- und Quellcode-Pflichten. Orbivo braucht davon nur dav1d (`docs/DECISIONS.md`, 11.11.2026).

**Pflichten bei der Auslieferung:** dynamisch gelinkt (shared), DLLs bleiben austauschbar, Lizenztexte beilegen, Quellcode verfügbar halten. Umgesetzt: Das Release-Paket (`release.ps1`) enthält `licenses\` und `THIRD-PARTY-NOTICES.txt` (aus `build/`); `release.ps1 -Publish` hängt das Quellcode-Archiv an jedes GitHub-Release.

**Update:**
1. In `build/ffmpeg/build-ffmpeg.sh` Versionen, URLs und SHA256 ändern (bei gleichen Quellen nur `BUILD_NUMBER` erhöhen), committen und pushen. Der Workflow baut und prüft (D3D11VA-Decoder, keine Fremd-DLLs, keine GPL).
2. Artefakt des Laufs laden (`gh run download`), testen, als GitHub-Release `ffmpeg-<Version>-<Build>` (Vorabversion) mit ZIP, Quellarchiv und `SHA256SUMS.txt` veröffentlichen.
3. Tag, Dateinamen und SHA256 in `fetch-native.ps1`, `build/THIRD-PARTY-NOTICES.txt` und dieser Datei anpassen.
4. Neue FFmpeg-**Haupt**version nur zusammen mit der passenden FFmpeg.AutoGen-Version; dann auch die DLL-Namen in `build/Native.targets`, `FFmpegRuntime.ExpectedLibraries`, `release.ps1` und `tools/tonanalyse/make-kit.ps1`.

## FFmpeg 9.0.2 voller Build (nur Testmedien)

`tools/testmedia` braucht Encoder (NVENC, SVT-AV1, OpenH264) und `drawtext`. Dafür lädt `fetch-native.ps1` den
BtbN-Build nach `native/ffmpeg-full/`. Er wird **nie** in die App oder ein Paket kopiert und nicht weitergegeben.

| | |
|---|---|
| Quelle | GitHub [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), Variante `win64-lgpl-shared-9.0`, Release `autobuild-2026-10-31-12-59` |
| Datei | `ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-shared-9.0.zip` (76,6 MB) |
| SHA256 | `e907b01fa4783913151237e37df2a10fa97d99a1598a021bf59d4ae62ffed4c0` |

**Vorhaltezeit:** BtbN behält die letzten 14 Tages-Builds und den letzten Build jedes Monats für zwei Jahre. Gepinnt ist der letzte Build vom Oktober 2026, er bleibt also bis etwa Oktober 2028 verfügbar.

## Steam Audio 4.8.1

| | |
|---|---|
| Quelle | GitHub [ValveSoftware/steam-audio](https://github.com/ValveSoftware/steam-audio), Release `v4.8.1` (11.02.2026) |
| Datei | `steamaudio_4.8.1.zip` (181 MB) |
| SHA256 | `4a0aa5ec1176f38f0b0993a37c2259d9e86f27e22d5e24f83ec4c3cb9a1d5449` |
| Lizenz | Apache-2.0. Das SDK-Archiv enthält keinen Lizenztext, das Skript lädt ihn aus dem Repo-Tag `v4.8.1` (`LICENSE.md`, SHA256 `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`). Dazu `THIRDPARTY.md` aus dem Archiv. |

| Datei | Zweck | In der App-Ausgabe |
|---|---|---|
| `steamaudio/lib/windows-x64/phonon.dll` (50,5 MB, Dateiversion 4.8.1) | Binaural-Rendering, Ambisonics | ja |
| `TrueAudioNext.dll`, `GPUUtilities.dll` | Faltung auf AMD-GPUs | nein, nicht gebraucht |
| `phonon.lib`, Symbole, Unity/FMOD/Wwise/Unreal-Plugins | – | nein |

**Interop:** Die P/Invoke-Deklarationen (ab M7) werden gegen `phonon.h` aus genau dieser Version geschrieben. Die App prüft die Dateiversion beim Start (`SteamAudioRuntime`). Ein Update geht nur zusammen mit einem Abgleich der Header-Änderungen.

## OpenXR-Loader 1.1.65

| | |
|---|---|
| Quelle | GitHub [KhronosGroup/OpenXR-SDK-Source](https://github.com/KhronosGroup/OpenXR-SDK-Source), Release `release-1.1.65` (16.10.2026) |
| Datei | `openxr_loader_windows-1.1.65.zip` (32,5 MB) |
| SHA256 | `859d7de35b7a968112db770b09ed8bddeed3cb16fd8ae070643a857676bea6fd` |
| Lizenz | Apache-2.0, Text in `licenses/OpenXR-LICENSE.txt` (aus `share/doc/openxr/LICENSE` im Archiv) |

| Datei (aus `x64/bin/`) | Zweck | In der App-Ausgabe |
|---|---|---|
| `openxr_loader.dll` (Dateiversion 1.1.65) | Lädt das aktive OpenXR-Runtime (Varjo OpenXR aus Varjo Base). Silk.NET.OpenXR bringt keinen Loader mit | ja |
| `openxr-tools/hello_xr.exe` | Khronos-Beispiel: prüft die Brille unabhängig von Orbivo (`hello_xr.exe -g D3D11`) | nein |
| `openxr-tools/openxr_runtime_list.exe` | listet die installierten Runtimes | nein |
| API-Layer (Validation, API-Dump), Symbole, ARM/UWP/Win32 | – | nein |

Das Runtime selbst gehört nicht zur App. Varjo Base trägt es in der Registry ein (`HKLM\SOFTWARE\Khronos\OpenXR\1`, Wert `ActiveRuntime` = `C:\Program Files\Varjo\varjo-openxr\VarjoOpenXR.json`).

**Update:** Der Loader ist abwärtskompatibel; Orbivo fordert OpenXR 1.0 an. Neue Version: URL und SHA256 in `fetch-native.ps1`, `OpenXrLoader.ExpectedVersion` und diese Datei anpassen.
