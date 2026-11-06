using System.Numerics;
using Orbivo.Core.Geometry;
using static Orbivo.Core.Tests.Geometry.GeometryAssert;

namespace Orbivo.Core.Tests.Geometry;

/// <summary>
/// Orientation offset (F14), signs from docs/DECISIONS.md: the offset turns the picture; positive yaw = to the left,
/// positive pitch = front upwards, positive roll = counterclockwise as the viewer sees it.
/// </summary>
public sealed class OrientationOffsetTests
{
    [Theory]
    [InlineData(30, 0, 0, 30, 0)] // yaw +30: the front of the picture appears 30° to the left
    [InlineData(-45, 0, 0, -45, 0)]
    [InlineData(0, 20, 0, 0, 20)] // pitch +20: the front appears 20° up
    [InlineData(0, 0, 90, 0, 0)] // roll leaves the front where it is
    public void ContentRotation_MovesTheFront(double yaw, double pitch, double roll, double expectedYaw, double expectedPitch)
    {
        var offset = new OrientationOffset(yaw, pitch, roll);

        Vector3 front = Vector3.Transform(OrientationMath.Forward, offset.ToContentRotation());

        Equal(Direction(expectedYaw, expectedPitch), front, 1e-5f);
    }

    [Fact]
    public void Roll_TurnsCounterclockwiseForTheViewer()
    {
        // Counterclockwise: what was on the right moves up.
        var offset = new OrientationOffset(0, 0, 90);

        Vector3 right = Vector3.Transform(Vector3.UnitX, offset.ToContentRotation());

        Equal(Vector3.UnitY, right, 1e-5f);
    }

    [Fact]
    public void ApplyToView_LookingForward_SeesWhatWasOnTheRight()
    {
        // The picture turned 30° to the left: straight ahead now shows what the video has 30° to the right.
        var offset = new OrientationOffset(30, 0, 0);

        Quaternion view = offset.ApplyToView(Quaternion.Identity);
        Vector3 videoDirection = Vector3.Transform(OrientationMath.Forward, view);

        Equal(Direction(-30, 0), videoDirection, 1e-5f);
        Vector2 uv = ProjectionMath.Equirect360Uv(videoDirection);
        Assert.Equal(0.5 + (30.0 / 360.0), uv.X, 1e-5);
    }

    [Fact]
    public void ApplyToView_LookingAtTheTurnedFront_SeesTheFront()
    {
        var offset = new OrientationOffset(40, -15, 25);
        Vector3 shownFront = Vector3.Transform(OrientationMath.Forward, offset.ToContentRotation());

        // A view that looks where the front is shown sees the video's front in its center.
        Quaternion lookAtFront = OrientationMath.FromYawPitch(Radians(40), Radians(-15));
        Equal(shownFront, Vector3.Transform(OrientationMath.Forward, lookAtFront), 1e-5f);
        Vector3 seen = Vector3.Transform(OrientationMath.Forward, offset.ApplyToView(lookAtFront));

        Equal(OrientationMath.Forward, seen, 1e-5f);
    }

    [Fact]
    public void Zero_LeavesTheViewUnchanged()
    {
        Quaternion view = OrientationMath.FromYawPitch(0.3f, -0.2f);

        Assert.Equal(view, OrientationOffset.Zero.ApplyToView(view));
        Assert.True(OrientationOffset.Zero.IsZero);
    }
}
