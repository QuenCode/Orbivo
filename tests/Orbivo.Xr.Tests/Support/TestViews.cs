using Orbivo.Core.Geometry;

namespace Orbivo.Xr.Tests.Support;

internal static class TestViews
{
    public static float Radians(double degrees) => (float)(degrees * Math.PI / 180.0);

    /// <summary>Desktop-style view: yaw positive = left, pitch positive = up, symmetric FOV.</summary>
    public static ViewParameters Create(double yawDegrees, double pitchDegrees, double verticalFovDegrees, double aspectRatio)
    {
        return new ViewParameters(
            OrientationMath.FromYawPitch(Radians(yawDegrees), Radians(pitchDegrees)),
            FovAngles.FromVertical(Radians(verticalFovDegrees), (float)aspectRatio));
    }
}
