namespace Orbivo.Core.Playback;

/// <summary>Playback state as the operator sees it.</summary>
public enum PlaybackState
{
    /// <summary>No video loaded.</summary>
    NoMedia,

    /// <summary>The video is being opened and the first frames are decoded. The headset is black.</summary>
    Opening,

    /// <summary>Ready at the beginning. The headset is black until Play (F6).</summary>
    Ready,

    Playing,

    /// <summary>The current frame stays visible; the clock stands still.</summary>
    Paused,

    /// <summary>The end of the video is reached; the last frame stays visible.</summary>
    Ended,

    /// <summary>Opening or decoding failed; details in the status and the log.</summary>
    Failed,
}

/// <summary>What the player has to do after a transition. Several can apply at once.</summary>
[Flags]
public enum PlaybackActions
{
    None = 0,

    /// <summary>Open the requested file (closing the current one).</summary>
    OpenMedia = 1 << 0,

    /// <summary>Open the current file again from the beginning.</summary>
    RestartMedia = 1 << 1,

    /// <summary>Start the clock at the first frame of the video.</summary>
    StartClock = 1 << 2,

    PauseClock = 1 << 3,

    ResumeClock = 1 << 4,

    /// <summary>Stop the clock at the beginning.</summary>
    StopClock = 1 << 5,

    /// <summary>Show video frames in the headset.</summary>
    ShowVideo = 1 << 6,

    /// <summary>Show black in the headset.</summary>
    HideVideo = 1 << 7,

    /// <summary>
    /// Jump to the requested position: hold the clock at the target and let the decoder decode from there.
    /// The headset keeps the last frame until the new one is ready.
    /// </summary>
    SeekMedia = 1 << 8,

    /// <summary>Close decoders and file; nothing is loaded afterwards.</summary>
    CloseMedia = 1 << 9,
}

/// <summary>
/// Transport logic of the player without any I/O (docs/ARCHITECTURE.md, "Core/Playback"). The player control thread
/// feeds it commands and media events and carries out the returned actions.
/// </summary>
public sealed class PlaybackStateMachine
{
    public PlaybackState State { get; private set; } = PlaybackState.NoMedia;

    /// <summary>True if Play was pressed while the video was still opening; playback starts as soon as it is ready.</summary>
    public bool PlayPending { get; private set; }

    /// <summary>
    /// True while a seek runs in <see cref="PlaybackState.Ready"/>, <see cref="PlaybackState.Playing"/> or
    /// <see cref="PlaybackState.Paused"/>. The clock stands still until <see cref="SeekCompleted"/>.
    /// A seek while opening is part of opening and does not set this.
    /// </summary>
    public bool Seeking { get; private set; }

    public PlaybackActions Open()
    {
        State = PlaybackState.Opening;
        PlayPending = false;
        Seeking = false;
        return PlaybackActions.OpenMedia | PlaybackActions.HideVideo | PlaybackActions.StopClock;
    }

    /// <summary>The operator closes the video (playlist entry removed). Allowed in every state, also while opening.</summary>
    public PlaybackActions Close()
    {
        if (State == PlaybackState.NoMedia)
        {
            return PlaybackActions.None;
        }

        State = PlaybackState.NoMedia;
        PlayPending = false;
        Seeking = false;
        return PlaybackActions.CloseMedia | PlaybackActions.HideVideo | PlaybackActions.StopClock;
    }

    public PlaybackActions Play()
    {
        switch (State)
        {
            case PlaybackState.Opening:
                PlayPending = true;
                return PlaybackActions.None;
            case PlaybackState.Ready:
                State = PlaybackState.Playing;

                // During a seek the clock starts once the new position is decoded (SeekCompleted).
                return Seeking ? PlaybackActions.ShowVideo : PlaybackActions.StartClock | PlaybackActions.ShowVideo;
            case PlaybackState.Paused:
                State = PlaybackState.Playing;
                return Seeking ? PlaybackActions.None : PlaybackActions.ResumeClock;
            case PlaybackState.Ended:
                State = PlaybackState.Opening;
                PlayPending = true;
                return PlaybackActions.RestartMedia | PlaybackActions.HideVideo | PlaybackActions.StopClock;
            default:
                return PlaybackActions.None;
        }
    }

    public PlaybackActions Pause()
    {
        switch (State)
        {
            case PlaybackState.Playing:
                State = PlaybackState.Paused;
                return Seeking ? PlaybackActions.None : PlaybackActions.PauseClock;
            case PlaybackState.Opening:
                PlayPending = false;
                return PlaybackActions.None;
            default:
                return PlaybackActions.None;
        }
    }

    public PlaybackActions Stop()
    {
        switch (State)
        {
            case PlaybackState.Playing:
            case PlaybackState.Paused:
            case PlaybackState.Ended:
                State = PlaybackState.Opening;
                PlayPending = false;
                Seeking = false;
                return PlaybackActions.RestartMedia | PlaybackActions.HideVideo | PlaybackActions.StopClock;
            case PlaybackState.Opening:
                PlayPending = false;
                return PlaybackActions.None;
            default:
                return PlaybackActions.None;
        }
    }

    /// <summary>
    /// The operator jumps to another position. Ready stays black, Playing and Paused keep their state,
    /// Ended becomes Paused. While opening, the decoder starts at the target instead of the beginning.
    /// </summary>
    public PlaybackActions Seek()
    {
        switch (State)
        {
            case PlaybackState.Opening:
                return PlaybackActions.SeekMedia;
            case PlaybackState.Ready:
            case PlaybackState.Playing:
            case PlaybackState.Paused:
                Seeking = true;
                return PlaybackActions.SeekMedia;
            case PlaybackState.Ended:
                State = PlaybackState.Paused;
                Seeking = true;
                return PlaybackActions.SeekMedia;
            default:
                return PlaybackActions.None;
        }
    }

    /// <summary>The frames at the seek target are decoded.</summary>
    public PlaybackActions SeekCompleted()
    {
        if (!Seeking)
        {
            return PlaybackActions.None;
        }

        Seeking = false;
        return State == PlaybackState.Playing ? PlaybackActions.ResumeClock : PlaybackActions.None;
    }

    /// <summary>The first frames are decoded.</summary>
    public PlaybackActions MediaReady()
    {
        if (State != PlaybackState.Opening)
        {
            return PlaybackActions.None;
        }

        if (PlayPending)
        {
            PlayPending = false;
            State = PlaybackState.Playing;
            return PlaybackActions.StartClock | PlaybackActions.ShowVideo;
        }

        State = PlaybackState.Ready;
        return PlaybackActions.None;
    }

    /// <summary>The last frame has been due for its full duration.</summary>
    public PlaybackActions EndReached()
    {
        if (State != PlaybackState.Playing || Seeking)
        {
            return PlaybackActions.None;
        }

        State = PlaybackState.Ended;
        return PlaybackActions.PauseClock;
    }

    /// <summary>Opening or decoding failed.</summary>
    public PlaybackActions MediaFailed()
    {
        if (State == PlaybackState.NoMedia)
        {
            return PlaybackActions.None;
        }

        State = PlaybackState.Failed;
        PlayPending = false;
        Seeking = false;
        return PlaybackActions.HideVideo | PlaybackActions.StopClock;
    }
}
