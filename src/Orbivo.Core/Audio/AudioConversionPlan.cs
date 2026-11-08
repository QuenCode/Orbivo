namespace Orbivo.Core.Audio;

/// <summary>How the channels of a sound track reach the stereo output.</summary>
public enum ChannelHandling
{
    /// <summary>Two channels, passed through.</summary>
    Stereo,

    /// <summary>One channel, played on both sides.</summary>
    MonoOnBoth,

    /// <summary>
    /// More than two channels, mixed down to stereo with the standard matrix of swresample. Binaural rendering of
    /// surround and Ambisonics is planned for later (docs/ROADMAP.md).
    /// </summary>
    Downmix,
}

/// <summary>
/// What the audio decoder does with a sound track before it reaches the ring buffer: always interleaved float32
/// stereo at the output sample rate (docs/ARCHITECTURE.md, "Audio").
/// </summary>
/// <param name="SourceChannels">Channels of the track.</param>
/// <param name="SourceSampleRate">Sample rate of the track.</param>
/// <param name="OutputSampleRate">Sample rate of the output device.</param>
/// <param name="Channels">Channel handling.</param>
public sealed record AudioConversionPlan(int SourceChannels, int SourceSampleRate, int OutputSampleRate, ChannelHandling Channels)
{
    /// <summary>The ring buffer and the output always carry stereo.</summary>
    public const int OutputChannels = 2;

    /// <summary>The sample rate changes (swresample resamples).</summary>
    public bool Resamples => SourceSampleRate != OutputSampleRate;

    /// <summary>The track is mixed down; the operator gets a hint, because its channel directions are lost.</summary>
    public bool IsDownmix => Channels == ChannelHandling.Downmix;

    /// <summary>Plans the conversion of a track with <paramref name="sourceChannels"/> channels.</summary>
    public static AudioConversionPlan Create(int sourceChannels, int sourceSampleRate, int outputSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputSampleRate, 1);
        ChannelHandling handling = sourceChannels switch
        {
            1 => ChannelHandling.MonoOnBoth,
            2 => ChannelHandling.Stereo,
            _ => ChannelHandling.Downmix,
        };
        return new AudioConversionPlan(sourceChannels, sourceSampleRate, outputSampleRate, handling);
    }

    /// <summary>Short German description for the operator, e.g. "Stereo, 48 kHz" or "6 Kanäle → Stereo, 44,1 → 48 kHz".</summary>
    public string Describe()
    {
        string channels = Channels switch
        {
            ChannelHandling.Stereo => "Stereo",
            ChannelHandling.MonoOnBoth => "Mono auf beiden Seiten",
            _ => $"{SourceChannels} Kanäle → Stereo heruntergemischt",
        };
        string rate = Resamples
            ? $"{FormatKilohertz(SourceSampleRate)} → {FormatKilohertz(OutputSampleRate)} kHz"
            : $"{FormatKilohertz(SourceSampleRate)} kHz";
        return $"{channels}, {rate}";
    }

    private static string FormatKilohertz(int rate) =>
        (rate / 1000.0).ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
}
