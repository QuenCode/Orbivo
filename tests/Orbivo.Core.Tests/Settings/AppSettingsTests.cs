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
        Assert.Equal(new AppSettings(), settings);
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
            HeadSampleRateHz: 30);
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
        Assert.Equal(0, settings.HeadSampleRateHz);
    }
}
