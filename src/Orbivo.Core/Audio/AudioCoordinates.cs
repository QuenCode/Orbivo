using System.Numerics;

namespace Orbivo.Core.Audio;

/// <summary>
/// The one place where directions of the sound are converted (CLAUDE.md rule 9, docs/ARCHITECTURE.md "Audio").
/// </summary>
/// <remarks>
/// Directions in the video (content frame) use the OpenXR convention: right-handed, +X right, +Y up, −Z front;
/// an azimuth is measured like a yaw, positive to the LEFT. Steam Audio uses the same axes (phonon.h: "+x right, +y up,
/// −z ahead"), and its binaural effect takes the direction from the listener to the source in the listener's own
/// frame. So a direction relative to the head goes to Steam Audio unchanged; only the head rotation is taken out here.
/// </remarks>
public static class AudioCoordinates
{
    /// <summary>
    /// Direction of a sound in the horizontal plane of the video, <paramref name="azimuthDegrees"/> from the front of
    /// the picture, positive to the left (like yaw).
    /// </summary>
    public static Vector3 HorizontalDirection(double azimuthDegrees)
    {
        double radians = azimuthDegrees * Math.PI / 180.0;
        return new Vector3((float)-Math.Sin(radians), 0f, (float)-Math.Cos(radians));
    }

    /// <summary>
    /// Where a sound from <paramref name="contentDirection"/> in the video comes from for a head with orientation
    /// <paramref name="headInContent"/> (see <see cref="ListenerPose"/>): the direction in the head's frame, which is
    /// what Steam Audio expects. Allocation-free.
    /// </summary>
    public static Vector3 ToListener(Vector3 contentDirection, Quaternion headInContent)
    {
        Vector3 relative = Vector3.Transform(contentDirection, Quaternion.Conjugate(headInContent));
        float length = relative.Length();
        return length > 1e-6f ? relative / length : OrientationFront;
    }

    /// <summary>
    /// Directions of the left and right virtual speaker (at ±<paramref name="widthDegrees"/>) relative to the head.
    /// </summary>
    public static (Vector3 Left, Vector3 Right) StereoSpeakers(double widthDegrees, Quaternion headInContent)
    {
        return (
            ToListener(HorizontalDirection(widthDegrees), headInContent),
            ToListener(HorizontalDirection(-widthDegrees), headInContent));
    }

    private static Vector3 OrientationFront => new(0f, 0f, -1f);
}
