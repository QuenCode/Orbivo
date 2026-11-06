using Orbivo.Core.Formats;
using Orbivo.Core.Playback;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Orbivo.Media;

/// <summary>
/// The frames of one opened video on the GPU: a fixed ring of NV12 or P010 textures the decoder copies into
/// (docs/ARCHITECTURE.md, "Datenfluss Video", step 3), plus the <see cref="FramePacer"/> that hands them to the render thread.
/// </summary>
/// <remarks>
/// Textures are created by the decode thread when the first frame arrives (size and format are known only then),
/// before that frame is published. The render thread reads a slot only after taking it from the pacer.
/// </remarks>
internal sealed class VideoFrameQueue : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly Slot[] _slots;
    private VideoFormat _format;
    private bool _disposed;

    public VideoFrameQueue(ID3D11Device device, int slotCount, VideoFormat format)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _slots = new Slot[slotCount];
        _format = format ?? throw new ArgumentNullException(nameof(format));
        Pacer = new FramePacer(slotCount);
    }

    public FramePacer Pacer { get; }

    /// <summary>
    /// How the frames are shown. The control thread replaces it (operator override), the render thread reads it with
    /// every frame; an immutable reference, so no lock is needed.
    /// </summary>
    public VideoFormat PictureFormat
    {
        get => Volatile.Read(ref _format);
        set => Volatile.Write(ref _format, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool HasTextures { get; private set; }

    public VideoColorConstants Color { get; private set; }

    /// <summary>
    /// Decode thread: creates the slot textures for frames of this size and format. Called once, before the first
    /// frame is published.
    /// </summary>
    /// <param name="format"><see cref="Format.NV12"/> or <see cref="Format.P010"/>.</param>
    /// <param name="width">Even width in pixels.</param>
    /// <param name="height">Even height in pixels.</param>
    /// <param name="color">Color conversion constants for the shader.</param>
    public void CreateTextures(Format format, int width, int height, in VideoColorConstants color)
    {
        if (HasTextures)
        {
            throw new InvalidOperationException("The frame textures already exist.");
        }

        (Format lumaFormat, Format chromaFormat) = format switch
        {
            Format.NV12 => (Format.R8_UNorm, Format.R8G8_UNorm),
            Format.P010 => (Format.R16_UNorm, Format.R16G16_UNorm),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Only NV12 and P010 are supported."),
        };

        FormatSupport support = _device.CheckFormatSupport(format);
        if ((support & (FormatSupport.Texture2D | FormatSupport.ShaderSample)) != (FormatSupport.Texture2D | FormatSupport.ShaderSample))
        {
            throw new NotSupportedException($"The GPU cannot sample {format} textures (support: {support}).");
        }

        var description = new Texture2DDescription(format, (uint)width, (uint)height, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.ShaderResource);
        for (int i = 0; i < _slots.Length; i++)
        {
            ID3D11Texture2D texture = _device.CreateTexture2D(description);
            _slots[i] = new Slot(
                texture,
                _device.CreateShaderResourceView(texture, new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, lumaFormat)),
                _device.CreateShaderResourceView(texture, new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, chromaFormat)));
        }

        Width = width;
        Height = height;
        Color = color;
        HasTextures = true;
    }

    /// <summary>Decode thread: the texture of a slot taken with <see cref="FramePacer.TryAcquireFreeSlot"/>.</summary>
    public ID3D11Texture2D GetTexture(int slot) => _slots[slot].Texture;

    /// <summary>Render thread: the frame due at media time <paramref name="targetTicks"/>. Does not block or allocate.</summary>
    public bool TryGetFrame(long targetTicks, out VideoFrame frame)
    {
        if (!Pacer.TrySelect(targetTicks, out int slot, out long pts))
        {
            frame = default;
            return false;
        }

        Slot selected = _slots[slot];
        frame = new VideoFrame(selected.Luma, selected.Chroma, Width, Height, PictureFormat, Color, pts);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Slot slot in _slots)
        {
            slot.Chroma?.Dispose();
            slot.Luma?.Dispose();
            slot.Texture?.Dispose();
        }

        Pacer.Dispose();
    }

    /// <summary>Approximate GPU memory of all slots, for the log.</summary>
    public long TextureBytes(Format format)
    {
        long bytesPerPixel = format == Format.P010 ? 2 : 1;
        return _slots.Length * (long)Width * Height * 3 / 2 * bytesPerPixel;
    }

    private readonly record struct Slot(ID3D11Texture2D Texture, ID3D11ShaderResourceView Luma, ID3D11ShaderResourceView Chroma);
}
