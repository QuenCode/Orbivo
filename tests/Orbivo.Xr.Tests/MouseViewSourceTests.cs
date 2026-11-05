using System.Numerics;
using Orbivo.Core.Geometry;
using Orbivo.Xr.DebugViewport;

namespace Orbivo.Xr.Tests;

public sealed class MouseViewSourceTests
{
    [Fact]
    public void StartsLookingForward()
    {
        var source = new MouseViewSource();

        Assert.Equal(0f, source.Yaw);
        Assert.Equal(0f, source.Pitch);
        Assert.Equal(MouseViewSource.DefaultVerticalFovDegrees, source.VerticalFovDegrees);
        Assert.Equal(1, source.ViewCount);
    }

    [Fact]
    public void DragRight_GrabsTheImage_AndTurnsTheViewLeft()
    {
        var source = CreateWithFov90(height: 200);

        // 90° vertical FOV over 200 px: 2 * tan(45°) / 200 = 0.01 rad per pixel at the center.
        source.BeginDrag(100, 100);
        source.DragTo(200, 100);

        Assert.Equal(1.0f, source.Yaw, 1e-5f);
        Assert.Equal(0f, source.Pitch);
    }

    [Fact]
    public void DragDown_TiltsTheViewUp()
    {
        var source = CreateWithFov90(height: 200);

        source.BeginDrag(100, 100);
        source.DragTo(100, 150);

        Assert.Equal(0.5f, source.Pitch, 1e-5f);
    }

    [Fact]
    public void Pitch_IsLimitedTo90Degrees()
    {
        var source = CreateWithFov90(height: 200);

        source.BeginDrag(0, 0);
        source.DragTo(0, 10_000);

        Assert.Equal(MathF.PI / 2f, source.Pitch, 1e-6f);
    }

    [Fact]
    public void MovingWithoutButton_DoesNothing()
    {
        var source = CreateWithFov90(height: 200);

        source.DragTo(150, 150);
        source.BeginDrag(0, 0);
        source.EndDrag();
        source.DragTo(150, 150);

        Assert.Equal(0f, source.Yaw);
        Assert.Equal(0f, source.Pitch);
    }

    [Fact]
    public void WheelAway_ZoomsIn_WithinLimits()
    {
        var source = new MouseViewSource();

        source.Zoom(MouseViewSource.WheelDeltaPerNotch);
        Assert.Equal(MouseViewSource.DefaultVerticalFovDegrees - MouseViewSource.FovStepDegrees, source.VerticalFovDegrees);

        source.Zoom(-100 * MouseViewSource.WheelDeltaPerNotch);
        Assert.Equal(MouseViewSource.MaxVerticalFovDegrees, source.VerticalFovDegrees);

        source.Zoom(100 * MouseViewSource.WheelDeltaPerNotch);
        Assert.Equal(MouseViewSource.MinVerticalFovDegrees, source.VerticalFovDegrees);
    }

    [Fact]
    public void Reset_LooksForwardAgain()
    {
        var source = CreateWithFov90(height: 200);
        source.BeginDrag(0, 0);
        source.DragTo(80, 30);
        source.Zoom(240);

        source.Reset();

        Assert.Equal(0f, source.Yaw);
        Assert.Equal(0f, source.Pitch);
        Assert.Equal(MouseViewSource.DefaultVerticalFovDegrees, source.VerticalFovDegrees);
    }

    [Fact]
    public void GetViews_UsesYawPitchAndWindowAspect()
    {
        var source = new MouseViewSource();
        source.SetViewportSize(1600, 800);
        source.BeginDrag(0, 0);
        source.DragTo(400, 0);

        Span<ViewParameters> views = stackalloc ViewParameters[1];
        source.GetViews(views);

        Vector3 forward = Vector3.Transform(OrientationMath.Forward, views[0].Orientation);
        Assert.True(forward.X < 0f, "Dragging right must look to the left (-X).");
        Assert.Equal(2.0, Math.Tan(views[0].Fov.AngleRight) / Math.Tan(views[0].Fov.AngleUp), 1e-4);
    }

    private static MouseViewSource CreateWithFov90(int height)
    {
        var source = new MouseViewSource();
        source.SetViewportSize(height, height);
        source.Zoom(-3 * MouseViewSource.WheelDeltaPerNotch); // 75° -> 90°
        Assert.Equal(90f, source.VerticalFovDegrees);
        return source;
    }
}
