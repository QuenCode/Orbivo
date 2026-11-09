<#
.SYNOPSIS
    Orbivo-Tonanalyse: technische Kennwerte der Tonspur(en) eines Videos, ohne persönliche oder inhaltliche Daten.

.DESCRIPTION
    Ermittelt, wie Orbivo die Tonspur sieht: Codec, Kanäle, Kanal-Layout, Samplerate, Bitrate, AAC-Konfiguration,
    Ambisonics-Metadaten (SA3D) und Spherical-Video-Metadaten des Bilds. Dazu eine Pegel- und Korrelationsanalyse der
    Kanäle, mit der sich Kanalreihenfolge und Normierung (AmbiX/FuMa) prüfen lassen.

    Datenschutz: Der Bericht enthält keine Datei- oder Ordnernamen, keine Metadaten-Texte (Kamera, Software, Datum,
    Ort, Sprache, Titel), keine Tonausschnitte und keinen zeitlichen Verlauf, nur zusammengefasste Zahlen über die
    ganze analysierte Dauer. MP4-Bereiche mit Benutzer- oder Gerätedaten (udta, meta) werden nicht gelesen.
    Fehlermeldungen von FFmpeg (die den Pfad enthalten können) werden nicht übernommen. Das Skript baut keine
    Netzwerkverbindung auf und schreibt nur die Ergebnisdatei.

.PARAMETER Path
    Eine oder mehrere Videodateien. Ohne Angabe öffnet sich ein Dateidialog.

.PARAMETER MaxSeconds
    Höchstens so viele Sekunden je Tonspur analysieren (ab Beginn). Standard 600.

.PARAMETER FFmpegDir
    Ordner mit ffprobe.exe und ffmpeg.exe. Standard: Unterordner "ffmpeg" neben dem Skript, dann der Skriptordner,
    dann native\win-x64 im Orbivo-Repository.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path,
    [double]$MaxSeconds = 600,
    [string]$FFmpegDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$csharp = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace OrbivoTonanalyse
{
    // Reads the technical boxes of an MP4/MOV file: sample entries of sound and picture tracks.
    // Never descends into udta or meta (user data, device data, location) and never reports text fields.
    public static class Mp4Scan
    {
        private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

        public static List<string> Scan(string path)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long length = fs.Length;
                long pos = 0;
                bool moovFound = false;
                bool fragmented = false;
                bool first = true;
                while (pos + 8 <= length)
                {
                    byte[] head = ReadAt(fs, pos, 16);
                    long size = BE32(head, 0);
                    string type = Encoding.ASCII.GetString(head, 4, 4);
                    int header = 8;
                    if (size == 1) { size = (long)BE64(head, 8); header = 16; }
                    else if (size == 0) { size = length - pos; }
                    if (first)
                    {
                        if (type != "ftyp" && type != "moov" && type != "mdat" && type != "wide" && type != "free")
                        {
                            lines.Add("Kein MP4/MOV-Container erkannt (Box-Analyse übersprungen).");
                            return lines;
                        }
                        first = false;
                    }

                    if (size < header || pos + size > length)
                    {
                        lines.Add("Box-Struktur: unerwartetes Ende oder beschädigte Box auf oberster Ebene.");
                        break;
                    }

                    if (type == "moof") fragmented = true;
                    if (type == "moov")
                    {
                        moovFound = true;
                        if (size - header > 512L * 1024 * 1024)
                        {
                            lines.Add("moov größer als 512 MB, Box-Analyse übersprungen.");
                        }
                        else
                        {
                            byte[] moov = ReadAt(fs, pos + header, (int)(size - header));
                            int track = 0;
                            foreach (Box box in Children(moov, 0, moov.Length))
                            {
                                if (box.Type == "trak")
                                {
                                    track++;
                                    ScanTrack(moov, box, track, lines);
                                }
                            }
                        }
                    }
                    pos += size;
                }

                if (!moovFound) lines.Add("Keine moov-Box gefunden.");
                if (fragmented) lines.Add("Datei ist fragmentiert (moof-Boxen).");
            }
            return lines;
        }

        private struct Box
        {
            public int Offset;
            public int Size;
            public int Header;
            public string Type;
            public int Body { get { return Offset + Header; } }
            public int End { get { return Offset + Size; } }
        }

        private static IEnumerable<Box> Children(byte[] d, int start, int end)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                long size = BE32(d, pos);
                int header = 8;
                if (size == 1)
                {
                    if (pos + 16 > end) yield break;
                    size = (long)BE64(d, pos + 8);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }
                if (size < header || pos + size > end) yield break;
                var box = new Box { Offset = pos, Size = (int)size, Header = header, Type = Encoding.ASCII.GetString(d, pos + 4, 4) };
                yield return box;
                pos += (int)size;
            }
        }

        private static void ScanTrack(byte[] d, Box trak, int number, List<string> lines)
        {
            string handler = null;
            bool sphericalV1 = false;
            Box? stsd = null;
            foreach (Box box in Children(d, trak.Body, trak.End))
            {
                if (box.Type == "uuid" && box.Size >= box.Header + 16 && IsSphericalV1(d, box.Body)) sphericalV1 = true;
                if (box.Type != "mdia") continue;
                foreach (Box m in Children(d, box.Body, box.End))
                {
                    if (m.Type == "hdlr" && m.Size >= m.Header + 12) handler = Encoding.ASCII.GetString(d, m.Body + 8, 4);
                    if (m.Type != "minf") continue;
                    foreach (Box mi in Children(d, m.Body, m.End))
                    {
                        if (mi.Type != "stbl") continue;
                        foreach (Box s in Children(d, mi.Body, mi.End))
                        {
                            if (s.Type == "stsd") stsd = s;
                        }
                    }
                }
            }

            string kind = handler == "soun" ? "Ton" : handler == "vide" ? "Bild" : "andere (" + Safe4(handler) + ")";
            lines.Add(string.Format("Spur {0}: {1}", number, kind));
            if (sphericalV1) lines.Add("  Spherical Video V1 (uuid-Box) vorhanden");
            if (stsd == null || (handler != "soun" && handler != "vide")) return;

            Box sd = stsd.Value;
            int entries = (int)BE32(d, sd.Body + 4);
            int index = 0;
            foreach (Box entry in Children(d, sd.Body + 8, sd.End))
            {
                if (index++ >= entries) break;
                if (handler == "soun") ScanAudioEntry(d, entry, lines);
                else ScanVisualEntry(d, entry, lines);
            }
        }

        private static void ScanAudioEntry(byte[] d, Box e, List<string> lines)
        {
            int b = e.Body;
            if (e.Size < e.Header + 28) { lines.Add("  Sample-Entry zu kurz"); return; }
            int version = BE16(d, b + 8);
            int channels = BE16(d, b + 16);
            int bits = BE16(d, b + 18);
            long rate = BE32(d, b + 24) >> 16;
            int childStart = b + 28 + (version == 1 ? 16 : version == 2 ? 36 : 0);
            lines.Add(string.Format("  Sample-Entry {0}: {1} Kanäle, {2} Bit, {3} Hz (Version {4})", Safe4(e.Type), channels, bits, rate, version));
            ScanAudioChildren(d, childStart, e.End, lines, 0);
        }

        private static void ScanAudioChildren(byte[] d, int start, int end, List<string> lines, int depth)
        {
            foreach (Box c in Children(d, start, end))
            {
                switch (c.Type)
                {
                    case "esds": ParseEsds(d, c.Body + 4, c.End, lines); break;
                    case "SA3D": ParseSa3d(d, c.Body, c.End, lines); break;
                    case "SAND": lines.Add("  SAND-Box (kopffester Zusatzton) vorhanden"); break;
                    case "chan": ParseChan(d, c.Body, c.End, lines); break;
                    case "dOps": ParseDops(d, c.Body, c.End, lines); break;
                    case "wave": if (depth < 2) ScanAudioChildren(d, c.Body, c.End, lines, depth + 1); break;
                    case "udta":
                    case "meta":
                        break; // never read: may hold device or user data
                    default:
                        lines.Add("  Box " + Safe4(c.Type) + " (" + (c.Size - c.Header) + " Byte)");
                        break;
                }
            }
        }

        private static void ScanVisualEntry(byte[] d, Box e, List<string> lines)
        {
            int b = e.Body;
            if (e.Size < e.Header + 78) { lines.Add("  Sample-Entry zu kurz"); return; }
            int width = BE16(d, b + 24);
            int height = BE16(d, b + 26);
            lines.Add(string.Format("  Sample-Entry {0}: {1} × {2}", Safe4(e.Type), width, height));
            foreach (Box c in Children(d, b + 78, e.End))
            {
                if (c.Type == "st3d" && c.Size >= c.Header + 5)
                {
                    int mode = d[c.Body + 4];
                    string name = mode == 0 ? "mono" : mode == 1 ? "oben-unten" : mode == 2 ? "links-rechts" : "unbekannt";
                    lines.Add(string.Format("  st3d: Stereo-Modus {0} ({1})", mode, name));
                }
                else if (c.Type == "sv3d")
                {
                    var found = new List<string>();
                    foreach (Box s in Children(d, c.Body, c.End))
                    {
                        if (s.Type != "proj") continue;
                        foreach (Box p in Children(d, s.Body, s.End)) found.Add(Safe4(p.Type));
                    }
                    lines.Add("  sv3d: Projektion mit " + (found.Count == 0 ? "keinen Unterboxen" : string.Join(", ", found.ToArray())));
                }
                else if (c.Type == "mshp" || c.Type == "SA3D" || c.Type == "proj")
                {
                    lines.Add("  Box " + Safe4(c.Type) + " vorhanden");
                }
            }
        }

        private static void ParseSa3d(byte[] d, int start, int end, List<string> lines)
        {
            if (end - start < 12) { lines.Add("  SA3D: zu kurz"); return; }
            int version = d[start];
            int type = d[start + 1];
            long order = BE32(d, start + 2);
            int ordering = d[start + 6];
            int normalization = d[start + 7];
            long count = BE32(d, start + 8);
            var map = new List<string>();
            for (int i = 0; i < count && start + 12 + 4 * i + 4 <= end && i < 64; i++) map.Add(BE32(d, start + 12 + 4 * i).ToString(De));
            lines.Add(string.Format(
                "  SA3D: Version {0}, Typ {1} ({2}), Ordnung {3}, Reihenfolge {4} ({5}), Normierung {6} ({7}), {8} Kanäle, Kanalzuordnung [{9}]",
                version, type, type == 0 ? "periphon" : "anderer", order, ordering, ordering == 0 ? "ACN" : "anderer",
                normalization, normalization == 0 ? "SN3D" : "anderer", count, string.Join(", ", map.ToArray())));
        }

        private static void ParseChan(byte[] d, int start, int end, List<string> lines)
        {
            if (end - start < 16) { lines.Add("  chan: zu kurz"); return; }
            long tag = BE32(d, start + 4);
            long bitmap = BE32(d, start + 8);
            long descriptions = BE32(d, start + 12);
            long high = tag >> 16;
            string name = high == 190 ? "HOA ACN/SN3D" : high == 191 ? "HOA ACN/N3D" : high == 0 ? "Kanalbeschreibungen" : high == 1 ? "Bitmap" : "Layout-Tag " + high;
            lines.Add(string.Format("  chan: {0}, {1} Kanäle laut Tag, Bitmap 0x{2:X}, {3} Beschreibungen", name, tag & 0xFFFF, bitmap, descriptions));
        }

        private static void ParseDops(byte[] d, int start, int end, List<string> lines)
        {
            if (end - start < 11) { lines.Add("  dOps: zu kurz"); return; }
            int outputChannels = d[start + 1];
            int family = d[start + 10];
            string text = string.Format("  dOps (Opus): {0} Kanäle, Mapping-Familie {1}", outputChannels, family);
            if (family != 0 && end - start >= 13 + outputChannels)
            {
                var map = new List<string>();
                for (int i = 0; i < outputChannels; i++) map.Add(d[start + 13 + i].ToString(De));
                text += string.Format(", {0} Streams, {1} gekoppelt, Zuordnung [{2}]", d[start + 11], d[start + 12], string.Join(", ", map.ToArray()));
            }
            lines.Add(text);
        }

        private static void ParseEsds(byte[] d, int start, int end, List<string> lines)
        {
            int pos = start;
            int tag; int len;
            if (!ReadDescriptor(d, ref pos, end, out tag, out len) || tag != 3) { lines.Add("  esds: kein ES-Deskriptor"); return; }
            pos += 2;
            int flags = d[pos++];
            if ((flags & 0x80) != 0) pos += 2;
            if ((flags & 0x40) != 0) pos += 1 + d[pos];
            if ((flags & 0x20) != 0) pos += 2;
            if (!ReadDescriptor(d, ref pos, end, out tag, out len) || tag != 4) { lines.Add("  esds: kein DecoderConfig"); return; }
            int objectType = d[pos];
            long maxBitrate = BE32(d, pos + 5);
            long avgBitrate = BE32(d, pos + 9);
            int configEnd = pos + len;
            pos += 13;
            lines.Add(string.Format("  esds: Objekttyp 0x{0:X2}, Bitrate max. {1} kb/s, mittel {2} kb/s", objectType, maxBitrate / 1000, avgBitrate / 1000));
            if (pos < configEnd && ReadDescriptor(d, ref pos, configEnd, out tag, out len) && tag == 5)
            {
                ParseAudioSpecificConfig(d, pos, Math.Min(pos + len, end), lines);
            }
        }

        private static bool ReadDescriptor(byte[] d, ref int pos, int end, out int tag, out int len)
        {
            tag = 0; len = 0;
            if (pos >= end) return false;
            tag = d[pos++];
            for (int i = 0; i < 4 && pos < end; i++)
            {
                int b = d[pos++];
                len = (len << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return pos + len <= end;
        }

        private sealed class Bits
        {
            private readonly byte[] _d; private readonly int _end; private int _bit;
            public Bits(byte[] d, int start, int end) { _d = d; _bit = start * 8; _end = end; }
            public bool Ok { get { return _bit <= _end * 8; } }
            public int Read(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++)
                {
                    int byteIndex = _bit >> 3;
                    int bit = byteIndex < _end ? (_d[byteIndex] >> (7 - (_bit & 7))) & 1 : 0;
                    v = (v << 1) | bit;
                    _bit++;
                }
                return v;
            }
        }

        private static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        private static void ParseAudioSpecificConfig(byte[] d, int start, int end, List<string> lines)
        {
            var bits = new Bits(d, start, end);
            int aot = ReadAot(bits);
            int rate = ReadRate(bits);
            int config = bits.Read(4);
            string extension = "";
            if (aot == 5 || aot == 29)
            {
                ReadRate(bits);
                extension = aot == 5 ? " mit SBR" : " mit PS";
                aot = ReadAot(bits);
            }
            lines.Add(string.Format("  AAC-Konfiguration: Objekttyp {0} ({1}{2}), {3} Hz, Kanal-Konfiguration {4} ({5})",
                aot, AotName(aot), extension, rate, config, ConfigName(config)));
            bool ga = aot == 1 || aot == 2 || aot == 3 || aot == 4 || aot == 6 || aot == 7 || (aot >= 17 && aot <= 23);
            if (!ga || config != 0) return;
            bits.Read(1); // frameLengthFlag
            if (bits.Read(1) == 1) bits.Read(14); // dependsOnCoreCoder
            bits.Read(1); // extensionFlag
            ParsePce(bits, lines);
        }

        private static void ParsePce(Bits bits, List<string> lines)
        {
            bits.Read(4); bits.Read(2); bits.Read(4);
            int front = bits.Read(4), side = bits.Read(4), back = bits.Read(4), lfe = bits.Read(2), assoc = bits.Read(3), cc = bits.Read(4);
            if (bits.Read(1) == 1) bits.Read(4);
            if (bits.Read(1) == 1) bits.Read(4);
            if (bits.Read(1) == 1) bits.Read(3);
            Func<int, string> elements = n =>
            {
                var list = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    bool cpe = bits.Read(1) == 1;
                    int tag = bits.Read(4);
                    list.Add((cpe ? "CPE" : "SCE") + tag);
                }
                return "[" + string.Join(", ", list.ToArray()) + "]";
            };
            string f = elements(front), s = elements(side), b = elements(back);
            lines.Add(string.Format("  PCE (Programm-Konfiguration): vorne {0}, seitlich {1}, hinten {2}, LFE {3}, sonstige {4}/{5}",
                f, s, b, lfe, assoc, cc));
            if (!bits.Ok) lines.Add("  PCE: Daten unvollständig");
            // The comment field of the PCE is free text and is deliberately not read.
        }

        private static int ReadAot(Bits bits)
        {
            int aot = bits.Read(5);
            return aot == 31 ? 32 + bits.Read(6) : aot;
        }

        private static int ReadRate(Bits bits)
        {
            int index = bits.Read(4);
            return index == 15 ? bits.Read(24) : index < Rates.Length ? Rates[index] : 0;
        }

        private static string AotName(int aot)
        {
            switch (aot)
            {
                case 1: return "AAC Main";
                case 2: return "AAC LC";
                case 4: return "AAC LTP";
                case 5: return "SBR";
                case 23: return "AAC LD";
                case 29: return "PS";
                case 39: return "AAC ELD";
                case 42: return "USAC";
                default: return "anderer";
            }
        }

        private static string ConfigName(int config)
        {
            switch (config)
            {
                case 0: return "aus PCE";
                case 1: return "1: C";
                case 2: return "2: L R";
                case 3: return "3: C L R";
                case 4: return "4: C L R hinten-C";
                case 5: return "5: C L R Ls Rs";
                case 6: return "6: 5.1";
                case 7: return "7: 7.1";
                default: return "andere";
            }
        }

        private static bool IsSphericalV1(byte[] d, int pos)
        {
            byte[] id = { 0xff, 0xcc, 0x82, 0x63, 0xf8, 0x55, 0x4a, 0x93, 0x88, 0x14, 0x58, 0x7a, 0x02, 0x52, 0x1f, 0xdd };
            for (int i = 0; i < 16; i++) if (d[pos + i] != id[i]) return false;
            return true;
        }

        private static string Safe4(string s)
        {
            if (s == null) return "?";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c >= 0x20 && c < 0x7F ? c : '?');
            return sb.ToString();
        }

        private static byte[] ReadAt(FileStream fs, long pos, int count)
        {
            fs.Position = pos;
            var buffer = new byte[count];
            int done = 0;
            while (done < count)
            {
                int n = fs.Read(buffer, done, count - done);
                if (n <= 0) break;
                done += n;
            }
            return buffer;
        }

        private static long BE32(byte[] d, int p) { return ((long)d[p] << 24) | ((long)d[p + 1] << 16) | ((long)d[p + 2] << 8) | d[p + 3]; }
        private static int BE16(byte[] d, int p) { return (d[p] << 8) | d[p + 1]; }
        private static ulong BE64(byte[] d, int p) { return ((ulong)BE32(d, p) << 32) | (ulong)BE32(d, p + 4); }
    }

    // Decodes one sound track with ffmpeg (float32, channels exactly as the decoder delivers them, which is what Orbivo
    // gets) and sums it up. Only totals over the whole analysed duration are reported, never a time course.
    public static class ChannelStats
    {
        private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

        public static List<string> Run(string ffmpeg, string file, int audioIndex, int channels, int sampleRate, double maxSeconds)
        {
            var lines = new List<string>();
            if (channels < 1 || channels > 64 || sampleRate < 1000) { lines.Add("  Kanalanalyse: Kanalzahl oder Samplerate unbekannt."); return lines; }

            var info = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = string.Format(CultureInfo.InvariantCulture,
                    "-nostdin -hide_banner -loglevel quiet -i \"{0}\" -map 0:a:{1} -t {2} -vn -sn -dn -f f32le -c:a pcm_f32le pipe:1",
                    file, audioIndex, maxSeconds),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            int n = channels;
            var sum2 = new double[n];
            var peak = new double[n];
            var cross = new double[n, n];
            var loudest = new long[n];
            long frames = 0;

            // Per 100 ms window (first-order assumption, channel 0 = W).
            int window = Math.Max(1, sampleRate / 10);
            var w2 = new double[n];
            bool foa = n >= 4;
            var ratioHistogram = new double[RatioEdges.Length + 1];    // |I| / W²
            var energyHistogram = new double[EnergyEdges.Length + 1];  // (X²+Y²+Z²) / W²
            var sectors = new double[10];            // 8 horizontal sectors + up + down, energy weighted
            double weightTotal = 0;
            double ix = 0, iy = 0, iz = 0, wE = 0, dirE = 0;
            int inWindow = 0;
            int silentWindows = 0, windows = 0;

            using (Process p = Process.Start(info))
            {
                p.ErrorDataReceived += (s, e) => { }; // discarded: ffmpeg messages may contain the path
                p.BeginErrorReadLine();
                Stream stream = p.StandardOutput.BaseStream;
                var buffer = new byte[4 * n * 4096];
                int filled = 0;
                var frame = new float[n];
                while (true)
                {
                    int read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0) break;
                    filled += read;
                    int frameBytes = 4 * n;
                    int whole = filled / frameBytes;
                    for (int f = 0; f < whole; f++)
                    {
                        int offset = f * frameBytes;
                        double best = -1; int bestChannel = 0;
                        for (int c = 0; c < n; c++)
                        {
                            float v = BitConverter.ToSingle(buffer, offset + 4 * c);
                            frame[c] = v;
                            double a = Math.Abs(v);
                            if (a > peak[c]) peak[c] = a;
                            sum2[c] += v * (double)v;
                            w2[c] += v * (double)v;
                            if (a > best) { best = a; bestChannel = c; }
                        }
                        for (int i = 0; i < n; i++)
                            for (int j = i + 1; j < n; j++)
                                cross[i, j] += frame[i] * (double)frame[j];
                        if (best > 1e-6) loudest[bestChannel]++;

                        if (foa)
                        {
                            double w = frame[0], y = frame[1], z = frame[2], x = frame[3];
                            wE += w * w; ix += w * x; iy += w * y; iz += w * z; dirE += x * x + y * y + z * z;
                        }

                        frames++;
                        if (++inWindow == window)
                        {
                            windows++;
                            if (foa)
                            {
                                double wRms = Math.Sqrt(wE / window);
                                if (wRms > 0.003) // about -50 dBFS
                                {
                                    double weight = wE;
                                    weightTotal += weight;
                                    double magnitude = Math.Sqrt(ix * ix + iy * iy + iz * iz);
                                    ratioHistogram[Bin(magnitude / wE, RatioEdges)] += weight;
                                    energyHistogram[Bin(dirE / wE, EnergyEdges)] += weight;
                                    double azimuth = Math.Atan2(iy, ix) * 180 / Math.PI;      // + = left (Orbivo yaw)
                                    double elevation = Math.Atan2(iz, Math.Sqrt(ix * ix + iy * iy)) * 180 / Math.PI;
                                    int sector;
                                    if (elevation > 45) sector = 8;
                                    else if (elevation < -45) sector = 9;
                                    else sector = (int)Math.Floor(((azimuth + 22.5) % 360 + 360) % 360 / 45) % 8;
                                    sectors[sector] += weight;
                                }
                                else silentWindows++;
                            }
                            inWindow = 0; wE = 0; ix = 0; iy = 0; iz = 0; dirE = 0;
                            Array.Clear(w2, 0, n);
                        }
                    }
                    int rest = filled - whole * frameBytes;
                    Buffer.BlockCopy(buffer, whole * frameBytes, buffer, 0, rest);
                    filled = rest;
                }
                p.WaitForExit();
                if (p.ExitCode != 0) lines.Add(string.Format("  ffmpeg meldete beim Dekodieren Code {0} (Meldung absichtlich nicht übernommen).", p.ExitCode));
            }

            if (frames == 0) { lines.Add("  Kanalanalyse: keine Samples dekodiert."); return lines; }
            lines.Add(string.Format(De, "  Analysiert: {0:0.0} s ({1} Kanäle, Reihenfolge wie vom Decoder geliefert)", frames / (double)sampleRate, n));

            int strongest = 0;
            for (int c = 0; c < n; c++) if (sum2[c] > sum2[strongest]) strongest = c;
            for (int c = 0; c < n; c++)
            {
                double rms = Math.Sqrt(sum2[c] / frames);
                lines.Add(string.Format(De, "  Kanal {0}: RMS {1} dBFS, Spitze {2} dBFS, relativ zu Kanal 0 {3} dB, lautester Kanal in {4:0.0} % der Samples",
                    c, Db(rms), Db(peak[c]), c == 0 ? "±0,0" : DbRatio(sum2[c], sum2[0]), 100.0 * loudest[c] / frames));
            }
            lines.Add(string.Format("  Energiereichster Kanal: {0}", strongest));

            var header = new StringBuilder("  Korrelation      ");
            for (int j = 0; j < n && j < 16; j++) header.Append(string.Format("{0,6}", "K" + j));
            lines.Add(header.ToString());
            for (int i = 0; i < n && i < 16; i++)
            {
                var row = new StringBuilder(string.Format("    Kanal {0,-2}       ", i));
                for (int j = 0; j < n && j < 16; j++)
                {
                    double r;
                    if (i == j) r = 1;
                    else
                    {
                        double c = i < j ? cross[i, j] : cross[j, i];
                        double denominator = Math.Sqrt(sum2[i] * sum2[j]);
                        r = denominator > 0 ? c / denominator : 0;
                    }
                    row.Append(string.Format(De, "{0,6:+0.00;-0.00;0.00}", r));
                }
                lines.Add(row.ToString());
            }

            if (foa && weightTotal > 0)
            {
                lines.Add("  Annahme für die folgenden Werte: Kanal 0 = W, 1 = Y, 2 = Z, 3 = X (AmbiX/ACN).");
                lines.Add(string.Format(De, "  Fenster zu 100 ms: {0}, davon zu leise (unter -50 dBFS auf Kanal 0): {1}", windows, silentWindows));
                lines.Add("  Energie (K1²+K2²+K3²)/K0² je Fenster, energiegewichtet [1 ≈ SN3D/AmbiX, 2 ≈ FuMa, 3 ≈ N3D]:");
                lines.Add("    " + Histogram(energyHistogram, weightTotal, EnergyEdges));
                lines.Add("  Gerichtetheit |Intensität|/K0² je Fenster, energiegewichtet [≤ 1 bei SN3D, bis 1,41 bei FuMa, bis 1,73 bei N3D]:");
                lines.Add("    " + Histogram(ratioHistogram, weightTotal, RatioEdges));
                string[] names = { "vorne", "vorne links", "links", "hinten links", "hinten", "hinten rechts", "rechts", "vorne rechts", "oben", "unten" };
                var parts = new List<string>();
                for (int s = 0; s < 10; s++) parts.Add(string.Format(De, "{0} {1:0} %", names[s], 100 * sectors[s] / weightTotal));
                lines.Add("  Hauptrichtung je Fenster, Anteil an der Energie (nur Summe, kein Zeitverlauf):");
                lines.Add("    " + string.Join(", ", parts.ToArray()));
            }
            return lines;
        }

        // Class edges centred on the values that tell the normalisations apart (see the report lines).
        private static readonly double[] EnergyEdges = { 0.75, 1.25, 1.75, 2.25, 2.75, 3.25 };
        private static readonly double[] RatioEdges = { 0.5, 0.9, 1.1, 1.3, 1.5, 1.8 };

        private static int Bin(double value, double[] edges)
        {
            int i = 0;
            while (i < edges.Length && value >= edges[i]) i++;
            return i;
        }

        private static string Histogram(double[] bins, double total, double[] edges)
        {
            var parts = new List<string>();
            for (int i = 0; i <= edges.Length; i++)
            {
                string range = i == 0
                    ? string.Format(De, "<{0:0.##}", edges[0])
                    : i == edges.Length
                        ? string.Format(De, "≥{0:0.##}", edges[i - 1])
                        : string.Format(De, "{0:0.##}–{1:0.##}", edges[i - 1], edges[i]);
                parts.Add(string.Format(De, "{0}: {1:0} %", range, 100 * bins[i] / total));
            }
            return string.Join(", ", parts.ToArray());
        }

        private static string Db(double amplitude)
        {
            return amplitude <= 1e-9 ? "-∞" : (20 * Math.Log10(amplitude)).ToString("0.0", De);
        }

        private static string DbRatio(double energy, double reference)
        {
            if (reference <= 0 || energy <= 0) return "–";
            return (10 * Math.Log10(energy / reference)).ToString("+0.0;-0.0;±0.0", De);
        }
    }
}
'@

function Find-FFmpeg {
    $candidates = @()
    if ($FFmpegDir) { $candidates += $FFmpegDir }
    $candidates += (Join-Path $PSScriptRoot 'ffmpeg')
    $candidates += $PSScriptRoot
    $candidates += (Join-Path $PSScriptRoot '..\..\native\win-x64')
    foreach ($dir in $candidates) {
        $probe = Join-Path $dir 'ffprobe.exe'
        $mpeg = Join-Path $dir 'ffmpeg.exe'
        if ((Test-Path -LiteralPath $probe) -and (Test-Path -LiteralPath $mpeg)) {
            return @{ Probe = (Resolve-Path -LiteralPath $probe).Path; Mpeg = (Resolve-Path -LiteralPath $mpeg).Path }
        }
    }
    throw 'ffprobe.exe und ffmpeg.exe nicht gefunden. Sie gehören in den Unterordner "ffmpeg" neben diesem Skript.'
}

function Select-Files {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = 'Video für die Tonanalyse wählen'
    $dialog.Filter = 'Videos (*.mp4;*.mov;*.mkv;*.webm)|*.mp4;*.mov;*.mkv;*.webm|Alle Dateien (*.*)|*.*'
    $dialog.Multiselect = $true
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return @() }
    return $dialog.FileNames
}

function Get-Value($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# Only these side data fields are copied (all numbers or fixed FFmpeg names, never free text).
$sideDataKeys = @('side_data_type', 'projection', 'yaw', 'pitch', 'roll', 'bound_left', 'bound_top', 'bound_right',
    'bound_bottom', 'padding', 'type', 'inverted', 'view', 'primary_eye', 'rotation')

function Format-Probe([string]$ffprobe, [string]$file) {
    $lines = New-Object System.Collections.Generic.List[string]
    $entries = 'format=format_name,duration,bit_rate,nb_streams:stream=index,codec_type,codec_name,profile,sample_fmt,' +
        'sample_rate,channels,channel_layout,bit_rate,width,height,avg_frame_rate,pix_fmt,color_space,color_transfer,' +
        'color_primaries,bits_per_raw_sample:stream_disposition=default:stream_side_data'
    $json = & $ffprobe -v quiet -of json -show_entries $entries -i $file 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) {
        $lines.Add("ffprobe konnte die Datei nicht lesen (Code $LASTEXITCODE, Meldung absichtlich nicht übernommen).")
        return @{ Lines = $lines; Audio = @() }
    }
    $data = ($json -join "`n") | ConvertFrom-Json
    $format = Get-Value $data 'format'
    $duration = Get-Value $format 'duration'
    $durationText = if ($duration) { '{0:0} s' -f [double]::Parse($duration, [Globalization.CultureInfo]::InvariantCulture) } else { 'unbekannt' }
    $lines.Add(('Container: {0}, Dauer {1}, Gesamtbitrate {2} kb/s, {3} Streams' -f (Get-Value $format 'format_name'), $durationText,
        [math]::Round([double](Get-Value $format 'bit_rate') / 1000), (Get-Value $format 'nb_streams')))

    # The layout as the container states it (before FFmpeg probes the decoder, which replaces e.g. "ambisonic 1"
    # from SA3D by the AAC layout "4.0"). Orbivo reads this one.
    $containerLayouts = @{}
    $raw = & $ffprobe -v quiet -nofind_stream_info -of json -show_entries 'stream=index,channel_layout' -i $file 2>$null
    if ($LASTEXITCODE -eq 0 -and $raw) {
        foreach ($s in @(Get-Value (($raw -join "`n") | ConvertFrom-Json) 'streams')) {
            if ($null -ne $s) { $containerLayouts["$(Get-Value $s 'index')"] = Get-Value $s 'channel_layout' }
        }
    }

    $audio = @()
    $audioIndex = 0
    foreach ($stream in @(Get-Value $data 'streams')) {
        if ($null -eq $stream) { continue }
        $type = Get-Value $stream 'codec_type'
        $default = Get-Value (Get-Value $stream 'disposition') 'default'
        $bitrate = Get-Value $stream 'bit_rate'
        $bitrateText = if ($bitrate) { '{0} kb/s' -f [math]::Round([double]$bitrate / 1000) } else { 'Bitrate unbekannt' }
        if ($type -eq 'audio') {
            $lines.Add(('Stream {0} (Ton {1}): {2} {3}, {4} Hz, {5} Kanäle, Layout Decoder "{6}", Layout Container "{7}", {8}, {9}{10}' -f
                (Get-Value $stream 'index'), $audioIndex, (Get-Value $stream 'codec_name'), (Get-Value $stream 'profile'),
                (Get-Value $stream 'sample_rate'), (Get-Value $stream 'channels'), (Get-Value $stream 'channel_layout'),
                $containerLayouts["$(Get-Value $stream 'index')"], (Get-Value $stream 'sample_fmt'), $bitrateText,
                $(if ($default -eq 1) { ', Standardspur' } else { '' })))
            $audio += @{ Index = $audioIndex; Channels = [int](Get-Value $stream 'channels'); Rate = [int](Get-Value $stream 'sample_rate') }
            $audioIndex++
        }
        elseif ($type -eq 'video') {
            $lines.Add(('Stream {0} (Bild): {1} {2}, {3} × {4}, {5} fps, {6}, Farbe {7}/{8}/{9}' -f
                (Get-Value $stream 'index'), (Get-Value $stream 'codec_name'), (Get-Value $stream 'profile'),
                (Get-Value $stream 'width'), (Get-Value $stream 'height'), (Get-Value $stream 'avg_frame_rate'),
                (Get-Value $stream 'pix_fmt'), (Get-Value $stream 'color_space'), (Get-Value $stream 'color_transfer'),
                (Get-Value $stream 'color_primaries')))
        }
        else {
            $lines.Add(('Stream {0}: {1}' -f (Get-Value $stream 'index'), $type))
        }
        foreach ($side in @(Get-Value $stream 'side_data_list')) {
            if ($null -eq $side) { continue }
            $parts = @()
            foreach ($key in $sideDataKeys) {
                $value = Get-Value $side $key
                if ($null -ne $value -and "$value" -match '^[\w .,+\-/]{0,40}$') { $parts += "$key=$value" }
            }
            if ($parts.Count -gt 0) { $lines.Add('  Side Data: ' + ($parts -join ', ')) }
        }
    }
    return @{ Lines = $lines; Audio = $audio }
}

try {
    Add-Type -TypeDefinition $csharp -Language CSharp
    $tools = Find-FFmpeg
    if (-not $Path -or $Path.Count -eq 0) { $Path = Select-Files }
    if (-not $Path -or $Path.Count -eq 0) { Write-Host 'Keine Datei gewählt.'; exit 1 }

    $report = New-Object System.Collections.Generic.List[string]
    $report.Add('Orbivo-Tonanalyse')
    $report.Add('Enthält nur technische Kennwerte: keine Datei- oder Ordnernamen, keine Metadaten-Texte (Kamera, Software,')
    $report.Add('Datum, Ort, Sprache), keine Tonausschnitte, kein zeitlicher Verlauf.')
    $report.Add(('FFmpeg: {0}' -f ((& $tools.Probe -v quiet -version 2>$null | Select-Object -First 1) -replace '^ffprobe version (\S+).*$', '$1')))
    $report.Add(('Analysedauer je Tonspur: höchstens {0} s' -f $MaxSeconds))

    $number = 0
    foreach ($file in $Path) {
        $number++
        $report.Add('')
        $report.Add("=== Datei $number ===")
        Write-Host "Analysiere Datei $number von $($Path.Count) ..."
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $report.Add('Datei nicht gefunden.'); continue }
        $full = (Resolve-Path -LiteralPath $file).Path
        $report.Add(('Größe: {0:0.0} MB, Endung {1}' -f ((Get-Item -LiteralPath $full).Length / 1MB), [IO.Path]::GetExtension($full).ToLowerInvariant()))

        $probe = Format-Probe $tools.Probe $full
        $report.AddRange([string[]]$probe.Lines)

        $report.Add('-- Boxen (MP4/MOV) --')
        try { $report.AddRange([string[]][OrbivoTonanalyse.Mp4Scan]::Scan($full)) }
        catch { $report.Add('Box-Analyse fehlgeschlagen: ' + $_.Exception.GetType().Name) }

        foreach ($track in $probe.Audio) {
            $report.Add("-- Kanalanalyse Ton $($track.Index) --")
            Write-Host "  Kanalanalyse Ton $($track.Index) ($($track.Channels) Kanäle) ..."
            try { $report.AddRange([string[]][OrbivoTonanalyse.ChannelStats]::Run($tools.Mpeg, $full, $track.Index, $track.Channels, $track.Rate, $MaxSeconds)) }
            catch { $report.Add('  Kanalanalyse fehlgeschlagen: ' + $_.Exception.GetType().Name) }
        }
    }

    $folder = $PSScriptRoot
    $probeFile = Join-Path $folder '.schreibtest'
    try { [IO.File]::WriteAllText($probeFile, ''); Remove-Item -LiteralPath $probeFile } catch { $folder = [Environment]::GetFolderPath('MyDocuments') }
    $target = Join-Path $folder 'Tonanalyse-Ergebnis.txt'
    $counter = 2
    while (Test-Path -LiteralPath $target) { $target = Join-Path $folder "Tonanalyse-Ergebnis-$counter.txt"; $counter++ }
    [IO.File]::WriteAllLines($target, $report, (New-Object System.Text.UTF8Encoding($true)))

    Write-Host ''
    $report | ForEach-Object { Write-Host $_ }
    Write-Host ''
    Write-Host "Ergebnis gespeichert: $target"
    Write-Host 'Bitte vor dem Weitergeben durchlesen.'
    exit 0
}
catch {
    Write-Host ('Fehler: ' + $_.Exception.Message)
    exit 1
}
