using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Playback;

public sealed class OutputFaderTests
{
    [Fact]
    public void StartsWithThePicture()
    {
        var fader = new OutputFader(new FakeTimeProvider());

        Assert.Equal(1f, fader.GetBrightness(0));
        Assert.Equal(OutputLevel.Picture, fader.Current);
    }

    [Fact]
    public void Blackout_IsImmediate_AndCanBeLifted()
    {
        var time = new FakeTimeProvider();
        var fader = new OutputFader(time);

        fader.SetBlackout(true);
        Assert.Equal(0f, fader.GetBrightness(time.Timestamp));
        Assert.True(fader.Current.TowardsBlack);
        Assert.False(fader.Current.Fading);

        fader.SetBlackout(false);
        Assert.Equal(1f, fader.GetBrightness(time.Timestamp));
    }

    [Fact]
    public void FadeOut_RunsLinearlyInPerceivedBrightness()
    {
        var time = new FakeTimeProvider();
        var fader = new OutputFader(time);
        long start = time.Timestamp;

        fader.FadeTo(black: true, TimeSpan.FromSeconds(2));

        Assert.Equal(1f, fader.GetBrightness(start));
        time.AdvanceMilliseconds(500);
        OutputLevel quarter = fader.Current;
        Assert.Equal(0.75f, quarter.Level, 1e-4f);
        Assert.True(quarter.Fading);
        Assert.True(quarter.TowardsBlack);

        // Halfway in perceived brightness is 0.5^2.2 ≈ 0.22 in linear light.
        Assert.Equal(MathF.Pow(0.5f, 2.2f), fader.GetBrightness(start + 1_000_000), 1e-4f);
        Assert.Equal(0f, fader.GetBrightness(start + 2_000_000));
        Assert.Equal(0f, fader.GetBrightness(start + 5_000_000));

        time.AdvanceMilliseconds(1500);
        Assert.False(fader.Current.Fading);
    }

    [Fact]
    public void FadeIn_FromHalfway_TakesHalfTheTime()
    {
        var time = new FakeTimeProvider();
        var fader = new OutputFader(time);
        fader.FadeTo(black: true, TimeSpan.FromSeconds(2));
        time.AdvanceMilliseconds(1000);

        long turn = time.Timestamp;
        fader.FadeTo(black: false, TimeSpan.FromSeconds(2));

        Assert.Equal(0.5f, fader.Current.Level, 1e-4f);
        Assert.Equal(1f, fader.GetBrightness(turn + 1_000_000));
        Assert.True(fader.GetBrightness(turn + 500_000) < 1f);
    }

    [Fact]
    public void BlackoutDuringAFade_WinsAtOnce()
    {
        var time = new FakeTimeProvider();
        var fader = new OutputFader(time);
        fader.FadeTo(black: false, TimeSpan.FromSeconds(1));
        fader.FadeTo(black: true, TimeSpan.FromSeconds(10));
        time.AdvanceMilliseconds(100);

        fader.SetBlackout(true);
        Assert.Equal(0f, fader.GetBrightness(time.Timestamp));
        Assert.False(fader.Current.Fading);
    }

    [Fact]
    public void ZeroDuration_IsACut()
    {
        var time = new FakeTimeProvider();
        var fader = new OutputFader(time);

        fader.FadeTo(black: true, TimeSpan.Zero);
        Assert.Equal(0f, fader.GetBrightness(time.Timestamp));
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(2f, 1f)]
    public void ToLinear_IsClamped(float level, float expected)
    {
        Assert.Equal(expected, OutputFader.ToLinear(level));
    }

    [Fact]
    public void GetLevel_IsThePerceivedLevelForTheSound()
    {
        var time = new FakeTimeProvider();
        var fader = new OutputFader(time);
        fader.FadeTo(black: true, TimeSpan.FromSeconds(1));
        long halfway = time.Timestamp + (time.TimestampFrequency / 2);
        Assert.Equal(0.5f, fader.GetLevel(halfway), 4);
        Assert.Equal(OutputFader.ToLinear(0.5f), fader.GetBrightness(halfway), 4);
        fader.SetBlackout(false);
        Assert.Equal(1f, fader.GetLevel(halfway));
    }
}
