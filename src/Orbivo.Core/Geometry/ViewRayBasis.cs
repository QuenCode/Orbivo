using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// Three world-space vectors from which the viewing direction of every pixel follows:
/// <c>direction = normalize(ndc.x * Right + ndc.y * Up + Center)</c>.
/// This is the inverse of (projection × view rotation) from docs/ARCHITECTURE.md written as three vectors,
/// so C# and HLSL never disagree about matrix layout.
/// </summary>
/// <param name="Right">Change of the (unnormalized) direction from the view center to the right edge, ndc.x = +1.</param>
/// <param name="Up">Change of the (unnormalized) direction from the view center to the top edge, ndc.y = +1.</param>
/// <param name="Center">Unnormalized direction at ndc (0, 0).</param>
public readonly record struct ViewRayBasis(Vector3 Right, Vector3 Up, Vector3 Center);
