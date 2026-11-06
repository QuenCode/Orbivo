using Orbivo.Core.Formats;
using Orbivo.Media;

namespace Orbivo.Xr.Rendering;

/// <summary>What a view shows: black, a still image or a decoded video frame. Built on the render thread without allocations.</summary>
public readonly struct PanoramaSource
{
    private PanoramaSource(StillImageTexture? image, StereoLayout imageLayout, VideoFrame video, bool isVideo)
    {
        Image = image;
        ImageLayout = imageLayout;
        Video = video;
        IsVideo = isVideo;
    }

    public static PanoramaSource Black => default;

    public bool IsBlack => !IsVideo && Image is null;

    internal StillImageTexture? Image { get; }

    internal StereoLayout ImageLayout { get; }

    internal VideoFrame Video { get; }

    internal bool IsVideo { get; }

    public static PanoramaSource FromImage(StillImageTexture? image, StereoLayout layout)
    {
        return new PanoramaSource(image, layout, default, isVideo: false);
    }

    public static PanoramaSource FromVideo(in VideoFrame frame)
    {
        return new PanoramaSource(null, StereoLayout.Mono, frame, isVideo: true);
    }
}
