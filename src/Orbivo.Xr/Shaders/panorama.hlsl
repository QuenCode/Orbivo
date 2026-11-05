// Renders a panorama with one fullscreen triangle per view (docs/ARCHITECTURE.md, "Rendering").
// M1: equirectangular 360 degree, mono.

#include "projection.hlsl"

// Layout must match Orbivo.Xr.Rendering.ViewConstants (64 bytes).
cbuffer ViewConstants : register(b0)
{
    float4 RayRight;   // xyz: ViewRayBasis.Right
    float4 RayUp;      // xyz: ViewRayBasis.Up
    float4 RayCenter;  // xyz: ViewRayBasis.Center
    float Brightness;  // 1 = full image, 0 = black (fade and blackout from M4)
    float3 Reserved;
};

Texture2D<float4> SourceImage : register(t0);
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

// Gamma (docs/DECISIONS.md): the source is read through an *_SRGB view, so sampling returns linear values,
// and the render target is an *_SRGB view that encodes again. The shader therefore works and outputs linear.
float4 PsEquirect360(VertexOutput input) : SV_Target
{
    float3 direction = DirectionFromNdc(RayRight.xyz, RayUp.xyz, RayCenter.xyz, input.Ndc);
    float2 uv = Equirect360Uv(direction);

    // SampleLevel 0: no mip selection, so there is no seam where u wraps around at the back.
    float3 color = SourceImage.SampleLevel(SourceSampler, uv, 0.0f).rgb;
    return float4(color * Brightness, 1.0f);
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
