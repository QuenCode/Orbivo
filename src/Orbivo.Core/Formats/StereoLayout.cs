namespace Orbivo.Core.Formats;

/// <summary>How the two eyes are packed into one image (F11). Left eye on top or on the left.</summary>
public enum StereoLayout
{
    Mono,
    TopBottom,
    SideBySide,
}

/// <summary>Eye index as OpenXR delivers the views: 0 = left, 1 = right.</summary>
public enum Eye
{
    Left = 0,
    Right = 1,
}

public static class StereoLayoutDetection
{
    /// <summary>
    /// Guess from the image size until metadata detection (M6): square images are top-bottom
    /// (two 2:1 equirect images on top of each other), everything else mono. Side-by-side cannot be told
    /// apart from mono by its size and must be chosen explicitly.
    /// </summary>
    public static StereoLayout FromSize(int width, int height)
    {
        return width > 0 && width == height ? StereoLayout.TopBottom : StereoLayout.Mono;
    }
}
