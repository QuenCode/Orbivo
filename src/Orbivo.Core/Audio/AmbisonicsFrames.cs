namespace Orbivo.Core.Audio;

/// <summary>
/// Brings decoded Ambisonics frames into the form of the ring buffer (M10): AmbiX, exactly (order + 1)² channels per
/// frame. FuMa first order (W X Y Z, W −3 dB) becomes W Y Z X with W raised by 3 dB; channels beyond the sound field
/// (e.g. a head-locked stereo pair) are dropped.
/// </summary>
public static class AmbisonicsFrames
{
    /// <summary>FuMa W is 1/√2 of SN3D W.</summary>
    public static readonly float FuMaWGain = MathF.Sqrt(2f);

    /// <summary>
    /// Copies <paramref name="frames"/> frames from <paramref name="source"/> (<paramref name="sourceChannels"/> samples
    /// each) to <paramref name="destination"/> (<paramref name="ambisonicsChannels"/> samples each, AmbiX).
    /// </summary>
    /// <param name="source">Interleaved decoded frames.</param>
    /// <param name="sourceChannels">Samples per source frame, at least <paramref name="ambisonicsChannels"/>.</param>
    /// <param name="destination">Interleaved AmbiX frames.</param>
    /// <param name="ambisonicsChannels">(order + 1)²; 4 if <paramref name="fuMa"/>.</param>
    /// <param name="frames">Frames to convert.</param>
    /// <param name="fuMa">The source is FuMa first order.</param>
    public static void ToAmbiX(
        ReadOnlySpan<float> source,
        int sourceChannels,
        Span<float> destination,
        int ambisonicsChannels,
        int frames,
        bool fuMa)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, ambisonicsChannels);
        if (fuMa && ambisonicsChannels != 4)
        {
            throw new ArgumentException("FuMa is supported for first order (4 channels) only.", nameof(ambisonicsChannels));
        }

        if (source.Length < frames * sourceChannels || destination.Length < frames * ambisonicsChannels)
        {
            throw new ArgumentException("The buffers are too small for the frames.");
        }

        if (fuMa)
        {
            for (int i = 0; i < frames; i++)
            {
                ReadOnlySpan<float> fuMaFrame = source.Slice(i * sourceChannels, 4);
                Span<float> ambiX = destination.Slice(i * 4, 4);
                ambiX[0] = fuMaFrame[0] * FuMaWGain; // W
                ambiX[1] = fuMaFrame[2]; // Y
                ambiX[2] = fuMaFrame[3]; // Z
                ambiX[3] = fuMaFrame[1]; // X
            }

            return;
        }

        if (sourceChannels == ambisonicsChannels)
        {
            source[..(frames * sourceChannels)].CopyTo(destination);
            return;
        }

        for (int i = 0; i < frames; i++)
        {
            source.Slice(i * sourceChannels, ambisonicsChannels).CopyTo(destination.Slice(i * ambisonicsChannels, ambisonicsChannels));
        }
    }
}
