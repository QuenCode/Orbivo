using Orbivo.Core.Formats;
using Orbivo.Media;

namespace Orbivo.Xr.Rendering;

/// <summary>What a view shows: black, a still image or a decoded video frame. Built on the render thread without allocations.</summary>
public readonly struct PanoramaSource
{
    private PanoramaSource(StillImageTexture? image, VideoFormat? imageFormat, VideoFrame video, bool isVideo)
    {
        Image = image;
        _imageFormat = imageFormat;
        Video = video;
        IsVideo = isVideo;
    }

    private readonly VideoFormat? _imageFormat;

    public static PanoramaSource Black => default;

    public bool IsBlack => !IsVideo && Image is null;

    internal StillImageTexture? Image { get; }

    /// <summary>Format of the image; equirect 360 mono for <see cref="Black"/> (debug outputs).</summary>
    internal VideoFormat ImageFormat => _imageFormat ?? VideoFormat.Default;

    internal VideoFrame Video { get; }

    internal bool IsVideo { get; }

    /// <summary>The format the picture is shown with.</summary>
    internal VideoFormat Format => IsVideo ? Video.Format : ImageFormat;

    /// <param name="image">The image, or null for black.</param>
    /// <param name="format">Its format; keep one instance and pass it every frame (no allocation).</param>
    public static PanoramaSource FromImage(StillImageTexture? image, VideoFormat format)
    {
        return new PanoramaSource(image, format, default, isVideo: false);
    }

    public static PanoramaSource FromVideo(in VideoFrame frame)
    {
        return new PanoramaSource(null, null, frame, isVideo: true);
    }
}
