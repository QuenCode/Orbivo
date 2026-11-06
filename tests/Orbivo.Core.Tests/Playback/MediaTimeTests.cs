using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Playback;

public sealed class MediaTimeTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("75", 75_000)]
    [InlineData("7.5", 7_500)]
    [InlineData("7,5", 7_500)]
    [InlineData(" 12.25 ", 12_250)]
    [InlineData("1:05", 65_000)]
    [InlineData("01:05.5", 65_500)]
    [InlineData("90:00", 5_400_000)]
    [InlineData("1:02:03", 3_723_000)]
    [InlineData("00:00:07.467", 7_467)]
    [InlineData("0:00:07,467", 7_467)]
    public void TryParse_Accepts(string text, long expectedMilliseconds)
    {
        Assert.True(MediaTime.TryParse(text, out TimeSpan time));
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:60")]
    [InlineData("1:60:00")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5:00")]
    [InlineData("1:")]
    [InlineData("1e3")]
    public void TryParse_Rejects(string? text)
    {
        Assert.False(MediaTime.TryParse(text, out _));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        var time = new TimeSpan(0, 1, 2, 3, 456);
        string text = MediaTime.Format(time);

        Assert.Equal("01:02:03.456", text);
        Assert.True(MediaTime.TryParse(text, out TimeSpan parsed));
        Assert.Equal(time, parsed);
    }

    [Fact]
    public void Format_RoundsToTheMillisecond()
    {
        // Three frames at 30 fps in whole ticks: 999 999 ticks.
        Assert.Equal("00:00:00.100", MediaTime.Format(TimeSpan.FromTicks(999_999)));
        Assert.Equal("00:00:00.033", MediaTime.Format(TimeSpan.FromTicks(333_333)));
    }

    [Fact]
    public void Format_ClampsNegativeTimes()
    {
        Assert.Equal("00:00:00.000", MediaTime.Format(TimeSpan.FromSeconds(-1)));
    }
}
