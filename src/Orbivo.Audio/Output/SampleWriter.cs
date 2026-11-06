using System.Buffers.Binary;

namespace Orbivo.Audio.Output;

/// <summary>Sample format of a device stream (the shared-mode mix format of the endpoint).</summary>
public enum SampleEncoding
{
    /// <summary>32-bit IEEE float (the usual mix format of Windows).</summary>
    Float32,

    /// <summary>16-bit signed integer.</summary>
    Int16,

    /// <summary>24-bit signed integer in 3 bytes.</summary>
    Int24,

    /// <summary>32-bit signed integer container (24 or 32 valid bits).</summary>
    Int32,
}

/// <summary>
/// Writes the renderer's interleaved float stereo into the device buffer: channel mapping and sample format.
/// Audio thread; no allocations (CLAUDE.md rule 2).
/// </summary>
/// <remarks>
/// Stereo goes to the first two channels (front left and right in every Windows channel mask); further channels get
/// silence. A mono device gets the mean of both sides. Integer formats are clipped.
/// </remarks>
public static class SampleWriter
{
    public static int BytesPerSample(SampleEncoding encoding) => encoding switch
    {
        SampleEncoding.Float32 => 4,
        SampleEncoding.Int16 => 2,
        SampleEncoding.Int24 => 3,
        SampleEncoding.Int32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    /// <summary>
    /// Writes <paramref name="frames"/> frames of <paramref name="stereo"/> (interleaved L/R) into
    /// <paramref name="destination"/> with <paramref name="channels"/> channels in <paramref name="encoding"/>.
    /// </summary>
    public static void Write(ReadOnlySpan<float> stereo, int frames, Span<byte> destination, int channels, SampleEncoding encoding)
    {
        int bytesPerSample = BytesPerSample(encoding);
        int bytesPerFrame = bytesPerSample * channels;
        if (stereo.Length < frames * 2 || destination.Length < frames * bytesPerFrame)
        {
            throw new ArgumentException("Buffer too small.");
        }

        if (encoding == SampleEncoding.Float32 && channels == 2)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(stereo[..(frames * 2)]).CopyTo(destination);
            return;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            float left = stereo[2 * frame];
            float right = stereo[(2 * frame) + 1];
            Span<byte> target = destination.Slice(frame * bytesPerFrame, bytesPerFrame);
            if (channels == 1)
            {
                WriteSample(target, 0.5f * (left + right), encoding);
                continue;
            }

            WriteSample(target, left, encoding);
            WriteSample(target[bytesPerSample..], right, encoding);
            target[(2 * bytesPerSample)..].Clear();
        }
    }

    private static void WriteSample(Span<byte> target, float value, SampleEncoding encoding)
    {
        switch (encoding)
        {
            case SampleEncoding.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(target, value);
                break;
            case SampleEncoding.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(target, (short)Math.Clamp(MathF.Round(value * 32767f), -32768f, 32767f));
                break;
            case SampleEncoding.Int24:
                int sample24 = (int)Math.Clamp(MathF.Round(value * 8388607f), -8388608f, 8388607f);
                target[0] = (byte)sample24;
                target[1] = (byte)(sample24 >> 8);
                target[2] = (byte)(sample24 >> 16);
                break;
            case SampleEncoding.Int32:
                double scaled = Math.Round(value * 2147483647.0);
                BinaryPrimitives.WriteInt32LittleEndian(target, (int)Math.Clamp(scaled, int.MinValue, int.MaxValue));
                break;
        }
    }
}
