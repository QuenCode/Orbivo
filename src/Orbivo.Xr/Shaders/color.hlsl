// Video color conversion: YUV texture samples -> linear RGB in BT.709 primaries.
//
// C# reference: src/Orbivo.Core/Formats/VideoColor.cs. The GPU tests in tests/Orbivo.Xr.Tests compare both.
// Gamma (docs/DECISIONS.md): linearized with the sRGB curve; the *_SRGB render target applies its inverse,
// so the decoded R'G'B' values reach the display unchanged.

#ifndef ORBIVO_COLOR_HLSL
#define ORBIVO_COLOR_HLSL

// sRGB electro-optical transfer function (IEC 61966-2-1). C#: VideoColor.SrgbToLinear.
float3 SrgbToLinear(float3 encoded)
{
    float3 low = encoded / 12.92f;
    float3 high = pow(abs((encoded + 0.055f) / 1.055f), 2.4f);
    return encoded <= 0.04045f ? low : high;
}

// sample: (luma, Cb, Cr) as the NV12/P010 views return them (normalized 0..1).
// yuvRow*: R'G'B' = dot(row, (sample, 1)), range and bit depth included (VideoColor.CreateConstants).
// primariesRow*: linear source primaries -> linear BT.709 (identity unless the video is BT.2020).
// C#: VideoColor.SampleToLinear.
float3 VideoSampleToLinear(float3 sample, float4 yuvRowR, float4 yuvRowG, float4 yuvRowB,
                           float4 primariesRow0, float4 primariesRow1, float4 primariesRow2)
{
    float4 s = float4(sample, 1.0f);
    float3 encoded = saturate(float3(dot(yuvRowR, s), dot(yuvRowG, s), dot(yuvRowB, s)));
    float3 linearColor = SrgbToLinear(encoded);
    return saturate(float3(
        dot(primariesRow0.xyz, linearColor),
        dot(primariesRow1.xyz, linearColor),
        dot(primariesRow2.xyz, linearColor)));
}

#endif
