using System.Text.Json.Nodes;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;

namespace Orbivo.Core.Tests.Formats;

public sealed class SidecarFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orbivo-sidecar-" + Guid.NewGuid().ToString("N"));

    public SidecarFileTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string Video => Path.Combine(_directory, "clip.mp4");

    private string SidecarPath => Video + ".orbivo.json";

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void PathFor_KeepsTheVideoExtension()
    {
        Assert.Equal(@"C:\v\clip.mp4.orbivo.json", SidecarFile.PathFor(@"C:\v\clip.mp4"));
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        SidecarFile sidecar = SidecarFile.Load(Video);

        Assert.False(sidecar.Exists);
        Assert.True(sidecar.Overrides.IsEmpty);
        Assert.Empty(sidecar.Warnings);
    }

    [Fact]
    public void Load_ReadsAllFields()
    {
        File.WriteAllText(SidecarPath, """
            {
              "version": 1,
              "projection": "fisheye",
              "stereoLayout": "SideBySide",
              "swapEyes": true,
              "fisheye": { "fovDeg": 200, "centerX": 0.48, "centerY": 0.51, "radius": 0.46 },
              "cubemapLayout": "6x1",
              "orientationOffsetDeg": { "yaw": 30, "pitch": -5, "roll": 2.5 }
            }
            """);

        SidecarFile sidecar = SidecarFile.Load(Video);

        Assert.Empty(sidecar.Warnings);
        Assert.Equal(
            new FormatOverrides(ProjectionType.Fisheye, StereoLayout.SideBySide, true, CubemapLayout.Strip6x1, new FisheyeParameters(200, 0.48, 0.51, 0.46), new OrientationOffset(30, -5, 2.5)),
            sidecar.Overrides);
    }

    [Fact]
    public void Load_NullFields_AreAutomatic()
    {
        File.WriteAllText(SidecarPath, """{ "version": 1, "projection": null, "stereoLayout": "TopBottom", "fisheye": null }""");

        SidecarFile sidecar = SidecarFile.Load(Video);

        Assert.Equal(new FormatOverrides(Layout: StereoLayout.TopBottom), sidecar.Overrides);
    }

    [Fact]
    public void Load_CubemapObject()
    {
        File.WriteAllText(SidecarPath, """{ "cubemapLayout": { "columns": 2, "rows": 3, "faceOrder": "FBLRUD", "faceRotation": "001100", "padding": 4 } }""");

        SidecarFile sidecar = SidecarFile.Load(Video);

        Assert.Equal(new CubemapLayout(2, 3, "fblrud", "001100", 4), sidecar.Overrides.Cubemap);
    }

    [Fact]
    public void Load_InvalidFields_AreIgnoredWithWarnings()
    {
        File.WriteAllText(SidecarPath, """
            {
              "projection": "Pyramid",
              "stereoLayout": 1,
              "swapEyes": "yes",
              "fisheye": { "fovDeg": 10 },
              "cubemapLayout": "4x4",
              "orientationOffsetDeg": { "yaw": 1000 },
              "swapEyesTypo": true
            }
            """);

        SidecarFile sidecar = SidecarFile.Load(Video);

        Assert.True(sidecar.Overrides.IsEmpty);
        Assert.Equal(6, sidecar.Warnings.Count);
    }

    [Fact]
    public void Load_BrokenJson_IsIgnoredAndBackedUpOnSave()
    {
        File.WriteAllText(SidecarPath, "{ not json");

        SidecarFile sidecar = SidecarFile.Load(Video);
        Assert.True(sidecar.IsBroken);
        Assert.True(sidecar.Overrides.IsEmpty);
        Assert.Single(sidecar.Warnings);

        sidecar.Save(new FormatOverrides(SwapEyes: true));

        Assert.Equal("{ not json", File.ReadAllText(SidecarPath + ".bak"));
        Assert.Equal(true, SidecarFile.Load(Video).Overrides.SwapEyes);
    }

    [Fact]
    public void NewerVersion_IsReadButNotWritten()
    {
        File.WriteAllText(SidecarPath, """{ "version": 2, "projection": "Eac" }""");

        SidecarFile sidecar = SidecarFile.Load(Video);

        Assert.True(sidecar.IsReadOnly);
        Assert.Equal(ProjectionType.Eac, sidecar.Overrides.Projection);
        Assert.Throws<InvalidOperationException>(() => sidecar.Save(FormatOverrides.None));
        Assert.Contains("\"version\": 2", File.ReadAllText(SidecarPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_RoundTripsAndWritesAllKnownKeys()
    {
        var overrides = new FormatOverrides(
            ProjectionType.Cubemap, StereoLayout.TopBottom, false, new CubemapLayout(3, 2, "rludfb", "000000", 2), null, new OrientationOffset(-90, 0, 0));

        SidecarFile.Load(Video).Save(overrides);

        Assert.Equal(overrides, SidecarFile.Load(Video).Overrides);
        JsonObject root = JsonNode.Parse(File.ReadAllText(SidecarPath))!.AsObject();
        Assert.Equal(
            ["version", "projection", "stereoLayout", "swapEyes", "fisheye", "cubemapLayout", "orientationOffsetDeg"],
            root.Select(pair => pair.Key));
        Assert.Equal("Cubemap", root["projection"]!.GetValue<string>());
        Assert.Null(root["fisheye"]);
        Assert.False(File.Exists(SidecarPath + ".tmp"));
    }

    [Fact]
    public void Save_PresetIsWrittenByName()
    {
        SidecarFile.Load(Video).Save(new FormatOverrides(Cubemap: CubemapLayout.YouTubeEac));

        Assert.Equal("eac", JsonNode.Parse(File.ReadAllText(SidecarPath))!["cubemapLayout"]!.GetValue<string>());
    }

    [Fact]
    public void Save_KeepsUnknownFields()
    {
        File.WriteAllText(SidecarPath, """
            { "version": 1, "projection": "Eac", "audioMode": { "type": "Ambisonics", "order": 1 }, "externalAudio": null }
            """);

        SidecarFile.Load(Video).Save(FormatOverrides.None);

        JsonObject root = JsonNode.Parse(File.ReadAllText(SidecarPath))!.AsObject();
        Assert.Null(root["projection"]);
        Assert.Equal(1, root["audioMode"]!["order"]!.GetValue<int>());
        Assert.True(root.ContainsKey("externalAudio"));
    }

    [Fact]
    public void Save_NothingLeft_DeletesTheFile()
    {
        SidecarFile sidecar = SidecarFile.Load(Video);
        sidecar.Save(new FormatOverrides(Projection: ProjectionType.Eac));
        Assert.True(File.Exists(SidecarPath));

        SidecarFile.Load(Video).Save(new FormatOverrides(Offset: OrientationOffset.Zero));

        Assert.False(File.Exists(SidecarPath));
    }
}
