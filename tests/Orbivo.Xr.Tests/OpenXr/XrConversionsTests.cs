using System.Numerics;
using Orbivo.Core.Geometry;
using Orbivo.Xr.OpenXr;
using Silk.NET.OpenXR;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests.OpenXr;

public sealed class XrConversionsTests
{
    private const float Tolerance = 1e-6f;

    [Fact]
    public void Quaternion_KeepsComponentOrder()
    {
        // OpenXR and System.Numerics both store x, y, z, w; nothing may be swapped or negated (CLAUDE.md rule 9).
        var xr = new Quaternionf(0.1f, 0.2f, 0.3f, 0.9f);

        Quaternion q = XrConversions.ToQuaternion(xr);

        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), q);
        Quaternionf back = XrConversions.ToQuaternionf(q);
        Assert.Equal((xr.X, xr.Y, xr.Z, xr.W), (back.X, back.Y, back.Z, back.W));
    }

    [Fact]
    public void Fov_KeepsAllFourAngles()
    {
        var fov = new Fovf(-0.9f, 0.7f, 0.8f, -1.0f);

        FovAngles angles = XrConversions.ToFovAngles(fov);

        Assert.Equal(new FovAngles(-0.9f, 0.7f, 0.8f, -1.0f), angles);
    }

    [Fact]
    public void ViewParameters_UseOrientationAndFov_AndIgnoreThePosition()
    {
        Quaternion lookLeft = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var pose = new Posef(XrConversions.ToQuaternionf(lookLeft), new Vector3f(0.3f, 1.7f, -2f));
        var fov = new Fovf(-0.9f, 0.7f, 0.8f, -1.0f);

        ViewParameters parameters = XrConversions.ToViewParameters(pose, fov, orientationValid: true, Quaternion.Identity);

        Vector3 forward = Vector3.Transform(OrientationMath.Forward, parameters.Orientation);
        Assert.Equal(-1f, forward.X, Tolerance);
        Assert.Equal(0f, forward.Y, Tolerance);
        Assert.Equal(0f, forward.Z, Tolerance);
        Assert.Equal(new FovAngles(-0.9f, 0.7f, 0.8f, -1.0f), parameters.Fov);
    }

    [Fact]
    public void ViewParameters_NormalizeTheOrientation()
    {
        var pose = new Posef(new Quaternionf(0f, 0f, 0f, 1.01f), new Vector3f(0f, 0f, 0f));
        var fov = new Fovf(-1f, 1f, 1f, -1f);

        ViewParameters parameters = XrConversions.ToViewParameters(pose, fov, orientationValid: true, Quaternion.Identity);

        Assert.Equal(1f, parameters.Orientation.Length(), Tolerance);
    }

    [Fact]
    public void ViewParameters_UseFallback_WhenOrientationIsInvalid()
    {
        Quaternion fallback = OrientationMath.FromYawPitch(0.5f, 0.2f);
        var pose = new Posef(new Quaternionf(0f, 1f, 0f, 0f), new Vector3f(0f, 0f, 0f));
        var fov = new Fovf(-1f, 1f, 1f, -1f);

        Assert.Equal(fallback, XrConversions.ToViewParameters(pose, fov, orientationValid: false, fallback).Orientation);
    }

    [Fact]
    public void ViewParameters_UseFallback_ForAZeroQuaternion()
    {
        Quaternion fallback = OrientationMath.FromYawPitch(0.5f, 0.2f);
        var pose = new Posef(new Quaternionf(0f, 0f, 0f, 0f), new Vector3f(0f, 0f, 0f));
        var fov = new Fovf(-1f, 1f, 1f, -1f);

        Assert.Equal(fallback, XrConversions.ToViewParameters(pose, fov, orientationValid: true, fallback).Orientation);
    }

    [Fact]
    public void SwapchainFormat_PrefersRgbaSrgb()
    {
        long[] offered = [(long)Format.R10G10B10A2_UNorm, (long)Format.B8G8R8A8_UNorm_SRgb, (long)Format.R8G8B8A8_UNorm_SRgb];

        Assert.Equal(Format.R8G8B8A8_UNorm_SRgb, XrConversions.ChooseSwapchainFormat(offered));
    }

    [Fact]
    public void SwapchainFormat_FallsBackToBgraSrgb()
    {
        long[] offered = [(long)Format.R8G8B8A8_UNorm, (long)Format.B8G8R8A8_UNorm_SRgb];

        Assert.Equal(Format.B8G8R8A8_UNorm_SRgb, XrConversions.ChooseSwapchainFormat(offered));
    }

    [Fact]
    public void SwapchainFormat_NeverPicksALinearFormat()
    {
        // A non-sRGB format would show the image too bright (gamma, docs/DECISIONS.md).
        long[] offered = [(long)Format.R8G8B8A8_UNorm, (long)Format.B8G8R8A8_UNorm, (long)Format.R16G16B16A16_Float];

        Assert.Equal(Format.Unknown, XrConversions.ChooseSwapchainFormat(offered));
    }

    [Theory]
    [InlineData(11_111_111L, 90f)]
    [InlineData(16_666_667L, 60f)]
    [InlineData(0L, 0f)]
    public void RefreshRate_FromDisplayPeriod(long periodNanoseconds, float expectedHz)
    {
        Assert.Equal(expectedHz, XrConversions.RefreshRateHz(periodNanoseconds), 0.01f);
    }

    [Fact]
    public void Version_IsPackedLikeXrMakeVersion()
    {
        ulong version = XrConversions.MakeVersion(1, 0, 34);

        Assert.Equal((1UL << 48) | 34UL, version);
        Assert.Equal("1.0.34", XrConversions.FormatVersion(version));
        Assert.Equal("4.14.2", XrConversions.FormatVersion(XrConversions.MakeVersion(4, 14, 2)));
    }
}
