using System.Numerics;
using Orbivo.Audio.Output;
using Orbivo.Audio.Spatial;
using Orbivo.Core.Audio;
using Orbivo.Core.Geometry;

namespace Orbivo.Audio.Tests.Output;

/// <summary>
/// Sound fixed to the picture inside the renderer (M7), with a fake spatializer that swaps the channels: the output
/// shows exactly which media frame went through it, so the extra block of delay must not shift sound against the clock.
/// </summary>
public sealed class AudioRendererSpatialTests
{
    private const int Rate = 48_000;
    private const long Frequency = 10_000_000;
    private const long Second = TimeSpan.TicksPerSecond;
    private const int Block = 256;

    [Theory]
    [InlineData(480)]
    [InlineData(441)]
    [InlineData(100)]
    public void FixedToPicture_OutputIsAlignedToTheMediaPosition(int period)
    {
        using var run = new Run(period);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        Assert.True(run.Renderer.SpatialActive);
        run.FillFrom(Rate, 2 * Rate);
        run.Hold(Second);
        run.Renderer.Play();
        float[] output = run.Periods(20);

        // After the 240-frame fade-in, every frame is the swapped sound of its own media frame.
        for (int i = 240; i < output.Length / 2; i++)
        {
            Assert.Equal(-Ramp(Rate + i), output[2 * i], 6);
            Assert.Equal(Ramp(Rate + i), output[(2 * i) + 1], 6);
        }

        Assert.Equal(0, run.Renderer.UnderrunFrames);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void SwitchingWhilePlaying_DipsAndStaysAligned()
    {
        using var run = new Run(480);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(0, 4 * Rate);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(10);

        run.Renderer.SetPlacement(StereoPlacement.HeadLocked);
        Assert.True(run.Renderer.SpatialActive, "the switch waits for the dip");
        long first = run.Renderer.WrittenFrames;
        float[] plain = run.Periods(10);
        Assert.False(run.Renderer.SpatialActive);
        AssertAligned(plain, first - run.StartStreamFrame, swapped: false, skipFrames: 1200);

        run.Renderer.SetPlacement(StereoPlacement.Default);
        first = run.Renderer.WrittenFrames;
        float[] fixedAgain = run.Periods(10);
        Assert.True(run.Renderer.SpatialActive);
        AssertAligned(fixedAgain, first - run.StartStreamFrame, swapped: true, skipFrames: 1200);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void Seek_PrimesTheDelayLineAtTheNewPosition()
    {
        using var run = new Run(480);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(0, 2 * Rate);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(10);

        int generation = run.Ring.RequestFlush();
        run.Hold(3 * Second);
        run.Periods(1);
        Assert.True(run.Ring.IsFlushed(generation));
        run.Ring.Start(3 * Rate, Rate);
        run.Write(3 * Rate, Rate);
        run.Renderer.Play();
        float[] output = run.Periods(5);

        for (int i = 240; i < output.Length / 2; i++)
        {
            Assert.Equal(-Ramp((3 * Rate) + i), output[2 * i], 6);
        }
    }

    [Fact]
    public void EachBlockUsesTheNewestHeadOrientationAndTheWidth()
    {
        var listener = new ListenerOrientation();
        using var run = new Run(480, listener);
        run.Renderer.SetPlacement(new StereoPlacement(true, 45));
        run.FillFrom(0, Rate);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(2);
        AssertClose(AudioCoordinates.HorizontalDirection(45), run.Spatializer.LastLeft);
        AssertClose(AudioCoordinates.HorizontalDirection(-45), run.Spatializer.LastRight);

        // Head turned left by 90Â°: the left speaker (+45Â°) is now at âˆ’45Â°, front right.
        listener.Publish(OrientationMath.FromYawPitch(MathF.PI / 2, 0), 0);
        run.Periods(2);
        AssertClose(AudioCoordinates.HorizontalDirection(-45), run.Spatializer.LastLeft);

        // A new width applies without a dip.
        run.Renderer.SetPlacement(new StereoPlacement(true, 80));
        run.Periods(2);
        AssertClose(AudioCoordinates.HorizontalDirection(-10), run.Spatializer.LastLeft);
    }

    [Fact]
    public void WithoutAStage_FixedToPictureFallsBackToPlainStereo()
    {
        using var run = new Run(480, withStage: false);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        Assert.False(run.Renderer.SpatialActive);
        run.FillFrom(0, Rate);
        run.Hold(0);
        run.Renderer.Play();
        float[] output = run.Periods(3);

        Assert.Equal(Ramp(400), output[2 * 400], 6);
    }

    [Fact]
    public void Render_FixedToPicture_DoesNotAllocate()
    {
        using var run = new Run(480);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(0, 2 * Rate);
        run.Hold(0);
        run.Renderer.Play();
        var buffer = new float[2 * 480];
        run.PeriodsInto(buffer, 5);

        long before = GC.GetAllocatedBytesForCurrentThread();
        run.PeriodsInto(buffer, 50);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static float Ramp(long frame) => ((frame % (1 << 16)) / (float)(1 << 16)) + 0.01f;

    private static void AssertAligned(float[] output, long firstMediaFrame, bool swapped, int skipFrames)
    {
        // Stream frames and media frames move together from Play at 0 on (no gaps in this test).
        for (int i = skipFrames; i < output.Length / 2; i++)
        {
            float expected = Ramp(firstMediaFrame + i);
            Assert.Equal(swapped ? -expected : expected, output[2 * i], 6);
        }
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
    }

    /// <summary>Swaps left and right and remembers the directions of the last block.</summary>
    private sealed class SwapSpatializer : IBlockSpatializer
    {
        public int BlockFrames => Block;

        public int SampleRate => Rate;

        public Vector3 LastLeft { get; private set; }

        public Vector3 LastRight { get; private set; }

        public void Reset()
        {
        }

        public void Process(ReadOnlySpan<float> stereoIn, Span<float> stereoOut, Vector3 leftDirection, Vector3 rightDirection)
        {
            for (int i = 0; i < Block; i++)
            {
                stereoOut[2 * i] = stereoIn[(2 * i) + 1];
                stereoOut[(2 * i) + 1] = stereoIn[2 * i];
            }

            LastLeft = leftDirection;
            LastRight = rightDirection;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Renderer on virtual time like in <see cref="AudioRendererTests"/>, with a spatial stage.</summary>
    private sealed class Run : IDisposable
    {
        private const int LatencyPeriods = 2;
        private readonly int _period;
        private readonly long _startTimestamp = Frequency;
        private int _holdSequence;

        public Run(int period, ListenerOrientation? listener = null, bool withStage = true)
        {
            _period = period;
            Ring = new AudioRingBuffer(2, 1 << 19);
            Clock = new AudioPlaybackClock();
            Renderer = new AudioRenderer(Ring, Clock, Rate, Frequency);
            Now = _startTimestamp;
            if (withStage)
            {
                Renderer.SetStage(new SpatialStage(Spatializer, listener));
            }
        }

        /// <summary>Stream frame at which the tests press Play (after the one flush period of <see cref="FillFrom"/>).</summary>
        public long StartStreamFrame { get; private set; }

        public SwapSpatializer Spatializer { get; } = new();

        public AudioRingBuffer Ring { get; }

        public AudioPlaybackClock Clock { get; }

        public AudioRenderer Renderer { get; }

        public long Now { get; private set; }

        public long HeardTimestamp(long streamFrame) =>
            _startTimestamp + ((streamFrame + (LatencyPeriods * (long)_period)) * Frequency / Rate);

        public void Hold(long mediaTicks)
        {
            _holdSequence = Clock.RequestHold(mediaTicks);
            Renderer.Hold(mediaTicks, _holdSequence);
            StartStreamFrame = Renderer.WrittenFrames;
        }

        public void FillFrom(long firstFrame, int frames)
        {
            Ring.RequestFlush();
            PeriodsInto(new float[2 * _period], 1);
            Ring.Start(firstFrame, Rate);
            Write(firstFrame, frames);
        }

        public void Write(long firstFrame, int frames)
        {
            var data = new float[2 * frames];
            for (int i = 0; i < frames; i++)
            {
                data[2 * i] = Ramp(firstFrame + i);
                data[(2 * i) + 1] = -Ramp(firstFrame + i);
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
                long written = Renderer.WrittenFrames;
                double heardNow = written - (LatencyPeriods * (long)_period);
                Renderer.UpdatePosition(Now, heardNow, Now);
                Renderer.Render(output, _period, HeardTimestamp(written), 1f);
                Now += _period * Frequency / Rate;
            }
        }

        public long ClockErrorTicks()
        {
            long frame = Renderer.WrittenFrames - 1;
            return Clock.GetMediaTicks(HeardTimestamp(frame)) - Renderer.Tracker.MediaTicksAt(frame);
        }

        public void Dispose()
        {
            Renderer.SetStage(null);
            Ring.Dispose();
        }
    }
}
