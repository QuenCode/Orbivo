using System.Diagnostics;
using System.Numerics;
using Orbivo.Core.Audio;
using Orbivo.Media.Tests.Support;

namespace Orbivo.Media.Tests;

/// <summary>
/// Ambisonics sound tracks (M10) through the real demuxer and decoder: the audio mode is detected (F23) and the sound
/// field reaches the ring buffer as AmbiX with its directions intact. The direction test videos play pink noise bursts
/// from eight directions (tools/testmedia, manifest.json); the energy-weighted intensity vector W·(X, Y, Z) of each burst
/// must point there.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class AmbisonicsDecoderTests(HardwareDecodeFixture fixture)
{
    /// <summary>Noise bursts of each direction (start times in seconds, 0.2 s long), Ambisonics axes X front, Y left, Z up.</summary>
    private static readonly (string Name, double YawDegrees, double PitchDegrees, double[] Bursts)[] Directions =
    [
        ("vorne", 0, 0, [1.71, 2.06, 2.41]),
        ("rechts", -90, 0, [4.647, 4.997, 5.347]),
        ("hinten", 180, 0, [7.68, 8.03, 8.38]),
        ("links", 90, 0, [10.685, 11.035, 11.385]),
        ("oben", 0, 90, [13.644, 13.994, 14.344]),
        ("unten", 0, -90, [16.622, 16.972, 17.322]),
        ("vorne links", 45, 0, [19.898, 20.248, 20.598]),
        ("vorne rechts", -45, 0, [23.121, 23.471, 23.821]),
    ];

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Sa3d_IsAmbiXFromMetadata_WithAllDirections(int outputRate)
    {
        fixture.RequireHardware();
        using var run = FieldRun.Open(TestVideos.AmbisonicsSa3d, outputRate, null);

        Assert.Equal(new EmbeddedAudioLayout(4, 1, LayoutKnown: false), run.Demuxer.AudioLayout);
        Assert.Equal(ChannelHandling.Ambisonics, run.Decoder.Plan.Channels);
        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.AmbiX, 1), run.Decoder.Plan.Mode!.Mode);
        Assert.Equal(AudioModeSource.Metadata, run.Decoder.Plan.Mode.Source);
        AssertDirections(run.CollectUntilEnd(), outputRate);
    }

    [Fact]
    public void WithoutMetadata_IsGuessedAsAmbiX()
    {
        fixture.RequireHardware();
        using var run = FieldRun.Open(TestVideos.AmbisonicsWithoutMetadata, 48_000, null);

        Assert.Equal(new EmbeddedAudioLayout(4, null, LayoutKnown: false), run.Demuxer.AudioLayout);
        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.AmbiX, 1), run.Decoder.Plan.Mode!.Mode);
        Assert.Equal(AudioModeSource.ChannelCount, run.Decoder.Plan.Mode.Source);
        AssertDirections(run.CollectUntilEnd(), 48_000);
    }

    [Fact]
    public void FuMa_FromTheSidecarChoice_IsConvertedToAmbiX()
    {
        fixture.RequireHardware();
        using var run = FieldRun.Open(TestVideos.AmbisonicsFuMa, 48_000, AudioModeChoice.FuMa);

        Assert.True(run.Decoder.Plan.ConvertsFuMa);
        Assert.Equal(AudioModeSource.Sidecar, run.Decoder.Plan.Mode!.Source);
        float[][] field = run.CollectUntilEnd();
        AssertDirections(field, 48_000);

        // AmbiX W: X² + Y² + Z² of a single source equals W² (SN3D), not 2 W² as in FuMa.
        double ratio = EnergyRatio(field, 48_000, Directions[0].Bursts[0]);
        Assert.InRange(ratio, 0.75, 1.25);
    }

    [Fact]
    public void ThirdOrder_Brings16ChannelsIntoTheRing()
    {
        fixture.RequireHardware();
        using var run = FieldRun.Open(TestVideos.AmbisonicsThirdOrder, 48_000, null);

        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.AmbiX, 3), run.Decoder.Plan.Mode!.Mode);
        Assert.Equal(16, run.Decoder.Plan.RingChannels);
        float[][] field = run.CollectUntilEnd();
        Assert.Equal(16, field.Length);
        AssertDirections(field, 48_000);

        // The higher orders carry sound too (a source at the front has energy in ACN 8, the cos 2φ term of order 2).
        double acn8 = Rms(field[8], 48_000, Directions[0].Bursts[0]);
        double w = Rms(field[0], 48_000, Directions[0].Bursts[0]);
        Assert.InRange(acn8 / w, 0.5, 1.0);
    }

    [Fact]
    public void ChannelsChoice_MixesTheFieldDownToStereo()
    {
        fixture.RequireHardware();
        using var run = FieldRun.Open(TestVideos.AmbisonicsSa3d, 48_000, AudioModeChoice.Channels);

        Assert.Equal(ChannelHandling.Downmix, run.Decoder.Plan.Channels);
        Assert.Equal(2, run.Decoder.Plan.RingChannels);
        Assert.Equal(2, run.CollectUntilEnd().Length);
    }

    private static void AssertDirections(float[][] field, int rate)
    {
        foreach ((string name, double yaw, double pitch, double[] bursts) in Directions)
        {
            Vector3 intensity = Vector3.Zero;
            foreach (double burst in bursts)
            {
                intensity += Intensity(field, rate, burst);
            }

            Vector3 measured = Vector3.Normalize(intensity);
            double yawRad = yaw * Math.PI / 180;
            double pitchRad = pitch * Math.PI / 180;
            var expected = new Vector3(
                (float)(Math.Cos(pitchRad) * Math.Cos(yawRad)),
                (float)(Math.Cos(pitchRad) * Math.Sin(yawRad)),
                (float)Math.Sin(pitchRad));
            float match = Vector3.Dot(measured, expected);
            TestContext.Current.TestOutputHelper?.WriteLine($"{name}: measured {measured}, match {match:0.000}");
            Assert.True(match > 0.95f, $"{name}: sound comes from {measured}, expected {expected}");
        }
    }

    /// <summary>Mean of W·(X, Y, Z) over a burst, in Ambisonics axes (ACN 3 = X, 1 = Y, 2 = Z).</summary>
    private static Vector3 Intensity(float[][] field, int rate, double start)
    {
        (int from, int to) = Window(field[0].Length, rate, start);
        double x = 0, y = 0, z = 0;
        for (int i = from; i < to; i++)
        {
            double w = field[0][i];
            x += w * field[3][i];
            y += w * field[1][i];
            z += w * field[2][i];
        }

        return new Vector3((float)x, (float)y, (float)z);
    }

    private static double EnergyRatio(float[][] field, int rate, double start)
    {
        double w = Rms(field[0], rate, start);
        double xyz = Math.Pow(Rms(field[1], rate, start), 2) + Math.Pow(Rms(field[2], rate, start), 2) + Math.Pow(Rms(field[3], rate, start), 2);
        return xyz / (w * w);
    }

    private static double Rms(float[] channel, int rate, double start)
    {
        (int from, int to) = Window(channel.Length, rate, start);
        double sum = 0;
        for (int i = from; i < to; i++)
        {
            sum += channel[i] * (double)channel[i];
        }

        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>The middle 0.15 s of a 0.2 s burst (its fades left out), as indices from media frame 0.</summary>
    private static (int From, int To) Window(int length, int rate, double start)
    {
        int from = (int)((start + 0.025) * rate);
        int to = Math.Min(length, (int)((start + 0.175) * rate));
        Assert.True(to > from, $"the sound ends before {start} s");
        return (from, to);
    }

    /// <summary>Demuxer and audio decoder of one file; the test reads the ring as the output thread would.</summary>
    private sealed class FieldRun : IDisposable
    {
        private FieldRun(MediaDemuxer demuxer, AudioRingBuffer ring, AudioDecoder decoder)
        {
            Demuxer = demuxer;
            Ring = ring;
            Decoder = decoder;
        }

        public MediaDemuxer Demuxer { get; }

        public AudioRingBuffer Ring { get; }

        public AudioDecoder Decoder { get; }

        public static FieldRun Open(string path, int outputRate, AudioModeChoice? choice)
        {
            var logger = TestOutputLoggerFactory.Instance.CreateLogger("AmbisonicsDecoder");
            MediaDemuxer demuxer = MediaDemuxer.Open(path, withAudio: true, logger);
            var ring = new AudioRingBuffer(AmbisonicsLayout.MaxChannels, 65_536);
            AudioDecoder decoder = AudioDecoder.Open(demuxer, ring, outputRate, choice, logger);
            decoder.Start();
            demuxer.Start();
            return new FieldRun(demuxer, ring, decoder);
        }

        /// <summary>Reads everything up to the end; returns each channel from media frame 0 (silence before the first frame).</summary>
        public float[][] CollectUntilEnd()
        {
            int channels = Decoder.Plan.RingChannels;
            var samples = new List<float>[channels];
            for (int c = 0; c < channels; c++)
            {
                samples[c] = new List<float>(48_000 * 27);
            }

            var buffer = new float[channels * 4096];
            bool started = false;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (Ring.FlushPending)
                {
                    Ring.AcknowledgeFlush();
                }

                if (!Ring.HasStart)
                {
                    Thread.Sleep(1);
                    continue;
                }

                if (!started)
                {
                    Assert.Equal(channels, Ring.FrameChannels);
                    Assert.InRange(Ring.ReadPosition, 0, 48);
                    for (int c = 0; c < channels; c++)
                    {
                        samples[c].AddRange(new float[(int)Ring.ReadPosition]);
                    }

                    started = true;
                }

                int read = Ring.Read(buffer, 4096);
                for (int i = 0; i < read; i++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        samples[c].Add(buffer[(i * channels) + c]);
                    }
                }

                if (read == 0)
                {
                    if (Ring.EndOfData)
                    {
                        return [.. samples.Select(list => list.ToArray())];
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
