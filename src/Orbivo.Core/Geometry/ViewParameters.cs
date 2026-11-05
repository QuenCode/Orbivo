using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// One view to render: orientation of the view in world space and its field of view.
/// There is no position, 360° content is shown rotation only (CLAUDE.md rule 6).
/// </summary>
/// <param name="Orientation">Rotates view space into world space (OpenXR convention, CLAUDE.md rule 9).</param>
/// <param name="Fov">Field of view of the view.</param>
public readonly record struct ViewParameters(Quaternion Orientation, FovAngles Fov);
