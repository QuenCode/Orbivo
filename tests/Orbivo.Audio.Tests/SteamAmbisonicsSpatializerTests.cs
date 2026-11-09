using System.Diagnostics;
using System.Numerics;
using Orbivo.Audio.Spatial;
using Orbivo.Core.Audio;
using Orbivo.Core.Geometry;

namespace Orbivo.Audio.Tests;

/// <summary>
/// Ambisonics binaural rendering with the real phonon.dll (M10). Nobody listens here, so directions are measured by the
/// interaural level difference, and the axis convention is pinned against Steam Audio's own encoder. Skipped if
/// phonon.dll is not in the output folder.
/// </summary>
public sealed class SteamAmbisonicsSpatializerTests : IDisposable
{
    private const int Rate = 48_000;
    private const int Blocks = 120;
    private const int WarmUpBlocks = 20;

    private readonly ITestOutputHelper _output;
    private readonly SteamAmbisonicsSpatializer _spatializer;

    public SteamAmbisonicsSpatializerTests(ITestOutputHelper output)
    {
        _output = output;
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, SteamAudioRuntime.LibraryFileName)), "phonon.dll missing (native/README.md)");
        _spatializer = SteamAmbisonicsSpatializer.Create(Rate);
    }

    /// <summary>
    /// Steam Audio encodes a direction (OpenXR axes) into the same ACN channels as AmbiX does for that direction (X
    /// front, Y left, Z up), with the orthonormal scale √((2l + 1) / 4π). If an update of phonon.dll changed this, every
    /// direction of the Ambisonics sound would be wrong.
    /// </summary>
    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0.6, -0.48, -0.64)]
    [InlineData(-0.36, 0.48, 0.8)]
    public void SteamAudioChannels_AreAmbiXWithTheOrthonormalScale(float x, float y, float z)
    {
        var direction = Vector3.Normalize(new Vector3(x, y, z));
        float[] steam = SteamAmbisonicsEncoder.Gains(direction, 2);
        float[] ambiX = AmbiXSecondOrder(direction);

        for (int acn = 0; acn < 9; acn++)
        {
            float scale = MathF.Sqrt(((2 * AmbisonicsLayout.GetDegree(acn)) + 1) / (4 * MathF.PI));
            Assert.Equal(ambiX[acn] * scale, steam[acn], 3);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void LookingAtTheFront_SourceOnTheLeft_IsLouderOnTheLeftEar(int order)
    {
        (double left, double right) = EarLevels(Left, order, Quaternion.Identity);

        _output.WriteLine($"order {order}: left {Db(left):0.0} dB, right {Db(right):0.0} dB");
        Assert.True(Db(left) - Db(right) > 6, $"interaural level difference {Db(left) - Db(right):0.0} dB");
    }

    [Fact]
    public void LookingAtTheFront_SourceOnTheRight_IsLouderOnTheRightEar()
    {
        (double left, double right) = EarLevels(-Left, 1, Quaternion.Identity);

        Assert.True(Db(right) - Db(left) > 6, $"interaural level difference {Db(right) - Db(left):0.0} dB");
    }

    [Fact]
    public void HeadTurnedLeft_TheFrontOfTheFieldIsOnTheRightEar()
    {
        // The field stays with the picture: after turning left by 90°, the picture's front is to the right.
        (double left, double right) = EarLevels(Front, 1, OrientationMath.FromYawPitch(MathF.PI / 2, 0));

        _output.WriteLine($"left {Db(left):0.0} dB, right {Db(right):0.0} dB");
        Assert.True(Db(right) - Db(left) > 6, $"interaural level difference {Db(right) - Db(left):0.0} dB");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void HeadTurnedToTheSource_HearsItCentered(int order)
    {
        (double left, double right) = EarLevels(Left, order, OrientationMath.FromYawPitch(MathF.PI / 2, 0));
        Assert.True(Math.Abs(Db(left) - Db(right)) < 1.5, $"left {Db(left):0.0} dB, right {Db(right):0.0} dB");

        // Looking up at a source above: centered as well (and not from the side, as a wrong Z axis would give).
        (left, right) = EarLevels(Vector3.UnitY, order, OrientationMath.FromYawPitch(0, MathF.PI / 2));
        Assert.True(Math.Abs(Db(left) - Db(right)) < 1.5, $"up: left {Db(left):0.0} dB, right {Db(right):0.0} dB");

        // Looking up, a source on the left of the picture stays on the left.
        (left, right) = EarLevels(Left, order, OrientationMath.FromYawPitch(0, MathF.PI / 2));
        Assert.True(Db(left) - Db(right) > 6, $"looking up, left source: left {Db(left):0.0} dB, right {Db(right):0.0} dB");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FrontSource_IsAboutAsLoudAsTheMonoSignal(int order)
    {
        (double left, double right) = EarLevels(Front, order, Quaternion.Identity);
        double input = 0.25 / Math.Sqrt(3);

        _output.WriteLine($"order {order}: in {Db(input):0.0} dB, out left {Db(left):0.0} dB, right {Db(right):0.0} dB");
        // The built-in HRTF is not exactly symmetric; first order shows about 1 dB, higher orders less.
        Assert.True(Math.Abs(Db(left) - Db(right)) < 1.5, "a source in front stays centered");
        Assert.InRange(((Db(left) + Db(right)) / 2) - Db(input), -1.5, 1.5);
    }

    /// <summary>
    /// Real recordings (M10 analysis of the project's material) are mostly diffuse, with W well above the directional
    /// channels. Such fields should not play much louder or quieter than a single source with the same W level.
    /// Measured with phonon.dll 4.8.1: W only −2.6 dB, half the directional energy −2.0 dB, ideal diffuse −0.7 dB
    /// (first order; third order −0.6 dB) relative to a source in front.
    /// </summary>
    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(1, 0.5)]
    [InlineData(1, 1.0)]
    [InlineData(3, 1.0)]
    public void DiffuseField_IsAboutAsLoudAsAFrontSourceWithTheSameW(int order, double directionalShare)
    {
        int frames = _spatializer.BlockFrames;
        int channels = AmbisonicsLayout.GetChannelCount(order);
        float[] input = DiffuseField(order, directionalShare, Blocks * frames, seed: 2);
        var output = new float[2 * frames];
        double ears = 0;
        double w = 0;
        int counted = 0;
        _spatializer.Reset();
        for (int block = 0; block < Blocks; block++)
        {
            _spatializer.Process(input.AsSpan(channels * block * frames, channels * frames), channels, output, Quaternion.Identity);
            if (block < WarmUpBlocks)
            {
                continue;
            }

            for (int i = 0; i < frames; i++)
            {
                ears += ((output[2 * i] * (double)output[2 * i]) + (output[(2 * i) + 1] * (double)output[(2 * i) + 1])) / 2;
                double sample = input[channels * ((block * frames) + i)];
                w += sample * sample;
            }

            counted += frames;
        }

        (double left, double right) = EarLevels(Front, order, Quaternion.Identity);
        double frontGain = ((Db(left) + Db(right)) / 2) - Db(0.25 / Math.Sqrt(3));
        double diffuseGain = Db(Math.Sqrt(ears / counted)) - Db(Math.Sqrt(w / counted));
        _output.WriteLine($"order {order}, directional share {directionalShare:0.0}: diffuse {diffuseGain:+0.0;-0.0} dB, front source {frontGain:+0.0;-0.0} dB (ear level relative to W)");
        Assert.InRange(diffuseGain - frontGain, -3.5, 1.5);
    }

    [Fact]
    public void TurningTheHead_DoesNotClick()
    {
        int frames = _spatializer.BlockFrames;
        float[] gains = AmbiXGains(Left, 1);
        var input = new float[4 * frames];
        var output = new float[2 * frames];
        double maxStep = 0;
        double steadyMaxStep = 0;
        float previous = 0;
        for (int block = 0; block < 400; block++)
        {
            for (int i = 0; i < frames; i++)
            {
                int n = (block * frames) + i;
                float sample = 0.5f * MathF.Sin(2 * MathF.PI * 440 * n / Rate);
                for (int c = 0; c < 4; c++)
                {
                    input[(4 * i) + c] = sample * gains[c];
                }
            }

            bool turning = block >= 200;
            float yaw = turning ? 2 * MathF.PI * (block - 200) * frames / Rate : 0;
            _spatializer.Process(input, 4, output, OrientationMath.FromYawPitch(yaw, 0));
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
        Assert.True(maxStep < 2.5 * steadyMaxStep, $"turning {maxStep:0.0000} vs steady {steadyMaxStep:0.0000}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Process_DoesNotAllocateAndIsFast(int channels)
    {
        int frames = _spatializer.BlockFrames;
        float[] input = Field(Front, AmbisonicsLayout.GetDegree(channels - 1), frames, seed: 3);
        var output = new float[2 * frames];
        Quaternion head = OrientationMath.FromYawPitch(0.3f, 0.1f);
        _spatializer.Process(input, channels, output, head);

        const int runs = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < runs; i++)
        {
            _spatializer.Process(input, channels, output, head);
        }

        long ticks = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double blockMs = frames * 1000.0 / Rate;
        double averageMs = ticks * 1000.0 / Stopwatch.Frequency / runs;
        _output.WriteLine($"{channels} channels: {averageMs * 1000:0} µs per block of {blockMs:0.00} ms ({averageMs / blockMs:P1} of real time)");

        Assert.Equal(0, allocated);
        Assert.True(averageMs < blockMs * 0.2, $"{averageMs:0.000} ms per block");
    }

    [Fact]
    public void Reset_ClearsTheFilterTail()
    {
        int frames = _spatializer.BlockFrames;
        float[] loud = Field(Front, 1, frames, seed: 4);
        var silence = new float[4 * frames];
        var output = new float[2 * frames];
        _spatializer.Process(loud, 4, output, Quaternion.Identity);

        _spatializer.Reset();
        _spatializer.Process(silence, 4, output, Quaternion.Identity);

        Assert.All(output, sample => Assert.Equal(0f, sample, 1e-6f));
    }

    [Fact]
    public void Process_RejectsAChannelCountThatIsNoOrder()
    {
        int frames = _spatializer.BlockFrames;
        Assert.Throws<ArgumentOutOfRangeException>(() => _spatializer.Process(new float[6 * frames], 6, new float[2 * frames], Quaternion.Identity));
    }

    public void Dispose()
    {
        _spatializer?.Dispose();
    }

    /// <summary>Left of the picture (OpenXR axes).</summary>
    private static Vector3 Left => -Vector3.UnitX;

    private static Vector3 Front => -Vector3.UnitZ;

    private (double Left, double Right) EarLevels(Vector3 source, int order, Quaternion head)
    {
        int frames = _spatializer.BlockFrames;
        int channels = AmbisonicsLayout.GetChannelCount(order);
        float[] input = Field(source, order, Blocks * frames, seed: 1);
        var output = new float[2 * frames];
        double left = 0;
        double right = 0;
        int counted = 0;
        _spatializer.Reset();
        for (int block = 0; block < Blocks; block++)
        {
            _spatializer.Process(input.AsSpan(channels * block * frames, channels * frames), channels, output, head);
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

    /// <summary>White noise (±0.25) from <paramref name="source"/>, encoded as interleaved AmbiX.</summary>
    private static float[] Field(Vector3 source, int order, int frames, int seed)
    {
        float[] gains = AmbiXGains(source, order);
        var random = new Random(seed);
        var samples = new float[gains.Length * frames];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)((random.NextDouble() * 0.5) - 0.25);
            for (int c = 0; c < gains.Length; c++)
            {
                samples[(gains.Length * i) + c] = value * gains[c];
            }
        }

        return samples;
    }

    /// <summary>
    /// Independent noise from 64 random directions; the directional channels are scaled by
    /// <paramref name="directionalShare"/> (0 = W only, 1 = an ideal diffuse field, where the first order carries as much
    /// energy as W in SN3D).
    /// </summary>
    private static float[] DiffuseField(int order, double directionalShare, int frames, int seed)
    {
        const int Sources = 64;
        var random = new Random(seed);
        int channels = AmbisonicsLayout.GetChannelCount(order);
        var gains = new float[Sources][];
        for (int s = 0; s < Sources; s++)
        {
            Vector3 direction;
            do
            {
                direction = new Vector3((float)((random.NextDouble() * 2) - 1), (float)((random.NextDouble() * 2) - 1), (float)((random.NextDouble() * 2) - 1));
            }
            while (direction.LengthSquared() is > 1 or < 0.01f);

            gains[s] = AmbiXGains(Vector3.Normalize(direction), order);
            for (int c = 1; c < channels; c++)
            {
                gains[s][c] *= (float)directionalShare;
            }
        }

        var samples = new float[channels * frames];
        float scale = 0.25f / MathF.Sqrt(Sources);
        for (int i = 0; i < frames; i++)
        {
            for (int s = 0; s < Sources; s++)
            {
                float value = (float)((random.NextDouble() * 2) - 1) * scale;
                for (int c = 0; c < channels; c++)
                {
                    samples[(channels * i) + c] += value * gains[s][c];
                }
            }
        }

        return samples;
    }

    /// <summary>AmbiX (SN3D) gains of a direction in OpenXR axes, from Steam Audio's encoder (pinned above).</summary>
    private static float[] AmbiXGains(Vector3 direction, int order)
    {
        float[] gains = SteamAmbisonicsEncoder.Gains(direction, order);
        for (int acn = 0; acn < gains.Length; acn++)
        {
            gains[acn] /= MathF.Sqrt(((2 * AmbisonicsLayout.GetDegree(acn)) + 1) / (4 * MathF.PI));
        }

        return gains;
    }

    /// <summary>Real spherical harmonics up to order 2 in ACN order with SN3D, for a direction in OpenXR axes.</summary>
    private static float[] AmbiXSecondOrder(Vector3 direction)
    {
        // AmbiX axes: X front (−Z), Y left (−X), Z up (+Y).
        float x = -direction.Z;
        float y = -direction.X;
        float z = direction.Y;
        float root3 = MathF.Sqrt(3);
        return
        [
            1,
            y,
            z,
            x,
            root3 * x * y,
            root3 * y * z,
            0.5f * ((3 * z * z) - 1),
            root3 * x * z,
            0.5f * root3 * ((x * x) - (y * y)),
        ];
    }

    private static double Db(double rms) => 20 * Math.Log10(Math.Max(rms, 1e-12));
}
