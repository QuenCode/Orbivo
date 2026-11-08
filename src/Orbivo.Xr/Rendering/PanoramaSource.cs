using Orbivo.Core.Formats;
using Orbivo.Core.Playback;
using Orbivo.Media;

namespace Orbivo.Xr.Rendering;

/// <summary>
/// What a view shows: black, a still image, a decoded video frame or the camera picture (see-through, F7).
/// Built on the render thread without allocations.
/// </summary>
public readonly struct PanoramaSource
{
    private PanoramaSource(StillImageTexture? image, VideoFormat? imageFormat, VideoFrame video, bool isVideo, bool isSeeThrough)
    {
        Image = image;
        _imageFormat = imageFormat;
        Video = video;
        IsVideo = isVideo;
        IsSeeThrough = isSeeThrough;
    }

    private readonly VideoFormat? _imageFormat;

    public static PanoramaSource Black => default;

    /// <summary>
    /// The camera picture of the headset. In the headset the compositor shows it where the layer is transparent; the
    /// preview and the live view draw a dark stand-in (<see cref="PanoramaRenderer.SeeThroughStandIn"/>).
    /// </summary>
    public static PanoramaSource SeeThrough => new(null, null, default, isVideo: false, isSeeThrough: true);

    public bool IsBlack => !IsVideo && !IsSeeThrough && Image is null;

    /// <summary>True for <see cref="SeeThrough"/>.</summary>
    public bool IsSeeThrough { get; }

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
        return new PanoramaSource(image, format, default, isVideo: false, isSeeThrough: false);
    }

    public static PanoramaSource FromVideo(in VideoFrame frame)
    {
        return new PanoramaSource(null, null, frame, isVideo: true, isSeeThrough: false);
    }

    /// <summary>What the headset shows while no video frame is due (F7).</summary>
    /// <param name="view">The operator's choice.</param>
    /// <param name="image">The idle image with its format, or null (then <see cref="IdleView.Image"/> shows black).</param>
    public static PanoramaSource ForIdle(IdleView view, StillImageTexture? image)
    {
        return view switch
        {
            IdleView.SeeThrough => SeeThrough,
            IdleView.Image when image is not null => FromImage(image, image.PictureFormat),
            _ => Black,
        };
    }
}
