using System.Numerics;

namespace Orbivo.Core.Tests.Geometry;

internal static class GeometryAssert
{
    /// <summary>Unit vector for yaw/pitch in degrees, same formula as tools/testmedia (conventions.direction_vector).</summary>
    public static Vector3 Direction(double yawDegrees, double pitchDegrees)
    {
        double yaw = yawDegrees * Math.PI / 180.0;
        double pitch = pitchDegrees * Math.PI / 180.0;
        return new Vector3(
            (float)(-Math.Sin(yaw) * Math.Cos(pitch)),
            (float)Math.Sin(pitch),
            (float)(-Math.Cos(yaw) * Math.Cos(pitch)));
    }

    public static float Radians(double degrees) => (float)(degrees * Math.PI / 180.0);

    public static double Degrees(float radians) => radians * 180.0 / Math.PI;

    public static void Equal(Vector3 expected, Vector3 actual, float tolerance = 1e-5f)
    {
        Assert.True(
            Vector3.Distance(expected, actual) <= tolerance,
            $"Expected {expected}, got {actual} (distance {Vector3.Distance(expected, actual)}).");
    }

    public static void Equal(Vector2 expected, Vector2 actual, float tolerance = 1e-5f)
    {
        Assert.True(
            Vector2.Distance(expected, actual) <= tolerance,
            $"Expected {expected}, got {actual} (distance {Vector2.Distance(expected, actual)}).");
    }
}
