using System.Numerics;
using System.Runtime.InteropServices;
using Orbivo.Core.Geometry;

namespace Orbivo.Xr.Rendering;

/// <summary>
/// Constant buffer of panorama.hlsl (<c>cbuffer ViewConstants</c>). Layout must match the shader exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ViewConstants
{
    public const int SizeInBytes = 64;

    public Vector4 RayRight;
    public Vector4 RayUp;
    public Vector4 RayCenter;
    public float Brightness;
    public Vector3 Reserved;

    public static ViewConstants Create(in ViewParameters view, float brightness)
    {
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(view.Orientation, view.Fov);
        return new ViewConstants
        {
            RayRight = new Vector4(basis.Right, 0f),
            RayUp = new Vector4(basis.Up, 0f),
            RayCenter = new Vector4(basis.Center, 0f),
            Brightness = brightness,
        };
    }
}
