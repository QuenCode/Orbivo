using System.Globalization;
using Orbivo.Core.Localization;

namespace Orbivo.Core.Audio;

/// <summary>How the channels of a sound track reach the ring buffer.</summary>
public enum ChannelHandling
{
    /// <summary>Two channels, passed through.</summary>
    Stereo,

    /// <summary>One channel, played on both sides.</summary>
    MonoOnBoth,

    /// <summary>
    /// More than two ordinary channels, mixed down to stereo with the standard matrix of swresample. Binaural rendering
    /// of surround is planned for later (docs/ROADMAP.md).
    /// </summary>
    Downmix,

    /// <summary>
    /// An Ambisonics sound field (M10): all (order + 1)² channels go into the ring buffer as AmbiX (ACN/SN3D); FuMa is
    /// converted on the way. The output renders it binaurally.
    /// </summary>
    Ambisonics,
}

/// <summary>
/// What the audio decoder does with a sound track before it reaches the ring buffer: interleaved float32 at the output
/// sample rate, either stereo or an AmbiX sound field (docs/ARCHITECTURE.md, "Audio").
/// </summary>
/// <param name="SourceChannels">Channels of the track.</param>
/// <param name="SourceSampleRate">Sample rate of the track.</param>
/// <param name="OutputSampleRate">Sample rate of the output device.</param>
/// <param name="Channels">Channel handling.</param>
public sealed record AudioConversionPlan(int SourceChannels, int SourceSampleRate, int OutputSampleRate, ChannelHandling Channels)
{
    /// <summary>Channels in the ring buffer for everything except Ambisonics.</summary>
    public const int StereoChannels = 2;

    /// <summary>How the track is meant (AmbiX, FuMa or channels) and why; set by <see cref="Create(int, int, int, ResolvedAudioMode)"/>.</summary>
    public ResolvedAudioMode? Mode { get; init; }

    /// <summary>Channels per frame in the ring buffer: 2, or (order + 1)² for Ambisonics.</summary>
    public int RingChannels => Channels == ChannelHandling.Ambisonics ? Mode!.Mode.AmbisonicsChannels : StereoChannels;

    /// <summary>The track is FuMa and is converted to AmbiX by the decoder.</summary>
    public bool ConvertsFuMa => Channels == ChannelHandling.Ambisonics && Mode!.Mode.Kind == AudioModeChoice.FuMa;

    /// <summary>The sample rate changes (swresample resamples).</summary>
    public bool Resamples => SourceSampleRate != OutputSampleRate;

    /// <summary>The track is mixed down; the operator gets a hint, because its channel directions are lost.</summary>
    public bool IsDownmix => Channels == ChannelHandling.Downmix;

    /// <summary>Plans the conversion of a track with <paramref name="sourceChannels"/> ordinary channels.</summary>
    public static AudioConversionPlan Create(int sourceChannels, int sourceSampleRate, int outputSampleRate) =>
        Create(sourceChannels, sourceSampleRate, outputSampleRate, ResolvedFromChannels(sourceChannels));

    /// <summary>Plans the conversion of a track whose audio mode is decided (<see cref="AudioModeResolver"/>).</summary>
    public static AudioConversionPlan Create(int sourceChannels, int sourceSampleRate, int outputSampleRate, ResolvedAudioMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputSampleRate, 1);
        ArgumentNullException.ThrowIfNull(mode);
        ChannelHandling handling;
        if (mode.Mode.IsAmbisonics)
        {
            if (sourceChannels < mode.Mode.AmbisonicsChannels)
            {
                throw new ArgumentException($"{mode.Mode} needs {mode.Mode.AmbisonicsChannels} channels, the track has {sourceChannels}.", nameof(mode));
            }

            handling = ChannelHandling.Ambisonics;
        }
        else
        {
            handling = sourceChannels switch
            {
                1 => ChannelHandling.MonoOnBoth,
                2 => ChannelHandling.Stereo,
                _ => ChannelHandling.Downmix,
            };
        }

        return new AudioConversionPlan(sourceChannels, sourceSampleRate, outputSampleRate, handling) { Mode = mode };
    }

    /// <summary>
    /// Short description for the operator in the UI language, e.g. "Stereo, 48 kHz", "6 channels → mixed down to stereo,
    /// 44.1 → 48 kHz" or "Ambisonics order 1 (AmbiX, metadata), 48 kHz".
    /// </summary>
    public string Describe()
    {
        string channels = Channels switch
        {
            ChannelHandling.Stereo => "Stereo",
            ChannelHandling.MonoOnBoth => CoreText.ConversionMonoOnBoth,
            ChannelHandling.Ambisonics => Mode!.Describe(),
            _ => Loc.Format(CoreText.ConversionDownmix, SourceChannels),
        };
        string rate = Resamples
            ? $"{FormatKilohertz(SourceSampleRate)} → {FormatKilohertz(OutputSampleRate)} kHz"
            : $"{FormatKilohertz(SourceSampleRate)} kHz";
        return $"{channels}, {rate}";
    }

    private static ResolvedAudioMode ResolvedFromChannels(int sourceChannels) =>
        AudioModeResolver.Resolve(new EmbeddedAudioLayout(sourceChannels, null, LayoutKnown: true), AudioModeChoice.Channels) with
        {
            Source = AudioModeSource.ChannelCount,
        };

    private static string FormatKilohertz(int rate) =>
        (rate / 1000.0).ToString("0.###", CultureInfo.CurrentCulture);
}
