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

    public PlaybackActions Open()
    {
        State = PlaybackState.Opening;
        PlayPending = false;
        return PlaybackActions.OpenMedia | PlaybackActions.HideVideo | PlaybackActions.StopClock;
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
                return PlaybackActions.StartClock | PlaybackActions.ShowVideo;
            case PlaybackState.Paused:
                State = PlaybackState.Playing;
                return PlaybackActions.ResumeClock;
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
                return PlaybackActions.PauseClock;
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
                return PlaybackActions.RestartMedia | PlaybackActions.HideVideo | PlaybackActions.StopClock;
            case PlaybackState.Opening:
                PlayPending = false;
                return PlaybackActions.None;
            default:
                return PlaybackActions.None;
        }
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
        if (State != PlaybackState.Playing)
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
        return PlaybackActions.HideVideo | PlaybackActions.StopClock;
    }
}
