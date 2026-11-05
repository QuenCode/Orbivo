using System.Numerics;
using Orbivo.Core.Geometry;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests;

/// <summary>
/// CLAUDE.md rule 8: projection.hlsl and ProjectionMath.cs must agree. The shader writes its raw results
/// (direction, uv) per pixel; every pixel is compared with the C# reference for the same pixel center.
/// This also catches mirroring between NDC, pixel rows and the read-back image.
/// </summary>
public sealed class ProjectionShaderTests(WarpRendererFixture fixture) : IClassFixture<WarpRendererFixture>
{
    private const int Width = 64;
    private const int Height = 48;
    private const float DirectionTolerance = 1e-4f;
    private const float UvTolerance = 1e-4f;

    public static TheoryData<string> ViewNames => new(Views.Keys);

    private static readonly Dictionary<string, ViewParameters> Views = new()
    {
        ["forward-square-90"] = TestViews.Create(0, 0, 90, 1),
        ["look-left-16x9"] = TestViews.Create(90, 0, 75, 16.0 / 9.0),
        ["look-up-steep"] = TestViews.Create(0, 80, 100, 1.5),
        ["straight-up"] = TestViews.Create(0, 90, 90, 1),
        ["back-across-seam"] = TestViews.Create(180, 0, 60, 1),
        ["down-right-wide"] = TestViews.Create(-135, -60, 90, 2),
        ["headset-like-asymmetric"] = new ViewParameters(
            OrientationMath.FromYawPitch(TestViews.Radians(10), TestViews.Radians(-5)),
            new FovAngles(TestViews.Radians(-52), TestViews.Radians(43), TestViews.Radians(48), TestViews.Radians(-55))),
    };

    [Theory]
    [MemberData(nameof(ViewNames))]
    public void Direction_MatchesProjectionMath(string viewName)
    {
        ViewParameters view = Views[viewName];
        float[] gpu = RenderFloat4(view, PanoramaOutput.DebugDirection);
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(view.Orientation, view.Fov);

        var mismatches = new List<string>();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Vector3 expected = ProjectionMath.DirectionFromNdc(basis, ProjectionMath.PixelCenterToNdc(x, y, Width, Height));
                int i = ((y * Width) + x) * 4;
                var actual = new Vector3(gpu[i], gpu[i + 1], gpu[i + 2]);
                if (Vector3.Distance(expected, actual) > DirectionTolerance)
                {
                    mismatches.Add($"({x},{y}): expected {expected}, shader {actual}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} pixels differ, first: {string.Join("; ", mismatches.Take(5))}");
    }

    [Theory]
    [MemberData(nameof(ViewNames))]
    public void Equirect360Uv_MatchesProjectionMath(string viewName)
    {
        ViewParameters view = Views[viewName];
        float[] gpu = RenderFloat4(view, PanoramaOutput.DebugEquirect360Uv);
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(view.Orientation, view.Fov);

        var mismatches = new List<string>();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Vector3 direction = ProjectionMath.DirectionFromNdc(basis, ProjectionMath.PixelCenterToNdc(x, y, Width, Height));
                Vector2 expected = ProjectionMath.Equirect360Uv(direction);
                int i = ((y * Width) + x) * 4;
                var actual = new Vector2(gpu[i], gpu[i + 1]);

                // u wraps around at the back (0 == 1) and is meaningless directly at the poles.
                float du = MathF.Abs(expected.X - actual.X);
                du = MathF.Min(du, 1f - du);
                bool nearPole = MathF.Abs(direction.Y) > 0.9999f;
                if ((!nearPole && du > UvTolerance) || MathF.Abs(expected.Y - actual.Y) > UvTolerance)
                {
                    mismatches.Add($"({x},{y}): expected {expected}, shader {actual}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} pixels differ, first: {string.Join("; ", mismatches.Take(5))}");
    }

    [Fact]
    public void TopLeftPixel_LooksUpAndLeft()
    {
        // Independent of the C# reference: looking forward, the first pixel row is the top and the first column the left.
        float[] gpu = RenderFloat4(TestViews.Create(0, 0, 90, 1), PanoramaOutput.DebugDirection);

        Assert.True(gpu[0] < -0.5f, $"x = {gpu[0]}");
        Assert.True(gpu[1] > 0.5f, $"y = {gpu[1]}");
        Assert.True(gpu[2] < -0.5f, $"z = {gpu[2]}");
    }

    private float[] RenderFloat4(ViewParameters view, PanoramaOutput output)
    {
        using var target = new OffscreenTarget(fixture.Device, Width, Height, Format.R32G32B32A32_Float);
        fixture.Renderer.Render(fixture.Device.ImmediateContext, target.RenderTargetView, Width, Height, view, null, 1f, output);
        return target.Read<float>();
    }
}
