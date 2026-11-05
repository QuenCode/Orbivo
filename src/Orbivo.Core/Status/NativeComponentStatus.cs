namespace Orbivo.Core.Status;

/// <summary>
/// Result of the startup check of a native component (FFmpeg, Steam Audio, OpenXR runtime).
/// </summary>
/// <param name="Component">Short component name, e.g. "FFmpeg".</param>
/// <param name="IsAvailable">True if the component was found and is usable.</param>
/// <param name="Detail">Text for the operator UI (German): version or what is missing.</param>
public sealed record NativeComponentStatus(string Component, bool IsAvailable, string Detail);
