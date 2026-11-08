using System.Numerics;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;

namespace Orbivo.Core.Tests.Geometry;

public sealed class EyeSourceRectTests
{
    private const float Tolerance = 1e-6f;

    [Theory]
    [InlineData(Eye.Left)]
    [InlineData(Eye.Right)]
    public void Mono_BothEyesSeeTheWholeImage(Eye eye)
    {
        EyeSourceRect rect = ProjectionMath.CreateEyeSourceRect(StereoLayout.Mono, eye, 4096, 2048);

        AssertUv(new Vector2(0.25f, 0.75f), ProjectionMath.SourceUv(new Vector2(0.25f, 0.75f), rect));
        AssertUv(new Vector2(0f, 0f), ProjectionMath.SourceUv(Vector2.Zero, rect));
        AssertUv(new Vector2(1f, 1f), ProjectionMath.SourceUv(Vector2.One, rect));
    }

    [Fact]
    public void TopBottom_LeftEyeIsOnTop_RightEyeBelow()
    {
        EyeSourceRect left = ProjectionMath.CreateEyeSourceRect(StereoLayout.TopBottom, Eye.Left, 8192, 8192);
        EyeSourceRect right = ProjectionMath.CreateEyeSourceRect(StereoLayout.TopBottom, Eye.Right, 8192, 8192);

        // Forward (image center of each eye).
        AssertUv(new Vector2(0.5f, 0.25f), ProjectionMath.SourceUv(new Vector2(0.5f, 0.5f), left));
        AssertUv(new Vector2(0.5f, 0.75f), ProjectionMath.SourceUv(new Vector2(0.5f, 0.5f), right));
    }

    [Fact]
    public void TopBottom_PolesStayHalfATexelInsideTheEye_ButUStillWraps()
    {
        const int Height = 8192;
        EyeSourceRect left = ProjectionMath.CreateEyeSourceRect(StereoLayout.TopBottom, Eye.Left, Height, Height);
        EyeSourceRect right = ProjectionMath.CreateEyeSourceRect(StereoLayout.TopBottom, Eye.Right, Height, Height);
        float halfTexel = 0.5f / Height;

        // South pole of the left eye stops half a texel above the border to the right eye.
        Assert.Equal(0.5f - halfTexel, ProjectionMath.SourceUv(new Vector2(0.5f, 1f), left).Y, Tolerance);

        // North pole of the right eye stops half a texel below it.
        Assert.Equal(0.5f + halfTexel, ProjectionMath.SourceUv(new Vector2(0.5f, 0f), right).Y, Tolerance);

        // u is not clamped: the sampler wraps at the back seam.
        Assert.Equal(1f, ProjectionMath.SourceUv(new Vector2(1f, 0.5f), left).X, Tolerance);
        Assert.Equal(0f, ProjectionMath.SourceUv(new Vector2(0f, 0.5f), left).X, Tolerance);
    }

    [Fact]
    public void SideBySide_LeftEyeIsLeft_RightEyeRight_AndNeverCrossesOver()
    {
        const int Width = 8192;
        EyeSourceRect left = ProjectionMath.CreateEyeSourceRect(StereoLayout.SideBySide, Eye.Left, Width, 4096);
        EyeSourceRect right = ProjectionMath.CreateEyeSourceRect(StereoLayout.SideBySide, Eye.Right, Width, 4096);
        float halfTexel = 0.5f / Width;

        AssertUv(new Vector2(0.25f, 0.5f), ProjectionMath.SourceUv(new Vector2(0.5f, 0.5f), left));
        AssertUv(new Vector2(0.75f, 0.5f), ProjectionMath.SourceUv(new Vector2(0.5f, 0.5f), right));

        Assert.Equal(0.5f - halfTexel, ProjectionMath.SourceUv(new Vector2(1f, 0.5f), left).X, Tolerance);
        Assert.Equal(0.5f + halfTexel, ProjectionMath.SourceUv(new Vector2(0f, 0.5f), right).X, Tolerance);
        Assert.Equal(halfTexel, ProjectionMath.SourceUv(new Vector2(0f, 0.5f), left).X, Tolerance);
        Assert.Equal(1f - halfTexel, ProjectionMath.SourceUv(new Vector2(1f, 0.5f), right).X, Tolerance);
    }

    [Theory]
    [InlineData(8192, 8192, StereoLayout.TopBottom)]
    [InlineData(4096, 4096, StereoLayout.TopBottom)]
    [InlineData(8192, 4096, StereoLayout.Mono)]
    [InlineData(3840, 1920, StereoLayout.Mono)]
    [InlineData(8192, 2048, StereoLayout.SideBySide)]
    [InlineData(0, 0, StereoLayout.Mono)]
    public void DetectionFromSize(int width, int height, StereoLayout expected)
    {
        Assert.Equal(expected, StereoLayoutDetection.FromSize(width, height));
    }

    private static void AssertUv(Vector2 expected, Vector2 actual)
    {
        Assert.True(Vector2.Distance(expected, actual) < 1e-3f, $"expected {expected}, actual {actual}");
    }
}
