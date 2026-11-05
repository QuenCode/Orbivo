namespace Orbivo.Xr.Rendering;

/// <summary>
/// Decoded still image in memory: 8 bit per channel, byte order B, G, R, A, sRGB encoded, rows top to bottom
/// without padding. Decoding happens outside this assembly (the app uses WIC through WPF).
/// </summary>
public sealed class Bgra32Image
{
    public Bgra32Image(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength != (long)width * height * BytesPerPixel)
        {
            throw new ArgumentException($"Expected {(long)width * height * BytesPerPixel} bytes for {width}x{height}, got {pixels.LongLength}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public const int BytesPerPixel = 4;

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * BytesPerPixel;

    public byte[] Pixels { get; }
}
