using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// Helpers for orientations as quaternions (OpenXR convention, CLAUDE.md rule 9).
/// Yaw is a rotation about +Y by the right-hand rule, so positive yaw turns LEFT. Positive pitch looks up.
/// </summary>
public static class OrientationMath
{
    /// <summary>Forward direction in view space and in the world: −Z.</summary>
    public static readonly Vector3 Forward = new(0f, 0f, -1f);

    /// <summary>
    /// Orientation of a view that first tilts by <paramref name="pitch"/> about its own X axis
    /// and then turns by <paramref name="yaw"/> about the world Y axis (no roll). Angles in radians.
    /// </summary>
    public static Quaternion FromYawPitch(float yaw, float pitch)
    {
        // q1 * q2 applies q2 first: pitch about the local X axis, then yaw about the world Y axis.
        Quaternion yawRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        Quaternion pitchRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
        return Quaternion.Normalize(yawRotation * pitchRotation);
    }

    /// <summary>
    /// Yaw and pitch (radians) of the forward direction of an orientation. Roll is ignored.
    /// Looking straight up or down, the yaw is undefined and returned as 0.
    /// </summary>
    public static (float Yaw, float Pitch) ToYawPitch(Quaternion orientation)
    {
        Vector3 forward = Vector3.Transform(Forward, orientation);
        float horizontal = MathF.Sqrt((forward.X * forward.X) + (forward.Z * forward.Z));
        float yaw = horizontal < 1e-6f ? 0f : MathF.Atan2(-forward.X, -forward.Z);
        float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        return (yaw, pitch);
    }

    /// <summary>
    /// Yaw, pitch and roll (radians) of a head pose, in the order OpenXR heads turn: yaw about the world Y axis, then
    /// pitch about the head's X axis, then roll about its Z axis (positive = counterclockwise as the person sees it).
    /// <c>orientation = yaw · pitch · roll</c>. Looking straight up or down, yaw is 0 and the turn goes into roll.
    /// </summary>
    public static (float Yaw, float Pitch, float Roll) ToYawPitchRoll(Quaternion orientation)
    {
        orientation = Quaternion.Normalize(orientation);
        (float yaw, float pitch) = ToYawPitch(orientation);

        // What is left after taking out yaw and pitch is a pure turn about Z.
        Quaternion rest = Quaternion.Conjugate(FromYawPitch(yaw, pitch)) * orientation;
        if (rest.W < 0f)
        {
            rest = Quaternion.Negate(rest);
        }

        float roll = WrapAngle(2f * MathF.Atan2(rest.Z, rest.W));
        return (yaw, pitch, roll);
    }

    /// <summary>Wraps an angle in radians into (−π, π].</summary>
    public static float WrapAngle(float radians)
    {
        float wrapped = MathF.IEEERemainder(radians, 2f * MathF.PI);
        return wrapped <= -MathF.PI ? wrapped + (2f * MathF.PI) : wrapped;
    }
}
