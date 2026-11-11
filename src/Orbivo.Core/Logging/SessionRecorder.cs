using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Geometry;
using Orbivo.Core.Localization;
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

    /// <summary>
    /// "Nur bei laufendem Video": the recorder only waits; every Play starts a new pair of files, Stop, the end of the
    /// video, closing it or loading another one ends it. Pause stays in the same files.
    /// </summary>
    public bool PerVideo { get; init; }

    /// <summary>Transport state when the recording is switched on; with <see cref="PerVideo"/>, playing or paused starts a file at once.</summary>
    public PlaybackState PlaybackAtStart { get; init; } = PlaybackState.NoMedia;

    /// <summary>Loaded video when the recording is switched on, or null.</summary>
    public string? VideoAtStart { get; init; }
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
/// <param name="Waiting">"Nur bei laufendem Video" is on and no video plays: the recorder waits for Play.</param>
/// <param name="FilePairs">Pairs of files written since the recording was switched on.</param>
public sealed record RecordingStatus(
    bool Active,
    string? HeadFilePath,
    string? EventFilePath,
    long HeadRows,
    long EventRows,
    long DroppedRows,
    string? Error,
    TimeSpan Duration,
    bool Waiting = false,
    int FilePairs = 0)
{
    public static RecordingStatus Idle { get; } = new(false, null, null, 0, 0, 0, null, TimeSpan.Zero);
}

/// <summary>
/// Writes the head log (F50) and the event log (F51) of a session as two German CSV files, on its own thread
/// (docs/ARCHITECTURE.md, "Threads"). The render thread hands samples over through <see cref="Samples"/>, everyone
/// else events through <see cref="Events"/>; neither ever waits for the disk.
/// </summary>
/// <remarks>
/// Times: one anchor (UTC and Stopwatch) at the start of each pair of files; every row's time is the anchor plus the
/// Stopwatch time since, so the column is monotonic even if the system clock is adjusted (F50). The file shows it as
/// local time.
/// </remarks>
public sealed class SessionRecorder : IDisposable
{
    public const string HeadFileSuffix = "_kopf.csv";
    public const string EventFileSuffix = "_ereignisse.csv";

    public const string HeadHeader =
        "Uhrzeit;Sekunden;Video;Videozeit_s;Status;Inhalt;Helligkeit;Tracking;Kopf_qw;Kopf_qx;Kopf_qy;Kopf_qz;"
        + "Kopf_Yaw_Grad;Kopf_Pitch_Grad;Kopf_Roll_Grad;Blick_Video_Yaw_Grad;Blick_Video_Pitch_Grad;Recenter_Yaw_Grad";

    public const string EventHeader = "Uhrzeit;Sekunden;Ereignis;Video;Videozeit_s;Details";

    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// While waiting for Play, events and samples of this long are kept, so the Play command that started a video is
    /// in its file although the player reports "playing" a moment later.
    /// </summary>
    private static readonly long WaitingWindow = Stopwatch.Frequency * 5;

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
    private RecordingOptions? _session;
    private int _rateHz;
    private string? _idleView;
    private readonly List<SessionEvent> _eventBatch = [];
    private readonly Queue<SessionEvent> _waitingEvents = new();
    private readonly Queue<HeadSample> _pendingSamples = new();
    private StreamWriter? _headWriter;
    private StreamWriter? _eventWriter;
    private DateTime _anchorUtc;
    private long _anchorTimestamp;
    private long _sampleFloor;
    private long _lastFlush;
    private int _recenterVersion;
    private SessionEventKind? _lastCommand;
    private string? _lastVideoPath;
    private string _lastVideoName = string.Empty;
    private HeadSample _lastSample;
    private bool _hasLastSample;

    // Status, written by the writer thread.
    private RecordingStatus _status = RecordingStatus.Idle;
    private long _headRows;
    private long _eventRows;
    private long _droppedBase;

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
                    DroppedRows = Samples.Dropped + Events.Dropped - Volatile.Read(ref _droppedBase),
                    Duration = Stopwatch.GetElapsedTime(Volatile.Read(ref _anchorTimestamp)),
                }
                : status;
        }
    }

    /// <summary>
    /// File names of a recording: <c>2026-11-08_141203_P07_kopf.csv</c> and <c>…_ereignisse.csv</c>; with a video
    /// (one pair of files per video) <c>2026-11-08_141203_P07_Wald_kopf.csv</c>.
    /// </summary>
    public static string FileStem(DateTime localStart, string label, string? videoFile = null)
    {
        var stem = new StringBuilder(localStart.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture));
        foreach (string part in (ReadOnlySpan<string>)[CleanLabel(label), videoFile is null ? string.Empty : CleanLabel(Path.GetFileNameWithoutExtension(videoFile))])
        {
            if (part.Length > 0)
            {
                stem.Append('_').Append(part);
            }
        }

        return stem.ToString();
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

    /// <summary>
    /// Starts a new recording (a running one ends first); with <see cref="RecordingOptions.PerVideo"/> it waits for
    /// Play. UI thread; the files open on the writer thread.
    /// </summary>
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
                if (rate is { } hz && _session is not null)
                {
                    _rateHz = hz;
                    Samples.SetRate(hz);
                    WriteEvent(new SessionEvent(Stopwatch.GetTimestamp(), SessionEventKind.Recording, $"Kopf-Log: {DescribeRate(hz)}"));
                }

                Drain();
                if (stop || start is not null || disposing)
                {
                    EndSession(disposing ? "App wird beendet" : start is not null ? "Neue Aufzeichnung" : "Vom Operator beendet");
                }

                if (disposing)
                {
                    return;
                }

                if (start is not null)
                {
                    BeginSession(start);
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

    /// <summary>The operator switched the recording on: open the files, or wait for Play.</summary>
    private void BeginSession(RecordingOptions options)
    {
        _session = options;
        _rateHz = options.HeadSampleRateHz;
        _idleView = options.IdleView;
        _waitingEvents.Clear();
        _pendingSamples.Clear();

        // Rows queued before belong to no recording.
        while (Samples.TryTake(out _))
        {
        }

        Events.Activate();
        Samples.Activate(_rateHz);
        Volatile.Write(ref _droppedBase, 0);
        Volatile.Write(ref _status, new RecordingStatus(false, null, null, 0, 0, 0, null, TimeSpan.Zero, Waiting: options.PerVideo));
        long now = Stopwatch.GetTimestamp();
        if (!options.PerVideo)
        {
            OpenFiles(now, videoFile: null, sampleFloor: now - Stopwatch.Frequency, note: null);
            return;
        }

        _logger.LogInformation("Recording switched on, one pair of files per video: waiting for Play (head log {Rate}).", DescribeRate(_rateHz));
        if (options.PlaybackAtStart is PlaybackState.Playing or PlaybackState.Paused)
        {
            OpenFiles(now, options.VideoAtStart, sampleFloor: now, note: "Video lief schon beim Einschalten");
        }
    }

    /// <summary>The operator switched the recording off, started a new one or the app ends.</summary>
    private void EndSession(string reason)
    {
        if (_session is null)
        {
            return;
        }

        Samples.Deactivate();
        Drain();
        Events.Deactivate();
        EndFiles(reason);
        _session = null;
        _waitingEvents.Clear();
        _pendingSamples.Clear();
        Volatile.Write(ref _status, Volatile.Read(ref _status) with { Active = false, Waiting = false });
    }

    private void OpenFiles(long anchorTimestamp, string? videoFile, long sampleFloor, string? note)
    {
        RecordingOptions options = _session!;
        DateTime localStart = DateTime.Now - Stopwatch.GetElapsedTime(anchorTimestamp);
        string stem = FileStem(localStart, options.Label, options.PerVideo ? videoFile : null);
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
        Volatile.Write(ref _anchorTimestamp, anchorTimestamp);
        _anchorUtc = DateTime.UtcNow - Stopwatch.GetElapsedTime(anchorTimestamp);
        _sampleFloor = sampleFloor;
        _lastFlush = Stopwatch.GetTimestamp();
        _hasLastSample = false;
        _lastCommand = null;
        Volatile.Write(ref _headRows, 0);
        Volatile.Write(ref _eventRows, 0);
        Volatile.Write(ref _droppedBase, Samples.Dropped + Events.Dropped);
        _recenterVersion = _recenter?.Version ?? 0;
        int pairs = Volatile.Read(ref _status).FilePairs + 1;
        Volatile.Write(ref _status, new RecordingStatus(true, headPath, eventPath, 0, 0, 0, null, TimeSpan.Zero, Waiting: false, pairs));

        string label = CleanLabel(options.Label);
        var start = new StringBuilder();
        start.Append(CultureInfo.InvariantCulture, $"Start. Orbivo {options.AppVersion}, Kopf-Log: {DescribeRate(_rateHz)}, Sitzung: {(label.Length == 0 ? "–" : label)}");
        if (options.PerVideo)
        {
            start.Append(CultureInfo.InvariantCulture, $", ein Dateipaar je Video, Video: {videoFile ?? "–"}");
        }

        start.Append(CultureInfo.InvariantCulture, $", Rechner: {Environment.MachineName}, Zeitzone: {TimeZoneInfo.Local.Id}");
        if (note is not null)
        {
            start.Append(". ").Append(note);
        }

        WriteEvent(new SessionEvent(anchorTimestamp, SessionEventKind.Recording, start.ToString(), videoFile));
        if (_idleView is { } idleView)
        {
            WriteEvent(new SessionEvent(anchorTimestamp, SessionEventKind.IdleView, idleView + " (beim Start)", videoFile));
        }

        if (_recenter is { } recenter && recenter.YawDegrees != 0f)
        {
            WriteEvent(new SessionEvent(anchorTimestamp, SessionEventKind.Recenter, DescribeRecenter(recenter.YawDegrees) + " (schon vor dem Start)", videoFile));
        }

        _logger.LogInformation("Recording started: {HeadFile} and {EventFile}, head log {Rate}.", headPath, eventPath, DescribeRate(_rateHz));
    }

    private void EndFiles(string reason)
    {
        if (_headWriter is null)
        {
            return;
        }

        long dropped = Samples.Dropped + Events.Dropped - Volatile.Read(ref _droppedBase);
        WriteEvent(new SessionEvent(
            Stopwatch.GetTimestamp(),
            SessionEventKind.Recording,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Ende ({reason}). {Volatile.Read(ref _headRows)} Kopf-Zeilen, {Volatile.Read(ref _eventRows) + 1} Ereignisse{(dropped > 0 ? $", {dropped} Zeilen verloren" : string.Empty)}")));
        CloseFiles();
        RecordingStatus last = Volatile.Read(ref _status);
        Volatile.Write(ref _status, last with
        {
            Active = false,
            Waiting = _session?.PerVideo == true,
            HeadRows = Volatile.Read(ref _headRows),
            EventRows = Volatile.Read(ref _eventRows),
            DroppedRows = dropped,
            Duration = Stopwatch.GetElapsedTime(_anchorTimestamp),
        });
        _logger.LogInformation("Recording ended ({Reason}): {Rows} head rows, {Events} events, {Dropped} rows lost.", reason, _headRows, _eventRows, dropped);
    }

    private void Drain()
    {
        if (_session is not { } session)
        {
            return;
        }

        if (_headWriter is not null)
        {
            PollRecenter();
        }

        _eventBatch.Clear();
        while (Events.TryTake(out SessionEvent? sessionEvent))
        {
            _eventBatch.Add(sessionEvent!);
        }

        while (Samples.TryTake(out HeadSample sample))
        {
            _pendingSamples.Enqueue(sample);
        }

        // Events and samples go to separate files; each file is in time order on its own. Samples up to an event are
        // written before it, so a pair of files that ends on an event holds exactly the samples until then.
        foreach (SessionEvent sessionEvent in _eventBatch)
        {
            if (sessionEvent.Kind == SessionEventKind.IdleView)
            {
                _idleView = sessionEvent.Details;
            }

            if (_headWriter is not null)
            {
                WriteSamplesBefore(sessionEvent.Timestamp);
                WriteEvent(sessionEvent);
                if (sessionEvent.Kind is SessionEventKind.Play or SessionEventKind.Stop or SessionEventKind.Close or SessionEventKind.Load)
                {
                    _lastCommand = sessionEvent.Kind;
                }

                if (session.PerVideo && sessionEvent.PlaybackState is { } state && !IsRunning(state))
                {
                    EndFiles(EndReason(state));
                }
            }
            else if (session.PerVideo && sessionEvent.PlaybackState == PlaybackState.Playing)
            {
                StartVideoFiles(sessionEvent);
            }
            else
            {
                _waitingEvents.Enqueue(sessionEvent);
            }
        }

        _eventBatch.Clear();
        if (_headWriter is not null)
        {
            WriteSamplesBefore(long.MaxValue);
            return;
        }

        long oldest = Stopwatch.GetTimestamp() - WaitingWindow;
        while (_pendingSamples.TryPeek(out HeadSample old) && old.Timestamp < oldest)
        {
            _pendingSamples.Dequeue();
        }

        while (_waitingEvents.TryPeek(out SessionEvent? old) && old.Timestamp < oldest)
        {
            _waitingEvents.Dequeue();
        }
    }

    /// <summary>A video began to play while waiting: the files start with the Play command that started it.</summary>
    private void StartVideoFiles(SessionEvent playing)
    {
        long anchor = playing.Timestamp;
        foreach (SessionEvent waiting in _waitingEvents)
        {
            if (waiting.Kind == SessionEventKind.Play && waiting.Timestamp >= playing.Timestamp - WaitingWindow)
            {
                anchor = waiting.Timestamp;
            }
        }

        OpenFiles(anchor, playing.VideoFile, sampleFloor: anchor, note: null);
        foreach (SessionEvent waiting in _waitingEvents)
        {
            if (waiting.Timestamp >= anchor)
            {
                WriteEvent(waiting);
                if (waiting.Kind == SessionEventKind.Play)
                {
                    _lastCommand = SessionEventKind.Play;
                }
            }
        }

        _waitingEvents.Clear();
        WriteEvent(playing);
    }

    private void WriteSamplesBefore(long timestamp)
    {
        while (_pendingSamples.TryPeek(out HeadSample sample) && sample.Timestamp < timestamp)
        {
            _pendingSamples.Dequeue();
            if (sample.Timestamp < _sampleFloor)
            {
                continue;
            }

            WriteSample(sample);
            _lastSample = sample;
            _hasLastSample = true;
        }
    }

    /// <summary>Playing and paused belong to the video's pair of files; every other state ends it.</summary>
    private static bool IsRunning(PlaybackState state) => state is PlaybackState.Playing or PlaybackState.Paused;

    private string EndReason(PlaybackState state)
    {
        return state switch
        {
            PlaybackState.Ended => "Video zu Ende",
            PlaybackState.Failed => "Fehler beim Abspielen",
            _ => _lastCommand switch
            {
                SessionEventKind.Stop => "Stop",
                SessionEventKind.Close => "Video entladen",
                SessionEventKind.Load => "Anderes Video geladen",
                SessionEventKind.Play => "Video startet neu",
                _ => "Zustand " + DescribeState(state),
            },
        };
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
        GermanCsv.AppendLocalTime(line, _anchorUtc.AddTicks((long)(seconds * TimeSpan.TicksPerSecond)));
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
        _session = null;
        _waitingEvents.Clear();
        _pendingSamples.Clear();
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
            Waiting = false,
            HeadRows = Volatile.Read(ref _headRows),
            EventRows = Volatile.Read(ref _eventRows),
            Error = Loc.Format(CoreText.RecordingStopped, exception.Message),
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
