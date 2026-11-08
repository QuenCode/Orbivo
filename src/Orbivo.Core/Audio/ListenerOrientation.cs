using System.Numerics;
using Orbivo.Core.Concurrency;

namespace Orbivo.Core.Audio;

/// <summary>One published head orientation for the sound.</summary>
/// <param name="HeadInContent">
/// Orientation of the head in the video's own frame: the head pose with the picture's orientation offset (F14) taken
/// out, exactly as the renderer looks into the video. Identity = looking at the front of the picture.
/// </param>
/// <param name="Timestamp">Stopwatch timestamp the pose belongs to (predicted display time in the headset).</param>
public readonly record struct ListenerPose(Quaternion HeadInContent, long Timestamp)
{
    public static ListenerPose Front => new(Quaternion.Identity, 0);
}

/// <summary>
/// Hands the head orientation from the render thread to the audio output thread (docs/ARCHITECTURE.md, "Threads"):
/// the render thread of the headset (or of the preview with <c>--no-xr</c>) writes it every frame, the audio thread
/// reads it every block. Neither side blocks or allocates (CLAUDE.md rules 1 and 2). Only one render thread may write.
/// </summary>
public sealed class ListenerOrientation
{
    private readonly SnapshotCell<ListenerPose> _cell = new(ListenerPose.Front);

    /// <summary>Render thread. The renderer composes the head and the picture offset, so picture and sound always agree.</summary>
    public void Publish(Quaternion headInContent, long timestamp)
    {
        _cell.Write(new ListenerPose(headInContent, timestamp));
    }

    /// <summary>Any thread. The latest pose; looking at the front until the first one is published.</summary>
    public ListenerPose Read() => _cell.Read();
}
