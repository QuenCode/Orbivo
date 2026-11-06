using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// Turns the picture of one video (F14), in degrees. Signs as everywhere (docs/DECISIONS.md, right-hand rule):
/// positive yaw turns the picture to the left (what was in front is then on the left), positive pitch tilts the
/// front upwards, positive roll turns the picture counterclockwise as the viewer sees it.
/// </summary>
/// <param name="YawDegrees">Rotation about +Y (up).</param>
/// <param name="PitchDegrees">Rotation about +X (right).</param>
/// <param name="RollDegrees">Rotation about +Z (backwards, towards the viewer).</param>
public readonly record struct OrientationOffset(double YawDegrees, double PitchDegrees, double RollDegrees)
{
    /// <summary>Largest magnitude accepted for each angle.</summary>
    public const double MaxDegrees = 360;

    public static OrientationOffset Zero => default;

    public bool IsZero => YawDegrees == 0 && PitchDegrees == 0 && RollDegrees == 0;

    /// <summary>
    /// Rotation of the picture in the world: a point that lies in direction <c>p</c> of the video appears in
    /// direction <c>Transform(p, rotation)</c>. Roll first, then pitch, then yaw (like a head pose in OpenXR).
    /// </summary>
    public Quaternion ToContentRotation()
    {
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ToRadians(YawDegrees));
        Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, ToRadians(PitchDegrees));
        Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, ToRadians(RollDegrees));

        // q1 * q2 applies q2 first.
        return Quaternion.Normalize(yaw * pitch * roll);
    }

    /// <summary>
    /// Orientation for rendering: a view with <paramref name="viewOrientation"/> in the world looks at the video
    /// through the inverse picture rotation. Allocation-free, render thread.
    /// </summary>
    public Quaternion ApplyToView(Quaternion viewOrientation)
    {
        return IsZero ? viewOrientation : Quaternion.Normalize(Quaternion.Conjugate(ToContentRotation()) * viewOrientation);
    }

    private static float ToRadians(double degrees) => (float)(degrees * Math.PI / 180.0);
}
