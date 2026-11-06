using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// One eye's part of the source image: <c>uv = clamp(eyeUv, ClampMin, ClampMax) * Scale + Offset</c>.
/// Created by <see cref="ProjectionMath.CreateEyeSourceRect"/>.
/// </summary>
/// <param name="Scale">Size of the eye's image in texture coordinates.</param>
/// <param name="Offset">Top left corner of the eye's image in texture coordinates.</param>
/// <param name="ClampMin">Lower limit for coordinates within the eye's image.</param>
/// <param name="ClampMax">Upper limit for coordinates within the eye's image.</param>
public readonly record struct EyeSourceRect(Vector2 Scale, Vector2 Offset, Vector2 ClampMin, Vector2 ClampMax);
