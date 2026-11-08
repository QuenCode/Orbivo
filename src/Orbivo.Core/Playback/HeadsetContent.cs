namespace Orbivo.Core.Playback;

/// <summary>What the headset (or the preview) showed in a frame, before blackout and fade. For the head log (F50).</summary>
public enum HeadsetContent : byte
{
    /// <summary>Nothing rendered (no session, headset not showing frames).</summary>
    None,

    /// <summary>A video frame.</summary>
    Video,

    /// <summary>Idle view black (F7), or see-through that the runtime does not offer.</summary>
    Black,

    /// <summary>Idle view: the camera picture (see-through).</summary>
    SeeThrough,

    /// <summary>Idle view: the still image.</summary>
    Image,
}
