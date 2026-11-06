// Renders a panorama with one fullscreen triangle per view (docs/ARCHITECTURE.md, "Rendering").
// All projections of F10 (projection.hlsl), mono, top-bottom or side-by-side. Source: a still image (BGRA, sRGB view)
// or a decoded video frame (NV12/P010, converted to RGB here, M3).

#include "projection.hlsl"
#include "color.hlsl"

// Layout must match Orbivo.Xr.Rendering.ViewConstants (400 bytes).
cbuffer ViewConstants : register(b0)
{
    float4 RayRight;           // xyz: ViewRayBasis.Right (orientation offset F14 already applied)
    float4 RayUp;              // xyz: ViewRayBasis.Up
    float4 RayCenter;          // xyz: ViewRayBasis.Center
    float Brightness;          // 1 = full image, 0 = black (fade and blackout from M4)
    uint Projection;           // ProjectionType*
    float FisheyeHalfFov;      // ProjectionParameters.FisheyeHalfFov
    float Reserved;
    float4 SourceScaleOffset;  // the eye's part of the source image (EyeSourceRect: Scale, Offset)
    float4 SourceClamp;        // EyeSourceRect: ClampMin, ClampMax
    float4 YuvRowR;            // video only: VideoColorConstants
    float4 YuvRowG;
    float4 YuvRowB;
    float4 PrimariesRow0;
    float4 PrimariesRow1;
    float4 PrimariesRow2;
    float4 Fisheye;            // ProjectionParameters.Fisheye
    float4 CubeFaces[12];      // ProjectionParameters.Faces
};

Texture2D<float4> SourceImage : register(t0);
Texture2D<float> VideoLuma : register(t1);
Texture2D<float2> VideoChroma : register(t2);
SamplerState SourceSampler : register(s0);

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Ndc : TEXCOORD0;
};

// Fullscreen triangle without a vertex buffer: (-1,-1), (-1,3), (3,-1) covers the whole target.
VertexOutput VsFullscreenTriangle(uint vertexId : SV_VertexID)
{
    float2 ndc = float2(vertexId == 2 ? 3.0f : -1.0f, vertexId == 1 ? 3.0f : -1.0f);

    VertexOutput output;
    output.Position = float4(ndc, 0.0f, 1.0f);
    output.Ndc = ndc;
    return output;
}

// Where the pixel samples the source image; valid = 0 where the projection has no picture (shown black).
float2 SourceUvForPixel(float2 ndc, out float valid)
{
    float3 direction = DirectionFromNdc(RayRight.xyz, RayUp.xyz, RayCenter.xyz, ndc);
    float2 eyeUv = ProjectedEyeUv(direction, Projection, Fisheye, FisheyeHalfFov, CubeFaces, valid);
    return SourceUv(eyeUv, SourceScaleOffset, SourceClamp);
}

// Gamma (docs/DECISIONS.md): the source is read through an *_SRGB view, so sampling returns linear values,
// and the render target is an *_SRGB view that encodes again. The shader therefore works and outputs linear.
float4 PsPanoramaImage(VertexOutput input) : SV_Target
{
    float valid;
    float2 uv = SourceUvForPixel(input.Ndc, valid);

    // SampleLevel 0: no mip selection, so there is no seam where u wraps around at the back.
    float3 color = SourceImage.SampleLevel(SourceSampler, uv, 0.0f).rgb;
    return float4(color * (Brightness * valid), 1.0f);
}

// Video frame: luma and chroma planes of the same NV12/P010 texture, converted and linearized (color.hlsl).
float4 PsPanoramaVideo(VertexOutput input) : SV_Target
{
    float valid;
    float2 uv = SourceUvForPixel(input.Ndc, valid);
    float luma = VideoLuma.SampleLevel(SourceSampler, uv, 0.0f);
    float2 chroma = VideoChroma.SampleLevel(SourceSampler, uv, 0.0f);
    float3 color = VideoSampleToLinear(float3(luma, chroma), YuvRowR, YuvRowG, YuvRowB, PrimariesRow0, PrimariesRow1, PrimariesRow2);
    return float4(color * (Brightness * valid), 1.0f);
}

// Test outputs for Orbivo.Xr.Tests: the raw math results instead of colors.
float4 PsDebugDirection(VertexOutput input) : SV_Target
{
    return float4(DirectionFromNdc(RayRight.xyz, RayUp.xyz, RayCenter.xyz, input.Ndc), 1.0f);
}

float4 PsDebugEquirect360Uv(VertexOutput input) : SV_Target
{
    float3 direction = DirectionFromNdc(RayRight.xyz, RayUp.xyz, RayCenter.xyz, input.Ndc);
    return float4(Equirect360Uv(direction), 0.0f, 1.0f);
}

// xy = source uv, z = valid.
float4 PsDebugSourceUv(VertexOutput input) : SV_Target
{
    float valid;
    float2 uv = SourceUvForPixel(input.Ndc, valid);
    return float4(uv, valid, 1.0f);
}
