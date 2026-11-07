using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbivo.Core.Settings;

/// <summary>
/// Operator settings that survive a restart (docs/DECISIONS.md, M5): sound output device, volume and their lock. Mute is not kept
/// on purpose: after a restart the sound is on. M9 adds more (format per video, last playlist).
/// </summary>
/// <param name="AudioDeviceId">Windows endpoint id of the sound output, null = Windows default.</param>
/// <param name="Volume">Volume level 0–1 (before the loudness curve).</param>
/// <param name="SoundLocked">
/// True: output device and volume are locked in the operator window until the operator unlocks them.
/// </param>
public sealed record AppSettings(string? AudioDeviceId = null, float Volume = AppSettings.DefaultVolume, bool SoundLocked = false)
{
    public const float DefaultVolume = 0.8f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Default file: <c>%LOCALAPPDATA%\Orbivo\settings.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Orbivo",
        "settings.json");

    /// <summary>
    /// Reads <paramref name="path"/>. A missing file gives the defaults; an unreadable one too, with the reason in
    /// <paramref name="problem"/> (to log and show; the file is overwritten at the next save).
    /// </summary>
    public static AppSettings Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            if (settings is null)
            {
                problem = "Die Einstellungsdatei ist leer.";
                return new AppSettings();
            }

            return settings with
            {
                Volume = float.IsFinite(settings.Volume) ? Math.Clamp(settings.Volume, 0f, 1f) : DefaultVolume,
                AudioDeviceId = string.IsNullOrWhiteSpace(settings.AudioDeviceId) ? null : settings.AudioDeviceId,
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"Einstellungen nicht lesbar ({exception.Message}); es gelten die Vorgaben.";
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings (through a temporary file, so a crash never leaves half a file).</summary>
    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
