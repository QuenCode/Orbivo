using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbivo.Xr.Rendering;

namespace Orbivo.Xr.Tests.Support;

/// <summary>Access to the generated test media (tools/testmedia) and PNG reading/writing through WIC.</summary>
internal static class TestMedia
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    /// <summary>Equirect 360 mono test image from <c>python tools/testmedia/make_testmedia.py</c>.</summary>
    public static string MonoTestImagePath =>
        Path.Combine(RepositoryRoot.Value, "tools", "testmedia", "out", "equirect360_mono_4096x2048.png");

    /// <summary>Rendered views are written here so that a human (or Claude) can look at them.</summary>
    public static string RenderedViewsDirectory
    {
        get
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "rendered-views");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static Bgra32Image LoadBgra32(string path)
    {
        using FileStream stream = File.OpenRead(path);
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
        byte[] pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return new Bgra32Image(frame.PixelWidth, frame.PixelHeight, pixels);
    }

    public static void SavePng(string path, int width, int height, byte[] bgra)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orbivo.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Orbivo.sln not found above {AppContext.BaseDirectory}.");
    }
}
