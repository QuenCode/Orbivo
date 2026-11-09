using Orbivo.Core.Audio;

namespace Orbivo.Core.Tests.Audio;

public sealed class AudioGainTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, 0.125f)]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    public void FromLevel_IsTheCube(float level, float expected)
    {
        Assert.Equal(expected, AudioGain.FromLevel(level), 6);
    }

    [Fact]
    public void HalfLevel_IsAboutMinus18Decibels()
    {
        Assert.Equal(-18.06, AudioGain.ToDecibels(AudioGain.FromLevel(0.5f)), 1);
        Assert.Equal(double.NegativeInfinity, AudioGain.ToDecibels(0));
    }

    [Fact]
    public void GainSmoother_ReachesTheTargetWithinAFewTimeConstants_WithoutJumps()
    {
        var smoother = new GainSmoother(48_000, 1f);
        float previous = smoother.Value;
        float largestStep = 0;
        for (int i = 0; i < 48_000 / 20; i++)
        {
            float value = smoother.Next(0f);
            largestStep = Math.Max(largestStep, Math.Abs(value - previous));
            previous = value;
        }

        Assert.Equal(0f, smoother.Value);

        // A jump from full to silence is spread over the time constant: no sample-to-sample step above 0.5 %.
        Assert.True(largestStep < 0.005f, $"largest step {largestStep}");
    }

    [Fact]
    public void TransportRamp_TakesFiveMilliseconds_AndIsSmooth()
    {
        var ramp = new TransportRamp(48_000);
        Assert.Equal(240, ramp.Frames);
        Assert.True(ramp.IsSilent);

        ramp.FadeIn();
        Assert.Equal(240, ramp.RemainingFrames);
        float previous = 0;
        float largestStep = 0;
        for (int i = 0; i < 240; i++)
        {
            float gain = ramp.Next();
            Assert.True(gain >= previous);
            largestStep = Math.Max(largestStep, gain - previous);
            previous = gain;
        }

        Assert.True(ramp.IsOpen);
        Assert.Equal(1f, previous);
        Assert.Equal(0, ramp.RemainingFrames);

        // Smoothstep: the steepest step is 1.5 times the linear one.
        Assert.True(largestStep <= 1.5f / 240 + 1e-5f, $"largest step {largestStep}");

        ramp.FadeOut();
        Assert.Equal(240, ramp.RemainingFrames);
        for (int i = 0; i < 120; i++)
        {
            ramp.Next();
        }

        Assert.Equal(120, ramp.RemainingFrames);
        ramp.FadeIn();
        Assert.Equal(120, ramp.RemainingFrames);
    }

    [Theory]
    [InlineData(1, ChannelHandling.MonoOnBoth)]
    [InlineData(2, ChannelHandling.Stereo)]
    [InlineData(6, ChannelHandling.Downmix)]
    [InlineData(8, ChannelHandling.Downmix)]
    public void ConversionPlan_ChoosesTheChannelHandling(int channels, ChannelHandling expected)
    {
        AudioConversionPlan plan = AudioConversionPlan.Create(channels, 48_000, 48_000);
        Assert.Equal(expected, plan.Channels);
        Assert.Equal(expected == ChannelHandling.Downmix, plan.IsDownmix);
        Assert.Equal(2, AudioConversionPlan.StereoChannels);
    }

    [Fact]
    public void ConversionPlan_ResamplesOnlyWhenTheRatesDiffer()
    {
        Assert.False(AudioConversionPlan.Create(2, 48_000, 48_000).Resamples);
        AudioConversionPlan plan = AudioConversionPlan.Create(2, 44_100, 48_000);
        Assert.True(plan.Resamples);
        Assert.Equal("Stereo, 44,1 → 48 kHz", plan.Describe());
        Assert.Equal("6 Kanäle → Stereo heruntergemischt, 48 kHz", AudioConversionPlan.Create(6, 48_000, 48_000).Describe());
        Assert.Equal("Mono auf beiden Seiten, 48 kHz", AudioConversionPlan.Create(1, 48_000, 48_000).Describe());
    }
}
