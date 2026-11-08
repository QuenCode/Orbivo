using System.Diagnostics;
using System.Numerics;
using Orbivo.Audio.Spatial;
using Orbivo.Core.Audio;
using Orbivo.Core.Geometry;

namespace Orbivo.Audio.Tests;

/// <summary>
/// Sound fixed to the picture with the real phonon.dll (M7). Nobody listens here, so the direction is measured: the ear
/// closer to a speaker gets more level (interaural level difference). Skipped if phonon.dll is not in the output folder.
/// </summary>
public sealed class SteamStereoSpatializerTests : IDisposable
{
    private const int Rate = 48_000;
    private const int Blocks = 120;
    private const int WarmUpBlocks = 20;
    private const double Width = 60;

    private readonly ITestOutputHelper _output;
    private readonly SteamStereoSpatializer _spatializer;

    public SteamStereoSpatializerTests(ITestOutputHelper output)
    {
        _output = output;
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, SteamAudioRuntime.LibraryFileName)), "phonon.dll missing (native/README.md)");
        _spatializer = SteamStereoSpatializer.Create(Rate);
    }

    private enum Channel
    {
        Left,
        Right,
        Both,
    }

    [Fact]
    public void LookingAtTheFront_LeftChannelIsLouderOnTheLeftEar()
    {
        (double left, double right) = EarLevels(Channel.Left, OrientationMath.FromYawPitch(0, 0));

        _output.WriteLine($"left {Db(left):0.0} dB, right {Db(right):0.0} dB");
        Assert.True(Db(left) - Db(right) > 6, $"interaural level difference {Db(left) - Db(right):0.0} dB");
    }

    [Fact]
    public void LookingAtTheFront_RightChannelIsLouderOnTheRightEar()
    {
        (double left, double right) = EarLevels(Channel.Right, OrientationMath.FromYawPitch(0, 0));

        Assert.True(Db(right) - Db(left) > 6, $"interaural level difference {Db(right) - Db(left):0.0} dB");
    }

    [Fact]
    public void HeadTurnedLeft_LeftChannelMovesToTheRightEar()
    {
        // The requirement's example: the tree on the left (left channel) is on the right once the head turns left by 90°.
        (double left, double right) = EarLevels(Channel.Left, OrientationMath.FromYawPitch(MathF.PI / 2, 0));

        _output.WriteLine($"left {Db(left):0.0} dB, right {Db(right):0.0} dB");
        Assert.True(Db(right) - Db(left) > 3, $"interaural level difference {Db(right) - Db(left):0.0} dB");
    }

    [Fact]
    public void HeadTurnedRight_RightChannelMovesToTheLeftEar()
    {
        (double left, double right) = EarLevels(Channel.Right, OrientationMath.FromYawPitch(-MathF.PI / 2, 0));

        Assert.True(Db(left) - Db(right) > 3, $"interaural level difference {Db(left) - Db(right):0.0} dB");
    }

    [Fact]
    public void Center_IsBalancedAndAboutAsLoudAsPlainStereo()
    {
        (double left, double right) = EarLevels(Channel.Both, Quaternion.Identity);
        double input = Rms(Noise(Channel.Both, Blocks * _spatializer.BlockFrames, seed: 1).Where((_, i) => i % 2 == 0).ToArray());

        _output.WriteLine($"in {Db(input):0.0} dB, out left {Db(left):0.0} dB, right {Db(right):0.0} dB");
        Assert.True(Math.Abs(Db(left) - Db(right)) < 1, "a centered sound stays centered");
        Assert.InRange(Db(left) - Db(input), -1, 1);
    }

    [Fact]
    public void TurningTheHead_DoesNotClick()
    {
        // A 440 Hz tone on the left channel while the head turns at 360°/s, a new direction every block. A step in the
        // filters would show as a sample-to-sample jump far above what the tone itself has.
        int frames = _spatializer.BlockFrames;
        var input = new float[2 * frames];
        var output = new float[2 * frames];
        double maxStep = 0;
        double steadyMaxStep = 0;
        float previous = 0;
        for (int block = 0; block < 400; block++)
        {
            for (int i = 0; i < frames; i++)
            {
                int n = (block * frames) + i;
                input[2 * i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / Rate);
                input[(2 * i) + 1] = 0;
            }

            bool turning = block >= 200;
            float yaw = turning ? 2 * MathF.PI * (block - 200) * frames / Rate : 0;
            (Vector3 l, Vector3 r) = AudioCoordinates.StereoSpeakers(Width, OrientationMath.FromYawPitch(yaw, 0));
            _spatializer.Process(input, output, l, r);
            for (int i = 0; i < frames; i++)
            {
                float sample = output[2 * i];
                double step = Math.Abs(sample - previous);
                previous = sample;
                if (block < WarmUpBlocks)
                {
                    continue;
                }

                if (turning)
                {
                    maxStep = Math.Max(maxStep, step);
                }
                else
                {
                    steadyMaxStep = Math.Max(steadyMaxStep, step);
                }
            }
        }

        _output.WriteLine($"largest step: steady {steadyMaxStep:0.0000}, turning {maxStep:0.0000}");

        // The ear level changes with the direction, so allow some growth, but no clicks.
        Assert.True(maxStep < 2.5 * steadyMaxStep, $"turning {maxStep:0.0000} vs steady {steadyMaxStep:0.0000}");
    }

    [Fact]
    public void Process_DoesNotAllocateAndIsFast()
    {
        int frames = _spatializer.BlockFrames;
        float[] input = Noise(Channel.Both, frames, seed: 3);
        var output = new float[2 * frames];
        (Vector3 l, Vector3 r) = AudioCoordinates.StereoSpeakers(Width, Quaternion.Identity);
        _spatializer.Process(input, output, l, r);

        const int runs = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < runs; i++)
        {
            _spatializer.Process(input, output, l, r);
        }

        long ticks = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double blockMs = frames * 1000.0 / Rate;
        double averageMs = ticks * 1000.0 / Stopwatch.Frequency / runs;
        _output.WriteLine($"{averageMs * 1000:0} µs per block of {blockMs:0.00} ms ({averageMs / blockMs:P1} of real time)");

        Assert.Equal(0, allocated);
        Assert.True(averageMs < blockMs * 0.2, $"{averageMs:0.000} ms per block");
    }

    [Fact]
    public void Reset_ClearsTheFilterTail()
    {
        int frames = _spatializer.BlockFrames;
        float[] loud = Noise(Channel.Both, frames, seed: 4);
        var silence = new float[2 * frames];
        var output = new float[2 * frames];
        (Vector3 l, Vector3 r) = AudioCoordinates.StereoSpeakers(Width, Quaternion.Identity);
        _spatializer.Process(loud, output, l, r);

        _spatializer.Reset();
        _spatializer.Process(silence, output, l, r);

        Assert.All(output, sample => Assert.Equal(0f, sample, 1e-6f));
    }

    public void Dispose()
    {
        _spatializer?.Dispose();
    }

    private (double Left, double Right) EarLevels(Channel channel, Quaternion head)
    {
        int frames = _spatializer.BlockFrames;
        float[] input = Noise(channel, Blocks * frames, seed: 1);
        var output = new float[2 * frames];
        (Vector3 l, Vector3 r) = AudioCoordinates.StereoSpeakers(Width, head);
        double left = 0;
        double right = 0;
        int counted = 0;
        for (int block = 0; block < Blocks; block++)
        {
            _spatializer.Process(input.AsSpan(2 * block * frames, 2 * frames), output, l, r);
            if (block < WarmUpBlocks)
            {
                continue;
            }

            for (int i = 0; i < frames; i++)
            {
                left += output[2 * i] * (double)output[2 * i];
                right += output[(2 * i) + 1] * (double)output[(2 * i) + 1];
            }

            counted += frames;
        }

        return (Math.Sqrt(left / counted), Math.Sqrt(right / counted));
    }

    /// <summary>Interleaved white noise (±0.25) on the given channels.</summary>
    private static float[] Noise(Channel channel, int frames, int seed)
    {
        var random = new Random(seed);
        var samples = new float[2 * frames];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)((random.NextDouble() * 0.5) - 0.25);
            samples[2 * i] = channel is Channel.Left or Channel.Both ? value : 0;
            samples[(2 * i) + 1] = channel is Channel.Right or Channel.Both ? value : 0;
        }

        return samples;
    }

    private static double Rms(float[] samples) => Math.Sqrt(samples.Sum(s => s * (double)s) / samples.Length);

    private static double Db(double rms) => 20 * Math.Log10(Math.Max(rms, 1e-12));
}
