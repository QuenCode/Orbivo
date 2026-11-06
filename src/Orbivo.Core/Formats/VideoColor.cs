using System.Numerics;

namespace Orbivo.Core.Formats;

/// <summary>YUV → R'G'B' matrix coefficients of the stream (F15).</summary>
public enum YuvMatrix
{
    Bt601,
    Bt709,
    Bt2020,
}

/// <summary>Value range of the stream: limited ("TV", 16–235 for 8 bit) or full ("PC", 0–255).</summary>
public enum YuvRange
{
    Limited,
    Full,
}

/// <summary>Color primaries of the stream. BT.601 primaries are treated as BT.709 (the difference is not visible).</summary>
public enum ColorPrimaries
{
    Bt709,
    Bt2020,
}

/// <summary>Color description of a YUV 4:2:0 video stream.</summary>
/// <param name="Matrix">YUV → R'G'B' coefficients.</param>
/// <param name="Range">Limited or full range.</param>
/// <param name="Primaries">Color primaries.</param>
/// <param name="BitDepth">8 (NV12 textures) or 10 (P010 textures, 10 bit in the high bits of 16).</param>
public readonly record struct VideoColorFormat(YuvMatrix Matrix, YuvRange Range, ColorPrimaries Primaries, int BitDepth);

/// <summary>
/// Shader constants for the color conversion: <c>rgb' = (dot(R, s), dot(G, s), dot(B, s))</c> with
/// <c>s = (luma sample, Cb sample, Cr sample, 1)</c> as the texture returns them (normalized 0–1),
/// then linearization, then <c>linear = (dot(P0, rgb), dot(P1, rgb), dot(P2, rgb))</c> into BT.709 primaries.
/// </summary>
public readonly record struct VideoColorConstants(
    Vector4 YuvRowR,
    Vector4 YuvRowG,
    Vector4 YuvRowB,
    Vector4 PrimariesRow0,
    Vector4 PrimariesRow1,
    Vector4 PrimariesRow2);

/// <summary>
/// C# reference of the video color conversion in <c>src/Orbivo.Xr/Shaders/color.hlsl</c>; the GPU tests compare both.
/// </summary>
/// <remarks>
/// Gamma (docs/DECISIONS.md): the shader writes linear values into an *_SRGB target, which encodes them with the
/// sRGB curve again. Linearizing with the exact sRGB curve therefore puts the decoded R'G'B' values unchanged
/// on the display, like common video players do.
/// </remarks>
public static class VideoColor
{
    /// <summary>Linear BT.2020 → linear BT.709 (ITU-R BT.2087), rows.</summary>
    private static readonly Vector3[] Bt2020ToBt709 =
    [
        new(1.6604910f, -0.5876411f, -0.0728499f),
        new(-0.1245505f, 1.1328999f, -0.0083494f),
        new(-0.0181508f, -0.1005789f, 1.1187297f),
    ];

    /// <summary>
    /// Color description for a stream whose metadata leaves things open: matrix by size (HD and larger BT.709),
    /// primaries follow the matrix, range limited.
    /// </summary>
    public static VideoColorFormat ResolveUnspecified(YuvMatrix? matrix, YuvRange? range, ColorPrimaries? primaries, int height, int bitDepth)
    {
        YuvMatrix resolvedMatrix = matrix ?? (height >= 720 ? YuvMatrix.Bt709 : YuvMatrix.Bt601);
        ColorPrimaries resolvedPrimaries = primaries ?? (resolvedMatrix == YuvMatrix.Bt2020 ? ColorPrimaries.Bt2020 : ColorPrimaries.Bt709);
        return new VideoColorFormat(resolvedMatrix, range ?? YuvRange.Limited, resolvedPrimaries, bitDepth);
    }

    public static VideoColorConstants CreateConstants(in VideoColorFormat format)
    {
        if (format.BitDepth is not (8 or 10))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format.BitDepth, "Only 8 and 10 bit are supported.");
        }

        (float kr, float kb) = format.Matrix switch
        {
            YuvMatrix.Bt601 => (0.299f, 0.114f),
            YuvMatrix.Bt709 => (0.2126f, 0.0722f),
            _ => (0.2627f, 0.0593f),
        };
        float kg = 1f - kr - kb;

        // Texture sample → integer code: 8 bit: code / 255. P010: the 10-bit code sits in the high bits of 16.
        double sampleToCode = format.BitDepth == 8 ? 255.0 : 65535.0 / 64.0;
        double scale = 1 << (format.BitDepth - 8);
        double yScale, yOffset, cScale, cOffset;
        if (format.Range == YuvRange.Limited)
        {
            yScale = sampleToCode / (219.0 * scale);
            yOffset = -16.0 / 219.0;
            cScale = sampleToCode / (224.0 * scale);
            cOffset = -128.0 / 224.0;
        }
        else
        {
            double maxCode = (1 << format.BitDepth) - 1;
            yScale = sampleToCode / maxCode;
            yOffset = 0.0;
            cScale = sampleToCode / maxCode;
            cOffset = -(1 << (format.BitDepth - 1)) / maxCode;
        }

        // R = Y + 2(1−Kr)·Cr, B = Y + 2(1−Kb)·Cb, G = Y − 2Kb(1−Kb)/Kg·Cb − 2Kr(1−Kr)/Kg·Cr.
        float crToR = 2f * (1f - kr);
        float cbToB = 2f * (1f - kb);
        float cbToG = -2f * kb * (1f - kb) / kg;
        float crToG = -2f * kr * (1f - kr) / kg;

        Vector4 Row(float cbFactor, float crFactor) => new(
            (float)yScale,
            (float)(cbFactor * cScale),
            (float)(crFactor * cScale),
            (float)(yOffset + ((cbFactor + crFactor) * cOffset)));

        Vector3[] primaries = format.Primaries == ColorPrimaries.Bt2020
            ? Bt2020ToBt709
            : [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

        return new VideoColorConstants(
            Row(0f, crToR),
            Row(cbToG, crToG),
            Row(cbToB, 0f),
            new Vector4(primaries[0], 0f),
            new Vector4(primaries[1], 0f),
            new Vector4(primaries[2], 0f));
    }

    /// <summary>
    /// The whole shader color path for one texel: samples (luma, Cb, Cr, normalized as the texture returns them)
    /// → linear BT.709 RGB in [0, 1]. HLSL: <c>VideoSampleToLinear</c>.
    /// </summary>
    public static Vector3 SampleToLinear(Vector3 sample, in VideoColorConstants constants)
    {
        var s = new Vector4(sample, 1f);
        var encoded = Vector3.Clamp(
            new Vector3(Vector4.Dot(constants.YuvRowR, s), Vector4.Dot(constants.YuvRowG, s), Vector4.Dot(constants.YuvRowB, s)),
            Vector3.Zero,
            Vector3.One);
        var linear = new Vector3(SrgbToLinear(encoded.X), SrgbToLinear(encoded.Y), SrgbToLinear(encoded.Z));
        return Vector3.Clamp(
            new Vector3(
                Vector3.Dot(AsVector3(constants.PrimariesRow0), linear),
                Vector3.Dot(AsVector3(constants.PrimariesRow1), linear),
                Vector3.Dot(AsVector3(constants.PrimariesRow2), linear)),
            Vector3.Zero,
            Vector3.One);
    }

    /// <summary>sRGB electro-optical transfer function (IEC 61966-2-1). HLSL: <c>SrgbToLinear</c>.</summary>
    public static float SrgbToLinear(float encoded)
    {
        return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Inverse of <see cref="SrgbToLinear"/>, as the *_SRGB render target applies it.</summary>
    public static float LinearToSrgb(float linear)
    {
        return linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
    }

    private static Vector3 AsVector3(Vector4 value) => new(value.X, value.Y, value.Z);
}
