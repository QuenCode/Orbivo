using Orbivo.Core.Logging;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Logging;

/// <summary>German texts of the event log that the player and the app build.</summary>
public sealed class SessionEventTextTests
{
    [Theory]
    [InlineData(0, "0:00:00,000")]
    [InlineData(83.5, "0:01:23,500")]
    [InlineData(3725.0004, "1:02:05,000")]
    [InlineData(-1, "0:00:00,000")]
    public void Time_IsHoursMinutesSecondsWithComma(double seconds, string expected)
    {
        Assert.Equal(expected, SessionEventText.Time(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Seconds_UseADecimalComma()
    {
        Assert.Equal("1,5 s", SessionEventText.Seconds(TimeSpan.FromSeconds(1.5)));
        Assert.Equal("0,25 s", SessionEventText.Seconds(TimeSpan.FromSeconds(0.25)));
    }

    [Theory]
    [InlineData(PlaybackState.NoMedia, "Kein Video")]
    [InlineData(PlaybackState.Ready, "Bereit")]
    [InlineData(PlaybackState.Playing, "Läuft")]
    [InlineData(PlaybackState.Paused, "Pause")]
    public void States_AreGerman(PlaybackState state, string expected)
    {
        Assert.Equal(expected, SessionRecorder.DescribeState(state));
    }
}
