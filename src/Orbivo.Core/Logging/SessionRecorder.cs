using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Geometry;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Logging;

/// <summary>Settings of one recording.</summary>
/// <param name="Folder">Folder for the two files; created if missing.</param>
/// <param name="Label">Optional test person or session name for the file names, e.g. "P07"; may be empty.</param>
/// <param name="HeadSampleRateHz">Rows per second of the head log, 0 = every frame.</param>
/// <param name="AppVersion">Written into the first event.</param>
public sealed record RecordingOptions(string Folder, string Label, int HeadSampleRateHz, string AppVersion)
{
    /// <summary>What the headset shows while idle when the recording starts (German), written as the first idle view event.</summary>
    public string? IdleView { get; init; }
}

/// <summary>State of the recording for the operator UI (a snapshot, CLAUDE.md rule 3).</summary>
/// <param name="Active">True while files are written.</param>
/// <param name="HeadFilePath">Head log of the current or last recording, or null.</param>
/// <param name="EventFilePath">Event log of the current or last recording, or null.</param>
/// <param name="HeadRows">Rows written to the head log.</param>
/// <param name="EventRows">Rows written to the event log.</param>
/// <param name="DroppedRows">Head and event rows lost because the writer fell behind.</param>
/// <param name="Error">Why the recording stopped or could not start (German), or null.</param>
/// <param name="Duration">Time since the start of the recording.</param>
public sealed record RecordingStatus(
    bool Active,
    string? HeadFilePath,
    string? EventFilePath,
    long HeadRows,
    long EventRows,
    long DroppedRows,
    string? Error,
    TimeSpan Duration)
{
    public static RecordingStatus Idle { get; } = new(false, null, null, 0, 0, 0, null, TimeSpan.Zero);
}

/// <summary>
/// Writes the head log (F50) and the event log (F51) of a session as two German CSV files, on its own thread
/// (docs/ARCHITECTURE.md, "Threads"). The render thread hands samples over through <see cref="Samples"/>, everyone
/// else events through <see cref="Events"/>; neither ever waits for the disk.
/// </summary>
/// <remarks>
/// Times: one anchor (UTC and Stopwatch) at the start of each recording; every row's UTC time is the anchor plus the
/// Stopwatch time since, so the column is monotonic even if the system clock is adjusted (F50).
/// </remarks>
public sealed class SessionRecorder : IDisposable
{
    public const string HeadFileSuffix = "_kopf.csv";
    public const string EventFileSuffix = "_ereignisse.csv";

    public const string HeadHeader =
        "Zeit_UTC;Sekunden;Video;Videozeit_s;Status;Inhalt;Helligkeit;Tracking;Kopf_qw;Kopf_qx;Kopf_qy;Kopf_qz;"
        + "Kopf_Yaw_Grad;Kopf_Pitch_Grad;Kopf_Roll_Grad;Blick_Video_Yaw_Grad;Blick_Video_Pitch_Grad;Recenter_Yaw_Grad";

    public const string EventHeader = "Zeit_UTC;Sekunden;Ereignis;Video;Videozeit_s;Details";

    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly RecenterControl? _recenter;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly StringBuilder _line = new(256);

    // Requests from the UI thread, taken by the writer thread under _gate.
    private RecordingOptions? _startRequest;
    private bool _stopRequest;
    private bool _disposing;
    private int? _rateRequest;

    // Writer thread only.
    private StreamWriter? _headWriter;
    private StreamWriter? _eventWriter;
    private DateTime _anchorUtc;
    private long _anchorTimestamp;
    private long _lastFlush;
    private int _recenterVersion;
    private string? _lastVideoPath;
    private string _lastVideoName = string.Empty;
    private HeadSample _lastSample;
    private bool _hasLastSample;

    // Status, written by the writer thread.
    private RecordingStatus _status = RecordingStatus.Idle;
    private long _headRows;
    private long _eventRows;

    /// <param name="logger">Diagnostic log.</param>
    /// <param name="recenter">Recenter, logged when the render thread carried it out; null = not logged.</param>
    public SessionRecorder(ILogger logger, RecenterControl? recenter)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _recenter = recenter;
        _recenterVersion = recenter?.Version ?? 0;
        _thread = new Thread(WriterThread)
        {
            Name = "Orbivo session recorder",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Hand-over from the render thread (headset or preview).</summary>
    public HeadSampleGate Samples { get; } = new();

    /// <summary>Hand-over from the player, the app and the render host (connection events).</summary>
    public SessionEventQueue Events { get; } = new();

    /// <summary>Any thread.</summary>
    public RecordingStatus Status
    {
        get
        {
            RecordingStatus status = Volatile.Read(ref _status);
            return status.Active
                ? status with
                {
                    HeadRows = Volatile.Read(ref _headRows),
                    EventRows = Volatile.Read(ref _eventRows),
                    DroppedRows = Samples.Dropped + Events.Dropped,
                    Duration = Stopwatch.GetElapsedTime(_anchorTimestamp),
                }
                : status;
        }
    }

    /// <summary>File names of a recording: <c>2026-11-08_141203_P07_kopf.csv</c> and <c>…_ereignisse.csv</c>.</summary>
    public static string FileStem(DateTime localStart, string label)
    {
        string stamp = localStart.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string clean = CleanLabel(label);
        return clean.Length == 0 ? stamp : $"{stamp}_{clean}";
    }

    /// <summary>The label as it goes into a file name: invalid characters become "_", at most 40 characters.</summary>
    public static string CleanLabel(string label)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (char c in label.Trim())
        {
            builder.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        string clean = builder.ToString().Trim('_', '.');
        return clean.Length > 40 ? clean[..40] : clean;
    }

    /// <summary>Starts a new recording (a running one ends first). UI thread; the files open on the writer thread.</summary>
    public void Start(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            _startRequest = options;
            _stopRequest = false;

            // The error of an earlier recording no longer counts; a new one shows if this start fails too.
            Volatile.Write(ref _status, Volatile.Read(ref _status) with { Error = null });
        }

        _wake.Set();
    }

    /// <summary>Ends the recording; queued rows are still written. UI thread.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _startRequest = null;
            _stopRequest = true;
        }

        _wake.Set();
    }

    /// <summary>Changes the rate of the head log of the running recording and of the next ones. Any thread.</summary>
    public void SetHeadSampleRate(int rateHz)
    {
        lock (_gate)
        {
            _rateRequest = rateHz;
        }

        _wake.Set();
    }

    /// <summary>Ends a running recording (all queued rows written) and stops the thread.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposing = true;
        }

        _wake.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Session recorder thread did not stop within 5 s; the last rows may be missing.");
            return;
        }

        _wake.Dispose();
    }

    private void WriterThread()
    {
        while (true)
        {
            _wake.WaitOne(DrainInterval);
            RecordingOptions? start;
            bool stop;
            bool disposing;
            int? rate;
            lock (_gate)
            {
                start = _startRequest;
                stop = _stopRequest;
                disposing = _disposing;
                rate = _rateRequest;
                _startRequest = null;
                _stopRequest = false;
                _rateRequest = null;
            }

            try
            {
                if (rate is { } hz && _headWriter is not null)
                {
                    Samples.SetRate(hz);
                    WriteEvent(new SessionEvent(Stopwatch.GetTimestamp(), SessionEventKind.Recording, $"Kopf-Log: {DescribeRate(hz)}"));
                }

                Drain();
                if (stop || start is not null || disposing)
                {
                    EndRecording(disposing ? "App wird beendet" : start is not null ? "Neue Aufzeichnung" : "Vom Operator beendet");
                }

                if (disposing)
                {
                    return;
                }

                if (start is not null)
                {
                    BeginRecording(start);
                }

                if (_headWriter is not null && Stopwatch.GetElapsedTime(_lastFlush) >= FlushInterval)
                {
                    _headWriter.Flush();
                    _eventWriter?.Flush();
                    _lastFlush = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Security.SecurityException)
            {
                // Disk full, folder gone, invalid folder: the recording stops, the app runs on (status shows why).
                Fail(exception);
            }
        }
    }

    private void BeginRecording(RecordingOptions options)
    {
        DateTime localNow = DateTime.Now;
        string stem = FileStem(localNow, options.Label);
        Directory.CreateDirectory(options.Folder);
        string headPath = UniquePath(options.Folder, stem, HeadFileSuffix);
        string eventPath = Path.Combine(options.Folder, Path.GetFileName(headPath)[..^HeadFileSuffix.Length] + EventFileSuffix);

        _headWriter = new StreamWriter(new FileStream(headPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), GermanCsv.FileEncoding, 1 << 16);
        try
        {
            _eventWriter = new StreamWriter(new FileStream(eventPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), GermanCsv.FileEncoding, 1 << 14);
        }
        catch
        {
            _headWriter.Dispose();
            _headWriter = null;
            throw;
        }

        _headWriter.WriteLine(HeadHeader);
        _eventWriter.WriteLine(EventHeader);
        _anchorTimestamp = Stopwatch.GetTimestamp();
        _anchorUtc = DateTime.UtcNow;
        _lastFlush = _anchorTimestamp;
        _hasLastSample = false;
        Volatile.Write(ref _headRows, 0);
        Volatile.Write(ref _eventRows, 0);
        _recenterVersion = _recenter?.Version ?? 0;

        // Rows queued before the anchor belong to no recording.
        while (Samples.TryTake(out _))
        {
        }

        Events.Activate();
        Samples.Activate(options.HeadSampleRateHz);
        Volatile.Write(ref _status, new RecordingStatus(true, headPath, eventPath, 0, 0, 0, null, TimeSpan.Zero));

        string label = CleanLabel(options.Label);
        WriteEvent(new SessionEvent(
            _anchorTimestamp,
            SessionEventKind.Recording,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Start. Orbivo {options.AppVersion}, Kopf-Log: {DescribeRate(options.HeadSampleRateHz)}, Proband/Sitzung: {(label.Length == 0 ? "–" : label)}, Rechner: {Environment.MachineName}, Zeitzone: {TimeZoneInfo.Local.Id}")));
        if (options.IdleView is { } idleView)
        {
            WriteEvent(new SessionEvent(_anchorTimestamp, SessionEventKind.IdleView, idleView + " (beim Start)"));
        }

        if (_recenter is { } recenter && recenter.YawDegrees != 0f)
        {
            WriteEvent(new SessionEvent(_anchorTimestamp, SessionEventKind.Recenter, DescribeRecenter(recenter.YawDegrees) + " (schon vor dem Start)"));
        }

        _logger.LogInformation("Recording started: {HeadFile} and {EventFile}, head log {Rate}.", headPath, eventPath, DescribeRate(options.HeadSampleRateHz));
    }

    private void EndRecording(string reason)
    {
        if (_headWriter is null)
        {
            return;
        }

        Samples.Deactivate();
        Drain();
        Events.Deactivate();
        long dropped = Samples.Dropped + Events.Dropped;
        WriteEvent(new SessionEvent(
            Stopwatch.GetTimestamp(),
            SessionEventKind.Recording,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Ende ({reason}). {Volatile.Read(ref _headRows)} Kopf-Zeilen, {Volatile.Read(ref _eventRows) + 1} Ereignisse{(dropped > 0 ? $", {dropped} Zeilen verloren" : string.Empty)}")));
        CloseFiles();
        RecordingStatus last = Status;
        Volatile.Write(ref _status, last with
        {
            Active = false,
            HeadRows = Volatile.Read(ref _headRows),
            EventRows = Volatile.Read(ref _eventRows),
            DroppedRows = dropped,
            Duration = Stopwatch.GetElapsedTime(_anchorTimestamp),
        });
        _logger.LogInformation("Recording ended ({Reason}): {Rows} head rows, {Events} events, {Dropped} rows lost.", reason, _headRows, _eventRows, dropped);
    }

    private void Drain()
    {
        if (_headWriter is null)
        {
            return;
        }

        PollRecenter();

        // Events and samples go to separate files; each file is in time order on its own.
        while (Events.TryTake(out SessionEvent? sessionEvent))
        {
            WriteEvent(sessionEvent!);
        }

        while (Samples.TryTake(out HeadSample sample))
        {
            if (sample.Timestamp < _anchorTimestamp - Stopwatch.Frequency)
            {
                continue;
            }

            WriteSample(sample);
            _lastSample = sample;
            _hasLastSample = true;
        }
    }

    private void PollRecenter()
    {
        if (_recenter is not { } recenter)
        {
            return;
        }

        int version = recenter.Version;
        if (version != _recenterVersion)
        {
            _recenterVersion = version;
            WriteEvent(new SessionEvent(Stopwatch.GetTimestamp(), SessionEventKind.Recenter, DescribeRecenter(recenter.YawDegrees)));
        }
    }

    private void WriteSample(in HeadSample sample)
    {
        StringBuilder line = _line.Clear();
        AppendTime(line, sample.Timestamp);
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendText(line, VideoName(sample.VideoFile));
        GermanCsv.AppendSeparator(line);
        if (sample.VideoPtsTicks != long.MinValue)
        {
            GermanCsv.AppendNumber(line, sample.VideoPtsTicks / (double)TimeSpan.TicksPerSecond, 3);
        }

        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendText(line, DescribeState(sample.State));
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendText(line, DescribeContent(sample.Content));
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendNumber(line, sample.Brightness, 3);
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendInteger(line, sample.Tracked ? 1 : 0);

        Quaternion head = Quaternion.Normalize(sample.TrackedHead);
        foreach (float component in (ReadOnlySpan<float>)[head.W, head.X, head.Y, head.Z])
        {
            GermanCsv.AppendSeparator(line);
            GermanCsv.AppendNumber(line, component, 6);
        }

        (float yaw, float pitch, float roll) = OrientationMath.ToYawPitchRoll(head);
        (float videoYaw, float videoPitch) = OrientationMath.ToYawPitch(Quaternion.Normalize(sample.HeadInContent));
        foreach (float radians in (ReadOnlySpan<float>)[yaw, pitch, roll, videoYaw, videoPitch])
        {
            GermanCsv.AppendSeparator(line);
            GermanCsv.AppendNumber(line, float.RadiansToDegrees(radians), 2);
        }

        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendNumber(line, sample.RecenterYawDegrees, 2);
        _headWriter!.WriteLine(line);
        Interlocked.Increment(ref _headRows);
    }

    private void WriteEvent(SessionEvent sessionEvent)
    {
        if (_eventWriter is null)
        {
            return;
        }

        StringBuilder line = _line.Clear();
        AppendTime(line, sessionEvent.Timestamp);
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendText(line, SessionEventQueue.Label(sessionEvent.Kind));
        GermanCsv.AppendSeparator(line);

        string? videoFile = sessionEvent.VideoFile ?? (_hasLastSample ? _lastSample.VideoFile : null);
        GermanCsv.AppendText(line, VideoName(videoFile));
        GermanCsv.AppendSeparator(line);
        TimeSpan? videoTime = sessionEvent.VideoTime;
        if (videoTime is null && sessionEvent.VideoFile is null && _hasLastSample && _lastSample.VideoPtsTicks != long.MinValue)
        {
            videoTime = TimeSpan.FromTicks(_lastSample.VideoPtsTicks);
        }

        if (videoTime is { } time)
        {
            GermanCsv.AppendNumber(line, time.TotalSeconds, 3);
        }

        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendText(line, sessionEvent.Details);
        _eventWriter.WriteLine(line);
        Interlocked.Increment(ref _eventRows);
    }

    private void AppendTime(StringBuilder line, long timestamp)
    {
        long elapsed = timestamp - _anchorTimestamp;
        double seconds = elapsed / (double)Stopwatch.Frequency;
        GermanCsv.AppendUtc(line, _anchorUtc.AddTicks((long)(seconds * TimeSpan.TicksPerSecond)));
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendNumber(line, seconds, 6);
    }

    private string VideoName(string? path)
    {
        if (path is null)
        {
            return string.Empty;
        }

        if (!ReferenceEquals(path, _lastVideoPath))
        {
            _lastVideoPath = path;
            _lastVideoName = Path.GetFileName(path);
        }

        return _lastVideoName;
    }

    private void Fail(Exception exception)
    {
        _logger.LogError(exception, "Writing the recording failed; the recording stops.");
        Samples.Deactivate();
        Events.Deactivate();
        try
        {
            CloseFiles();
        }
        catch (Exception closeException) when (closeException is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(closeException, "Closing the recording files failed as well.");
        }

        RecordingStatus last = Volatile.Read(ref _status);
        Volatile.Write(ref _status, last with
        {
            Active = false,
            HeadRows = Volatile.Read(ref _headRows),
            EventRows = Volatile.Read(ref _eventRows),
            Error = $"Aufzeichnung gestoppt: {exception.Message}",
        });
    }

    private void CloseFiles()
    {
        StreamWriter? head = _headWriter;
        StreamWriter? events = _eventWriter;
        _headWriter = null;
        _eventWriter = null;
        try
        {
            head?.Dispose();
        }
        finally
        {
            events?.Dispose();
        }
    }

    private static string UniquePath(string folder, string stem, string suffix)
    {
        for (int attempt = 1; ; attempt++)
        {
            string name = attempt == 1 ? stem : $"{stem}_{attempt}";
            string path = Path.Combine(folder, name + suffix);
            if (!File.Exists(path) && !File.Exists(Path.Combine(folder, name + EventFileSuffix)))
            {
                return path;
            }
        }
    }

    public static string DescribeRate(int rateHz) => rateHz <= 0 ? "jedes Bild" : string.Create(CultureInfo.InvariantCulture, $"{rateHz} Hz");

    public static string DescribeRecenter(float yawDegrees)
    {
        if (yawDegrees == 0f)
        {
            return "Zurückgesetzt (Vorderseite des Videos)";
        }

        string side = yawDegrees > 0f ? "links" : "rechts";
        return string.Create(GermanCsv.Numbers, $"Vorne liegt {Math.Abs(yawDegrees):0.0}° {side} der Vorderseite des Videos (Yaw {yawDegrees:+0.0;-0.0}°)");
    }

    public static string DescribeState(PlaybackState state)
    {
        return state switch
        {
            PlaybackState.NoMedia => "Kein Video",
            PlaybackState.Opening => "Öffnet",
            PlaybackState.Ready => "Bereit",
            PlaybackState.Playing => "Läuft",
            PlaybackState.Paused => "Pause",
            PlaybackState.Ended => "Ende",
            PlaybackState.Failed => "Fehler",
            _ => state.ToString(),
        };
    }

    public static string DescribeContent(HeadsetContent content)
    {
        return content switch
        {
            HeadsetContent.Video => "Video",
            HeadsetContent.Black => "Schwarz",
            HeadsetContent.SeeThrough => "See-through",
            HeadsetContent.Image => "Standbild",
            _ => "Nichts",
        };
    }
}
