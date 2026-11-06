// Projection math shared by all Orbivo shaders.
//
// C# reference: src/Orbivo.Core/Geometry/ProjectionMath.cs. CLAUDE.md rule 8: always change both together.
// The GPU tests in tests/Orbivo.Xr.Tests compare both pixel by pixel.
//
// Coordinates (CLAUDE.md rule 9): right-handed, +X right, +Y up, -Z forward.
// NDC: x = -1 left edge, +1 right edge; y = -1 bottom edge, +1 top edge.
// Texture coordinates: u = 0 left edge, v = 0 top edge of the image.

#ifndef ORBIVO_PROJECTION_HLSL
#define ORBIVO_PROJECTION_HLSL

static const float OrbivoPi = 3.14159265f;

// Normalized world-space viewing direction for a position in normalized device coordinates.
// rayRight, rayUp and rayCenter come from ProjectionMath.CreateViewRayBasis.
float3 DirectionFromNdc(float3 rayRight, float3 rayUp, float3 rayCenter, float2 ndc)
{
    return normalize(ndc.x * rayRight + ndc.y * rayUp + rayCenter);
}

// Texture coordinates in an equirectangular 360 degree image for a normalized direction.
// Image center = forward, left in the image = left, top of the image = up. u wraps around at the back.
float2 Equirect360Uv(float3 direction)
{
    float longitude = atan2(direction.x, -direction.z);
    float latitude = asin(clamp(direction.y, -1.0f, 1.0f));
    return float2(longitude / (2.0f * OrbivoPi) + 0.5f, 0.5f - latitude / OrbivoPi);
}

// Texture coordinates in the whole source image for coordinates within one eye's image (stereo layout, F11).
// scaleOffset: xy = size of the eye's image, zw = its top left corner. clampRange: xy = min, zw = max, in eye coordinates.
// C#: ProjectionMath.SourceUv with the values of ProjectionMath.CreateEyeSourceRect.
float2 SourceUv(float2 eyeUv, float4 scaleOffset, float4 clampRange)
{
    return clamp(eyeUv, clampRange.xy, clampRange.zw) * scaleOffset.xy + scaleOffset.zw;
}

#endif
