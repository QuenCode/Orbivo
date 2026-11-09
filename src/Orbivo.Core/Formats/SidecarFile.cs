using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbivo.Core.Audio;
using Orbivo.Core.Geometry;

namespace Orbivo.Core.Formats;

/// <summary>
/// The sidecar <c>&lt;video file with extension&gt;.orbivo.json</c> next to a video (F13, F14; schema in
/// docs/ARCHITECTURE.md). Holds the operator's format choices; null fields mean automatic.
/// </summary>
/// <remarks>
/// Fields Orbivo does not know (or does not handle yet, such as <c>externalAudio</c>) are kept when saving. Invalid
/// fields are ignored with a warning. A file that cannot be read at all is copied to <c>.bak</c> before it is
/// overwritten. A file from a newer Orbivo (<c>version</c> above <see cref="CurrentVersion"/>) is read but never
/// written, nor is one that could not be opened.
/// </remarks>
public sealed class SidecarFile
{
    public const int CurrentVersion = 1;
    public const string Extension = ".orbivo.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly string[] KnownKeys =
        ["version", "projection", "stereoLayout", "swapEyes", "fisheye", "cubemapLayout", "orientationOffsetDeg", "audioFixedToPicture", "stereoWidthDeg", "audioMode"];

    private readonly JsonObject _root;

    private SidecarFile(string path, JsonObject root, FormatOverrides overrides, IReadOnlyList<string> warnings, bool broken, bool readOnly)
    {
        Path = path;
        _root = root;
        Overrides = overrides;
        Warnings = warnings;
        IsBroken = broken;
        IsReadOnly = readOnly;
    }

    /// <summary>Full path of the sidecar file.</summary>
    public string Path { get; }

    /// <summary>What the file says.</summary>
    public FormatOverrides Overrides { get; private set; }

    /// <summary>Problems found while reading (German, for the operator).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The file exists but is not valid JSON; it is backed up before the first save.</summary>
    public bool IsBroken { get; private set; }

    /// <summary>The file comes from a newer Orbivo or could not be read, and is not written.</summary>
    public bool IsReadOnly { get; }

    /// <summary>True if the file is on disk.</summary>
    public bool Exists => File.Exists(Path);

    /// <summary>Path of the sidecar of <paramref name="videoPath"/>.</summary>
    public static string PathFor(string videoPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(videoPath);
        return videoPath + Extension;
    }

    /// <summary>
    /// Reads the sidecar of <paramref name="videoPath"/>. Never throws for a missing, broken or unreadable file; the
    /// problem is in <see cref="Warnings"/> and the overrides are then empty.
    /// </summary>
    public static SidecarFile Load(string videoPath)
    {
        string path = PathFor(videoPath);
        var warnings = new List<string>();
        if (!File.Exists(path))
        {
            return new SidecarFile(path, [], FormatOverrides.None, warnings, broken: false, readOnly: false);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Sidecar {System.IO.Path.GetFileName(path)} nicht lesbar ({exception.Message}); Format automatisch.");
            return new SidecarFile(path, [], FormatOverrides.None, warnings, broken: false, readOnly: true);
        }
        catch (JsonException exception)
        {
            warnings.Add($"Sidecar {System.IO.Path.GetFileName(path)} ist kein gültiges JSON ({exception.Message}); wird ignoriert und bei der nächsten Änderung als .bak gesichert.");
            return new SidecarFile(path, [], FormatOverrides.None, warnings, broken: true, readOnly: false);
        }

        if (node is not JsonObject root)
        {
            warnings.Add($"Sidecar {System.IO.Path.GetFileName(path)} enthält kein JSON-Objekt; wird ignoriert und bei der nächsten Änderung als .bak gesichert.");
            return new SidecarFile(path, [], FormatOverrides.None, warnings, broken: true, readOnly: false);
        }

        bool newer = false;
        if (root["version"] is JsonValue versionValue && versionValue.TryGetValue(out int version) && version > CurrentVersion)
        {
            newer = true;
            warnings.Add($"Sidecar stammt von einer neueren Orbivo-Version (Version {version}); Änderungen werden nicht gespeichert.");
        }

        var overrides = new FormatOverrides(
            ReadEnum<ProjectionType>(root, "projection", warnings),
            ReadEnum<StereoLayout>(root, "stereoLayout", warnings),
            ReadBool(root, "swapEyes", warnings),
            ReadCubemap(root, warnings),
            ReadFisheye(root, warnings),
            ReadOffset(root, warnings),
            ReadBool(root, "audioFixedToPicture", warnings),
            ReadStereoWidth(root, warnings),
            ReadEnum<AudioModeChoice>(root, "audioMode", warnings));
        return new SidecarFile(path, root, overrides, warnings, broken: false, readOnly: newer);
    }

    /// <summary>
    /// Stores <paramref name="overrides"/>: writes all known fields (null = automatic) and keeps the others; deletes
    /// the file if nothing is left to store. Writes through a temporary file. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> if the disk refuses, <see cref="InvalidOperationException"/> if
    /// <see cref="IsReadOnly"/>.
    /// </summary>
    public void Save(FormatOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Das Sidecar stammt von einer neueren Orbivo-Version oder ist nicht lesbar und wird nicht überschrieben.");
        }

        if (IsBroken && File.Exists(Path))
        {
            File.Copy(Path, Path + ".bak", overwrite: true);
            IsBroken = false;
        }

        bool hasOtherFields = _root.Any(pair => !KnownKeys.Contains(pair.Key, StringComparer.Ordinal));
        if (overrides.IsEmpty && !hasOtherFields)
        {
            File.Delete(Path);
            Overrides = overrides;
            return;
        }

        var document = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["projection"] = overrides.Projection?.ToString(),
            ["stereoLayout"] = overrides.Layout?.ToString(),
            ["swapEyes"] = overrides.SwapEyes,
            ["fisheye"] = overrides.Fisheye is { } fisheye
                ? new JsonObject
                {
                    ["fovDeg"] = fisheye.FovDegrees,
                    ["centerX"] = fisheye.CenterX,
                    ["centerY"] = fisheye.CenterY,
                    ["radius"] = fisheye.Radius,
                }
                : null,
            ["cubemapLayout"] = WriteCubemap(overrides.Cubemap),
            ["orientationOffsetDeg"] = overrides.Offset is { IsZero: false } offset
                ? new JsonObject { ["yaw"] = offset.YawDegrees, ["pitch"] = offset.PitchDegrees, ["roll"] = offset.RollDegrees }
                : null,
            ["audioFixedToPicture"] = overrides.AudioFixedToPicture,
            ["stereoWidthDeg"] = overrides.StereoWidthDegrees,
            ["audioMode"] = overrides.AudioMode?.ToString(),
        };
        foreach ((string key, JsonNode? value) in _root)
        {
            if (!KnownKeys.Contains(key, StringComparer.Ordinal))
            {
                document[key] = value?.DeepClone();
            }
        }

        string temporary = Path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(temporary, Path, overwrite: true);
        Overrides = overrides;
    }

    private static JsonNode? WriteCubemap(CubemapLayout? layout)
    {
        if (layout is null)
        {
            return null;
        }

        if (layout.PresetName is { } name && layout.PaddingPixels == 0)
        {
            return name;
        }

        return new JsonObject
        {
            ["columns"] = layout.Columns,
            ["rows"] = layout.Rows,
            ["faceOrder"] = layout.FaceOrder,
            ["faceRotation"] = layout.FaceRotation,
            ["padding"] = layout.PaddingPixels,
        };
    }

    private static T? ReadEnum<T>(JsonObject root, string key, List<string> warnings)
        where T : struct, Enum
    {
        JsonNode? node = root[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text)
            && Enum.TryParse(text, ignoreCase: true, out T parsed) && Enum.IsDefined(parsed) && !int.TryParse(text, out _))
        {
            return parsed;
        }

        warnings.Add($"Sidecar: „{key}“ = {node.ToJsonString()} unbekannt (erlaubt: {string.Join(", ", Enum.GetNames<T>())}); ignoriert.");
        return null;
    }

    private static bool? ReadBool(JsonObject root, string key, List<string> warnings)
    {
        JsonNode? node = root[key];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out bool flag))
        {
            return flag;
        }

        warnings.Add($"Sidecar: „{key}“ = {node.ToJsonString()} ist kein true/false; ignoriert.");
        return null;
    }

    private static CubemapLayout? ReadCubemap(JsonObject root, List<string> warnings)
    {
        JsonNode? node = root["cubemapLayout"];
        switch (node)
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue(out string? name):
                if (CubemapLayout.FromPresetName(name) is { } preset)
                {
                    return preset;
                }

                warnings.Add($"Sidecar: Cubemap-Layout „{name}“ unbekannt (erlaubt: {string.Join(", ", CubemapLayout.Presets.Select(p => p.Name))} oder ein Objekt); ignoriert.");
                return null;
            case JsonObject layout:
                int? columns = ReadNumber<int>(layout, "columns");
                int? rows = ReadNumber<int>(layout, "rows");
                string? order = layout["faceOrder"] is JsonValue o && o.TryGetValue(out string? orderText) ? orderText : null;
                string? rotation = layout["faceRotation"] is JsonValue r && r.TryGetValue(out string? rotationText) ? rotationText : "000000";
                int padding = layout["padding"] is null ? 0 : ReadNumber<int>(layout, "padding") ?? -1;
                string? error = "columns/rows fehlen";
                if (columns is { } c && rows is { } n && CubemapLayout.TryValidate(c, n, order, rotation, padding, out error))
                {
                    return new CubemapLayout(c, n, order!, rotation!, padding);
                }

                warnings.Add($"Sidecar: Cubemap-Layout {node.ToJsonString()} ungültig ({error}); ignoriert.");
                return null;
            default:
                warnings.Add($"Sidecar: Cubemap-Layout {node.ToJsonString()} ungültig; ignoriert.");
                return null;
        }
    }

    private static FisheyeParameters? ReadFisheye(JsonObject root, List<string> warnings)
    {
        JsonNode? node = root["fisheye"];
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject fisheye)
        {
            FisheyeParameters standard = FisheyeParameters.Default;
            var parameters = new FisheyeParameters(
                ReadDouble(fisheye, "fovDeg", standard.FovDegrees),
                ReadDouble(fisheye, "centerX", standard.CenterX),
                ReadDouble(fisheye, "centerY", standard.CenterY),
                ReadDouble(fisheye, "radius", standard.Radius));
            if (parameters.TryValidate(out string? error))
            {
                return parameters;
            }

            warnings.Add($"Sidecar: Fisheye-Werte ungültig ({error}); ignoriert.");
            return null;
        }

        warnings.Add($"Sidecar: „fisheye“ = {node.ToJsonString()} ist kein Objekt; ignoriert.");
        return null;
    }

    private static OrientationOffset? ReadOffset(JsonObject root, List<string> warnings)
    {
        JsonNode? node = root["orientationOffsetDeg"];
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject offset)
        {
            double yaw = ReadDouble(offset, "yaw", 0);
            double pitch = ReadDouble(offset, "pitch", 0);
            double roll = ReadDouble(offset, "roll", 0);
            if (new[] { yaw, pitch, roll }.All(v => double.IsFinite(v) && Math.Abs(v) <= OrientationOffset.MaxDegrees))
            {
                return new OrientationOffset(yaw, pitch, roll);
            }
        }

        warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Sidecar: Ausrichtung {node.ToJsonString()} ungültig (je Achse höchstens ±{OrientationOffset.MaxDegrees}°); ignoriert."));
        return null;
    }

    private static double? ReadStereoWidth(JsonObject root, List<string> warnings)
    {
        JsonNode? node = root["stereoWidthDeg"];
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out double width) && Audio.StereoPlacement.IsValidWidth(width))
        {
            return width;
        }

        warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Sidecar: Stereo-Breite {node.ToJsonString()} ungültig (erlaubt {Audio.StereoPlacement.MinWidthDegrees:0}–{Audio.StereoPlacement.MaxWidthDegrees:0}°); ignoriert."));
        return null;
    }

    /// <summary>A number field; NaN if it is there but not a number, <paramref name="fallback"/> if it is missing.</summary>
    private static double ReadDouble(JsonObject parent, string key, double fallback)
    {
        JsonNode? node = parent[key];
        if (node is null)
        {
            return fallback;
        }

        return node is JsonValue value && value.TryGetValue(out double number) ? number : double.NaN;
    }

    private static T? ReadNumber<T>(JsonObject parent, string key)
        where T : struct
    {
        return parent[key] is JsonValue value && value.TryGetValue(out T number) ? number : null;
    }
}
