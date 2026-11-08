using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// Recenter (F30/F31): the test person's current viewing direction becomes the front of the video, yaw only.
/// The player asks for it (UI command), the render thread of the headset or the preview carries it out with the head
/// pose of its next frame and applies the correction to every view, so picture, live view and sound agree
/// (docs/ARCHITECTURE.md, "Rendering"). Kept across videos and reconnects, not saved (docs/DECISIONS.md, M8).
/// </summary>
/// <remarks>
/// Requests from any thread, carried out by one render thread at a time. Neither side blocks or allocates
/// (CLAUDE.md rule 1). The latest request wins.
/// </remarks>
public sealed class RecenterControl
{
    private const int None = 0;
    private const int Recenter = 1;
    private const int Reset = 2;

    /// <summary>Below this length of the horizontal part of the forward direction (about 84° up or down) the head's up axis decides.</summary>
    private const float SteepLookThreshold = 0.1f;

    private int _pending;
    private float _yawRadians;
    private int _version;
    private Quaternion _correction = Quaternion.Identity;

    /// <summary>Any thread. Turn of the video's front, in degrees (positive = to the left, right-hand rule).</summary>
    public float YawDegrees => float.RadiansToDegrees(Volatile.Read(ref _yawRadians));

    /// <summary>Any thread. Counts every change the render thread carried out (recenter or reset).</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Any thread. True while a request waits for the render thread (e.g. no head tracking yet).</summary>
    public bool IsPending => Volatile.Read(ref _pending) != None;

    /// <summary>Any thread. The next frame with a tracked head makes its viewing direction the front.</summary>
    public void RequestRecenter() => Volatile.Write(ref _pending, Recenter);

    /// <summary>Any thread. The video's own front applies again from the next frame.</summary>
    public void RequestReset() => Volatile.Write(ref _pending, Reset);

    /// <summary>
    /// Render thread, once per frame with the head orientation of that frame. Carries out a waiting request and
    /// returns the correction to put in front of every view orientation: <c>view' = correction · view</c>.
    /// A recenter waits while the head is not tracked.
    /// </summary>
    public Quaternion Update(Quaternion head, bool headTracked)
    {
        if (Volatile.Read(ref _pending) != None)
        {
            int request = Interlocked.Exchange(ref _pending, None);
            if (request == Recenter && !headTracked)
            {
                // Keep it for a later frame, unless a newer request came in meanwhile.
                Interlocked.CompareExchange(ref _pending, Recenter, None);
            }
            else if (request is Recenter or Reset)
            {
                float yaw = request == Recenter ? HeadingOf(head) : 0f;
                _correction = CorrectionFor(yaw);
                Volatile.Write(ref _yawRadians, yaw);
                Interlocked.Increment(ref _version);
            }
        }

        return _correction;
    }

    /// <summary>
    /// Heading (yaw about +Y, radians, positive = left) the head faces. Exact for any pitch and roll up to about 84°
    /// up or down (the forward direction projected onto the horizon); steeper, the head's up axis tells where the face points.
    /// </summary>
    public static float HeadingOf(Quaternion head)
    {
        head = Quaternion.Normalize(head);
        Vector3 forward = Vector3.Transform(OrientationMath.Forward, head);
        var horizontal = new Vector2(forward.X, forward.Z);
        if (horizontal.Length() < SteepLookThreshold)
        {
            // Looking down, the top of the head points forward; looking up, it points backward.
            Vector3 up = Vector3.Transform(Vector3.UnitY, head);
            float sign = forward.Y < 0f ? 1f : -1f;
            horizontal = new Vector2(forward.X + (sign * up.X), forward.Z + (sign * up.Z));
        }

        return horizontal.LengthSquared() < 1e-12f ? 0f : MathF.Atan2(-horizontal.X, -horizontal.Y);
    }

    /// <summary>Puts the correction from <see cref="Update"/> in front of a view's orientation. Allocation-free.</summary>
    public static ViewParameters Apply(in ViewParameters view, Quaternion correction)
    {
        return correction.IsIdentity ? view : view with { Orientation = Quaternion.Normalize(correction * view.Orientation) };
    }

    /// <summary>Correction that turns a view facing <paramref name="yawRadians"/> back to the front.</summary>
    public static Quaternion CorrectionFor(float yawRadians)
    {
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, -yawRadians);
    }
}

/// <summary>Recenter state for the operator UI (a snapshot, CLAUDE.md rule 3).</summary>
/// <param name="Available">False if the render thread has no recenter control.</param>
/// <param name="YawDegrees">Turn of the video's front, positive = to the left; 0 = the video's own front.</param>
/// <param name="Pending">True while a request waits for the render thread (no head tracking).</param>
/// <param name="Version">Counts the changes carried out, so a reader sees a recenter to the same angle.</param>
public readonly record struct RecenterStatus(bool Available, float YawDegrees, bool Pending, int Version)
{
    public bool IsActive => YawDegrees != 0f;

    public static RecenterStatus Of(RecenterControl? control)
    {
        return control is null ? default : new RecenterStatus(true, control.YawDegrees, control.IsPending, control.Version);
    }
}
