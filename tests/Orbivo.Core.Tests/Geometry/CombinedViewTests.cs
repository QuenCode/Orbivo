using System.Numerics;
using Orbivo.Core.Geometry;
using static Orbivo.Core.Tests.Geometry.GeometryAssert;

namespace Orbivo.Core.Tests.Geometry;

public sealed class CombinedViewTests
{
    [Fact]
    public void ParallelEyes_GiveTheUnionOfBothFieldsOfView()
    {
        // Like a headset: the left eye sees further left, the right eye further right; the inner edges overlap.
        Quaternion head = OrientationMath.FromYawPitch(Radians(30), Radians(-10));
        var left = new ViewParameters(head, new FovAngles(Radians(-55), Radians(40), Radians(45), Radians(-50)));
        var right = new ViewParameters(head, new FovAngles(Radians(-40), Radians(55), Radians(47), Radians(-48)));

        ViewParameters combined = CombinedView.Create(left, right);

        Assert.Equal(-55, Degrees(combined.Fov.AngleLeft), 3);
        Assert.Equal(55, Degrees(combined.Fov.AngleRight), 3);
        Assert.Equal(47, Degrees(combined.Fov.AngleUp), 3);
        Assert.Equal(-50, Degrees(combined.Fov.AngleDown), 3);
        Equal(Vector3.Transform(OrientationMath.Forward, head), Vector3.Transform(OrientationMath.Forward, combined.Orientation));
    }

    [Fact]
    public void CantedEyes_LookAlongTheHeadAndWidenTheView()
    {
        // Each eye turned 10° outwards: left eye yaw +10° (left), right eye yaw -10° (right).
        var fov = new FovAngles(Radians(-45), Radians(45), Radians(45), Radians(-45));
        var left = new ViewParameters(OrientationMath.FromYawPitch(Radians(10), 0f), fov);
        var right = new ViewParameters(OrientationMath.FromYawPitch(Radians(-10), 0f), fov);

        ViewParameters combined = CombinedView.Create(left, right);

        Equal(OrientationMath.Forward, Vector3.Transform(OrientationMath.Forward, combined.Orientation));
        Assert.Equal(-55, Degrees(combined.Fov.AngleLeft), 2);
        Assert.Equal(55, Degrees(combined.Fov.AngleRight), 2);
        Assert.Equal(45, Degrees(combined.Fov.AngleUp), 2);
        Assert.Equal(-45, Degrees(combined.Fov.AngleDown), 2);
    }

    [Fact]
    public void OppositeQuaternionSigns_GiveTheSameView()
    {
        var fov = new FovAngles(Radians(-50), Radians(50), Radians(40), Radians(-40));
        Quaternion head = OrientationMath.FromYawPitch(Radians(170), Radians(5));
        ViewParameters combined = CombinedView.Create(new ViewParameters(head, fov), new ViewParameters(-head, fov));

        Equal(Vector3.Transform(OrientationMath.Forward, head), Vector3.Transform(OrientationMath.Forward, combined.Orientation));
        Assert.Equal(fov.AngleLeft, combined.Fov.AngleLeft, 4);
        Assert.Equal(fov.AngleRight, combined.Fov.AngleRight, 4);
    }

    [Fact]
    public void VeryWideEdges_AreLimited()
    {
        var fov = new FovAngles(Radians(-89), Radians(89), Radians(89), Radians(-89));
        ViewParameters combined = CombinedView.Create(new ViewParameters(Quaternion.Identity, fov), new ViewParameters(Quaternion.Identity, fov));

        Assert.Equal(CombinedView.MaxEdgeAngle, combined.Fov.AngleRight, 5);
        Assert.Equal(-CombinedView.MaxEdgeAngle, combined.Fov.AngleDown, 5);
        Assert.True(float.IsFinite(CombinedView.AspectRatio(combined.Fov)));
    }

    [Fact]
    public void AspectRatio_UsesTheFlatImage()
    {
        // ±45° horizontal: width 2; +45°/-26.57° vertical: height 1 + 0.5.
        var fov = new FovAngles(Radians(-45), Radians(45), Radians(45), MathF.Atan(-0.5f));
        Assert.Equal(2f / 1.5f, CombinedView.AspectRatio(fov), 4);
    }

    [Theory]
    [InlineData(1.6f, 1920, 1080, 1728, 1080)]
    [InlineData(1.6f, 800, 1000, 800, 500)]
    [InlineData(1f, 300, 200, 200, 200)]
    [InlineData(1f, 0, 200, 1, 200)]
    public void FitSize_KeepsTheAspectRatioInsideTheLimit(float aspect, int maxWidth, int maxHeight, int width, int height)
    {
        Assert.Equal((width, height), CombinedView.FitSize(aspect, maxWidth, maxHeight));
    }
}
