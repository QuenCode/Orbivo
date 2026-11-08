using System.Diagnostics;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orbivo.Core.Geometry;
using Orbivo.Core.Logging;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Logging;

/// <summary>Head log (F50) and event log (F51): German CSV, written on the recorder's own thread.</summary>
public sealed class SessionRecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orbivo-recording-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Recording_WritesBothFilesAsGermanCsv()
    {
        var recenter = new RecenterControl();
        var recorder = new SessionRecorder(NullLogger.Instance, recenter);
        recorder.Start(Options("P 07"));
        WaitUntil(() => recorder.Status.Active);

        long start = Stopwatch.GetTimestamp();
        string video = @"C:\Videos\wald;see.mp4";
        Quaternion head = new OrientationOffset(30, 10, 5).ToContentRotation();
        for (int frame = 0; frame < 90; frame++)
        {
            recorder.Samples.Offer(Sample(start + (frame * (Stopwatch.Frequency / 90)), head, video, ptsSeconds: 1.5 + (frame / 90.0)));
        }

        recorder.Events.Record(SessionEventKind.Seek, "von 0:01,5 nach 0:30,0", video, TimeSpan.FromSeconds(30));
        recorder.Events.Record(SessionEventKind.IdleView, "See-through");
        recenter.RequestRecenter();
        recenter.Update(head, headTracked: true);
        WaitUntil(() => recorder.Status.HeadRows == 90 && recorder.Status.EventRows >= 4);
        RecordingStatus running = recorder.Status;
        recorder.Dispose();

        Assert.NotNull(running.HeadFilePath);
        Assert.EndsWith("_P_07_kopf.csv", running.HeadFilePath, StringComparison.Ordinal);
        Assert.EndsWith("_P_07_ereignisse.csv", running.EventFilePath, StringComparison.Ordinal);

        byte[] headBytes = File.ReadAllBytes(running.HeadFilePath!);
        Assert.Equal([0xEF, 0xBB, 0xBF], headBytes[..3]);
        string[] headLines = File.ReadAllLines(running.HeadFilePath!, Encoding.UTF8);
        Assert.Equal(SessionRecorder.HeadHeader, headLines[0]);
        Assert.Equal(91, headLines.Length);
        string[] first = headLines[1].Split(';');
        Assert.Equal(SessionRecorder.HeadHeader.Split(';').Length, first.Length - 1); // the file name holds a quoted ';'
        Assert.Contains("\"wald;see.mp4\"", headLines[1], StringComparison.Ordinal);
        Assert.Contains(";1,500;Läuft;Video;1,000;1;", headLines[1], StringComparison.Ordinal);
        Assert.Contains(";30,00;10,00;5,00;", headLines[1], StringComparison.Ordinal); // yaw, pitch, roll of the head

        string[] eventLines = File.ReadAllLines(running.EventFilePath!, Encoding.UTF8);
        Assert.Equal(SessionRecorder.EventHeader, eventLines[0]);
        Assert.StartsWith("Aufzeichnung", eventLines[1].Split(';')[2], StringComparison.Ordinal);
        Assert.Contains(eventLines, line => line.Contains(";Seek;\"wald;see.mp4\";30,000;von 0:01,5 nach 0:30,0", StringComparison.Ordinal));
        Assert.Contains(eventLines, line => line.Contains(";Leerlauf;", StringComparison.Ordinal) && line.EndsWith(";See-through", StringComparison.Ordinal));
        Assert.Contains(eventLines, line => line.Contains(";Recenter;", StringComparison.Ordinal) && line.Contains("30,0° links", StringComparison.Ordinal));
        Assert.Contains("Ende (App wird beendet)", eventLines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Times_AreMonotonicAndMatchTheSeconds()
    {
        var recorder = new SessionRecorder(NullLogger.Instance, null);
        recorder.Start(Options(string.Empty));
        WaitUntil(() => recorder.Status.Active);
        long start = Stopwatch.GetTimestamp();
        for (int frame = 0; frame < 30; frame++)
        {
            recorder.Samples.Offer(Sample(start + (frame * (Stopwatch.Frequency / 10)), Quaternion.Identity, null, ptsSeconds: double.NaN));
        }

        WaitUntil(() => recorder.Status.HeadRows == 30);
        string path = recorder.Status.HeadFilePath!;
        recorder.Dispose();

        double previous = double.MinValue;
        DateTime previousUtc = DateTime.MinValue;
        foreach (string line in File.ReadAllLines(path).Skip(1))
        {
            string[] fields = line.Split(';');
            double seconds = double.Parse(fields[1], GermanCsv.Numbers);
            DateTime utc = DateTime.ParseExact(fields[0], "yyyy-MM-dd HH:mm:ss,fff", System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(seconds > previous);
            Assert.True(utc >= previousUtc);
            Assert.Equal(string.Empty, fields[2]); // no video
            Assert.Equal(string.Empty, fields[3]);
            previous = seconds;
            previousUtc = utc;
        }

        Assert.Equal(2.9, previous, 0.05);
    }

    [Fact]
    public void Events_WithoutRecording_AreDropped()
    {
        var recorder = new SessionRecorder(NullLogger.Instance, null);
        recorder.Events.Record(SessionEventKind.Play, "vorher");
        recorder.Samples.Offer(Sample(Stopwatch.GetTimestamp(), Quaternion.Identity, null, 0));

        Assert.False(recorder.Events.IsActive);
        Assert.Equal(0, recorder.Samples.Pending);
        recorder.Dispose();
    }

    [Fact]
    public void EachStart_BeginsANewPairOfFiles()
    {
        var recorder = new SessionRecorder(NullLogger.Instance, null);
        recorder.Start(Options("A"));
        WaitUntil(() => recorder.Status.Active);
        string firstFile = recorder.Status.HeadFilePath!;
        recorder.Stop();
        WaitUntil(() => !recorder.Status.Active);
        recorder.Start(Options("A"));
        WaitUntil(() => recorder.Status.Active && recorder.Status.HeadFilePath != firstFile);
        recorder.Dispose();

        Assert.Equal(2, Directory.GetFiles(_folder, "*" + SessionRecorder.HeadFileSuffix).Length);
        Assert.Equal(2, Directory.GetFiles(_folder, "*" + SessionRecorder.EventFileSuffix).Length);
        Assert.Contains("Ende (Vom Operator beendet)", File.ReadAllLines(firstFile[..^SessionRecorder.HeadFileSuffix.Length] + SessionRecorder.EventFileSuffix)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void UnwritableFolder_StopsTheRecordingWithAReason()
    {
        // A file where the folder should be.
        Directory.CreateDirectory(_folder);
        string blocked = Path.Combine(_folder, "blockiert");
        File.WriteAllText(blocked, "x");
        var recorder = new SessionRecorder(NullLogger.Instance, null);

        recorder.Start(new RecordingOptions(blocked, string.Empty, 0, "test"));
        WaitUntil(() => recorder.Status.Error is not null);
        recorder.Dispose();

        Assert.False(recorder.Status.Active);
        Assert.StartsWith("Aufzeichnung gestoppt", recorder.Status.Error, StringComparison.Ordinal);
        Assert.False(recorder.Samples.IsActive);
    }

    [Theory]
    [InlineData(0, 90)] // every frame
    [InlineData(45, 45)]
    [InlineData(30, 30)]
    [InlineData(10, 10)]
    public void Gate_ThinsOutToTheRate(int rateHz, int expectedPerSecond)
    {
        var gate = new HeadSampleGate();
        gate.Activate(rateHz);
        long start = Stopwatch.GetTimestamp();
        var random = new Random(7);
        for (int frame = 0; frame < 900; frame++)
        {
            // 90 fps with ±1 ms jitter.
            long jitter = (long)((random.NextDouble() - 0.5) * 2 * Stopwatch.Frequency / 1000);
            gate.Offer(Sample(start + (frame * Stopwatch.Frequency / 90) + jitter, Quaternion.Identity, null, 0));
        }

        Assert.InRange(gate.Pending, (expectedPerSecond * 10) - 2, (expectedPerSecond * 10) + 2);
    }

    [Fact]
    public void Gate_Offer_DoesNotAllocate()
    {
        var gate = new HeadSampleGate();
        string video = "clip.mp4";
        long start = Stopwatch.GetTimestamp();
        gate.Offer(Sample(start, Quaternion.Identity, video, 0));
        gate.Activate(45);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 2000; frame++)
        {
            gate.Offer(Sample(start + (frame * Stopwatch.Frequency / 90), Quaternion.Identity, video, 0));
            gate.TryTake(out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Gate_CountsWhatDoesNotFit()
    {
        var gate = new HeadSampleGate();
        gate.Activate(0);
        long start = Stopwatch.GetTimestamp();
        for (int frame = 0; frame < HeadSampleGate.Capacity + 5; frame++)
        {
            gate.Offer(Sample(start + frame, Quaternion.Identity, null, 0));
        }

        Assert.Equal(5, gate.Dropped);
    }

    [Theory]
    [InlineData("P07", "P07")]
    [InlineData("  Proband 7  ", "Proband_7")]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData("", "")]
    [InlineData("...", "")]
    public void CleanLabel_GivesASafeFileName(string label, string expected)
    {
        Assert.Equal(expected, SessionRecorder.CleanLabel(label));
    }

    [Fact]
    public void FileStem_SortsByTime()
    {
        var time = new DateTime(2026, 11, 8, 14, 12, 3);
        Assert.Equal("2026-11-08_141203_P07", SessionRecorder.FileStem(time, "P07"));
        Assert.Equal("2026-11-08_141203", SessionRecorder.FileStem(time, " "));
    }

    [Fact]
    public void GermanCsv_UsesDecimalCommaAndQuotes()
    {
        var line = new StringBuilder();
        GermanCsv.AppendNumber(line, -12.3456, 3);
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendText(line, "sagt \"hallo\"; tschüss");
        GermanCsv.AppendSeparator(line);
        GermanCsv.AppendNumber(line, double.NaN, 2);

        Assert.Equal("-12,346;\"sagt \"\"hallo\"\"; tschüss\";", line.ToString());
    }

    private RecordingOptions Options(string label) => new(_folder, label, 0, "test");

    private static HeadSample Sample(long timestamp, Quaternion head, string? video, double ptsSeconds)
    {
        return new HeadSample(
            timestamp,
            Tracked: true,
            head,
            head,
            RecenterYawDegrees: 0f,
            video is null ? HeadsetContent.Black : HeadsetContent.Video,
            Brightness: 1f,
            video is null ? PlaybackState.NoMedia : PlaybackState.Playing,
            video,
            double.IsNaN(ptsSeconds) || video is null ? long.MinValue : (long)(ptsSeconds * TimeSpan.TicksPerSecond));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for the recorder.");
            Thread.Sleep(10);
        }
    }
}
