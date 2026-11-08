using System.Text.Json;
using System.Text.Json.Serialization;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Settings;

/// <summary>
/// Operator settings that survive a restart (docs/DECISIONS.md, M5 and M8): sound output device, volume and their lock,
/// idle view and recording. Mute and recenter are not kept on purpose: after a restart the sound is on and the video's
/// own front applies. M9 adds more (last playlist).
/// </summary>
/// <param name="AudioDeviceId">Windows endpoint id of the sound output, null = Windows default.</param>
/// <param name="Volume">Volume level 0–1 (before the loudness curve).</param>
/// <param name="SoundLocked">
/// True: output device and volume are locked in the operator window until the operator unlocks them.
/// </param>
/// <param name="IdleView">What the headset shows while no video runs (F7).</param>
/// <param name="IdleImagePath">Still image for <see cref="Playback.IdleView.Image"/>, or null.</param>
/// <param name="RecordingEnabled">True: head log and event log are written (F50–F52), from the start of the app on.</param>
/// <param name="RecordingFolder">Folder for the recordings, null = <see cref="DefaultRecordingFolder"/>.</param>
/// <param name="HeadSampleRateHz">Rows per second of the head log; 0 = every frame (90 Hz in the headset).</param>
public sealed record AppSettings(
    string? AudioDeviceId = null,
    float Volume = AppSettings.DefaultVolume,
    bool SoundLocked = false,
    IdleView IdleView = IdleView.Black,
    string? IdleImagePath = null,
    bool RecordingEnabled = false,
    string? RecordingFolder = null,
    int HeadSampleRateHz = 0)
{
    public const float DefaultVolume = 0.8f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Default folder of the recordings: <c>Documents\Orbivo\Aufzeichnungen</c>.</summary>
    public static string DefaultRecordingFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Orbivo",
        "Aufzeichnungen");

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
                IdleView = Enum.IsDefined(settings.IdleView) ? settings.IdleView : IdleView.Black,
                IdleImagePath = string.IsNullOrWhiteSpace(settings.IdleImagePath) ? null : settings.IdleImagePath,
                RecordingFolder = string.IsNullOrWhiteSpace(settings.RecordingFolder) ? null : settings.RecordingFolder,
                HeadSampleRateHz = Math.Clamp(settings.HeadSampleRateHz, 0, 1000),
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
