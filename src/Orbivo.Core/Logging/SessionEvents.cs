using System.Collections.Concurrent;
using System.Diagnostics;

namespace Orbivo.Core.Logging;

/// <summary>Kind of an entry of the event log (F51); the German label is what the CSV shows.</summary>
public enum SessionEventKind
{
    /// <summary>Recording started or ended, settings of the recording.</summary>
    Recording,

    /// <summary>A video is opened.</summary>
    Load,

    /// <summary>Operator command Play.</summary>
    Play,

    /// <summary>Operator command Pause.</summary>
    Pause,

    /// <summary>Operator command Stop.</summary>
    Stop,

    /// <summary>The video was closed (removed from the playlist).</summary>
    Close,

    /// <summary>Jump to a time.</summary>
    Seek,

    /// <summary>The transport state changed (ready, playing, paused, end, no video).</summary>
    State,

    /// <summary>Loop on or off.</summary>
    Loop,

    /// <summary>Blackout on or off.</summary>
    Blackout,

    /// <summary>Fade to black or back to the picture.</summary>
    Fade,

    /// <summary>Recenter requested, carried out or taken back.</summary>
    Recenter,

    /// <summary>Format of the video changed by the operator.</summary>
    Format,

    /// <summary>Sound fixed to the picture, stereo width.</summary>
    Sound,

    /// <summary>Volume.</summary>
    Volume,

    /// <summary>Mute on or off.</summary>
    Mute,

    /// <summary>Sound output device.</summary>
    AudioDevice,

    /// <summary>Idle view or idle image changed.</summary>
    IdleView,

    /// <summary>Headset connection and OpenXR session state.</summary>
    Headset,

    /// <summary>An error the operator saw.</summary>
    Error,
}

/// <summary>One entry of the event log.</summary>
/// <param name="Timestamp">Stopwatch timestamp when it happened.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Details">Details in German, e.g. "von 0:12,0 nach 1:30,0".</param>
/// <param name="VideoFile">Loaded video (full path); null = the one of the latest head sample.</param>
/// <param name="VideoTime">Video time; null = the one of the latest head sample.</param>
public sealed record SessionEvent(long Timestamp, SessionEventKind Kind, string Details, string? VideoFile = null, TimeSpan? VideoTime = null);

/// <summary>Takes events from any thread for the event log (F51). Never blocks; dropped while no recording runs.</summary>
public interface ISessionEventLog
{
    /// <summary>Any thread except the render and audio threads in their loops (allocates).</summary>
    void Record(SessionEventKind kind, string details, string? videoFile = null, TimeSpan? videoTime = null);
}

/// <summary>Collects events for the writer thread of the recording. Thread-safe.</summary>
public sealed class SessionEventQueue : ISessionEventLog
{
    /// <summary>More than this many waiting events are dropped (the writer is stuck), so memory stays bounded.</summary>
    public const int MaxPending = 10_000;

    private readonly ConcurrentQueue<SessionEvent> _events = new();
    private int _active;
    private long _dropped;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>Events lost because too many were waiting.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    public void Record(SessionEventKind kind, string details, string? videoFile = null, TimeSpan? videoTime = null)
    {
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        if (_events.Count >= MaxPending)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _events.Enqueue(new SessionEvent(Stopwatch.GetTimestamp(), kind, details, videoFile, videoTime));
    }

    internal void Activate()
    {
        _events.Clear();
        Volatile.Write(ref _dropped, 0);
        Volatile.Write(ref _active, 1);
    }

    internal void Deactivate() => Volatile.Write(ref _active, 0);

    internal bool TryTake(out SessionEvent? sessionEvent) => _events.TryDequeue(out sessionEvent);

    /// <summary>German label for the CSV.</summary>
    public static string Label(SessionEventKind kind)
    {
        return kind switch
        {
            SessionEventKind.Recording => "Aufzeichnung",
            SessionEventKind.Load => "Laden",
            SessionEventKind.Play => "Play",
            SessionEventKind.Pause => "Pause",
            SessionEventKind.Stop => "Stop",
            SessionEventKind.Close => "Entladen",
            SessionEventKind.Seek => "Seek",
            SessionEventKind.State => "Zustand",
            SessionEventKind.Loop => "Loop",
            SessionEventKind.Blackout => "Schwarzbild",
            SessionEventKind.Fade => "Fade",
            SessionEventKind.Recenter => "Recenter",
            SessionEventKind.Format => "Format",
            SessionEventKind.Sound => "Ton",
            SessionEventKind.Volume => "Lautstärke",
            SessionEventKind.Mute => "Stumm",
            SessionEventKind.AudioDevice => "Tonausgabe",
            SessionEventKind.IdleView => "Leerlauf",
            SessionEventKind.Headset => "Brille",
            SessionEventKind.Error => "Fehler",
            _ => kind.ToString(),
        };
    }
}
