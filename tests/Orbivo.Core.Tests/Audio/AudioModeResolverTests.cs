using Orbivo.Core.Audio;

namespace Orbivo.Core.Tests.Audio;

/// <summary>Audio mode detection (F23, M10) and the conversion plan that follows from it.</summary>
public sealed class AudioModeResolverTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 9)]
    [InlineData(3, 16)]
    public void Metadata_AmbisonicOrder_IsAmbiX(int order, int channels)
    {
        ResolvedAudioMode resolved = AudioModeResolver.Resolve(new EmbeddedAudioLayout(channels, order, LayoutKnown: true), null);

        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.AmbiX, order), resolved.Mode);
        Assert.Equal(AudioModeSource.Metadata, resolved.Source);
        Assert.Null(resolved.Warning);
        Assert.Equal($"Ambisonics {order}. Ordnung (AmbiX, Metadaten)", resolved.Describe());
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(9, 2)]
    [InlineData(16, 3)]
    public void UnspecifiedLayout_WithAmbisonicsChannelCount_IsGuessedAsAmbiX(int channels, int order)
    {
        ResolvedAudioMode resolved = AudioModeResolver.Resolve(new EmbeddedAudioLayout(channels, null, LayoutKnown: false), null);

        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.AmbiX, order), resolved.Mode);
        Assert.Equal(AudioModeSource.ChannelCount, resolved.Source);
        Assert.Equal($"Ambisonics {order}. Ordnung (AmbiX, aus Kanalzahl geschätzt)", resolved.Describe());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    [InlineData(4, true)]
    [InlineData(8, false)]
    public void OtherTracks_AreChannels(int channels, bool layoutKnown)
    {
        ResolvedAudioMode resolved = AudioModeResolver.Resolve(new EmbeddedAudioLayout(channels, null, layoutKnown), null);

        Assert.Equal(AudioMode.Channels, resolved.Mode);
        Assert.Equal("Kanäle", resolved.Describe());
    }

    [Fact]
    public void Sidecar_FuMa_OverridesTheGuess()
    {
        ResolvedAudioMode resolved = AudioModeResolver.Resolve(new EmbeddedAudioLayout(4, null, false), AudioModeChoice.FuMa);

        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.FuMa, 1), resolved.Mode);
        Assert.Equal(AudioModeSource.Sidecar, resolved.Source);
        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.AmbiX, 1), resolved.Automatic);
        Assert.Equal("Ambisonics 1. Ordnung (AmbiX, aus Kanalzahl geschätzt)", resolved.DescribeAutomatic());
    }

    [Fact]
    public void Sidecar_Channels_TurnsAmbisonicsOff()
    {
        ResolvedAudioMode resolved = AudioModeResolver.Resolve(new EmbeddedAudioLayout(4, 1, true), AudioModeChoice.Channels);

        Assert.Equal(AudioMode.Channels, resolved.Mode);
        Assert.Equal("Kanäle (Sidecar)", resolved.Describe());
        Assert.Equal(AudioModeSource.Metadata, resolved.AutomaticSource);
    }

    [Theory]
    [InlineData(2, AudioModeChoice.AmbiX)]
    [InlineData(6, AudioModeChoice.AmbiX)]
    [InlineData(9, AudioModeChoice.FuMa)]
    public void Sidecar_ChoiceThatDoesNotFit_IsIgnoredWithAWarning(int channels, AudioModeChoice choice)
    {
        ResolvedAudioMode resolved = AudioModeResolver.Resolve(new EmbeddedAudioLayout(channels, null, false), choice);

        Assert.NotEqual(AudioModeSource.Sidecar, resolved.Source);
        Assert.Equal(resolved.Automatic, resolved.Mode);
        Assert.NotNull(resolved.Warning);
    }

    [Fact]
    public void FirstOrderWithHeadLockedStereo_PlaysTheSoundFieldAndWarns()
    {
        ResolvedAudioMode resolved = AudioModeResolver.Resolve(new EmbeddedAudioLayout(6, 1, true), null);

        Assert.Equal(AudioMode.Ambisonics(AudioModeChoice.AmbiX, 1), resolved.Mode);
        Assert.Contains("2 zusätzliche Kanäle", resolved.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_Ambisonics_CarriesAllChannelsIntoTheRing()
    {
        ResolvedAudioMode mode = AudioModeResolver.Resolve(new EmbeddedAudioLayout(4, 1, true), null);

        AudioConversionPlan plan = AudioConversionPlan.Create(4, 48_000, 48_000, mode);

        Assert.Equal(ChannelHandling.Ambisonics, plan.Channels);
        Assert.Equal(4, plan.RingChannels);
        Assert.False(plan.ConvertsFuMa);
        Assert.False(plan.IsDownmix);
        Assert.Equal("Ambisonics 1. Ordnung (AmbiX, Metadaten), 48 kHz", plan.Describe());
    }

    [Fact]
    public void Plan_FuMa_IsConverted()
    {
        ResolvedAudioMode mode = AudioModeResolver.Resolve(new EmbeddedAudioLayout(4, null, false), AudioModeChoice.FuMa);

        AudioConversionPlan plan = AudioConversionPlan.Create(4, 44_100, 48_000, mode);

        Assert.True(plan.ConvertsFuMa);
        Assert.Equal("Ambisonics 1. Ordnung (FuMa, Sidecar), 44,1 → 48 kHz", plan.Describe());
    }

    [Fact]
    public void Plan_QuadAsChannels_IsMixedDown()
    {
        ResolvedAudioMode mode = AudioModeResolver.Resolve(new EmbeddedAudioLayout(4, null, false), AudioModeChoice.Channels);

        AudioConversionPlan plan = AudioConversionPlan.Create(4, 48_000, 48_000, mode);

        Assert.Equal(ChannelHandling.Downmix, plan.Channels);
        Assert.Equal(2, plan.RingChannels);
    }
}
