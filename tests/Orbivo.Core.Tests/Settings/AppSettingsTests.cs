using Orbivo.Core.Localization;
using Orbivo.Core.Playback;
using Orbivo.Core.Settings;

namespace Orbivo.Core.Tests.Settings;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orbivo-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "sub", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_GivesTheDefaults()
    {
        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.Null(problem);
        Assert.Null(settings.AudioDeviceId);
        Assert.Equal(AppSettings.DefaultVolume, settings.Volume);
    }

    [Fact]
    public void SaveAndLoad_KeepDeviceAndVolume()
    {
        new AppSettings("{0.0.0.00000000}.{abc}", 0.35f).Save(FilePath);
        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.Null(problem);
        Assert.Equal("{0.0.0.00000000}.{abc}", settings.AudioDeviceId);
        Assert.Equal(0.35f, settings.Volume);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void SaveAndLoad_KeepTheSoundLock()
    {
        new AppSettings(null, 0.5f, SoundLocked: true).Save(FilePath);
        Assert.True(AppSettings.Load(FilePath, out _).SoundLocked);
    }

    [Fact]
    public void OlderFileWithoutTheLock_IsUnlocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "AudioDeviceId": null, "Volume": 0.5 }""");
        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.Null(problem);
        Assert.False(settings.SoundLocked);
        Assert.Equal(0.5f, settings.Volume);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void BrokenFile_GivesTheDefaults_AndSaysWhy(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);
        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.NotNull(problem);
        Assert.Equal(new AppSettings(Language: "en"), settings);
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "AudioDeviceId": "  ", "Volume": 7 }""");
        AppSettings settings = AppSettings.Load(FilePath, out _);
        Assert.Null(settings.AudioDeviceId);
        Assert.Equal(1f, settings.Volume);
    }

    [Fact]
    public void SaveAndLoad_KeepIdleViewAndRecording()
    {
        var saved = new AppSettings(
            IdleView: IdleView.SeeThrough,
            IdleImagePath: @"C:\Bilder\raum.png",
            RecordingEnabled: true,
            RecordingFolder: @"D:\Studie",
            HeadSampleRateHz: 10,
            RecordingPerVideo: true,
            Language: "de");
        saved.Save(FilePath);

        Assert.Contains("\"SeeThrough\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Equal(saved, AppSettings.Load(FilePath, out string? problem));
        Assert.Null(problem);
    }

    [Fact]
    public void FileFromBeforeM8_GivesBlackIdleViewAndNoRecording()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "AudioDeviceId": null, "Volume": 0.5, "SoundLocked": true }""");
        AppSettings settings = AppSettings.Load(FilePath, out _);
        Assert.Equal(IdleView.Black, settings.IdleView);
        Assert.Null(settings.IdleImagePath);
        Assert.False(settings.RecordingEnabled);
        Assert.Equal(30, settings.HeadSampleRateHz);
        Assert.False(settings.RecordingPerVideo);
    }

    [Fact]
    public void SaveAndLoad_KeepPlaylistSwitchesAndFade()
    {
        string[] playlist = [@"D:\Videos\a.mp4", @"D:\Videos\b.mp4", @"D:\Videos\a.mp4"];
        new AppSettings(Playlist: playlist, PlaylistSelected: 2, AutoAdvance: false, Loop: true, FadeSeconds: 2.5).Save(FilePath);

        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.Null(problem);
        Assert.Equal(playlist, settings.Playlist);
        Assert.Equal(2, settings.PlaylistSelected);
        Assert.False(settings.AutoAdvance);
        Assert.True(settings.Loop);
        Assert.Equal(2.5, settings.FadeSeconds);
    }

    [Fact]
    public void FileFromBeforeM9_GivesAnEmptyPlaylistAndTheOldDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "AudioDeviceId": null, "Volume": 0.5, "RecordingPerVideo": true }""");
        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.Null(problem);
        Assert.Null(settings.Playlist);
        Assert.Equal(-1, settings.PlaylistSelected);
        Assert.True(settings.AutoAdvance);
        Assert.False(settings.Loop);
        Assert.Equal(AppSettings.DefaultFadeSeconds, settings.FadeSeconds);
    }

    [Fact]
    public void HandEditedPlaylistAndFade_AreCleaned()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "Playlist": [ "", "  ", "D:\\x.mp4" ], "PlaylistSelected": -7, "FadeSeconds": 900 }""");
        AppSettings settings = AppSettings.Load(FilePath, out _);
        Assert.Equal([@"D:\x.mp4"], settings.Playlist);
        Assert.Equal(-1, settings.PlaylistSelected);
        Assert.Equal(AppSettings.MaxFadeSeconds, settings.FadeSeconds);

        File.WriteAllText(FilePath, """{ "Playlist": [ "" ], "FadeSeconds": 0 }""");
        settings = AppSettings.Load(FilePath, out _);
        Assert.Null(settings.Playlist);
        Assert.Equal(AppSettings.MinFadeSeconds, settings.FadeSeconds);
    }

    [Fact]
    public void VeryFirstStart_IsEnglish()
    {
        AppSettings settings = AppSettings.Load(FilePath, out _);
        Assert.Equal("en", settings.Language);
        Assert.Equal(AppLanguage.English, settings.AppLanguage);
    }

    [Fact]
    public void FileFromBeforeM11_StaysGerman()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "AudioDeviceId": null, "Volume": 0.5 }""");
        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.Null(problem);
        Assert.Equal("de", settings.Language);
        Assert.Equal(AppLanguage.German, settings.AppLanguage);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    [InlineData("DE-de", "de")]
    [InlineData("fr", "en")]
    [InlineData("", "en")]
    public void SavedLanguage_IsKept_UnknownOnesGiveEnglish(string stored, string expected)
    {
        new AppSettings(Language: stored).Save(FilePath);
        AppSettings settings = AppSettings.Load(FilePath, out string? problem);
        Assert.Null(problem);
        Assert.Equal(expected, settings.Language);
        Assert.DoesNotContain("AppLanguage", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }
}
