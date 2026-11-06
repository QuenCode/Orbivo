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
}
