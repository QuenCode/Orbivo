using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbivo.Xr.Rendering;

namespace Orbivo.App.Imaging;

/// <summary>
/// Decodes still images (PNG, JPEG …) with WIC through WPF, so no extra package is needed (docs/DECISIONS.md).
/// Runs on a background thread; the result is handed to the renderer as BGRA pixels.
/// </summary>
internal static class StillImageLoader
{
    public static Bgra32Image LoadBgra32(string path)
    {
        using FileStream stream = File.OpenRead(path);

        // Pixel values are used as they are (sRGB); embedded color profiles are ignored.
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
        {
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        }

        int stride = frame.PixelWidth * Bgra32Image.BytesPerPixel;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(stride * frame.PixelHeight);
        frame.CopyPixels(pixels, stride, 0);
        return new Bgra32Image(frame.PixelWidth, frame.PixelHeight, pixels);
    }
}
