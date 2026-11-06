using System.Numerics;
using System.Runtime.InteropServices;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;

namespace Orbivo.Xr.Rendering;

/// <summary>
/// Constant buffer of panorama.hlsl (<c>cbuffer ViewConstants</c>). Layout must match the shader exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ViewConstants
{
    public const int SizeInBytes = 400;

    public Vector4 RayRight;
    public Vector4 RayUp;
    public Vector4 RayCenter;
    public float Brightness;
    public uint Projection;
    public float FisheyeHalfFov;
    public float Reserved;
    public Vector4 SourceScaleOffset;
    public Vector4 SourceClamp;
    public Vector4 YuvRowR;
    public Vector4 YuvRowG;
    public Vector4 YuvRowB;
    public Vector4 PrimariesRow0;
    public Vector4 PrimariesRow1;
    public Vector4 PrimariesRow2;
    public Vector4 Fisheye;
    public CubeFaceRows CubeFaces;

    /// <param name="view">Orientation and field of view of the view in the world.</param>
    /// <param name="offset">Orientation offset of the picture (F14), applied to the view here.</param>
    /// <param name="projection">Projection constants of the eye.</param>
    /// <param name="source">The eye's part of the source image.</param>
    /// <param name="color">YUV conversion for video, default for images.</param>
    /// <param name="brightness">1 = full image, 0 = black.</param>
    public static ViewConstants Create(
        in ViewParameters view,
        in OrientationOffset offset,
        in ProjectionParameters projection,
        in EyeSourceRect source,
        in VideoColorConstants color,
        float brightness)
    {
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(offset.ApplyToView(view.Orientation), view.Fov);
        return new ViewConstants
        {
            RayRight = new Vector4(basis.Right, 0f),
            RayUp = new Vector4(basis.Up, 0f),
            RayCenter = new Vector4(basis.Center, 0f),
            Brightness = brightness,
            Projection = (uint)projection.Projection,
            FisheyeHalfFov = projection.FisheyeHalfFov,
            SourceScaleOffset = new Vector4(source.Scale, source.Offset.X, source.Offset.Y),
            SourceClamp = new Vector4(source.ClampMin, source.ClampMax.X, source.ClampMax.Y),
            YuvRowR = color.YuvRowR,
            YuvRowG = color.YuvRowG,
            YuvRowB = color.YuvRowB,
            PrimariesRow0 = color.PrimariesRow0,
            PrimariesRow1 = color.PrimariesRow1,
            PrimariesRow2 = color.PrimariesRow2,
            Fisheye = projection.Fisheye,
            CubeFaces = projection.Faces,
        };
    }
}
