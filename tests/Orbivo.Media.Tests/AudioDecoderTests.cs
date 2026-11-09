using System.Diagnostics;
using Orbivo.Core.Audio;
using Orbivo.Media.Tests.Support;

namespace Orbivo.Media.Tests;

/// <summary>
/// Real sound decoding (AAC through FFmpeg and swresample) of the test videos, without any output device: a test
/// consumer takes the ring buffer's content by media position. The test videos beep (1 kHz) for two frames at every
/// full second, exactly when the white field flashes; the beep therefore has to start at whole seconds of media time.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class AudioDecoderTests(HardwareDecodeFixture fixture)
{
    /// <summary>The beep has amplitude 0.5; its first loud sample is the onset (AAC pre-echo stays well below).</summary>
    private const float OnsetThreshold = 0.15f;

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Mono4K_BeepsAtEveryFullSecond(int outputRate)
    {
        fixture.RequireHardware();
        using var run = SoundRun.Open(TestVideos.Mono4K, outputRate);
        Assert.Equal("aac", run.Decoder.Info.Codec);
        Assert.Equal(ChannelHandling.Stereo, run.Decoder.Plan.Channels);
        Assert.Equal(outputRate != 48_000, run.Decoder.Plan.Resamples);

        float[] left = run.CollectUntilEnd(out long first);
        Assert.Null(run.Decoder.ErrorMessage);
        Assert.InRange(first, 0, outputRate / 1000);

        // 20 s of sound.
        Assert.InRange(first + left.Length, 19.9 * outputRate, 20.1 * outputRate);
        for (int second = 0; second < 20; second++)
        {
            long onset = FindOnset(left, first, second * (long)outputRate - (outputRate / 10));
            double offsetMs = (onset - (second * (long)outputRate)) * 1000.0 / outputRate;
            TestContext.Current.TestOutputHelper?.WriteLine($"beep {second}: {offsetMs:+0.00;-0.00} ms");
            Assert.InRange(offsetMs, -1.0, 1.0);
        }
    }

    [Fact]
    public void Seek_StartsTheRingExactlyAtTheTarget()
    {
        fixture.RequireHardware();
        using var run = SoundRun.Open(TestVideos.Mono4K, 48_000);
        run.CollectSome(TimeSpan.FromMilliseconds(300));

        int seekId = run.Demuxer.RequestSeek(TimeSpan.FromSeconds(7.5).Ticks);
        var stopwatch = Stopwatch.StartNew();
        while (!run.Decoder.IsPrerolled)
        {
            run.ServeFlush();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "seek did not finish");
            Thread.Sleep(1);
        }

        Assert.Equal(seekId, run.Demuxer.RequestedSeekId);
        Assert.True(run.Ring.HasStart);
        Assert.Equal(360_000, run.Ring.ReadPosition);

        float[] left = run.CollectUntilEnd(out long first);
        Assert.Equal(360_000, first);
        long onset = FindOnset(left, first, 360_000);
        Assert.InRange(onset - 384_000, -48, 48);
    }

    private static long FindOnset(float[] left, long first, long from)
    {
        for (long position = Math.Max(from, first); position < first + left.Length; position++)
        {
            if (Math.Abs(left[position - first]) > OnsetThreshold)
            {
                return position;
            }
        }

        Assert.Fail($"No beep after frame {from}.");
        return -1;
    }

    /// <summary>Demuxer and audio decoder of one file, with the test standing in for the audio output thread.</summary>
    private sealed class SoundRun : IDisposable
    {
        private SoundRun(MediaDemuxer demuxer, AudioRingBuffer ring, AudioDecoder decoder)
        {
            Demuxer = demuxer;
            Ring = ring;
            Decoder = decoder;
        }

        public MediaDemuxer Demuxer { get; }

        public AudioRingBuffer Ring { get; }

        public AudioDecoder Decoder { get; }

        public static SoundRun Open(string path, int outputRate)
        {
            var logger = TestOutputLoggerFactory.Instance.CreateLogger("AudioDecoder");
            MediaDemuxer demuxer = MediaDemuxer.Open(path, withAudio: true, logger);
            Assert.True(demuxer.AudioStreamIndex >= 0, "the test video has a sound track");
            var ring = new AudioRingBuffer(2, 65_536);
            AudioDecoder decoder = AudioDecoder.Open(demuxer, ring, outputRate, null, logger);
            decoder.Start();
            demuxer.Start();
            return new SoundRun(demuxer, ring, decoder);
        }

        public void ServeFlush()
        {
            if (Ring.FlushPending)
            {
                Ring.AcknowledgeFlush();
            }
        }

        public void CollectSome(TimeSpan duration)
        {
            var buffer = new float[2 * 4096];
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < duration)
            {
                ServeFlush();
                Ring.Read(buffer, 4096);
                Thread.Sleep(1);
            }
        }

        /// <summary>Reads everything up to the end of the track; returns the left channel from media frame <paramref name="first"/>.</summary>
        public float[] CollectUntilEnd(out long first)
        {
            var left = new List<float>(48_000 * 21);
            var buffer = new float[2 * 4096];
            first = -1;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                ServeFlush();
                if (!Ring.HasStart)
                {
                    Thread.Sleep(1);
                    continue;
                }

                // The position before the read that returns the first frames. (Checking AvailableFrames first raced
                // with the decoder: sound arriving in between was read before "first" was set.)
                long position = Ring.ReadPosition;
                int read = Ring.Read(buffer, 4096);
                if (first < 0 && read > 0)
                {
                    first = position;
                }

                for (int i = 0; i < read; i++)
                {
                    left.Add(buffer[2 * i]);
                    Assert.InRange(buffer[2 * i] - buffer[(2 * i) + 1], -0.02f, 0.02f); // the same beep on both sides (AAC codes them apart)
                }

                if (read == 0)
                {
                    if (Ring.EndOfData)
                    {
                        return left.ToArray();
                    }

                    Assert.Null(Decoder.ErrorMessage);
                    Thread.Sleep(1);
                }
            }

            Assert.Fail("The sound track did not end.");
            return [];
        }

        public void Dispose()
        {
            Decoder.Dispose();
            Demuxer.Dispose();
            Ring.Dispose();
        }
    }
}
