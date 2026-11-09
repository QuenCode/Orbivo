using System.Numerics;
using Orbivo.Audio.Output;
using Orbivo.Audio.Spatial;
using Orbivo.Core.Audio;
using Orbivo.Core.Geometry;

namespace Orbivo.Audio.Tests.Output;

/// <summary>
/// Ambisonics fields inside the renderer (M10), with a fake spatializer that puts W on the left ear and X on the right:
/// the output shows exactly which media frame went through it, so the block of delay must not shift the sound against
/// the clock. Field frames: W = ramp(m), Y = ramp(m) / 4, Z = 0, X = −ramp(m), higher channels = channel number.
/// </summary>
public sealed class AudioRendererAmbisonicsTests
{
    private const int Rate = 48_000;
    private const long Frequency = 10_000_000;
    private const long Second = TimeSpan.TicksPerSecond;
    private const int Block = 256;

    [Theory]
    [InlineData(480, 4)]
    [InlineData(441, 9)]
    [InlineData(1100, 16)]
    public void Field_OutputIsAlignedToTheMediaPosition(int period, int channels)
    {
        using var run = new Run(period);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(Rate, 2 * Rate, channels);
        run.Hold(Second);
        run.Renderer.Play();
        float[] output = run.Periods(20);

        Assert.True(run.Renderer.SpatialActive);
        Assert.True(run.Renderer.FieldBinaural);
        Assert.Equal(channels, run.Renderer.SourceChannels);
        Assert.Equal(channels, run.Spatializer.LastChannels);
        if (channels > 4)
        {
            Assert.Equal(channels - 1, run.Spatializer.LastHighestChannel);
        }
        for (int i = 240; i < output.Length / 2; i++)
        {
            Assert.Equal(Ramp(Rate + i), output[2 * i], 6);
            Assert.Equal(-Ramp(Rate + i), output[(2 * i) + 1], 6);
        }

        Assert.Equal(0, run.Renderer.UnderrunFrames);
        Assert.InRange(run.ClockErrorTicks(), -1000, 1000);
    }

    [Fact]
    public void FixedToPicture_FollowsTheHead_HeadLocked_LooksAtTheFront()
    {
        var listener = new ListenerOrientation();
        Quaternion turned = OrientationMath.FromYawPitch(MathF.PI / 2, 0);
        listener.Publish(turned, 0);
        using var run = new Run(480, listener);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(0, 4 * Rate, 4);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(4);
        Assert.Equal(turned, run.Spatializer.LastHead);

        // Off: the field turns with the head, through a dip, and stays aligned.
        run.Renderer.SetPlacement(StereoPlacement.HeadLocked);
        long first = run.Renderer.WrittenFrames;
        float[] output = run.Periods(10);
        Assert.Equal(Quaternion.Identity, run.Spatializer.LastHead);
        Assert.False(run.Renderer.SpatialActive);
        Assert.True(run.Renderer.FieldBinaural);
        for (int i = 1200; i < output.Length / 2; i++)
        {
            Assert.Equal(Ramp(first - run.StartStreamFrame + i), output[2 * i], 6);
        }
    }

    [Fact]
    public void WithoutAStage_TheFieldPlaysThroughTheCardioidPair()
    {
        using var run = new Run(480, withStage: false);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(0, Rate, 4);
        run.Hold(0);
        run.Renderer.Play();
        float[] output = run.Periods(3);

        Assert.False(run.Renderer.FieldBinaural);
        Assert.False(run.Renderer.SpatialActive);
        float w = Ramp(400);
        Assert.Equal(0.5f * (w + (w / 4)), output[2 * 400], 6);
        Assert.Equal(0.5f * (w - (w / 4)), output[(2 * 400) + 1], 6);
    }

    [Fact]
    public void NewFileWithAnotherChannelCount_PlaysAlignedAfterTheFlush()
    {
        using var run = new Run(480);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(0, 2 * Rate, 2);
        run.Hold(0);
        run.Renderer.Play();
        run.Periods(10);
        Assert.Equal(2, run.Renderer.SourceChannels);

        int generation = run.Ring.RequestFlush();
        run.Hold(3 * Second);
        run.Periods(1);
        Assert.True(run.Ring.IsFlushed(generation));
        run.Ring.Start(3 * Rate, Rate, 4);
        run.Write(3 * Rate, Rate, 4);
        run.Renderer.Play();
        float[] output = run.Periods(5);

        Assert.Equal(4, run.Renderer.SourceChannels);
        for (int i = 240; i < output.Length / 2; i++)
        {
            Assert.Equal(Ramp((3 * Rate) + i), output[2 * i], 6);
        }
    }

    [Fact]
    public void Render_Field_DoesNotAllocate()
    {
        using var run = new Run(480);
        run.Renderer.SetPlacement(StereoPlacement.Default);
        run.FillFrom(0, 2 * Rate, 16);
        run.Hold(0);
        run.Renderer.Play();
        var buffer = new float[2 * 480];
        run.PeriodsInto(buffer, 5);

        long before = GC.GetAllocatedBytesForCurrentThread();
        run.PeriodsInto(buffer, 50);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static float Ramp(long frame) => ((frame % (1 << 16)) / (float)(1 << 16)) + 0.01f;

    /// <summary>W to the left ear, X to the right; remembers the head and the channels of the last block.</summary>
    private sealed class FakeAmbisonicsSpatializer : IAmbisonicsSpatializer
    {
        public int BlockFrames => Block;

        public int SampleRate => Rate;

        public int MaxOrder => 3;

        public Quaternion LastHead { get; private set; }

        public int LastChannels { get; private set; }

        /// <summary>Value of the last channel in the last frame: its channel number if the field arrived whole.</summary>
        public float LastHighestChannel { get; private set; }

        public void Reset()
        {
        }

        public void Process(ReadOnlySpan<float> ambiX, int channels, Span<float> stereoOut, Quaternion headInContent)
        {
            for (int i = 0; i < Block; i++)
            {
                stereoOut[2 * i] = ambiX[i * channels];
                stereoOut[(2 * i) + 1] = ambiX[(i * channels) + 3];
            }

            LastHead = headInContent;
            LastChannels = channels;
            LastHighestChannel = ambiX[(Block * channels) - 1];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Renderer on virtual time like in <see cref="AudioRendererTests"/>, with an Ambisonics stage.</summary>
    private sealed class Run : IDisposable
    {
        private const int LatencyPeriods = 2;
        private readonly int _period;
        private readonly long _startTimestamp = Frequency;
        private int _holdSequence;

        public Run(int period, ListenerOrientation? listener = null, bool withStage = true)
        {
            _period = period;
            Ring = new AudioRingBuffer(AmbisonicsLayout.MaxChannels, 1 << 19);
            Clock = new AudioPlaybackClock();
            Renderer = new AudioRenderer(Ring, Clock, Rate, Frequency);
            Now = _startTimestamp;
            if (withStage)
            {
                Renderer.SetAmbisonicsStage(new AmbisonicsStage(Spatializer, listener));
            }
        }

        public long StartStreamFrame { get; private set; }

        public FakeAmbisonicsSpatializer Spatializer { get; } = new();

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

        public void FillFrom(long firstFrame, int frames, int channels)
        {
            Ring.RequestFlush();
            PeriodsInto(new float[2 * _period], 1);
            Ring.Start(firstFrame, Rate, channels);
            Write(firstFrame, frames, channels);
        }

        public void Write(long firstFrame, int frames, int channels)
        {
            var data = new float[channels * frames];
            for (int i = 0; i < frames; i++)
            {
                float ramp = Ramp(firstFrame + i);
                Span<float> frame = data.AsSpan(channels * i, channels);
                if (channels == 2)
                {
                    frame[0] = ramp;
                    frame[1] = -ramp;
                    continue;
                }

                frame[0] = ramp;
                frame[1] = ramp / 4;
                frame[2] = 0;
                frame[3] = -ramp;
                for (int c = 4; c < channels; c++)
                {
                    frame[c] = c;
                }
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
            Renderer.SetAmbisonicsStage(null);
            Ring.Dispose();
        }
    }
}
