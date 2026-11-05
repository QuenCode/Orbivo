using Orbivo.Core.Geometry;
using static Orbivo.Core.Tests.Geometry.GeometryAssert;

namespace Orbivo.Core.Tests.Geometry;

public sealed class FovAnglesTests
{
    [Fact]
    public void FromVertical_SquareAspect_IsSymmetric()
    {
        FovAngles fov = FovAngles.FromVertical(Radians(90), 1f);

        Assert.Equal(-45, Degrees(fov.AngleLeft), 1e-4);
        Assert.Equal(45, Degrees(fov.AngleRight), 1e-4);
        Assert.Equal(45, Degrees(fov.AngleUp), 1e-4);
        Assert.Equal(-45, Degrees(fov.AngleDown), 1e-4);
    }

    [Fact]
    public void FromVertical_WideAspect_WidensHorizontallyWithSquarePixels()
    {
        FovAngles fov = FovAngles.FromVertical(Radians(90), 2f);

        // tan(half horizontal) = aspect * tan(half vertical) = 2.
        Assert.Equal(2.0, Math.Tan(fov.AngleRight), 1e-5);
        Assert.Equal(-2.0, Math.Tan(fov.AngleLeft), 1e-5);
        Assert.Equal(90, Degrees(fov.Vertical), 1e-4);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(180, 1)]
    [InlineData(90, 0)]
    [InlineData(90, -1)]
    [InlineData(double.NaN, 1)]
    public void FromVertical_RejectsInvalidArguments(double verticalDegrees, double aspect)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FovAngles.FromVertical(Radians(verticalDegrees), (float)aspect));
    }
}
