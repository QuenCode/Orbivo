using System.Text.Json;
using System.Text.Json.Serialization;
using Orbivo.Core.Localization;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Settings;

/// <summary>
/// Operator settings that survive a restart (docs/DECISIONS.md, M5 and M8): sound output device, volume and their lock,
/// idle view, recording, since M9 the playlist with its switches and the fade duration, and since M11 the language.
/// Mute and recenter are not kept on purpose: after a restart the sound is on and the video's own front applies.
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
/// <param name="RecordingPerVideo">"Nur bei laufendem Video": one pair of files per video, nothing written while no video plays.</param>
/// <param name="Playlist">Full paths of the playlist in its order (M9), null = empty. The same file may appear more than once.</param>
/// <param name="PlaylistSelected">Index of the entry that was loaded last, selected (not loaded) after a restart; -1 = none.</param>
/// <param name="AutoAdvance">"Automatisch weiter zum nächsten Video" (F3).</param>
/// <param name="Loop">"Aktuelles Video wiederholen" (F3).</param>
/// <param name="FadeSeconds">Fade duration (F5), 0.1–30 s.</param>
/// <param name="Language">
/// "en" or "de" (M11). <see cref="Load"/> always fills it: English without a settings file (very first start), German
/// for a file from before M11, English for an unknown value. Null only in a record made in code (= English).
/// </param>
public sealed record AppSettings(
    string? AudioDeviceId = null,
    float Volume = AppSettings.DefaultVolume,
    bool SoundLocked = false,
    IdleView IdleView = IdleView.Black,
    string? IdleImagePath = null,
    bool RecordingEnabled = false,
    string? RecordingFolder = null,
    int HeadSampleRateHz = AppSettings.DefaultHeadSampleRateHz,
    bool RecordingPerVideo = false,
    IReadOnlyList<string>? Playlist = null,
    int PlaylistSelected = -1,
    bool AutoAdvance = true,
    bool Loop = false,
    double FadeSeconds = AppSettings.DefaultFadeSeconds,
    string? Language = null)
{
    public const float DefaultVolume = 0.8f;

    public const double DefaultFadeSeconds = 1.0;

    public const double MinFadeSeconds = 0.1;

    public const double MaxFadeSeconds = 30;

    /// <summary>30 rows per second are enough for head movement and keep the files small.</summary>
    public const int DefaultHeadSampleRateHz = 30;

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

    /// <summary>The language of the operator window (English if not set).</summary>
    [JsonIgnore]
    public AppLanguage AppLanguage =>AppLanguages.FromCode(Language) ?? AppLanguage.English;

    /// <summary>
    /// Reads <paramref name="path"/>. A missing file gives the defaults; an unreadable one too, with the reason in
    /// <paramref name="problem"/> (English, for the log; the file is overwritten at the next save).
    /// </summary>
    public static AppSettings Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            return FirstStartDefaults;
        }

        try
        {
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            if (settings is null)
            {
                problem = "The settings file is empty; the defaults apply.";
                return FirstStartDefaults;
            }

            return settings with
            {
                Volume = float.IsFinite(settings.Volume) ? Math.Clamp(settings.Volume, 0f, 1f) : DefaultVolume,
                AudioDeviceId = string.IsNullOrWhiteSpace(settings.AudioDeviceId) ? null : settings.AudioDeviceId,
                IdleView = Enum.IsDefined(settings.IdleView) ? settings.IdleView : IdleView.Black,
                IdleImagePath = string.IsNullOrWhiteSpace(settings.IdleImagePath) ? null : settings.IdleImagePath,
                RecordingFolder = string.IsNullOrWhiteSpace(settings.RecordingFolder) ? null : settings.RecordingFolder,
                HeadSampleRateHz = Math.Clamp(settings.HeadSampleRateHz, 0, 1000),
                Playlist = CleanPlaylist(settings.Playlist),
                PlaylistSelected = Math.Max(settings.PlaylistSelected, -1),
                FadeSeconds = double.IsFinite(settings.FadeSeconds)
                    ? Math.Clamp(settings.FadeSeconds, MinFadeSeconds, MaxFadeSeconds)
                    : DefaultFadeSeconds,
                Language = AppLanguages.ToCode(settings.Language is null
                    ? AppLanguages.BeforeChoice
                    : AppLanguages.FromCode(settings.Language) ?? AppLanguages.FirstStart),
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"Settings not readable ({exception.Message}); the defaults apply.";
            return FirstStartDefaults;
        }
    }

    /// <summary>The defaults, in the language of a very first start.</summary>
    private static AppSettings FirstStartDefaults => new(Language: AppLanguages.ToCode(AppLanguages.FirstStart));

    /// <summary>Drops empty paths from a hand-edited file; an empty list is stored as null.</summary>
    private static string[]? CleanPlaylist(IReadOnlyList<string>? paths)
    {
        string[] clean = paths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? [];
        return clean.Length == 0 ? null : clean;
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
