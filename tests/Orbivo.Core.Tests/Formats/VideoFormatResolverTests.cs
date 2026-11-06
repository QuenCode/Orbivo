using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;

namespace Orbivo.Core.Tests.Formats;

public sealed class VideoFormatResolverTests
{
    private static readonly SphericalMapping Equirect = new(SphericalProjectionKind.Equirectangular);
    private static readonly SphericalMapping Vr180 = new(SphericalProjectionKind.EquirectangularTile, BoundLeft: 0.25, BoundRight: 0.25);
    private static readonly SphericalMapping Cube = new(SphericalProjectionKind.Cubemap);

    [Theory]
    [InlineData(8192, 8192, StereoLayout.TopBottom)]
    [InlineData(8192, 4096, StereoLayout.Mono)]
    [InlineData(4096, 1024, StereoLayout.SideBySide)]
    [InlineData(1000, 700, StereoLayout.Mono)]
    public void NoMetadata_GuessesEquirect360FromSize(int width, int height, StereoLayout expected)
    {
        ResolvedVideoFormat result = VideoFormatResolver.Resolve(EmbeddedFormatMetadata.None, width, height, FormatOverrides.None);

        Assert.Equal(ProjectionType.Equirect360, result.Format.Projection);
        Assert.Equal(expected, result.Format.Layout);
        Assert.Equal(FormatSource.Default, result.Sources.Projection);
        Assert.Equal(FormatSource.Guess, result.Sources.Layout);
        Assert.Equal(result.Format, result.Automatic);
    }

    [Fact]
    public void Metadata_Sv3dAndSt3d_AreUsed()
    {
        var metadata = new EmbeddedFormatMetadata(Equirect, new Stereo3DInfo(Stereo3DKind.SideBySide));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 8192, 4096, FormatOverrides.None);

        Assert.Equal((ProjectionType.Equirect360, StereoLayout.SideBySide, false), (result.Format.Projection, result.Format.Layout, result.Format.SwapEyes));
        Assert.Equal(FormatSource.Metadata, result.Sources.Projection);
        Assert.Equal(FormatSource.Metadata, result.Sources.Layout);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Metadata_Vr180Tile_IsEquirect180()
    {
        var metadata = new EmbeddedFormatMetadata(Vr180, new Stereo3DInfo(Stereo3DKind.SideBySide));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 8192, 4096, FormatOverrides.None);

        Assert.Equal(ProjectionType.Equirect180, result.Format.Projection);
        Assert.Equal(StereoLayout.SideBySide, result.Format.Layout);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Metadata_OtherTile_IsEquirect360WithWarning()
    {
        var metadata = new EmbeddedFormatMetadata(new SphericalMapping(SphericalProjectionKind.EquirectangularTile, BoundTop: 0.25, BoundBottom: 0.25), null);

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 4096, 1024, FormatOverrides.None);

        Assert.Equal(ProjectionType.Equirect360, result.Format.Projection);
        Assert.Contains(result.Warnings, w => w.Contains("Ausschnitt", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_CubemapWithPadding_UsesGrid3x2WithPadding()
    {
        var metadata = new EmbeddedFormatMetadata(Cube with { Padding = 8 }, new Stereo3DInfo(Stereo3DKind.TwoD));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 3072, 2048, FormatOverrides.None);

        Assert.Equal(ProjectionType.Cubemap, result.Format.Projection);
        Assert.Equal("3x2", result.Format.Cubemap.PresetName);
        Assert.Equal(8, result.Format.Cubemap.PaddingPixels);
        Assert.Equal(FormatSource.Metadata, result.Sources.Cubemap);
    }

    [Fact]
    public void Metadata_InvertedStereo_SwapsEyes()
    {
        var metadata = new EmbeddedFormatMetadata(Equirect, new Stereo3DInfo(Stereo3DKind.TopBottom, Inverted: true));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 4096, 4096, FormatOverrides.None);

        Assert.True(result.Format.SwapEyes);
        Assert.Equal(FormatSource.Metadata, result.Sources.SwapEyes);
    }

    [Fact]
    public void Metadata_Sv3dWithoutSt3d_GuessesAndWarnsForStereo()
    {
        ResolvedVideoFormat stereo = VideoFormatResolver.Resolve(new EmbeddedFormatMetadata(Equirect, null), 4096, 4096, FormatOverrides.None);
        ResolvedVideoFormat mono = VideoFormatResolver.Resolve(new EmbeddedFormatMetadata(Equirect, null), 4096, 2048, FormatOverrides.None);

        Assert.Equal(StereoLayout.TopBottom, stereo.Format.Layout);
        Assert.Contains(stereo.Warnings, w => w.Contains("st3d", StringComparison.Ordinal));
        Assert.Equal(StereoLayout.Mono, mono.Format.Layout);
        Assert.Empty(mono.Warnings);
    }

    [Theory]
    [InlineData(SphericalProjectionKind.Rectilinear)]
    [InlineData(SphericalProjectionKind.ParametricImmersive)]
    [InlineData((SphericalProjectionKind)42)]
    public void Metadata_UnsupportedProjection_FallsBackWithWarning(SphericalProjectionKind kind)
    {
        ResolvedVideoFormat result = VideoFormatResolver.Resolve(new EmbeddedFormatMetadata(new SphericalMapping(kind), null), 4096, 2048, FormatOverrides.None);

        Assert.Equal(ProjectionType.Equirect360, result.Format.Projection);
        Assert.Contains(result.Warnings, w => w.Contains("nicht unterstützt", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_UnsupportedStereo_GuessesWithWarning()
    {
        var metadata = new EmbeddedFormatMetadata(Equirect, new Stereo3DInfo(Stereo3DKind.FrameSequence));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 4096, 4096, FormatOverrides.None);

        Assert.Equal(StereoLayout.TopBottom, result.Format.Layout);
        Assert.Equal(FormatSource.Guess, result.Sources.Layout);
        Assert.Contains(result.Warnings, w => w.Contains("FrameSequence", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_Pose_IsReportedNotApplied()
    {
        var metadata = new EmbeddedFormatMetadata(Equirect with { YawDegrees = 90 }, new Stereo3DInfo(Stereo3DKind.TwoD));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 4096, 2048, FormatOverrides.None);

        Assert.True(result.Format.Offset.IsZero);
        Assert.Contains(result.Warnings, w => w.Contains("Yaw 90", StringComparison.Ordinal));
    }

    [Fact]
    public void Sidecar_OverridesMetadataPerField()
    {
        var metadata = new EmbeddedFormatMetadata(Cube, new Stereo3DInfo(Stereo3DKind.TwoD));
        var overrides = new FormatOverrides(Cubemap: CubemapLayout.Strip6x1, Offset: new OrientationOffset(10, 0, 0));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 6144, 1024, overrides);

        Assert.Equal(ProjectionType.Cubemap, result.Format.Projection);
        Assert.Equal(FormatSource.Metadata, result.Sources.Projection);
        Assert.Equal(CubemapLayout.Strip6x1, result.Format.Cubemap);
        Assert.Equal(FormatSource.Sidecar, result.Sources.Cubemap);
        Assert.Equal(new OrientationOffset(10, 0, 0), result.Format.Offset);
        Assert.Equal(FormatSource.Sidecar, result.Sources.Offset);
        Assert.Equal(CubemapLayout.Grid3x2, result.Automatic.Cubemap);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Sidecar_ProjectionChangesTheLayoutGuess()
    {
        var overrides = new FormatOverrides(Projection: ProjectionType.Cubemap, Cubemap: CubemapLayout.Strip1x6);

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(EmbeddedFormatMetadata.None, 1024, 6144, overrides);

        Assert.Equal(StereoLayout.Mono, result.Format.Layout);
        Assert.Equal(ProjectionType.Equirect360, result.Automatic.Projection);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Eac_GetsTheYouTubeLayout()
    {
        var metadata = new EmbeddedFormatMetadata(null, new Stereo3DInfo(Stereo3DKind.TopBottom));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 3072, 4096, new FormatOverrides(Projection: ProjectionType.Eac));

        Assert.Equal(CubemapLayout.YouTubeEac, result.Format.Cubemap);
        Assert.Equal(StereoLayout.TopBottom, result.Format.Layout);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CommandLine_WinsOverSidecarAndMetadata()
    {
        var metadata = new EmbeddedFormatMetadata(Equirect, new Stereo3DInfo(Stereo3DKind.TopBottom));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(
            metadata, 4096, 4096, new FormatOverrides(Layout: StereoLayout.SideBySide), StereoLayout.Mono);

        Assert.Equal(StereoLayout.Mono, result.Format.Layout);
        Assert.Equal(StereoLayout.Mono, result.Automatic.Layout);
        Assert.Equal(FormatSource.CommandLine, result.Sources.Layout);
    }

    [Fact]
    public void ImplausibleAspect_Warns()
    {
        ResolvedVideoFormat result = VideoFormatResolver.Resolve(
            EmbeddedFormatMetadata.None, 4096, 2048, new FormatOverrides(Projection: ProjectionType.Fisheye, Layout: StereoLayout.TopBottom));

        Assert.Contains(result.Warnings, w => w.Contains("Seitenverhältnis", StringComparison.Ordinal));
    }

    [Fact]
    public void SqueezedSideBySideEquirect_DoesNotWarn()
    {
        var metadata = new EmbeddedFormatMetadata(Equirect, new Stereo3DInfo(Stereo3DKind.SideBySide));

        ResolvedVideoFormat result = VideoFormatResolver.Resolve(metadata, 4096, 2048, FormatOverrides.None);

        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(ProjectionType.Fisheye, 4096, 4096, StereoLayout.Mono)]
    [InlineData(ProjectionType.Fisheye, 4096, 2048, StereoLayout.SideBySide)]
    [InlineData(ProjectionType.Equirect180, 4096, 2048, StereoLayout.SideBySide)]
    [InlineData(ProjectionType.Equirect180, 2048, 4096, StereoLayout.TopBottom)]
    [InlineData(ProjectionType.Cubemap, 3072, 4096, StereoLayout.TopBottom)]
    [InlineData(ProjectionType.Cubemap, 6144, 2048, StereoLayout.SideBySide)]
    public void GuessLayout_FitsTheProjection(ProjectionType projection, int width, int height, StereoLayout expected)
    {
        Assert.Equal(expected, VideoFormatResolver.GuessLayout(projection, CubemapLayout.Grid3x2, width, height));
    }
}
