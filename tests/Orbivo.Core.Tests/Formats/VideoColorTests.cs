using System.Numerics;
using Orbivo.Core.Formats;

namespace Orbivo.Core.Tests.Formats;

public sealed class VideoColorTests
{
    private const float Tolerance = 0.004f;

    private static readonly VideoColorFormat Bt709Limited8 = new(YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 8);

    [Theory]
    [InlineData(16, 128, 128, 0f, 0f, 0f)] // black
    [InlineData(235, 128, 128, 1f, 1f, 1f)] // white
    [InlineData(63, 102, 240, 1f, 0f, 0f)] // BT.709 red
    [InlineData(173, 42, 26, 0f, 1f, 0f)] // BT.709 green
    [InlineData(32, 240, 118, 0f, 0f, 1f)] // BT.709 blue
    public void Bt709Limited8Bit_KnownCodes(int y, int cb, int cr, float r, float g, float b)
    {
        Vector3 rgb = Encoded(Samples8(y, cb, cr), Bt709Limited8);

        AssertClose(new Vector3(r, g, b), rgb, 0.01f);
    }

    [Fact]
    public void Limited10Bit_BlackAndWhite()
    {
        var format = new VideoColorFormat(YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 10);

        AssertClose(Vector3.Zero, Encoded(Samples10(64, 512, 512), format), Tolerance);
        AssertClose(Vector3.One, Encoded(Samples10(940, 512, 512), format), Tolerance);
    }

    [Fact]
    public void Full8Bit_BlackAndWhite()
    {
        var format = new VideoColorFormat(YuvMatrix.Bt709, YuvRange.Full, ColorPrimaries.Bt709, 8);

        AssertClose(Vector3.Zero, Encoded(Samples8(0, 128, 128), format), Tolerance);
        AssertClose(Vector3.One, Encoded(Samples8(255, 128, 128), format), Tolerance);
    }

    [Theory]
    [InlineData(YuvMatrix.Bt601)]
    [InlineData(YuvMatrix.Bt709)]
    [InlineData(YuvMatrix.Bt2020)]
    public void GrayStaysGray(YuvMatrix matrix)
    {
        var format = new VideoColorFormat(matrix, YuvRange.Limited, ColorPrimaries.Bt709, 8);
        Vector3 rgb = Encoded(Samples8(126, 128, 128), format);

        Assert.Equal(rgb.X, rgb.Y, Tolerance);
        Assert.Equal(rgb.X, rgb.Z, Tolerance);
        Assert.Equal((126f - 16f) / 219f, rgb.X, Tolerance);
    }

    [Fact]
    public void SampleToLinear_UsesTheSrgbCurve_SoTheSrgbTargetRestoresTheCodes()
    {
        VideoColorConstants constants = VideoColor.CreateConstants(Bt709Limited8);
        Vector3 samples = Samples8(126, 128, 128);

        Vector3 linear = VideoColor.SampleToLinear(samples, constants);
        float displayed = VideoColor.LinearToSrgb(linear.X);

        Assert.Equal((126f - 16f) / 219f, displayed, 1e-4f);
        Assert.True(linear.X < displayed, "the linear value of a mid gray is darker than its code");
    }

    [Fact]
    public void Bt2020Primaries_KeepNeutralColors_AndSaturateBt709Red()
    {
        var format = new VideoColorFormat(YuvMatrix.Bt2020, YuvRange.Limited, ColorPrimaries.Bt2020, 10);
        VideoColorConstants constants = VideoColor.CreateConstants(format);

        Vector3 gray = VideoColor.SampleToLinear(Samples10(502, 512, 512), constants);
        Assert.Equal(gray.X, gray.Y, Tolerance);
        Assert.Equal(gray.X, gray.Z, Tolerance);

        // A BT.2020 red is more saturated than BT.709 can show: green and blue clip to zero.
        Vector3 red = VideoColor.SampleToLinear(Samples10(250, 409, 960), constants);
        Assert.True(red.X > 0.5f, $"red {red}");
        Assert.Equal(0f, red.Y, Tolerance);
        Assert.Equal(0f, red.Z, Tolerance);
    }

    [Theory]
    [InlineData(null, null, null, 2160, YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709)]
    [InlineData(null, null, null, 576, YuvMatrix.Bt601, YuvRange.Limited, ColorPrimaries.Bt709)]
    [InlineData(YuvMatrix.Bt2020, null, null, 4096, YuvMatrix.Bt2020, YuvRange.Limited, ColorPrimaries.Bt2020)]
    [InlineData(YuvMatrix.Bt709, YuvRange.Full, ColorPrimaries.Bt709, 4096, YuvMatrix.Bt709, YuvRange.Full, ColorPrimaries.Bt709)]
    public void ResolveUnspecified_FillsGaps(
        YuvMatrix? matrix,
        YuvRange? range,
        ColorPrimaries? primaries,
        int height,
        YuvMatrix expectedMatrix,
        YuvRange expectedRange,
        ColorPrimaries expectedPrimaries)
    {
        VideoColorFormat format = VideoColor.ResolveUnspecified(matrix, range, primaries, height, 8);

        Assert.Equal(new VideoColorFormat(expectedMatrix, expectedRange, expectedPrimaries, 8), format);
    }

    [Fact]
    public void SrgbCurve_RoundTrips()
    {
        for (float value = 0f; value <= 1f; value += 0.01f)
        {
            Assert.Equal(value, VideoColor.LinearToSrgb(VideoColor.SrgbToLinear(value)), 1e-5f);
        }
    }

    /// <summary>R'G'B' before linearization, recovered from the reference path.</summary>
    private static Vector3 Encoded(Vector3 samples, VideoColorFormat format)
    {
        Vector3 linear = VideoColor.SampleToLinear(samples, VideoColor.CreateConstants(format));
        return new Vector3(VideoColor.LinearToSrgb(linear.X), VideoColor.LinearToSrgb(linear.Y), VideoColor.LinearToSrgb(linear.Z));
    }

    private static Vector3 Samples8(int y, int cb, int cr) => new Vector3(y, cb, cr) / 255f;

    /// <summary>P010: the 10-bit code sits in the high bits of a 16-bit value.</summary>
    private static Vector3 Samples10(int y, int cb, int cr) => new Vector3(y << 6, cb << 6, cr << 6) / 65535f;

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(
            Vector3.Distance(expected, actual) <= tolerance * 1.8f,
            $"expected {expected}, actual {actual}");
    }
}
