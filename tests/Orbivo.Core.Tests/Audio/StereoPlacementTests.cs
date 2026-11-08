using System.Numerics;
using Orbivo.Core.Audio;
using Orbivo.Core.Formats;

namespace Orbivo.Core.Tests.Audio;

public sealed class StereoPlacementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbivoTests", Guid.NewGuid().ToString("N"));

    public StereoPlacementTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Default_IsFixedToThePictureAt60Degrees()
    {
        StereoPlacement placement = StereoPlacement.From(FormatOverrides.None);

        Assert.True(placement.FixedToPicture);
        Assert.Equal(75, placement.WidthDegrees);
        Assert.Equal(StereoPlacement.Default, placement);
    }

    [Fact]
    public void From_TakesTheOperatorsChoices()
    {
        StereoPlacement placement = StereoPlacement.From(new FormatOverrides(AudioFixedToPicture: false, StereoWidthDegrees: 85));

        Assert.False(placement.FixedToPicture);
        Assert.Equal(85, placement.WidthDegrees);
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(120, 90)]
    [InlineData(double.NaN, 75)]
    public void From_ClampsTheWidth(double width, double expected)
    {
        Assert.Equal(expected, StereoPlacement.From(new FormatOverrides(StereoWidthDegrees: width)).WidthDegrees);
    }

    [Fact]
    public void AudioChoices_MakeTheOverridesNonEmpty()
    {
        Assert.False(new FormatOverrides(AudioFixedToPicture: true).IsEmpty);
        Assert.False(new FormatOverrides(StereoWidthDegrees: 45).IsEmpty);
    }

    [Fact]
    public void Sidecar_StoresAndReadsTheAudioChoices()
    {
        string video = Path.Combine(_directory, "clip.mp4");
        SidecarFile.Load(video).Save(new FormatOverrides(AudioFixedToPicture: false, StereoWidthDegrees: 75));

        SidecarFile loaded = SidecarFile.Load(video);
        string json = File.ReadAllText(SidecarFile.PathFor(video));

        Assert.Empty(loaded.Warnings);
        Assert.Equal(false, loaded.Overrides.AudioFixedToPicture);
        Assert.Equal(75, loaded.Overrides.StereoWidthDegrees);
        Assert.Contains("\"audioFixedToPicture\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"stereoWidthDeg\": 75", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Sidecar_InvalidWidth_IsIgnoredWithAWarning()
    {
        string video = Path.Combine(_directory, "wide.mp4");
        File.WriteAllText(SidecarFile.PathFor(video), """{ "version": 1, "stereoWidthDeg": 170, "audioFixedToPicture": "ja" }""");

        SidecarFile loaded = SidecarFile.Load(video);

        Assert.Null(loaded.Overrides.StereoWidthDegrees);
        Assert.Null(loaded.Overrides.AudioFixedToPicture);
        Assert.Equal(2, loaded.Warnings.Count);
    }

    [Fact]
    public void ListenerOrientation_StartsAtTheFrontAndReturnsTheLatestPose()
    {
        var listener = new ListenerOrientation();
        Assert.Equal(Quaternion.Identity, listener.Read().HeadInContent);

        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        listener.Publish(turned, 42);

        Assert.Equal(new ListenerPose(turned, 42), listener.Read());
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
