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

// Values of Orbivo.Core.Formats.ProjectionType.
static const uint ProjectionTypeEquirect360 = 0;
static const uint ProjectionTypeEquirect180 = 1;
static const uint ProjectionTypeCubemap = 2;
static const uint ProjectionTypeEac = 3;
static const uint ProjectionTypeFisheye = 4;

// Equirectangular 180 degree (VR180): the front half, left edge = 90 degrees left. valid = 0 for the back half.
float2 Equirect180Uv(float3 direction, out float valid)
{
    float longitude = atan2(direction.x, -direction.z);
    float latitude = asin(clamp(direction.y, -1.0f, 1.0f));
    valid = abs(longitude) <= 0.5f * OrbivoPi ? 1.0f : 0.0f;
    return float2(longitude / OrbivoPi + 0.5f, 0.5f - latitude / OrbivoPi);
}

// The cube face a normalized direction hits (Orbivo.Core.Formats.CubeFace: 0 right, 1 left, 2 up, 3 down, 4 front,
// 5 back) and the position on it: s to the right, t upwards, in [-1, 1], as seen from the middle of the cube.
// Ties on a cube edge go to X before Y before Z, as in C#.
uint CubeFaceSt(float3 direction, out float2 st)
{
    float3 a = abs(direction);
    if (a.x >= a.y && a.x >= a.z)
    {
        if (direction.x > 0.0f)
        {
            st = float2(direction.z, direction.y) / a.x;
            return 0;
        }

        st = float2(-direction.z, direction.y) / a.x;
        return 1;
    }

    if (a.y >= a.z)
    {
        if (direction.y > 0.0f)
        {
            st = float2(direction.x, direction.z) / a.y;
            return 2;
        }

        st = float2(direction.x, -direction.z) / a.y;
        return 3;
    }

    if (direction.z < 0.0f)
    {
        st = float2(direction.x, direction.y) / a.z;
        return 4;
    }

    st = float2(-direction.x, direction.y) / a.z;
    return 5;
}

// Equi-angular correction (EAC): tan(angle) in [-1, 1] -> angle / 45 degrees in [-1, 1].
float2 EquiAngular(float2 st)
{
    return (4.0f / OrbivoPi) * atan(st);
}

// Texture coordinates within one eye's picture for a normalized direction, for every projection (F10).
// valid = 0 where the picture has nothing (back of VR180, outside the fisheye circle).
// Constants from ProjectionMath.CreateProjectionParameters: fisheye = circle center (xy) and radius along u and v (zw),
// fisheyeHalfFov in radians, faces = two rows per cube face (transform, then center and clamp half size).
float2 ProjectedEyeUv(float3 direction, uint projection, float4 fisheye, float fisheyeHalfFov, float4 faces[12], out float valid)
{
    valid = 1.0f;
    if (projection == ProjectionTypeEquirect180)
    {
        return Equirect180Uv(direction, valid);
    }

    if (projection == ProjectionTypeCubemap || projection == ProjectionTypeEac)
    {
        float2 st;
        uint face = CubeFaceSt(direction, st);
        if (projection == ProjectionTypeEac)
        {
            st = EquiAngular(st);
        }

        float2 q = float2(0.5f * st.x, -0.5f * st.y);
        float4 transform = faces[2 * face];
        float4 placement = faces[2 * face + 1];
        float2 uv = placement.xy + float2(dot(transform.xy, q), dot(transform.zw, q));
        return clamp(uv, placement.xy - placement.zw, placement.xy + placement.zw);
    }

    if (projection == ProjectionTypeFisheye)
    {
        // Equidistant: the angle from the optical axis (forward, -Z) grows linearly with the distance from the center.
        float2 lateral = direction.xy;
        float lateralLength = length(lateral);
        float angle = atan2(lateralLength, -direction.z);
        valid = angle <= fisheyeHalfFov ? 1.0f : 0.0f;
        float scale = (lateralLength > 1e-6f ? angle / max(lateralLength, 1e-6f) : 1.0f) / fisheyeHalfFov;
        return float2(fisheye.x + lateral.x * scale * fisheye.z, fisheye.y - lateral.y * scale * fisheye.w);
    }

    return Equirect360Uv(direction);
}

// Texture coordinates in the whole source image for coordinates within one eye's image (stereo layout, F11).
// scaleOffset: xy = size of the eye's image, zw = its top left corner. clampRange: xy = min, zw = max, in eye coordinates.
// C#: ProjectionMath.SourceUv with the values of ProjectionMath.CreateEyeSourceRect.
float2 SourceUv(float2 eyeUv, float4 scaleOffset, float4 clampRange)
{
    return clamp(eyeUv, clampRange.xy, clampRange.zw) * scaleOffset.xy + scaleOffset.zw;
}

#endif
