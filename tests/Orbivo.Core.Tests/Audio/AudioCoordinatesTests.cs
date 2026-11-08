using System.Numerics;
using Orbivo.Core.Audio;
using Orbivo.Core.Geometry;

namespace Orbivo.Core.Tests.Audio;

public sealed class AudioCoordinatesTests
{
    private const float Tolerance = 1e-5f;

    private static readonly Vector3 Front = new(0, 0, -1);
    private static readonly Vector3 Back = new(0, 0, 1);
    private static readonly Vector3 Left = new(-1, 0, 0);
    private static readonly Vector3 Right = new(1, 0, 0);
    private static readonly Vector3 Up = new(0, 1, 0);
    private static readonly Vector3 Down = new(0, -1, 0);

    [Theory]
    [InlineData(0, 0f, 0f, -1f)]
    [InlineData(90, -1f, 0f, 0f)]
    [InlineData(-90, 1f, 0f, 0f)]
    [InlineData(180, 0f, 0f, 1f)]
    public void HorizontalDirection_PositiveAzimuthIsLeft(double azimuth, float x, float y, float z)
    {
        AssertClose(new Vector3(x, y, z), AudioCoordinates.HorizontalDirection(azimuth));
    }

    [Fact]
    public void ToListener_LookingAtTheFront_KeepsDirections()
    {
        AssertClose(Left, AudioCoordinates.ToListener(Left, Quaternion.Identity));
        AssertClose(Front, AudioCoordinates.ToListener(Front, Quaternion.Identity));
    }

    [Fact]
    public void ToListener_HeadTurnedLeft_LeftSoundIsInFrontAndFrontSoundIsRight()
    {
        // The example from the requirement: the tree on the left moves to the right when the head turns left.
        Quaternion turnedLeft = OrientationMath.FromYawPitch(MathF.PI / 2, 0);

        AssertClose(Front, AudioCoordinates.ToListener(Left, turnedLeft));
        AssertClose(Right, AudioCoordinates.ToListener(Front, turnedLeft));
        AssertClose(Back, AudioCoordinates.ToListener(Right, turnedLeft));
    }

    [Fact]
    public void ToListener_HeadTurnedRight_FrontSoundIsLeft()
    {
        Quaternion turnedRight = OrientationMath.FromYawPitch(-MathF.PI / 2, 0);

        AssertClose(Left, AudioCoordinates.ToListener(Front, turnedRight));
    }

    [Fact]
    public void ToListener_LookingUp_FrontSoundIsBelow()
    {
        Quaternion lookingUp = OrientationMath.FromYawPitch(0, MathF.PI / 2);

        AssertClose(Down, AudioCoordinates.ToListener(Front, lookingUp));
        AssertClose(Front, AudioCoordinates.ToListener(Up, lookingUp));
    }

    [Fact]
    public void ToListener_HeadTiltedOntoTheRightShoulder_SoundFromAboveIsLeft()
    {
        // A negative angle about +Z (pointing backwards) tilts the head to the right: right ear down, left ear up.
        Quaternion tiltedRight = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);

        AssertClose(Left, AudioCoordinates.ToListener(Up, tiltedRight));
        AssertClose(Down, AudioCoordinates.ToListener(Left, tiltedRight));
    }

    [Fact]
    public void PictureOffset_TurnsTheSoundWithThePicture()
    {
        // Yaw +90° moves the picture's front to the left; the head still looks at the world front. The renderer looks
        // into the video with offset.ApplyToView(head), and so does the sound: its front speaker is now on the left.
        var offset = new OrientationOffset(90, 0, 0);
        Quaternion headInContent = offset.ApplyToView(Quaternion.Identity);

        AssertClose(Left, AudioCoordinates.ToListener(Front, headInContent));
    }

    [Fact]
    public void StereoSpeakers_SitLeftAndRightOfTheFront()
    {
        (Vector3 left, Vector3 right) = AudioCoordinates.StereoSpeakers(60, Quaternion.Identity);

        AssertClose(new Vector3(-MathF.Sqrt(3) / 2, 0, -0.5f), left);
        AssertClose(new Vector3(MathF.Sqrt(3) / 2, 0, -0.5f), right);
    }

    [Fact]
    public void StereoSpeakers_HeadTurnedLeftBy90_BothSpeakersAreOnTheRight()
    {
        // Speakers at +60° and −60°, head at +90°: they are now at −30° (front right) and −150° (back right).
        (Vector3 left, Vector3 right) = AudioCoordinates.StereoSpeakers(60, OrientationMath.FromYawPitch(MathF.PI / 2, 0));

        AssertClose(AudioCoordinates.HorizontalDirection(-30), left);
        AssertClose(AudioCoordinates.HorizontalDirection(-150), right);
    }

    [Fact]
    public void ToListener_DoesNotAllocate()
    {
        Quaternion head = OrientationMath.FromYawPitch(0.3f, 0.1f);
        AudioCoordinates.StereoSpeakers(45, head);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            AudioCoordinates.StereoSpeakers(45, head);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < Tolerance, $"expected {expected}, got {actual}");
    }
}
