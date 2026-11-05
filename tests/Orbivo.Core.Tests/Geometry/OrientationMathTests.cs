using System.Numerics;
using Orbivo.Core.Geometry;
using static Orbivo.Core.Tests.Geometry.GeometryAssert;

namespace Orbivo.Core.Tests.Geometry;

public sealed class OrientationMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 0)]
    [InlineData(-90, 0)]
    [InlineData(180, 0)]
    [InlineData(0, 90)]
    [InlineData(0, -90)]
    [InlineData(45, 30)]
    [InlineData(-120, -60)]
    public void FromYawPitch_ForwardMatchesTestMediaConvention(double yawDegrees, double pitchDegrees)
    {
        Quaternion orientation = OrientationMath.FromYawPitch(Radians(yawDegrees), Radians(pitchDegrees));

        Equal(Direction(yawDegrees, pitchDegrees), Vector3.Transform(OrientationMath.Forward, orientation));
    }

    [Fact]
    public void FromYawPitch_PositiveYawTurnsLeft()
    {
        Quaternion orientation = OrientationMath.FromYawPitch(Radians(90), 0);

        Equal(new Vector3(-1, 0, 0), Vector3.Transform(OrientationMath.Forward, orientation));
    }

    [Fact]
    public void FromYawPitch_PitchesAboutTheLocalAxis()
    {
        // Turn left, then look up 45°: forward ends between left and up. Pitching about the world X axis instead
        // would leave the direction at pure left.
        Quaternion orientation = OrientationMath.FromYawPitch(Radians(90), Radians(45));

        Equal(Vector3.Normalize(new Vector3(-1, 1, 0)), Vector3.Transform(OrientationMath.Forward, orientation));
    }

    [Fact]
    public void FromYawPitch_HasNoRoll()
    {
        // The view's right axis stays horizontal for any yaw/pitch.
        Quaternion orientation = OrientationMath.FromYawPitch(Radians(30), Radians(50));

        Vector3 right = Vector3.Transform(Vector3.UnitX, orientation);
        Assert.Equal(0f, right.Y, 1e-6f);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 10)]
    [InlineData(-135, -45)]
    [InlineData(179, 89)]
    public void ToYawPitch_RoundTrips(double yawDegrees, double pitchDegrees)
    {
        Quaternion orientation = OrientationMath.FromYawPitch(Radians(yawDegrees), Radians(pitchDegrees));

        (float yaw, float pitch) = OrientationMath.ToYawPitch(orientation);

        Assert.Equal(yawDegrees, Degrees(yaw), 1e-3);
        Assert.Equal(pitchDegrees, Degrees(pitch), 1e-3);
    }

    [Fact]
    public void ToYawPitch_StraightUp_ReturnsZeroYaw()
    {
        (float yaw, float pitch) = OrientationMath.ToYawPitch(OrientationMath.FromYawPitch(Radians(70), Radians(90)));

        Assert.Equal(0f, yaw);
        Assert.Equal(90, Degrees(pitch), 1e-3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(180, 180)]
    [InlineData(-180, 180)]
    [InlineData(540, 180)]
    [InlineData(725, 5)]
    public void WrapAngle_ReturnsMinus180To180(double inputDegrees, double expectedDegrees)
    {
        Assert.Equal(expectedDegrees, Degrees(OrientationMath.WrapAngle(Radians(inputDegrees))), 1e-3);
    }
}
