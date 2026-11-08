using System.Numerics;
using Orbivo.Core.Geometry;
using static Orbivo.Core.Tests.Geometry.GeometryAssert;

namespace Orbivo.Core.Tests.Geometry;

/// <summary>Recenter (F30/F31): the current viewing direction becomes the front of the video, yaw only.</summary>
public sealed class RecenterControlTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(30, 0, 0)]
    [InlineData(-120, 0, 0)]
    [InlineData(179, 0, 0)]
    [InlineData(30, 40, 20)] // pitch and roll do not change the heading
    [InlineData(-75, -60, -35)]
    [InlineData(45, 89.9, 0)] // almost straight up
    [InlineData(45, -90, 0)] // straight down
    [InlineData(-100, 90, 0)] // straight up
    [InlineData(60, 87, 10)] // steep and rolled
    public void HeadingOf_IsTheYawOfTheHead(double yaw, double pitch, double roll)
    {
        Quaternion head = Head(yaw, pitch, roll);

        double heading = Degrees(RecenterControl.HeadingOf(head));

        Assert.Equal(yaw, heading, Math.Abs(roll) > 0 && Math.Abs(pitch) > 85 ? 12.0 : 1e-3);
    }

    [Fact]
    public void Recenter_MakesTheViewingDirectionTheFront()
    {
        var control = new RecenterControl();
        Quaternion head = Head(70, 25, 0);

        control.RequestRecenter();
        Quaternion correction = control.Update(head, headTracked: true);

        // The same head now looks at the front of the video, still 25° up.
        Vector3 forward = Vector3.Transform(OrientationMath.Forward, correction * head);
        Equal(Direction(0, 25), forward, 1e-5f);
        Assert.Equal(70, control.YawDegrees, 1e-3);
        Assert.Equal(1, control.Version);
        Assert.False(control.IsPending);
    }

    [Fact]
    public void Recenter_StaysUntilReset()
    {
        var control = new RecenterControl();
        control.RequestRecenter();
        control.Update(Head(-40, 0, 0), headTracked: true);

        // Turning the head on afterwards turns the view like before, relative to the new front.
        Quaternion correction = control.Update(Head(-10, 0, 0), headTracked: true);
        Vector3 forward = Vector3.Transform(OrientationMath.Forward, correction * Head(-10, 0, 0));
        Equal(Direction(30, 0), forward, 1e-5f);

        control.RequestReset();
        Assert.Equal(Quaternion.Identity, control.Update(Head(-10, 0, 0), headTracked: true));
        Assert.Equal(0, control.YawDegrees);
        Assert.Equal(2, control.Version);
    }

    [Fact]
    public void Recenter_WaitsForHeadTracking()
    {
        var control = new RecenterControl();
        control.RequestRecenter();

        Assert.Equal(Quaternion.Identity, control.Update(Head(50, 0, 0), headTracked: false));
        Assert.True(control.IsPending);
        Assert.Equal(0, control.Version);

        control.Update(Head(50, 0, 0), headTracked: true);
        Assert.False(control.IsPending);
        Assert.Equal(50, control.YawDegrees, 1e-3);
    }

    [Fact]
    public void LatestRequestWins()
    {
        var control = new RecenterControl();
        control.RequestRecenter();
        control.Update(Head(50, 0, 0), headTracked: true);

        control.RequestRecenter();
        control.RequestReset();
        control.Update(Head(20, 0, 0), headTracked: true);

        Assert.Equal(0, control.YawDegrees);
    }

    [Fact]
    public void Recenter_AgainFromARecenteredView_UsesTheRealHead()
    {
        // The render thread always passes the tracked head, not the corrected one.
        var control = new RecenterControl();
        control.RequestRecenter();
        control.Update(Head(30, 0, 0), headTracked: true);
        control.RequestRecenter();
        control.Update(Head(90, 0, 0), headTracked: true);

        Assert.Equal(90, control.YawDegrees, 1e-3);
    }

    [Fact]
    public void Update_WithoutRequest_DoesNotAllocate()
    {
        var control = new RecenterControl();
        control.Update(Quaternion.Identity, headTracked: true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            control.Update(Head(i % 360, 0, 0), headTracked: true);
            if (i % 100 == 0)
            {
                control.RequestRecenter();
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Head pose like OpenXR: yaw about the world Y axis, then pitch, then roll in the head's own frame.</summary>
    private static Quaternion Head(double yaw, double pitch, double roll)
    {
        return new OrientationOffset(yaw, pitch, roll).ToContentRotation();
    }
}
