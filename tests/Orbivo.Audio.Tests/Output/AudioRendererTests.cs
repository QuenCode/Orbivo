using Orbivo.Audio.Output;
using Orbivo.Core.Audio;

namespace Orbivo.Audio.Tests.Output;

/// <summary>
/// The renderer on virtual time: a device that asks for 480 frames every 10 ms and plays them 2 periods later.
/// The ring is filled by the test, which stands in for the decoder.
/// </summary>
public sealed class AudioRendererTests
{
    private const int Rate = 48_000;
    private const int Period = 480;
    private const long Frequency = 10_000_000;
    private const long Second = TimeSpan.TicksPerSecond;

    [Fact]
    public void Play_StartsAtTheHeldPosition_WithAFadeIn_AndTheClockStartsWhenItIsHeard()
    {
        using var run = new Run();
        run.FillFrom(Rate, 2 * Rate, Run.Ramp); // 2 s of sound from media 1 s; the sample value tells the media frame
        run.Hold(Second);
        run.Renderer.Play();
        long start = run.Renderer.WrittenFrames;
        float[] output = run.Periods(1);

        // Fade-in over 240 frames, then the data unchanged, starting at media frame 48000.
        Assert.InRange(output[0], 0f, 0.001f);
        Assert.InRange(output[2 * 120], 0.4f * Run.Ramp(Rate + 120), 0.6f * Run.Ramp(Rate + 120));
        for (int i = 240; i < Period; i++)
        {
            Assert.Equal(Run.Ramp(Rate + i), output[2 * i], 6);
            Assert.Equal(-Run.Ramp(Rate + i), output[(2 * i) + 1], 6);
        }

        // The first frame is heard 2 periods after it was written: the clock stands until then.
        long heard = run.HeardTimestamp(start);
        Assert.Equal(Second, run.Clock.GetMediaTicks(heard - 1));
        Near(Second + (Second / 100), run.Clock.GetMediaTicks(heard + (Frequency / 100)), 1000);

        run.Periods(100);
        Assert.Equal(0, run.Renderer.UnderrunFrames);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void Pause_StopsTheClockWhenTheLastFadedFrameIsHeard_AndPlayContinuesThere()
    {
        using var run = new Run();
        run.FillFrom(0, 4 * Rate, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        long start = run.Renderer.WrittenFrames;
        run.Periods(50);
        long pauseFrame = run.Renderer.WrittenFrames;
        run.Renderer.Pause();
        float[] fade = run.Periods(1);
        Assert.Equal(RendererState.Silent, run.Renderer.State);
        Assert.True(Math.Abs(fade[2 * 238]) > 0);
        Assert.Equal(0f, fade[2 * 239]);
        Assert.Equal(0f, fade[2 * 240]);

        // The clock runs on until the last frame of the fade is heard, then stands at its media time.
        long endMedia = run.Renderer.Tracker.MediaTicksAt(pauseFrame + 240);
        Near((pauseFrame + 240 - start) * Second / Rate, endMedia, 1);
        Assert.True(run.Clock.GetMediaTicks(run.HeardTimestamp(pauseFrame + 100)) < endMedia);
        run.Periods(20);
        Assert.Equal(endMedia, run.Clock.GetMediaTicks(run.Now));
        Assert.Equal(endMedia, run.Clock.GetMediaTicks(run.Now + Frequency));

        // Play goes on with the frame after the fade.
        run.Renderer.Play();
        long resumeFrame = run.Renderer.WrittenFrames;
        float[] resume = run.Periods(1);
        Assert.Equal(Run.Ramp(pauseFrame - start + 240 + 300), resume[2 * 300], 6);
        Near(endMedia, run.Clock.GetMediaTicks(run.HeardTimestamp(resumeFrame) - 1), 300);
        run.Periods(50);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void Play_DuringThePauseFade_TakesThePauseBack()
    {
        using var run = new Run(period: 100);
        run.FillFrom(0, Rate, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(50);
        run.Renderer.Pause();
        run.Periods(1);
        Assert.Equal(RendererState.FadingToPause, run.Renderer.State);
        run.Renderer.Play();
        Assert.Equal(RendererState.Playing, run.Renderer.State);
        run.Periods(100);
        Assert.Equal(long.MaxValue, run.Renderer.Tracker.Current.MaxMediaTicks);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void Hold_StandsTheClockAtOnce_AndFadesTheOldSoundOut()
    {
        using var run = new Run();
        run.FillFrom(0, 4 * Rate, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(20);

        // A seek to 3 s: the decoder asks for a flush at the same time.
        int generation = run.Ring.RequestFlush();
        run.Hold(3 * Second);
        Assert.Equal(3 * Second, run.Clock.GetMediaTicks(run.Now));
        float[] fade = run.Periods(1);
        Assert.Equal(RendererState.Silent, run.Renderer.State);
        Assert.True(Math.Abs(fade[2 * 100]) > 0, "the old sound fades out, it is not cut");
        Assert.True(run.Ring.IsFlushed(generation));

        run.Ring.Start(3 * Rate, Rate);
        run.Write(3 * Rate, Rate, Run.Ramp);
        run.Renderer.Play();
        float[] start = run.Periods(1);
        Assert.Equal(Run.Ramp((3 * Rate) + 400), start[2 * 400], 6);
        run.Periods(50);
        Assert.Equal(0, run.Renderer.UnderrunFrames);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void LateAndEarlyData_AreAlignedToTheMediaPosition()
    {
        using var run = new Run();

        // The sound starts 0.1 s after the picture: silence first, then the data at its media position.
        run.FillFrom(4800, Rate, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        float[] output = run.Periods(20);
        Assert.Equal(0f, output[2 * 4799]);
        Assert.Equal(Run.Ramp(4800), output[2 * 4800], 6);
        Assert.Equal(0, run.Renderer.UnderrunFrames);

        // A seek where the decoder delivers from before the target: the early part is dropped.
        run.Ring.RequestFlush();
        run.Hold(2 * Second);
        run.Periods(1);
        run.Ring.Start((2 * Rate) - 1000, Rate);
        run.Write((2 * Rate) - 1000, Rate, Run.Ramp);
        run.Renderer.Play();
        output = run.Periods(1);
        Assert.Equal(Run.Ramp((2 * Rate) + 300), output[2 * 300], 6);
    }

    [Fact]
    public void MissingData_CountsAsUnderrun_TheEndOfTheTrackDoesNot()
    {
        using var run = new Run();
        run.FillFrom(0, Period * 10, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(12);
        Assert.Equal(2 * Period, run.Renderer.UnderrunFrames);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000); // the clock follows the device, not the data

        run.Ring.MarkEnd();
        run.Periods(5);
        Assert.Equal(2 * Period, run.Renderer.UnderrunFrames);
    }

    [Fact]
    public void FlushWithoutHold_DipsAndGoesOnAtTheSameMediaTime()
    {
        using var run = new Run();
        run.FillFrom(0, Rate / 2, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(10);
        int generation = run.Ring.RequestFlush();
        run.Periods(1);
        Assert.True(run.Ring.IsFlushed(generation));
        Assert.Equal(RendererState.Playing, run.Renderer.State);

        // The decoder delivers again from the current position.
        long media = 11 * Period;
        run.Ring.Start(media, Rate);
        run.Write(media, Rate / 2, Run.Ramp);
        float[] output = run.Periods(1);
        Assert.Equal(Run.Ramp(media + 400), output[2 * 400], 6);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void RingOfAnotherRate_IsNotPlayed()
    {
        using var run = new Run();
        run.Ring.RequestFlush();
        run.Periods(1);
        run.Ring.Start(0, 44_100);
        run.Write(0, Rate, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        float[] output = run.Periods(2);
        Assert.True(run.Renderer.RateMismatch);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Attach_WhilePlaying_ContinuesAtTheHeardMediaTime_OnTheNewStream()
    {
        using var run = new Run();
        run.FillFrom(0, 2 * Rate, Run.Ramp);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(50);
        long heardMedia = run.Clock.GetMediaTicks(run.Now);
        run.Attach();
        Assert.Equal(0, run.Renderer.WrittenFrames);
        Near(heardMedia, run.Clock.GetMediaTicks(run.Now), 1);

        // The frames that were still in the old device are gone (read from the ring already): silence for them, then
        // the sound goes on aligned to the clock, which never jumped.
        float[] output = run.Periods(3);
        long heardFrame = heardMedia * Rate / Second;
        Assert.Equal(0f, output[2 * 400]);
        Assert.InRange(output[2 * 1200] - Run.Ramp(heardFrame + 1200), -2 * Run.Step, 2 * Run.Step);
        run.Periods(50);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void Transport_VolumeAndMute_NeverClick()
    {
        // A loud 1 kHz sine: a hard cut would jump by up to 0.8 from one sample to the next. The steepest step of the
        // sine itself is 2π·1000/48000·0.8 ≈ 0.105.
        using var run = new Run();
        run.FillFrom(0, 6 * Rate, Run.Sine);
        run.Hold(0);
        float[] gains = [1f, 0.2f, 1f, 0f, 1f];
        var all = new List<float>();
        run.Renderer.Play();
        for (int step = 0; step < 5; step++)
        {
            run.Gain = gains[step];
            all.AddRange(run.Periods(13));
        }

        run.Renderer.Pause();
        all.AddRange(run.Periods(7));
        run.Renderer.Play();
        all.AddRange(run.Periods(9));
        run.Ring.RequestFlush();
        run.Hold(4 * Second);
        all.AddRange(run.Periods(2));
        run.Ring.Start(4 * Rate, Rate);
        run.Write(4 * Rate, Rate, Run.Sine);
        run.Renderer.Play();
        all.AddRange(run.Periods(20));
        run.Hold(0);
        all.AddRange(run.Periods(2));

        float worst = 0;
        for (int i = 2; i < all.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs(all[i] - all[i - 2]));
        }

        Assert.InRange(worst, 0.05f, 0.115f);
    }

    [Fact]
    public void Periods_DoNotAllocate()
    {
        using var run = new Run();
        run.FillFrom(0, 2 * Rate, Run.Sine);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(5);

        var output = new float[2 * Period];
        long before = GC.GetAllocatedBytesForCurrentThread();
        run.PeriodsInto(output, 20);
        run.Renderer.Pause();
        run.PeriodsInto(output, 5);
        run.Renderer.Play();
        run.PeriodsInto(output, 5);
        run.Renderer.Hold(Second, 7);
        run.PeriodsInto(output, 5);
        run.Renderer.Play();
        run.PeriodsInto(output, 20);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Near(long expected, long actual, long tolerance) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    /// <summary>Renderer, ring and clock with a device on virtual time.</summary>
    private sealed class Run : IDisposable
    {
        public const float Step = 1f / (1 << 16);
        private const int LatencyPeriods = 2;
        private readonly int _period;
        private long _startTimestamp = Frequency; // virtual time starts at 1 s
        private int _holdSequence;

        public Run(int period = Period)
        {
            _period = period;
            Ring = new AudioRingBuffer(2, 1 << 19);
            Clock = new AudioPlaybackClock();
            Renderer = new AudioRenderer(Ring, Clock, Rate, Frequency);
            Now = _startTimestamp;
        }

        public AudioRingBuffer Ring { get; }

        public AudioPlaybackClock Clock { get; }

        public AudioRenderer Renderer { get; }

        public long Now { get; private set; }

        public float Gain { get; set; } = 1f;

        /// <summary>A rising signal that tells the media frame (left; right is the negative).</summary>
        public static float Ramp(long frame) => ((frame % (1 << 16)) * Step) + 0.01f;

        public static float Sine(long frame) => 0.8f * MathF.Sin(2 * MathF.PI * 1000 * frame / Rate);

        /// <summary>When the stream frame is heard: written one period ahead, played <see cref="LatencyPeriods"/> later.</summary>
        public long HeardTimestamp(long streamFrame) =>
            _startTimestamp + ((streamFrame + (LatencyPeriods * (long)_period)) * Frequency / Rate);

        public void Hold(long mediaTicks)
        {
            _holdSequence = Clock.RequestHold(mediaTicks);
            Renderer.Hold(mediaTicks, _holdSequence);
        }

        /// <summary>Flushes the ring (served by a silent period) and fills it from <paramref name="firstFrame"/>.</summary>
        public void FillFrom(long firstFrame, int frames, Func<long, float> signal)
        {
            Ring.RequestFlush();
            PeriodsInto(new float[2 * _period], 1);
            Ring.Start(firstFrame, Rate);
            Write(firstFrame, frames, signal);
        }

        public void Write(long firstFrame, int frames, Func<long, float> signal)
        {
            var data = new float[2 * frames];
            for (int i = 0; i < frames; i++)
            {
                data[2 * i] = signal(firstFrame + i);
                data[(2 * i) + 1] = -signal(firstFrame + i);
            }

            Assert.Equal(frames, Ring.Write(data));
        }

        public float[] Periods(int count)
        {
            var all = new float[2 * _period * count];
            var output = new float[2 * _period];
            for (int i = 0; i < count; i++)
            {
                PeriodsInto(output, 1);
                output.CopyTo(all, 2 * _period * i);
            }

            return all;
        }

        public void PeriodsInto(float[] output, int count)
        {
            for (int i = 0; i < count; i++)
            {
                // The device has just played the frame heard now; position reported at "now".
                long written = Renderer.WrittenFrames;
                double heardNow = written - (LatencyPeriods * (long)_period);
                Renderer.UpdatePosition(Now, heardNow, Now);
                Renderer.Render(output, _period, HeardTimestamp(written), Gain);
                Now += _period * Frequency / Rate;
            }
        }

        /// <summary>A new device stream at the current time.</summary>
        public void Attach()
        {
            Renderer.Attach(Rate, Now);
            _startTimestamp = Now;
        }

        /// <summary>Clock at the heard time of the latest written frame minus its media time.</summary>
        public long ClockErrorTicks()
        {
            AudioClockTracker tracker = Renderer.Tracker;
            long frame = Renderer.WrittenFrames - 1;
            return Clock.GetMediaTicks(HeardTimestamp(frame)) - tracker.MediaTicksAt(frame);
        }

        public void Dispose() => Ring.Dispose();
    }
}
