using System.Numerics;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests;

/// <summary>
/// CLAUDE.md rule 8 for all projections of F10: projection.hlsl (ProjectedEyeUv, stereo layout, swap, orientation offset)
/// against ProjectionMath, pixel by pixel on WARP.
/// </summary>
public sealed class ProjectionFormatShaderTests(WarpRendererFixture fixture) : IClassFixture<WarpRendererFixture>
{
    private const int Size = 48;
    private const float Tolerance = 2e-4f;

    public static TheoryData<string> Cases => new(FormatCases.Keys);

    private static Dictionary<string, (VideoFormat Format, int Width, int Height, Eye Eye)> FormatCases { get; } = new()
    {
        ["equirect180_mono"] = (Make(ProjectionType.Equirect180, StereoLayout.Mono), 64, 64, Eye.Left),
        ["equirect180_sbs_right"] = (Make(ProjectionType.Equirect180, StereoLayout.SideBySide), 128, 64, Eye.Right),
        ["equirect180_sbs_swapped"] = (Make(ProjectionType.Equirect180, StereoLayout.SideBySide) with { SwapEyes = true }, 128, 64, Eye.Left),
        ["cubemap_3x2"] = (Make(ProjectionType.Cubemap, StereoLayout.Mono), 96, 64, Eye.Left),
        ["cubemap_6x1"] = (Make(ProjectionType.Cubemap, StereoLayout.Mono) with { Cubemap = CubemapLayout.Strip6x1 }, 192, 32, Eye.Left),
        ["cubemap_1x6"] = (Make(ProjectionType.Cubemap, StereoLayout.Mono) with { Cubemap = CubemapLayout.Strip1x6 }, 32, 192, Eye.Left),
        ["cubemap_3x2_tb_right"] = (Make(ProjectionType.Cubemap, StereoLayout.TopBottom), 96, 128, Eye.Right),
        ["cubemap_padded_turned"] = (Make(ProjectionType.Cubemap, StereoLayout.Mono) with { Cubemap = new CubemapLayout(2, 3, "bfudlr", "123012", 2) }, 64, 96, Eye.Left),
        ["eac_youtube"] = (Make(ProjectionType.Eac, StereoLayout.Mono) with { Cubemap = CubemapLayout.YouTubeEac }, 96, 64, Eye.Left),
        ["eac_youtube_tb_left"] = (Make(ProjectionType.Eac, StereoLayout.TopBottom) with { Cubemap = CubemapLayout.YouTubeEac }, 96, 128, Eye.Left),
        ["fisheye180"] = (Make(ProjectionType.Fisheye, StereoLayout.Mono), 64, 64, Eye.Left),
        ["fisheye200_sbs_offcenter"] = (Make(ProjectionType.Fisheye, StereoLayout.SideBySide) with { Fisheye = new FisheyeParameters(200, 0.47, 0.52, 0.45) }, 128, 48, Eye.Right),
        ["equirect360_offset"] = (Make(ProjectionType.Equirect360, StereoLayout.Mono) with { Offset = new OrientationOffset(35, -20, 15) }, 64, 32, Eye.Left),
        ["cubemap_offset"] = (Make(ProjectionType.Cubemap, StereoLayout.Mono) with { Offset = new OrientationOffset(-60, 10, -30) }, 96, 64, Eye.Left),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SourceUv_MatchesProjectionMath(string name)
    {
        (VideoFormat format, int width, int height, Eye eye) = FormatCases[name];
        foreach (ViewParameters view in Views())
        {
            Compare(name, format, width, height, eye, view);
        }
    }

    private void Compare(string name, VideoFormat format, int width, int height, Eye eye, ViewParameters view)
    {
        var color = new VideoColorFormat(YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 8);
        using var frame = new TestVideoFrame(fixture.Device, width, height, 8, (16, 128, 128), (16, 128, 128), StereoLayout.Mono, color);
        PanoramaSource source = PanoramaSource.FromVideo(frame.Frame with { Format = format });

        using var target = new OffscreenTarget(fixture.Device, Size, Size, Format.R32G32B32A32_Float);
        fixture.Renderer.Render(fixture.Device.ImmediateContext, target.RenderTargetView, Size, Size, view, eye, source, 1f, PanoramaOutput.DebugSourceUv);
        float[] gpu = target.Read<float>();

        // The same steps in C#, as PanoramaRenderer feeds the shader.
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(format.Offset.ApplyToView(view.Orientation), view.Fov);
        bool wraps = format.Projection == ProjectionType.Equirect360;
        EyeSourceRect rect = ProjectionMath.CreateEyeSourceRect(format.Layout, format.SourceEye(eye), width, height, wraps);
        (int eyeWidth, int eyeHeight) = ProjectionMath.EyeSize(format.Layout, width, height);
        ProjectionParameters parameters = ProjectionMath.CreateProjectionParameters(format, eyeWidth, eyeHeight);

        var mismatches = new List<string>();
        int compared = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Vector3 direction = ProjectionMath.DirectionFromNdc(basis, ProjectionMath.PixelCenterToNdc(x, y, Size, Size));
                if (IsOnABoundary(direction, format, parameters))
                {
                    continue;
                }

                compared++;
                Vector2 expected = ProjectionMath.SourceUv(ProjectionMath.EyeUv(direction, parameters, out bool valid), rect);
                int i = ((y * Size) + x) * 4;
                var actual = new Vector2(gpu[i], gpu[i + 1]);
                bool actualValid = gpu[i + 2] > 0.5f;

                float du = MathF.Abs(expected.X - actual.X);
                if (wraps && format.Layout != StereoLayout.SideBySide)
                {
                    du = MathF.Min(du, 1f - du);
                }

                if (actualValid != valid || (valid && (du > Tolerance || MathF.Abs(expected.Y - actual.Y) > Tolerance)))
                {
                    mismatches.Add($"({x},{y}): expected {expected} valid {valid}, shader {actual} valid {actualValid}");
                }
            }
        }

        Assert.True(compared > Size * Size / 2, $"{name}: only {compared} pixels compared.");
        Assert.True(mismatches.Count == 0, $"{name}: {mismatches.Count} pixels differ, first: {string.Join("; ", mismatches.Take(5))}");
    }

    /// <summary>
    /// Pixels where float differences between CPU and GPU may legitimately pick another face or flip the valid flag:
    /// cube edges, the VR180 border, the fisheye circle, the equirect poles.
    /// </summary>
    private static bool IsOnABoundary(Vector3 direction, VideoFormat format, in ProjectionParameters parameters)
    {
        const float Margin = 1e-3f;
        Vector3 a = Vector3.Abs(direction);
        switch (format.Projection)
        {
            case ProjectionType.Cubemap:
            case ProjectionType.Eac:
                return MathF.Abs(a.X - a.Y) < Margin || MathF.Abs(a.X - a.Z) < Margin || MathF.Abs(a.Y - a.Z) < Margin;
            case ProjectionType.Equirect180:
                return MathF.Abs(MathF.Abs(MathF.Atan2(direction.X, -direction.Z)) - (0.5f * MathF.PI)) < Margin || a.Y > 1f - Margin;
            case ProjectionType.Fisheye:
                float angle = MathF.Atan2(new Vector2(direction.X, direction.Y).Length(), -direction.Z);
                return MathF.Abs(angle - parameters.FisheyeHalfFov) < Margin;
            default:
                return a.Y > 1f - Margin;
        }
    }

    private static IEnumerable<ViewParameters> Views()
    {
        yield return TestViews.Create(0, 0, 100, 1);
        yield return TestViews.Create(150, -30, 100, 1.5);
        yield return TestViews.Create(-70, 60, 110, 1);
        yield return TestViews.Create(30, -85, 120, 1);
        yield return TestViews.Create(-100, 5, 120, 1.2);
    }

    private static VideoFormat Make(ProjectionType projection, StereoLayout layout)
    {
        return VideoFormat.Default with { Projection = projection, Layout = layout };
    }
}
