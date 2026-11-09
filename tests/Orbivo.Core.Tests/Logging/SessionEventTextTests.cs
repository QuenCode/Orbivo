using Orbivo.Core.Audio;
using Orbivo.Core.Formats;
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

    [Fact]
    public void AudioModeChange_IsNamedAndCountsAsSound()
    {
        FormatOverrides before = FormatOverrides.None with { AudioFixedToPicture = true };
        FormatOverrides after = before with { AudioMode = AudioModeChoice.FuMa };

        Assert.Equal("Tonformat: Ambisonics FuMa", SessionEventText.FormatChanges(before, after));
        Assert.Equal("Tonformat: automatisch", SessionEventText.FormatChanges(after, before));
        Assert.True(SessionEventText.OnlySoundChanged(before, after));
    }

    [Fact]
    public void PictureChange_WithAudioMode_IsNotOnlySound()
    {
        FormatOverrides before = FormatOverrides.None;
        FormatOverrides after = before with { AudioMode = AudioModeChoice.AmbiX, SwapEyes = true };

        Assert.Equal("Augen tauschen: an; Tonformat: Ambisonics AmbiX", SessionEventText.FormatChanges(before, after));
        Assert.False(SessionEventText.OnlySoundChanged(before, after));
        Assert.False(SessionEventText.OnlySoundChanged(before, before));
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
