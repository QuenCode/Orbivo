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
    public const int SizeInBytes = 192;

    public Vector4 RayRight;
    public Vector4 RayUp;
    public Vector4 RayCenter;
    public float Brightness;
    public Vector3 Reserved;
    public Vector4 SourceScaleOffset;
    public Vector4 SourceClamp;
    public Vector4 YuvRowR;
    public Vector4 YuvRowG;
    public Vector4 YuvRowB;
    public Vector4 PrimariesRow0;
    public Vector4 PrimariesRow1;
    public Vector4 PrimariesRow2;

    public static ViewConstants Create(in ViewParameters view, in EyeSourceRect source, in VideoColorConstants color, float brightness)
    {
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(view.Orientation, view.Fov);
        return new ViewConstants
        {
            RayRight = new Vector4(basis.Right, 0f),
            RayUp = new Vector4(basis.Up, 0f),
            RayCenter = new Vector4(basis.Center, 0f),
            Brightness = brightness,
            SourceScaleOffset = new Vector4(source.Scale, source.Offset.X, source.Offset.Y),
            SourceClamp = new Vector4(source.ClampMin, source.ClampMax.X, source.ClampMax.Y),
            YuvRowR = color.YuvRowR,
            YuvRowG = color.YuvRowG,
            YuvRowB = color.YuvRowB,
            PrimariesRow0 = color.PrimariesRow0,
            PrimariesRow1 = color.PrimariesRow1,
            PrimariesRow2 = color.PrimariesRow2,
        };
    }
}
