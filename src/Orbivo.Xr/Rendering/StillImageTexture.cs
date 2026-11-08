using Orbivo.Core.Formats;
using Orbivo.Xr.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Orbivo.Xr.Rendering;

/// <summary>
/// A still image on the GPU, read through an sRGB view so that the shader samples linear values.
/// Can be created on any thread; the render thread only binds it.
/// </summary>
public sealed class StillImageTexture : IDisposable
{
    /// <summary>Largest texture side for feature level 11 (D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION).</summary>
    public const int MaxDimension = 16384;

    private readonly ID3D11Texture2D _texture;

    private StillImageTexture(ID3D11Texture2D texture, ID3D11ShaderResourceView view, int width, int height, VideoFormat format)
    {
        _texture = texture;
        ShaderResourceView = view;
        Width = width;
        Height = height;
        PictureFormat = format;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The image is shown as equirect 360 in this stereo layout (one instance, so the render thread does not allocate).</summary>
    public VideoFormat PictureFormat { get; }

    internal ID3D11ShaderResourceView ShaderResourceView { get; }

    /// <param name="device">Device of the render thread.</param>
    /// <param name="image">Pixels.</param>
    /// <param name="layout">Stereo layout of the equirect 360 image.</param>
    public static unsafe StillImageTexture Create(GraphicsDevice device, Bgra32Image image, StereoLayout layout = StereoLayout.Mono)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width > MaxDimension || image.Height > MaxDimension)
        {
            throw new ArgumentException($"Image {image.Width}x{image.Height} exceeds the Direct3D 11 limit of {MaxDimension} pixels per side.", nameof(image));
        }

        var description = new Texture2DDescription(
            Format.B8G8R8A8_UNorm_SRgb,
            (uint)image.Width,
            (uint)image.Height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.ShaderResource,
            usage: ResourceUsage.Immutable);

        ID3D11Texture2D texture;
        fixed (byte* pixels = image.Pixels)
        {
            texture = device.Device.CreateTexture2D(description, new SubresourceData(pixels, (uint)image.Stride));
        }

        try
        {
            ID3D11ShaderResourceView view = device.Device.CreateShaderResourceView(texture, null);
            return new StillImageTexture(texture, view, image.Width, image.Height, VideoFormat.Equirect360(layout));
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        ShaderResourceView.Dispose();
        _texture.Dispose();
    }
}
