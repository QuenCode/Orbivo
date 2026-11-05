using Orbivo.Core.Geometry;
using Orbivo.Xr.Rendering;

namespace Orbivo.Xr.DebugViewport;

/// <summary>
/// View of the desktop debug viewport, controlled with the mouse. The image is "grabbed" like in
/// YouTube 360 or Street View: the point under the cursor follows the cursor, so dragging to the right
/// turns the view to the left (docs/DECISIONS.md).
/// </summary>
/// <remarks>All members are called on the render thread (the window procedure runs there).</remarks>
public sealed class MouseViewSource : IViewSource
{
    public const float DefaultVerticalFovDegrees = 75f;
    public const float MinVerticalFovDegrees = 30f;
    public const float MaxVerticalFovDegrees = 120f;
    public const float FovStepDegrees = 5f;

    /// <summary>Mouse wheel units per notch (WHEEL_DELTA).</summary>
    public const int WheelDeltaPerNotch = 120;

    private const float HalfPi = MathF.PI / 2f;

    private int _width = 1;
    private int _height = 1;
    private bool _dragging;
    private int _lastX;
    private int _lastY;

    public MouseViewSource()
    {
        Reset();
    }

    /// <summary>Yaw in radians, positive = left.</summary>
    public float Yaw { get; private set; }

    /// <summary>Pitch in radians, positive = up, limited to ±90°.</summary>
    public float Pitch { get; private set; }

    public float VerticalFovDegrees { get; private set; }

    public bool IsDragging => _dragging;

    public int ViewCount => 1;

    public void SetViewportSize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public void BeginDrag(int x, int y)
    {
        _dragging = true;
        _lastX = x;
        _lastY = y;
    }

    public void DragTo(int x, int y)
    {
        if (!_dragging)
        {
            return;
        }

        // Angle per pixel at the view center, so the grabbed point stays under the cursor there.
        float radiansPerPixel = 2f * MathF.Tan(float.DegreesToRadians(VerticalFovDegrees) * 0.5f) / _height;
        int dx = x - _lastX;
        int dy = y - _lastY;
        _lastX = x;
        _lastY = y;

        // Drag right -> image moves right -> view turns left (positive yaw).
        // Drag down (window y grows downwards) -> image moves down -> view tilts up (positive pitch).
        Yaw = OrientationMath.WrapAngle(Yaw + (dx * radiansPerPixel));
        Pitch = Math.Clamp(Pitch + (dy * radiansPerPixel), -HalfPi, HalfPi);
    }

    public void EndDrag()
    {
        _dragging = false;
    }

    /// <summary>Mouse wheel: away from the user (positive delta) zooms in.</summary>
    public void Zoom(int wheelDelta)
    {
        float notches = (float)wheelDelta / WheelDeltaPerNotch;
        VerticalFovDegrees = Math.Clamp(
            VerticalFovDegrees - (notches * FovStepDegrees),
            MinVerticalFovDegrees,
            MaxVerticalFovDegrees);
    }

    /// <summary>Looks forward with the default field of view.</summary>
    public void Reset()
    {
        Yaw = 0f;
        Pitch = 0f;
        VerticalFovDegrees = DefaultVerticalFovDegrees;
    }

    public void GetViews(Span<ViewParameters> views)
    {
        views[0] = new ViewParameters(
            OrientationMath.FromYawPitch(Yaw, Pitch),
            FovAngles.FromVertical(float.DegreesToRadians(VerticalFovDegrees), (float)_width / _height));
    }
}
