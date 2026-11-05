using Orbivo.Core.Geometry;

namespace Orbivo.Xr.Rendering;

/// <summary>
/// Supplies the views to render each frame (docs/ARCHITECTURE.md, "--no-xr Debug-Modus"):
/// the mouse in the desktop debug viewport, from M2 the headset pose from OpenXR.
/// </summary>
public interface IViewSource
{
    /// <summary>Number of views: 1 for the desktop viewport, 2 for a stereo headset.</summary>
    int ViewCount { get; }

    /// <summary>
    /// Writes the current views into <paramref name="views"/> (length at least <see cref="ViewCount"/>).
    /// Called on the render thread every frame: must not block or allocate.
    /// </summary>
    void GetViews(Span<ViewParameters> views);
}
