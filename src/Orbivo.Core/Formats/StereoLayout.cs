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
    /// Guess for an equirect 360 still image from its size: square images are top-bottom (two 2:1 images on top of each
    /// other), 4:1 images side-by-side (two 2:1 images next to each other), everything else mono. A squeezed side-by-side
    /// image (2:1) cannot be told apart from mono by its size and must be chosen explicitly.
    /// </summary>
    public static StereoLayout FromSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return StereoLayout.Mono;
        }

        if (width == height)
        {
            return StereoLayout.TopBottom;
        }

        return width == 4 * height ? StereoLayout.SideBySide : StereoLayout.Mono;
    }
}
