using Orbivo.Xr.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Orbivo.Xr.Rendering;

/// <summary>
/// Render target in GPU memory that can be read back to the CPU. For tests and diagnostics only,
/// never on the frame path (reading back stalls the GPU).
/// </summary>
internal sealed class OffscreenTarget : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11Texture2D _staging;

    public OffscreenTarget(GraphicsDevice device, int width, int height, Format format)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        Width = width;
        Height = height;
        BytesPerPixel = format switch
        {
            Format.R32G32B32A32_Float => 16,
            Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported read-back format."),
        };

        _texture = device.Device.CreateTexture2D(new Texture2DDescription(
            format, (uint)width, (uint)height, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.RenderTarget));
        RenderTargetView = device.Device.CreateRenderTargetView(_texture, null);
        _staging = device.Device.CreateTexture2D(new Texture2DDescription(
            format,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging,
            cpuAccessFlags: CpuAccessFlags.Read));
    }

    public int Width { get; }

    public int Height { get; }

    public int BytesPerPixel { get; }

    public ID3D11RenderTargetView RenderTargetView { get; }

    /// <summary>Copies the target to the CPU, rows top to bottom without padding.</summary>
    public unsafe T[] Read<T>()
        where T : unmanaged
    {
        int rowBytes = Width * BytesPerPixel;
        if (rowBytes % sizeof(T) != 0)
        {
            throw new InvalidOperationException($"Row size {rowBytes} is not a multiple of {typeof(T).Name}.");
        }

        var result = new T[rowBytes / sizeof(T) * Height];
        ID3D11DeviceContext context = _device.ImmediateContext;
        context.CopyResource(_staging, _texture);
        MappedSubresource mapped = context.Map(_staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            fixed (T* destination = result)
            {
                for (int y = 0; y < Height; y++)
                {
                    Buffer.MemoryCopy(
                        (byte*)mapped.DataPointer + ((long)y * mapped.RowPitch),
                        (byte*)destination + ((long)y * rowBytes),
                        rowBytes,
                        rowBytes);
                }
            }
        }
        finally
        {
            context.Unmap(_staging, 0);
        }

        return result;
    }

    public void Dispose()
    {
        _staging.Dispose();
        RenderTargetView.Dispose();
        _texture.Dispose();
    }
}
