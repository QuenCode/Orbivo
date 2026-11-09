using System.Globalization;

namespace Orbivo.Core.Audio;

/// <summary>
/// How the channels of a sound track are meant (F21, F23): ordinary channels (mono, stereo, surround) or an Ambisonics
/// sound field. The operator can choose it per video (sidecar <c>audioMode</c>); null there means automatic.
/// </summary>
public enum AudioModeChoice
{
    /// <summary>Ordinary channels: mono and stereo as they are, more channels mixed down to stereo.</summary>
    Channels,

    /// <summary>Ambisonics in ACN channel order with SN3D normalization (AmbiX), order 1 to 3.</summary>
    AmbiX,

    /// <summary>Ambisonics in Furse-Malham order and normalization (W X Y Z, W −3 dB), first order only.</summary>
    FuMa,
}

/// <summary>Where the audio mode of a track comes from.</summary>
public enum AudioModeSource
{
    /// <summary>The operator's choice in the sidecar.</summary>
    Sidecar,

    /// <summary>The container says so (MP4 <c>SA3D</c>, Opus channel mapping family 2, …).</summary>
    Metadata,

    /// <summary>Guessed from the number of channels.</summary>
    ChannelCount,
}

/// <summary>The audio mode of a track: ordinary channels, or Ambisonics of an order in a convention.</summary>
/// <param name="Kind">Channels, AmbiX or FuMa.</param>
/// <param name="Order">Ambisonics order 1–3; 0 for ordinary channels.</param>
public readonly record struct AudioMode(AudioModeChoice Kind, int Order)
{
    public static AudioMode Channels => new(AudioModeChoice.Channels, 0);

    public bool IsAmbisonics => Kind != AudioModeChoice.Channels;

    /// <summary>Channels an Ambisonics track of this mode has: (order + 1)²; 0 for ordinary channels.</summary>
    public int AmbisonicsChannels => IsAmbisonics ? AmbisonicsLayout.GetChannelCount(Order) : 0;

    public static AudioMode Ambisonics(AudioModeChoice convention, int order) =>
        convention == AudioModeChoice.Channels
            ? throw new ArgumentException("Ambisonics needs AmbiX or FuMa.", nameof(convention))
            : new AudioMode(convention, order);

    /// <summary>German text, e.g. "Ambisonics 1. Ordnung (AmbiX)" or "Kanäle".</summary>
    public string Describe() => IsAmbisonics
        ? string.Create(CultureInfo.InvariantCulture, $"Ambisonics {Order}. Ordnung ({Kind})")
        : "Kanäle";
}

/// <summary>
/// The channel layout of a sound track as the container states it, before FFmpeg probes the decoder (which replaces
/// e.g. "ambisonic 1" from an MP4 <c>SA3D</c> box by the AAC layout "4.0").
/// </summary>
/// <param name="Channels">Channels of the track.</param>
/// <param name="AmbisonicsOrder">Ambisonics order if the container marks the track as Ambisonics (ACN/SN3D), else null.</param>
/// <param name="LayoutKnown">The container names the speakers (e.g. a WAV channel mask), so it is not Ambisonics.</param>
public sealed record EmbeddedAudioLayout(int Channels, int? AmbisonicsOrder, bool LayoutKnown)
{
    /// <summary>Short English text for the log.</summary>
    public string Describe() => AmbisonicsOrder is { } order
        ? string.Create(CultureInfo.InvariantCulture, $"{Channels} channels, ambisonic order {order}")
        : string.Create(CultureInfo.InvariantCulture, $"{Channels} channels, layout {(LayoutKnown ? "named" : "unspecified")}");
}

/// <summary>The audio mode in use and the one Orbivo would choose by itself, each with its source.</summary>
/// <param name="Mode">Mode in use.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="Automatic">Mode without the operator's choice.</param>
/// <param name="AutomaticSource">Where the automatic mode comes from.</param>
/// <param name="Warning">German hint for the operator (a choice that does not fit the track), or null.</param>
public sealed record ResolvedAudioMode(
    AudioMode Mode,
    AudioModeSource Source,
    AudioMode Automatic,
    AudioModeSource AutomaticSource,
    string? Warning)
{
    /// <summary>German text with the source, e.g. "Ambisonics 1. Ordnung (AmbiX, Metadaten)".</summary>
    public string Describe() => DescribeMode(Mode, Source);

    /// <summary>German text of the automatic mode, e.g. "Ambisonics 1. Ordnung (AmbiX, aus Kanalzahl geschätzt)".</summary>
    public string DescribeAutomatic() => DescribeMode(Automatic, AutomaticSource);

    public static string DescribeSource(AudioModeSource source) => source switch
    {
        AudioModeSource.Sidecar => "Sidecar",
        AudioModeSource.Metadata => "Metadaten",
        _ => "aus Kanalzahl geschätzt",
    };

    private static string DescribeMode(AudioMode mode, AudioModeSource source) => mode.IsAmbisonics
        ? string.Create(CultureInfo.InvariantCulture, $"Ambisonics {mode.Order}. Ordnung ({mode.Kind}, {DescribeSource(source)})")
        : source == AudioModeSource.Sidecar ? "Kanäle (Sidecar)" : "Kanäle";
}

/// <summary>
/// Decides how a sound track is meant (F23): the operator's choice (sidecar) before the container's metadata before a
/// guess from the channel count. 4, 9 or 16 channels without named speakers are taken as AmbiX of order 1, 2 or 3
/// (docs/DECISIONS.md, M10).
/// </summary>
public static class AudioModeResolver
{
    public static ResolvedAudioMode Resolve(EmbeddedAudioLayout layout, AudioModeChoice? choice)
    {
        ArgumentNullException.ThrowIfNull(layout);
        (AudioMode automatic, AudioModeSource automaticSource) = ResolveAutomatic(layout);
        if (choice is not { } chosen)
        {
            return new ResolvedAudioMode(automatic, automaticSource, automatic, automaticSource, ExtraChannelsWarning(layout, automatic));
        }

        if (chosen == AudioModeChoice.Channels)
        {
            return new ResolvedAudioMode(AudioMode.Channels, AudioModeSource.Sidecar, automatic, automaticSource, null);
        }

        if (AmbisonicsLayout.TryGetOrder(layout.Channels, out int order) && (chosen == AudioModeChoice.AmbiX || order == 1))
        {
            return new ResolvedAudioMode(AudioMode.Ambisonics(chosen, order), AudioModeSource.Sidecar, automatic, automaticSource, null);
        }

        string allowed = chosen == AudioModeChoice.FuMa ? "4 Kanäle (1. Ordnung)" : "4, 9 oder 16 Kanäle";
        string warning = string.Create(
            CultureInfo.InvariantCulture,
            $"Tonformat {chosen} aus dem Sidecar passt nicht zur Tonspur ({layout.Channels} Kanäle, erwartet {allowed}); automatisch.");
        return new ResolvedAudioMode(automatic, automaticSource, automatic, automaticSource, warning);
    }

    /// <summary>
    /// Ambisonics with more channels than its order needs (YouTube: first order plus a head-locked stereo pair). Only
    /// the sound field plays; the extra channels are dropped.
    /// </summary>
    private static string? ExtraChannelsWarning(EmbeddedAudioLayout layout, AudioMode mode)
    {
        int extra = mode.IsAmbisonics ? layout.Channels - mode.AmbisonicsChannels : 0;
        return extra > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Die Tonspur hat {extra} zusätzliche Kanäle neben dem Ambisonics-Klangfeld (z. B. kopffestes Stereo); sie werden nicht wiedergegeben.")
            : null;
    }

    private static (AudioMode Mode, AudioModeSource Source) ResolveAutomatic(EmbeddedAudioLayout layout)
    {
        if (layout.AmbisonicsOrder is { } order
            && order >= AmbisonicsLayout.MinSupportedOrder
            && order <= AmbisonicsLayout.MaxSupportedOrder
            && layout.Channels >= AmbisonicsLayout.GetChannelCount(order))
        {
            return (AudioMode.Ambisonics(AudioModeChoice.AmbiX, order), AudioModeSource.Metadata);
        }

        if (!layout.LayoutKnown && layout.AmbisonicsOrder is null && AmbisonicsLayout.TryGetOrder(layout.Channels, out int guessed))
        {
            return (AudioMode.Ambisonics(AudioModeChoice.AmbiX, guessed), AudioModeSource.ChannelCount);
        }

        return (AudioMode.Channels, layout.AmbisonicsOrder is null ? AudioModeSource.ChannelCount : AudioModeSource.Metadata);
    }
}
