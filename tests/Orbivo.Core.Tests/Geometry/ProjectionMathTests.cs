using System.Numerics;
using Orbivo.Core.Geometry;
using static Orbivo.Core.Tests.Geometry.GeometryAssert;

namespace Orbivo.Core.Tests.Geometry;

public sealed class ProjectionMathTests
{
    [Theory]
    [InlineData(0, 0, 0.5, 0.5)] // forward: image center
    [InlineData(90, 0, 0.25, 0.5)] // left: left half of the image
    [InlineData(-90, 0, 0.75, 0.5)] // right: right half
    [InlineData(45, 0, 0.375, 0.5)] // front left
    [InlineData(0, 90, 0.5, 0.0)] // up: top edge
    [InlineData(0, -90, 0.5, 1.0)] // down: bottom edge
    [InlineData(0, 30, 0.5, 1.0 / 3.0)]
    [InlineData(-30, -45, 0.5 + (30.0 / 360.0), 0.75)]
    public void Equirect360Uv_MapsKnownDirections(double yawDegrees, double pitchDegrees, double expectedU, double expectedV)
    {
        Vector2 uv = ProjectionMath.Equirect360Uv(Direction(yawDegrees, pitchDegrees));

        // At the poles the longitude is arbitrary; only v matters there.
        if (Math.Abs(pitchDegrees) < 90)
        {
            Assert.Equal(expectedU, uv.X, 1e-5);
        }

        Assert.Equal(expectedV, uv.Y, 1e-5);
    }

    [Fact]
    public void Equirect360Uv_BackIsAtTheWrapSeam()
    {
        Vector2 uv = ProjectionMath.Equirect360Uv(new Vector3(0f, 0f, 1f));

        float distanceToSeam = MathF.Min(uv.X, 1f - uv.X);
        Assert.True(distanceToSeam < 1e-6f, $"u = {uv.X}");
        Assert.Equal(0.5f, uv.Y, 1e-6f);
    }

    [Fact]
    public void Equirect360Uv_IsNotMirrored()
    {
        // Slightly right of forward must land right of the image center, slightly above must land above it.
        Vector2 right = ProjectionMath.Equirect360Uv(Direction(-1, 0));
        Vector2 up = ProjectionMath.Equirect360Uv(Direction(0, 1));

        Assert.True(right.X > 0.5f);
        Assert.True(up.Y < 0.5f);
    }

    [Fact]
    public void ViewRayBasis_IdentityWithSquare90DegreeFov_CoversPlusMinus45Degrees()
    {
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(Quaternion.Identity, FovAngles.FromVertical(Radians(90), 1f));

        Equal(new Vector3(0, 0, -1), ProjectionMath.DirectionFromNdc(basis, Vector2.Zero));
        Equal(Vector3.Normalize(new Vector3(1, 0, -1)), ProjectionMath.DirectionFromNdc(basis, new Vector2(1, 0)));
        Equal(Vector3.Normalize(new Vector3(-1, 0, -1)), ProjectionMath.DirectionFromNdc(basis, new Vector2(-1, 0)));
        Equal(Vector3.Normalize(new Vector3(0, 1, -1)), ProjectionMath.DirectionFromNdc(basis, new Vector2(0, 1)));
        Equal(Vector3.Normalize(new Vector3(-1, -1, -1)), ProjectionMath.DirectionFromNdc(basis, new Vector2(-1, -1)));
    }

    [Fact]
    public void ViewRayBasis_AsymmetricFov_EdgesLandOnTheFourAngles()
    {
        var fov = new FovAngles(Radians(-40), Radians(50), Radians(45), Radians(-35));
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(Quaternion.Identity, fov);

        static double Horizontal(Vector3 d) => Degrees(MathF.Atan2(d.X, -d.Z));
        static double Vertical(Vector3 d) => Degrees(MathF.Atan2(d.Y, -d.Z));

        Assert.Equal(-40, Horizontal(ProjectionMath.DirectionFromNdc(basis, new Vector2(-1, 0))), 1e-3);
        Assert.Equal(50, Horizontal(ProjectionMath.DirectionFromNdc(basis, new Vector2(1, 0))), 1e-3);
        Assert.Equal(45, Vertical(ProjectionMath.DirectionFromNdc(basis, new Vector2(0, 1))), 1e-3);
        Assert.Equal(-35, Vertical(ProjectionMath.DirectionFromNdc(basis, new Vector2(0, -1))), 1e-3);
    }

    [Fact]
    public void ViewRayBasis_LookingLeft_CenterIsLeftAndScreenRightPointsForward()
    {
        Quaternion lookLeft = OrientationMath.FromYawPitch(Radians(90), 0);
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(lookLeft, FovAngles.FromVertical(Radians(60), 16f / 9f));

        Vector3 center = ProjectionMath.DirectionFromNdc(basis, Vector2.Zero);
        Vector3 screenRight = ProjectionMath.DirectionFromNdc(basis, new Vector2(1, 0));

        Equal(new Vector3(-1, 0, 0), center);
        Assert.True(screenRight.Z < -0.5f, $"Right edge should turn towards forward (-Z), got {screenRight}.");
        Assert.Equal(0.25f, ProjectionMath.Equirect360Uv(center).X, 1e-5f);
        Assert.True(ProjectionMath.Equirect360Uv(screenRight).X > 0.25f, "Screen right must map to the right in the image.");
    }

    [Fact]
    public void ViewRayBasis_LookingUp_TopOfTheScreenPointsBackwards()
    {
        Quaternion lookUp = OrientationMath.FromYawPitch(0, Radians(90));
        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(lookUp, FovAngles.FromVertical(Radians(60), 1f));

        Vector3 center = ProjectionMath.DirectionFromNdc(basis, Vector2.Zero);
        Vector3 top = ProjectionMath.DirectionFromNdc(basis, new Vector2(0, 1));
        Vector3 bottom = ProjectionMath.DirectionFromNdc(basis, new Vector2(0, -1));

        Equal(new Vector3(0, 1, 0), center);
        Assert.True(top.Z > 0.4f, $"Top edge should tilt backwards (+Z), got {top}.");
        Assert.True(bottom.Z < -0.4f, $"Bottom edge should tilt forwards (-Z), got {bottom}.");
        Assert.Equal(0f, ProjectionMath.Equirect360Uv(center).Y, 1e-5f);
    }

    [Theory]
    [InlineData(0, 0, 4, 2, -0.75, 0.5)]
    [InlineData(3, 1, 4, 2, 0.75, -0.5)]
    [InlineData(0, 0, 1, 1, 0, 0)]
    public void PixelCenterToNdc_RowZeroIsTheTop(int x, int y, int width, int height, double expectedX, double expectedY)
    {
        Vector2 ndc = ProjectionMath.PixelCenterToNdc(x, y, width, height);

        Assert.Equal(expectedX, ndc.X, 1e-6);
        Assert.Equal(expectedY, ndc.Y, 1e-6);
    }
}
