# Native Abhängigkeiten

Die nativen DLLs sind **nicht im Git**. Sie gehören nach `native/win-x64/` und werden beim Build ins Ausgabeverzeichnis der App kopiert (`build/Native.targets`). Fehlen sie, bleibt der Build grün und gibt nur einen Hinweis aus. Die App meldet dann beim Start im Status, was fehlt.

## Einrichten

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File native\fetch-native.ps1
```

Das Skript lädt die gepinnten Archive nach `native/_downloads/` (ca. 290 MB), prüft die SHA256-Prüfsummen und entpackt nur die benötigten Dateien. Danach `dotnet build`. Mit `-Force` werden die Archive neu geladen.

Ergebnis:

```
native/win-x64/
  avcodec-63.dll  avformat-63.dll  avutil-61.dll  swresample-7.dll  swscale-10.dll
  avfilter-12.dll  avdevice-63.dll  ffmpeg.exe  ffprobe.exe
  phonon.dll
  openxr_loader.dll
  openxr-tools/  hello_xr.exe  openxr_runtime_list.exe
  licenses/  FFmpeg-LICENSE.txt  SteamAudio-LICENSE.md  SteamAudio-THIRDPARTY.md  OpenXR-LICENSE.txt
```

**Archive selbst sichern:** Die drei ZIP-Dateien aus `native/_downloads/` an einem eigenen Ort aufbewahren. GitHub-Releases können verschwinden (siehe Vorhaltezeit unten).

## FFmpeg 9.0.2 (LGPL, shared)

| | |
|---|---|
| Quelle | GitHub [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), Variante `win64-lgpl-shared-9.0` |
| Release | `autobuild-2026-10-31-12-59` |
| Datei | `ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-shared-9.0.zip` (76,6 MB) |
| SHA256 | `e907b01fa4783913151237e37df2a10fa97d99a1598a021bf59d4ae62ffed4c0` |
| FFmpeg-Version | `n9.0.2-39-g7adfbe40dc-20261031` |
| Lizenz | **LGPL v3 oder später** (BtbN baut mit `--enable-version3`), Text in `licenses/FFmpeg-LICENSE.txt` |

| Datei (aus `bin/`) | Zweck | In der App-Ausgabe |
|---|---|---|
| `avcodec-63.dll` | Decoder | ja |
| `avformat-63.dll` | Container/Demux | ja |
| `avutil-61.dll` | Hilfsfunktionen, Hardware-Kontext | ja |
| `swresample-7.dll` | Audio-Konvertierung | ja |
| `swscale-10.dll` | Bild-Konvertierung (nur Software-Fallback) | ja |
| `avfilter-12.dll`, `avdevice-63.dll` | nur für `ffmpeg.exe` | nein |
| `ffmpeg.exe`, `ffprobe.exe` | Testmedien (`tools/testmedia`), Analyse | nein |

**Gekoppelt an FFmpeg.AutoGen 9.0.1.1** (`Directory.Packages.props`). Die Bindings laden genau diese Hauptversionen (avcodec 63, avformat 63, avutil 61, swresample 7, swscale 10). Die App prüft beim Start Version und Lizenz (`FFmpegRuntime`) und lehnt Nicht-LGPL-Builds ab.

**Warum BtbN:** gyan.dev bietet Shared-Builds nur unter GPL an. Die BtbN-Variante `lgpl-shared` enthält keine GPL-Bibliotheken (kein x264/x265). Enthalten sind u. a. D3D11VA, NVDEC/NVENC, dav1d, SVT-AV1, OpenH264 und FreeType (`drawtext`).

**Vorhaltezeit:** BtbN behält die letzten 14 Tages-Builds und den letzten Build jedes Monats für zwei Jahre. Gepinnt ist der letzte Build vom Oktober 2026, er bleibt also bis etwa Oktober 2028 verfügbar.

**Update:** Neue FFmpeg-Hauptversion nur zusammen mit der passenden FFmpeg.AutoGen-Version. Dabei URL und SHA256 in `fetch-native.ps1`, die DLL-Liste in `build/Native.targets` und `FFmpegRuntime.ExpectedLibraries` sowie diese Datei anpassen.

**Pflichten bei der Auslieferung (M9):** dynamisch gelinkt (shared), DLLs bleiben austauschbar, Lizenztext und Hinweis auf Quelle (FFmpeg-Version, BtbN-Build-Skripte) beilegen. Umgesetzt: Das Release-Paket (`release.ps1`) enthält `licenses\` und `THIRD-PARTY-NOTICES.txt` (aus `build/`). Bei einem Update von FFmpeg, Steam Audio oder dem OpenXR-Loader dort Version und Quelle anpassen.

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
