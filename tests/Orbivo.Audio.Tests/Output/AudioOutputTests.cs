using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Orbivo.Audio.Output;
using Orbivo.Core.Audio;

namespace Orbivo.Audio.Tests.Output;

/// <summary>
/// The output thread in real time on simulated devices: nothing reaches a speaker. The test stands in for the decoder.
/// </summary>
public sealed class AudioOutputTests
{
    private const int Rate = 48_000;
    private const long Second = TimeSpan.TicksPerSecond;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Plays_TheRingAtTheClock_WithoutGlitchesOrAllocations()
    {
        var provider = new SimulatedEndpointProvider(new SimulatedEndpointOptions(LatencyFrames: 480, RecordFrames: 3 * Rate));
        using var output = new AudioOutput(() => provider, "sim", 1f, NullLogger.Instance);
        Assert.Equal(Rate, output.SampleRate);
        Assert.Equal(AudioEndpointState.Active, output.GetStatus().State);

        Fill(output, 0, 2 * Rate);
        output.Hold(0);
        output.Play();
        Thread.Sleep(400);
        long allocatedAfterWarmUp = output.PeriodAllocatedBytes;
        long periodsAfterWarmUp = output.Periods;
        Thread.Sleep(1000);

        SimulatedAudioEndpoint device = provider.Latest!;
        Assert.Equal(0, device.GlitchFrames);
        AudioOutputStatus status = output.GetStatus();
        Assert.Equal(0, status.UnderrunFrames);
        Assert.InRange(status.LatencyMilliseconds, 10, 70); // up to 50 ms buffer + 10 ms simulated latency
        Assert.True(output.Periods - periodsAfterWarmUp > 30, "the loop runs"); // the simulated device sleeps, one timer tick or more
        Assert.Equal(0, output.PeriodAllocatedBytes - allocatedAfterWarmUp);
        TestContext.Current.TestOutputHelper?.WriteLine($"allocated in all periods: {output.PeriodAllocatedBytes} bytes");

        // The sound starts at stream frame "start" with media frame 0, then follows without gaps.
        ReadOnlySpan<float> recorded = device.Recorded;
        int start = FirstSound(recorded);
        for (int media = 300; media < Rate; media += 997)
        {
            Assert.Equal(Signal(media), recorded[2 * (start + media)]);
        }

        // The clock shows the media time that is heard, to well within a millisecond.
        for (int media = Rate / 2; media < Rate; media += 4800)
        {
            long clock = output.Clock.GetMediaTicks(device.HeardTimestamp(start + media));
            Assert.InRange(clock - (media * Second / Rate), -Second / 1000, Second / 1000);
        }
    }

    [Fact]
    public void AmbisonicsField_PlaysBinaurally_WithoutAllocations()
    {
        var provider = new SimulatedEndpointProvider(new SimulatedEndpointOptions(LatencyFrames: 480, RecordFrames: 3 * Rate));
        using var output = new AudioOutput(() => provider, "sim", 1f, NullLogger.Instance, ambisonicsFactory: rate => new WToBothEars(rate));
        AudioRingBuffer ring = output.Ring;
        Assert.Equal(AmbisonicsLayout.MaxChannels, ring.Channels);

        int generation = ring.RequestFlush();
        Assert.True(ring.WaitForFlush(generation, Timeout));
        ring.Start(0, output.SampleRate, 4);
        var data = new float[4 * 2 * Rate];
        for (int i = 0; i < 2 * Rate; i++)
        {
            data[4 * i] = Signal(i);
        }

        Assert.Equal(2 * Rate, ring.Write(data));
        output.Hold(0);
        output.Play();
        Thread.Sleep(400);
        long allocatedAfterWarmUp = output.PeriodAllocatedBytes;
        Thread.Sleep(600);

        AudioOutputStatus status = output.GetStatus();
        Assert.True(status.Spatial!.FieldBinaural);
        Assert.Null(status.Spatial.AmbisonicsProblem);
        Assert.Equal(0, status.UnderrunFrames);
        Assert.Equal(0, output.PeriodAllocatedBytes - allocatedAfterWarmUp);

        // W reaches both ears, aligned to the media position like stereo.
        ReadOnlySpan<float> recorded = provider.Latest!.Recorded;
        int start = FirstSound(recorded);
        for (int media = 300; media < Rate / 2; media += 997)
        {
            Assert.Equal(Signal(media), recorded[2 * (start + media)]);
            Assert.Equal(Signal(media), recorded[(2 * (start + media)) + 1]);
        }
    }

    [Fact]
    public void Clock_FollowsADriftingDevice()
    {
        var provider = new SimulatedEndpointProvider(new SimulatedEndpointOptions(DriftPpm: 500, LatencyFrames: 960, RecordFrames: 4 * Rate));
        using var output = new AudioOutput(() => provider, null, 1f, NullLogger.Instance);
        Fill(output, 0, (5 * Rate) / 2);
        output.Hold(0);
        output.Play();
        Thread.Sleep(2200);

        SimulatedAudioEndpoint device = provider.Latest!;
        int start = FirstSound(device.Recorded);
        long last = device.WrittenFrames - start - 2000;
        for (long media = last - Rate; media < last; media += 4800)
        {
            long clock = output.Clock.GetMediaTicks(device.HeardTimestamp(start + media));
            Assert.InRange(clock - (media * Second / Rate), -Second / 1000, Second / 1000);
        }
    }

    [Fact]
    public void DeviceLoss_PlaysOnWithoutSound_AndTheDeviceComesBack()
    {
        var provider = new SimulatedEndpointProvider(new SimulatedEndpointOptions(RecordFrames: Rate));
        using var output = new AudioOutput(() => provider, "sim", 1f, NullLogger.Instance);
        Fill(output, 0, 2 * Rate);
        output.Hold(0);
        output.Play();
        Thread.Sleep(300);

        provider.Remove("sim");
        WaitFor(() => output.GetStatus().State == AudioEndpointState.Unavailable);
        AudioOutputStatus lost = output.GetStatus();
        Assert.NotNull(lost.Message);
        Assert.Equal(Rate, output.SampleRate);

        // The clock runs on the system timer.
        long before = output.Clock.GetMediaTicks(Stopwatch.GetTimestamp());
        Thread.Sleep(300);
        long after = output.Clock.GetMediaTicks(Stopwatch.GetTimestamp());
        Assert.InRange(after - before, 250 * TimeSpan.TicksPerMillisecond, 350 * TimeSpan.TicksPerMillisecond);

        // Plugged in again: taken back at once (device change), with sound.
        provider.Add(new SimulatedEndpointOptions(RecordFrames: Rate));
        WaitFor(() => output.GetStatus().State == AudioEndpointState.Active);
        Assert.Null(output.GetStatus().Message);
        Assert.Equal(2, provider.Opened.Count);
        Assert.True(provider.Opened[0].Disposed);
        Thread.Sleep(200);
        Assert.True(FirstSound(provider.Latest!.Recorded) >= 0);
    }

    [Fact]
    public void MissingDevice_IsRetried_AndItsRateIsTakenOver()
    {
        var provider = new SimulatedEndpointProvider();
        using var output = new AudioOutput(() => provider, "usb", 0.5f, NullLogger.Instance);
        AudioOutputStatus status = output.GetStatus();
        Assert.Equal(AudioEndpointState.Unavailable, status.State);
        Assert.Equal(48_000, output.SampleRate);
        Assert.Equal("usb", status.DeviceId);

        provider.Add(new SimulatedEndpointOptions(SampleRate: 44_100, DeviceId: "usb"));
        WaitFor(() => output.GetStatus().State == AudioEndpointState.Active);
        Assert.Equal(44_100, output.SampleRate);
    }

    [Fact]
    public void WindowsDefault_IsFollowedOnlyWhenSelected()
    {
        var provider = new SimulatedEndpointProvider(
            new SimulatedEndpointOptions(DeviceId: "a"),
            new SimulatedEndpointOptions(DeviceId: "b"));
        using var following = new AudioOutput(() => provider, null, 1f, NullLogger.Instance);
        using var fixedOnA = new AudioOutput(() => provider, "a", 1f, NullLogger.Instance);
        Assert.All(provider.Opened, endpoint => Assert.Equal("a", endpoint.DeviceId));

        provider.SetDefault("b");
        WaitFor(() => following.GetStatus().DeviceName == "Simuliert (b)");
        Thread.Sleep(100);
        Assert.Equal("Simuliert (a)", fixedOnA.GetStatus().DeviceName);
        Assert.Equal(3, provider.Opened.Count);
    }

    [Fact]
    public void SelectDevice_SwitchesAndKeepsTheClock()
    {
        var provider = new SimulatedEndpointProvider(
            new SimulatedEndpointOptions(DeviceId: "a"),
            new SimulatedEndpointOptions(DeviceId: "b", RecordFrames: Rate));
        using var output = new AudioOutput(() => provider, "a", 1f, NullLogger.Instance);
        Fill(output, 0, 2 * Rate);
        output.Hold(0);
        output.Play();
        Thread.Sleep(500);

        output.SelectDevice("b");
        WaitFor(() => output.GetStatus().DeviceId == "b" && provider.Latest!.DeviceId == "b");
        long before = output.Clock.GetMediaTicks(Stopwatch.GetTimestamp());
        Assert.InRange(before, 400 * TimeSpan.TicksPerMillisecond, 900 * TimeSpan.TicksPerMillisecond);
        Thread.Sleep(300);
        Assert.Equal(0, provider.Latest!.GlitchFrames);
        Assert.True(FirstSound(provider.Latest.Recorded) >= 0);
    }

    [Fact]
    public void Volume_AndMute_ScaleTheSound()
    {
        var provider = new SimulatedEndpointProvider(new SimulatedEndpointOptions(RecordFrames: 2 * Rate));
        using var output = new AudioOutput(() => provider, null, 0.5f, NullLogger.Instance);
        Fill(output, 0, 2 * Rate, constant: 0.8f);
        output.Hold(0);
        output.Play();
        Thread.Sleep(300);
        output.SetMuted(true);
        Thread.Sleep(300);

        ReadOnlySpan<float> recorded = provider.Latest!.Recorded;
        int start = FirstSound(recorded);
        Assert.Equal(0.8f * 0.125f, recorded[2 * (start + 4800)], 5); // level 0.5 → 0.5³
        Assert.Equal(0f, recorded[recorded.Length - 2]);
        AudioOutputStatus status = output.GetStatus();
        Assert.True(status.Muted);
        Assert.Equal(0.5f, status.Volume);
    }

    /// <summary>
    /// The real Windows default device (opt-in: ORBIVO_DEVICE_TESTS=1). Only silence is written, and the stream
    /// volume is 0 as well: nothing can be heard. Checks the real loop for allocations, latency and a running clock.
    /// </summary>
    [Fact]
    public void RealDevice_Silence_RunsWithoutAllocations()
    {
        if (Environment.GetEnvironmentVariable("ORBIVO_DEVICE_TESTS") != "1")
        {
            Assert.Skip("Opt-in: set ORBIVO_DEVICE_TESTS=1 (writes 5 s of silence at stream volume 0 to the default device).");
        }

        var logger = NullLogger.Instance;
        using var output = new AudioOutput(() => new WasapiEndpointProvider(logger, streamVolume: 0f), null, 0f, logger);
        AudioOutputStatus status = output.GetStatus();
        TestContext.Current.TestOutputHelper?.WriteLine($"{status.DeviceName}: {status.State}, {output.SampleRate} Hz, {status.DeviceChannels} ch");
        if (status.State != AudioEndpointState.Active)
        {
            Assert.Skip($"No usable sound device: {status.Message}");
        }

        // The ring stays empty: the renderer plays silence even while the clock runs.
        output.Hold(0);
        output.Play();
        Thread.Sleep(1000);
        long allocated = output.PeriodAllocatedBytes;
        long periods = output.Periods;
        long clockStart = output.Clock.GetMediaTicks(Stopwatch.GetTimestamp());
        var wall = Stopwatch.StartNew();
        Thread.Sleep(4000);
        long clockEnd = output.Clock.GetMediaTicks(Stopwatch.GetTimestamp());
        double clockSeconds = (clockEnd - clockStart) / (double)Second;
        status = output.GetStatus();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"periods {output.Periods - periods}, latency {status.LatencyMilliseconds:0.0} ms, clock {clockSeconds:0.0000} s in {wall.Elapsed.TotalSeconds:0.0000} s, " +
            $"drift {status.DriftPpm:0} ppm, allocated {output.PeriodAllocatedBytes - allocated} bytes (total {output.PeriodAllocatedBytes})");

        Assert.Equal(AudioEndpointState.Active, status.State);
        Assert.True(output.Periods - periods > 100, "the device asks for data");
        Assert.Equal(0, output.PeriodAllocatedBytes - allocated);
        Assert.InRange(status.LatencyMilliseconds, 1, 150);
        Assert.InRange(clockSeconds, wall.Elapsed.TotalSeconds - 0.01, wall.Elapsed.TotalSeconds + 0.01);
    }

    private static float Signal(long media) => 0.01f + ((media % 4096) / 8192f);

    /// <summary>A stand-in for Steam Audio that plays W on both ears.</summary>
    private sealed class WToBothEars(int sampleRate) : Spatial.IAmbisonicsSpatializer
    {
        public int BlockFrames => 256;

        public int SampleRate => sampleRate;

        public int MaxOrder => 3;

        public void Reset()
        {
        }

        public void Process(ReadOnlySpan<float> ambiX, int channels, Span<float> stereoOut, System.Numerics.Quaternion headInContent)
        {
            for (int i = 0; i < BlockFrames; i++)
            {
                stereoOut[2 * i] = ambiX[i * channels];
                stereoOut[(2 * i) + 1] = ambiX[i * channels];
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Stands in for the decoder: flush, start at <paramref name="firstFrame"/>, write.</summary>
    private static void Fill(AudioOutput output, long firstFrame, int frames, float? constant = null)
    {
        AudioRingBuffer ring = output.Ring;
        int generation = ring.RequestFlush();
        Assert.True(ring.WaitForFlush(generation, Timeout));
        ring.Start(firstFrame, output.SampleRate);
        var data = new float[2 * frames];
        for (int i = 0; i < frames; i++)
        {
            data[2 * i] = constant ?? Signal(firstFrame + i);
            data[(2 * i) + 1] = data[2 * i];
        }

        Assert.Equal(frames, ring.Write(data));
    }

    private static int FirstSound(ReadOnlySpan<float> recorded)
    {
        for (int i = 0; i < recorded.Length / 2; i++)
        {
            if (recorded[2 * i] != 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var wait = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(wait.Elapsed < Timeout, "timed out");
            Thread.Sleep(5);
        }
    }
}
