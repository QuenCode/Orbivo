using System.Diagnostics;
using Orbivo.Core.Concurrency;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Orbivo.Xr.LiveView;

/// <summary>A finished live view image: the top left <see cref="Width"/> × <see cref="Height"/> pixels of a slot texture.</summary>
/// <param name="Texture">Slot texture (B8G8R8A8, sRGB-encoded bytes).</param>
/// <param name="Width">Width of the image in the texture.</param>
/// <param name="Height">Height of the image in the texture.</param>
/// <param name="Timestamp">Stopwatch timestamp of the frame it shows.</param>
internal readonly record struct LiveViewFrame(ID3D11Texture2D Texture, int Width, int Height, long Timestamp);

/// <summary>
/// Hands the live view (F42) from a render thread (headset or preview window) to the live view presenter: three slot
/// textures with <see cref="TripleBufferIndex"/>, newest wins. The render thread draws about 30 times per second what
/// the viewer sees into its slot and never waits (CLAUDE.md rule 1); the presenter copies the newest slot into the
/// operator window.
/// </summary>
/// <remarks>
/// Both threads put their commands into the device's one immediate context, so the GPU runs them in that order: the
/// presenter's copy of a slot always comes after the drawing into it. The presenter only copies and clears and never
/// changes pipeline state, which the render thread sets for each draw.
/// </remarks>
public sealed class LiveViewFrames : IDisposable
{
    /// <summary>Largest image; a bigger panel shows it with a black border.</summary>
    public const int MaxWidth = 1920;

    public const int MaxHeight = 1080;

    /// <summary>Minimum time between two live view images: every 3rd frame at 90 Hz, every 2nd at 60 Hz.</summary>
    public static readonly long MinFrameInterval = Stopwatch.Frequency * 30 / 1000;

    private const Format TextureFormat = Format.B8G8R8A8_Typeless;
    private const Format ViewFormat = Format.B8G8R8A8_UNorm_SRgb;

    private readonly ID3D11Texture2D[] _textures = new ID3D11Texture2D[TripleBufferIndex.SlotCount];
    private readonly ID3D11RenderTargetView[] _targets = new ID3D11RenderTargetView[TripleBufferIndex.SlotCount];
    private readonly int[] _widths = new int[TripleBufferIndex.SlotCount];
    private readonly int[] _heights = new int[TripleBufferIndex.SlotCount];
    private readonly long[] _timestamps = new long[TripleBufferIndex.SlotCount];
    private readonly TripleBufferIndex _slots = new();

    private volatile bool _enabled;
    private int _targetWidth;
    private int _targetHeight;
    private long _lastFrameTimestamp;

    // Render thread only.
    private long _lastRenderTimestamp;

    // Presenter only.
    private bool _hasFrame;

    public LiveViewFrames(GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        try
        {
            for (int slot = 0; slot < TripleBufferIndex.SlotCount; slot++)
            {
                _textures[slot] = device.Device.CreateTexture2D(new Texture2DDescription(
                    TextureFormat, MaxWidth, MaxHeight, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.RenderTarget));
                _targets[slot] = device.Device.CreateRenderTargetView(
                    _textures[slot],
                    new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, ViewFormat));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>True while a presenter shows the live view; the render threads draw nothing for it otherwise.</summary>
    public bool Enabled
    {
        get => _enabled;
        internal set => _enabled = value;
    }

    /// <summary>Stopwatch timestamp of the newest image, 0 if none since the start. Any thread.</summary>
    public long LastFrameTimestamp => Volatile.Read(ref _lastFrameTimestamp);

    /// <summary>
    /// Render thread: draws what the viewer sees into the next slot if the live view is on and the last image is
    /// at least <see cref="MinFrameInterval"/> old. Does not block and does not allocate.
    /// </summary>
    /// <param name="renderer">The render thread's panorama renderer.</param>
    /// <param name="context">Immediate context of the device.</param>
    /// <param name="view">The view: for the headset the <see cref="CombinedView"/> of both eyes.</param>
    /// <param name="source">What the headset shows.</param>
    /// <param name="brightness">Blackout and fade, as in the headset.</param>
    /// <param name="timestamp">Stopwatch timestamp of the frame.</param>
    public void RenderIfDue(
        PanoramaRenderer renderer,
        ID3D11DeviceContext context,
        in ViewParameters view,
        in PanoramaSource source,
        float brightness,
        long timestamp)
    {
        if (!_enabled || timestamp - _lastRenderTimestamp < MinFrameInterval)
        {
            return;
        }

        int maxWidth = Math.Min(Volatile.Read(ref _targetWidth), MaxWidth);
        int maxHeight = Math.Min(Volatile.Read(ref _targetHeight), MaxHeight);
        if (maxWidth < 2 || maxHeight < 2)
        {
            return;
        }

        (int width, int height) = CombinedView.FitSize(CombinedView.AspectRatio(view.Fov), maxWidth, maxHeight);
        _lastRenderTimestamp = timestamp;
        int slot = _slots.WriteIndex;

        // The left eye's picture: for stereo video the left half or the top (F11).
        renderer.Render(context, _targets[slot], width, height, view, Eye.Left, source, brightness);
        _widths[slot] = width;
        _heights[slot] = height;
        _timestamps[slot] = timestamp;
        _slots.Publish();
        Volatile.Write(ref _lastFrameTimestamp, timestamp);
    }

    /// <summary>Presenter: the size of the area the image is shown in. Any thread.</summary>
    internal void SetTargetSize(int width, int height)
    {
        Volatile.Write(ref _targetWidth, width);
        Volatile.Write(ref _targetHeight, height);
    }

    /// <summary>
    /// Presenter: the newest image. Returns true with <paramref name="isNew"/> = false for the image it already had,
    /// false if no image was rendered since the start.
    /// </summary>
    internal bool TryGetNewest(out LiveViewFrame frame, out bool isNew)
    {
        isNew = _slots.TryAcquireNewest();
        _hasFrame |= isNew;
        if (!_hasFrame)
        {
            frame = default;
            return false;
        }

        int slot = _slots.ReadIndex;
        frame = new LiveViewFrame(_textures[slot], _widths[slot], _heights[slot], _timestamps[slot]);
        return true;
    }

    /// <summary>Frees the textures. The render threads and the presenter must be stopped before.</summary>
    public void Dispose()
    {
        _enabled = false;
        for (int slot = 0; slot < TripleBufferIndex.SlotCount; slot++)
        {
            _targets[slot]?.Dispose();
            _textures[slot]?.Dispose();
        }
    }
}
