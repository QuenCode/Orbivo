using System.Numerics;
using Orbivo.Core.Geometry;
using Silk.NET.OpenXR;
using Vortice.DXGI;

namespace Orbivo.Xr.OpenXr;

/// <summary>
/// Converts between OpenXR structs and Orbivo types. OpenXR and Orbivo use the same coordinate system
/// (CLAUDE.md rule 9), so nothing is mirrored or reordered here; this is the only place where the structs meet.
/// </summary>
internal static class XrConversions
{
    /// <summary>Swapchain formats in order of preference. Both are sRGB, see docs/DECISIONS.md (gamma).</summary>
    public static ReadOnlySpan<Format> PreferredSwapchainFormats => [Format.R8G8B8A8_UNorm_SRgb, Format.B8G8R8A8_UNorm_SRgb];

    public static Quaternion ToQuaternion(in Quaternionf q)
    {
        return new Quaternion(q.X, q.Y, q.Z, q.W);
    }

    public static Quaternionf ToQuaternionf(Quaternion q)
    {
        return new Quaternionf(q.X, q.Y, q.Z, q.W);
    }

    public static FovAngles ToFovAngles(in Fovf fov)
    {
        return new FovAngles(fov.AngleLeft, fov.AngleRight, fov.AngleUp, fov.AngleDown);
    }

    /// <summary>
    /// The view to render for one eye: orientation and field of view only. The head position is dropped,
    /// 360° content lies at infinity (CLAUDE.md rule 6).
    /// </summary>
    /// <param name="view">View from <c>xrLocateViews</c>.</param>
    /// <param name="orientationValid">False if the runtime flagged the orientation as invalid.</param>
    /// <param name="fallbackOrientation">Used when the orientation is invalid or not normalizable (last valid one).</param>
    public static ViewParameters ToViewParameters(in View view, bool orientationValid, Quaternion fallbackOrientation)
    {
        return ToViewParameters(view.Pose, view.Fov, orientationValid, fallbackOrientation);
    }

    /// <inheritdoc cref="ToViewParameters(in View, bool, Quaternion)"/>
    /// <param name="pose">Pose of the view; only the orientation is used.</param>
    /// <param name="fov">Field of view of the view.</param>
    /// <param name="orientationValid">False if the runtime flagged the orientation as invalid.</param>
    /// <param name="fallbackOrientation">Used when the orientation is invalid or not normalizable.</param>
    public static ViewParameters ToViewParameters(in Posef pose, in Fovf fov, bool orientationValid, Quaternion fallbackOrientation)
    {
        Quaternion orientation = fallbackOrientation;
        if (orientationValid)
        {
            Quaternion located = ToQuaternion(pose.Orientation);
            float lengthSquared = located.LengthSquared();
            if (lengthSquared > 0.5f && float.IsFinite(lengthSquared))
            {
                orientation = Quaternion.Normalize(located);
            }
        }

        return new ViewParameters(orientation, ToFovAngles(fov));
    }

    /// <summary>
    /// Picks the first format of <see cref="PreferredSwapchainFormats"/> that the runtime offers.
    /// Returns <see cref="Format.Unknown"/> if none is offered.
    /// </summary>
    /// <param name="runtimeFormats">DXGI format values from <c>xrEnumerateSwapchainFormats</c>, in runtime order.</param>
    public static Format ChooseSwapchainFormat(ReadOnlySpan<long> runtimeFormats)
    {
        foreach (Format preferred in PreferredSwapchainFormats)
        {
            if (runtimeFormats.Contains((long)preferred))
            {
                return preferred;
            }
        }

        return Format.Unknown;
    }

    /// <summary>Display refresh rate in Hz from <c>XrFrameState.predictedDisplayPeriod</c> (nanoseconds), 0 if unknown.</summary>
    public static float RefreshRateHz(long predictedDisplayPeriodNanoseconds)
    {
        return predictedDisplayPeriodNanoseconds > 0 ? 1e9f / predictedDisplayPeriodNanoseconds : 0f;
    }

    /// <summary>Formats an OpenXR version (major 16 bit, minor 16 bit, patch 32 bit) as text.</summary>
    public static string FormatVersion(ulong version)
    {
        return $"{version >> 48}.{(version >> 32) & 0xFFFF}.{version & 0xFFFFFFFF}";
    }

    /// <summary>OpenXR version number as XR_MAKE_VERSION builds it.</summary>
    public static ulong MakeVersion(ushort major, ushort minor, uint patch)
    {
        return ((ulong)major << 48) | ((ulong)minor << 32) | patch;
    }
}
