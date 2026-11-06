using System.Numerics;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using static Orbivo.Core.Tests.Geometry.GeometryAssert;

namespace Orbivo.Core.Tests.Geometry;

/// <summary>
/// Equirect 180, cubemap, EAC and fisheye in <see cref="ProjectionMath"/> (F10): known directions against expected
/// texture coordinates. The shader is compared with these functions pixel by pixel in Orbivo.Xr.Tests.
/// </summary>
public sealed class ProjectionTypesTests
{
    private const int EyeWidth = 600;
    private const int EyeHeight = 400;

    [Theory]
    [InlineData(0, 0, 0.5, 0.5)] // forward: center
    [InlineData(90, 0, 0.0, 0.5)] // left edge = 90° left
    [InlineData(-90, 0, 1.0, 0.5)] // right edge = 90° right
    [InlineData(45, 0, 0.25, 0.5)]
    [InlineData(-30, 45, 0.5 + (30.0 / 180.0), 0.25)]
    public void Equirect180Uv_MapsTheFrontHalf(double yaw, double pitch, double expectedU, double expectedV)
    {
        Vector2 uv = ProjectionMath.Equirect180Uv(Direction(yaw, pitch), out bool valid);

        Assert.True(valid);
        Assert.Equal(expectedU, uv.X, 1e-5);
        Assert.Equal(expectedV, uv.Y, 1e-5);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-120, 10)]
    [InlineData(180, 0)]
    public void Equirect180Uv_BackHalfIsInvalid(double yaw, double pitch)
    {
        ProjectionMath.Equirect180Uv(Direction(yaw, pitch), out bool valid);

        Assert.False(valid);
    }

    [Theory]
    [InlineData(0, 0, CubeFace.Front)]
    [InlineData(-90, 0, CubeFace.Right)]
    [InlineData(90, 0, CubeFace.Left)]
    [InlineData(180, 0, CubeFace.Back)]
    [InlineData(0, 90, CubeFace.Up)]
    [InlineData(0, -90, CubeFace.Down)]
    public void CubeFaceSt_FaceCentersAreAtTheMiddle(double yaw, double pitch, CubeFace expected)
    {
        CubeFace face = ProjectionMath.CubeFaceSt(Direction(yaw, pitch), out Vector2 st);

        Assert.Equal(expected, face);
        Equal(Vector2.Zero, st, 1e-5f);
    }

    [Theory]
    // A little right (yaw -10) or up (pitch +10) of each face center: s grows to the right, t upwards,
    // as seen by someone facing that face (faces.py in tools/testmedia).
    [InlineData(-10, 0, CubeFace.Front, 1, 0)]
    [InlineData(0, 10, CubeFace.Front, 0, 1)]
    [InlineData(-100, 0, CubeFace.Right, 1, 0)] // facing right, a little further right is towards the back
    [InlineData(80, 0, CubeFace.Left, 1, 0)] // facing left, a little to its right is towards the front
    [InlineData(170, 0, CubeFace.Back, 1, 0)] // facing back, its right is our left
    [InlineData(0, 80, CubeFace.Up, 0, -1)] // up face: the front lies at its bottom edge ("↓ VORNE")
    [InlineData(0, -80, CubeFace.Down, 0, 1)] // down face: the front lies at its top edge ("↑ VORNE")
    [InlineData(-10, 80, CubeFace.Up, 1, -1)] // up face: right is right
    public void CubeFaceSt_IsNotMirrored(double yaw, double pitch, CubeFace expected, int signS, int signT)
    {
        CubeFace face = ProjectionMath.CubeFaceSt(Direction(yaw, pitch), out Vector2 st);

        Assert.Equal(expected, face);
        Assert.Equal(signS, Sign(st.X));
        Assert.Equal(signT, Sign(st.Y));
    }

    [Theory]
    // 3 x 2 "rludfb": right, left, up / down, front, back
    [InlineData("3x2", -90, 0, 1.0 / 6, 0.25)]
    [InlineData("3x2", 90, 0, 0.5, 0.25)]
    [InlineData("3x2", 0, 90, 5.0 / 6, 0.25)]
    [InlineData("3x2", 0, -90, 1.0 / 6, 0.75)]
    [InlineData("3x2", 0, 0, 0.5, 0.75)]
    [InlineData("3x2", 180, 0, 5.0 / 6, 0.75)]
    // 6 x 1 and 1 x 6, same order
    [InlineData("6x1", 0, 0, 4.5 / 6, 0.5)]
    [InlineData("6x1", 0, 90, 2.5 / 6, 0.5)]
    [InlineData("1x6", 0, 0, 0.5, 4.5 / 6)]
    [InlineData("1x6", 180, 0, 0.5, 5.5 / 6)]
    // YouTube EAC "lfrdbu": left, front, right / down, back, up
    [InlineData("eac", 0, 0, 0.5, 0.25)]
    [InlineData("eac", 90, 0, 1.0 / 6, 0.25)]
    [InlineData("eac", -90, 0, 5.0 / 6, 0.25)]
    [InlineData("eac", 0, -90, 1.0 / 6, 0.75)]
    [InlineData("eac", 180, 0, 0.5, 0.75)]
    [InlineData("eac", 0, 90, 5.0 / 6, 0.75)]
    public void Cubemap_FaceCentersLandInTheirCells(string preset, double yaw, double pitch, double expectedU, double expectedV)
    {
        ProjectionType projection = preset == "eac" ? ProjectionType.Eac : ProjectionType.Cubemap;
        ProjectionParameters parameters = Parameters(projection, CubemapLayout.FromPresetName(preset)!);

        Vector2 uv = ProjectionMath.EyeUv(Direction(yaw, pitch), parameters, out bool valid);

        Assert.True(valid);
        Assert.Equal(expectedU, uv.X, 1e-5);
        Assert.Equal(expectedV, uv.Y, 1e-5);
    }

    [Fact]
    public void Cubemap_LinearWithinAFace_EacEquiAngular()
    {
        // 22.5° right of forward: tan = 0.414 of the half face for the cubemap, exactly half for EAC.
        Vector3 direction = Direction(-22.5, 0);
        ProjectionParameters cube = Parameters(ProjectionType.Cubemap, CubemapLayout.Grid3x2);
        ProjectionParameters eac = Parameters(ProjectionType.Eac, CubemapLayout.Grid3x2);

        Vector2 cubeUv = ProjectionMath.EyeUv(direction, cube, out _);
        Vector2 eacUv = ProjectionMath.EyeUv(direction, eac, out _);

        double tan = Math.Tan(22.5 * Math.PI / 180.0);
        Assert.Equal(0.5 + (tan * 0.5 / 3.0), cubeUv.X, 1e-5);
        Assert.Equal(0.5 + (0.5 * 0.5 / 3.0), eacUv.X, 1e-5);
        Assert.Equal(0.75, eacUv.Y, 1e-5);
    }

    [Fact]
    public void Eac_TurnedFacesFollowTheirRotation()
    {
        ProjectionParameters eac = Parameters(ProjectionType.Eac, CubemapLayout.YouTubeEac);

        // Back face, turned 90° clockwise: its top (towards up) lies on the right of its cell.
        Vector2 backUp = ProjectionMath.EyeUv(Direction(180, 20), eac, out _);
        Assert.True(backUp.X > 0.5 + 0.02, $"u = {backUp.X}");
        Assert.Equal(0.75, backUp.Y, 1e-5);

        // Up face, turned 270° clockwise: its top (towards the back) lies on the left of its cell.
        Vector2 upBack = ProjectionMath.EyeUv(Direction(180, 70), eac, out _);
        Assert.True(upBack.X < (5.0 / 6) - 0.02, $"u = {upBack.X}");
        Assert.Equal(0.75, upBack.Y, 1e-5);

        // Down face, turned 270° clockwise: its top (towards the front) lies on the left of its cell.
        Vector2 downFront = ProjectionMath.EyeUv(Direction(0, -70), eac, out _);
        Assert.True(downFront.X < (1.0 / 6) - 0.02, $"u = {downFront.X}");
    }

    [Fact]
    public void Cubemap_StaysHalfATexelInsideItsCell()
    {
        // Just before the edge between front and right, the sample must not reach into the neighbor cell.
        // (Exactly on the edge the right face wins: ties go to X first.)
        ProjectionParameters parameters = Parameters(ProjectionType.Cubemap, CubemapLayout.Grid3x2);
        Assert.Equal(CubeFace.Right, ProjectionMath.CubeFaceSt(Direction(-45, 0), out _));

        Vector2 uv = ProjectionMath.EyeUv(Direction(-44.999, 0), parameters, out _);

        double cellRight = 2.0 / 3.0;
        double cellLeft = 1.0 / 3.0;
        double halfTexel = 0.5 / EyeWidth;
        Assert.True(uv.X <= cellRight - halfTexel + 1e-6 && uv.X >= cellLeft + halfTexel - 1e-6, $"u = {uv.X}");
    }

    [Fact]
    public void Cubemap_PaddingShrinksTheFace()
    {
        var padded = new CubemapLayout(3, 2, "rludfb", "000000", paddingPixels: 10);
        ProjectionParameters parameters = Parameters(ProjectionType.Cubemap, padded);

        // Halfway to the right edge of the front face: tan(angle) = 0.5.
        Vector2 uv = ProjectionMath.EyeUv(Vector3.Normalize(new Vector3(0.5f, 0f, -1f)), parameters, out _);

        double faceWidth = (1.0 / 3.0) - (20.0 / EyeWidth);
        Assert.Equal(0.5 + (0.25 * faceWidth), uv.X, 1e-5);
    }

    [Theory]
    [InlineData(0, 0, 0.5, 0.5, true)] // forward: center
    [InlineData(-90, 0, 0.5 + (0.5 * EyeHeight / EyeWidth), 0.5, true)] // 90° right: right edge of the circle
    [InlineData(90, 0, 0.5 - (0.5 * EyeHeight / EyeWidth), 0.5, true)]
    [InlineData(0, 90, 0.5, 0.0, true)] // 90° up: top of the circle
    [InlineData(0, -45, 0.5, 0.75, true)] // 45° down: halfway to the bottom
    [InlineData(-100, 0, double.NaN, double.NaN, false)] // beyond 90°: outside a 180° circle
    public void Fisheye180_Centered(double yaw, double pitch, double expectedU, double expectedV, bool expectedValid)
    {
        ProjectionParameters parameters = Parameters(ProjectionType.Fisheye, CubemapLayout.Grid3x2, FisheyeParameters.Default);

        Vector2 uv = ProjectionMath.EyeUv(Direction(yaw, pitch), parameters, out bool valid);

        Assert.Equal(expectedValid, valid);
        if (expectedValid)
        {
            Assert.Equal(expectedU, uv.X, 1e-5);
            Assert.Equal(expectedV, uv.Y, 1e-5);
        }
    }

    [Fact]
    public void Fisheye_CenterRadiusAndFieldOfViewApply()
    {
        var fisheye = new FisheyeParameters(200, 0.45, 0.55, 0.4);
        ProjectionParameters parameters = Parameters(ProjectionType.Fisheye, CubemapLayout.Grid3x2, fisheye);

        Vector2 forward = ProjectionMath.EyeUv(Direction(0, 0), parameters, out bool forwardValid);
        Vector2 up = ProjectionMath.EyeUv(Direction(0, 50), parameters, out _);
        Vector2 beyondSide = ProjectionMath.EyeUv(Direction(-95, 0), parameters, out bool beyondValid);
        ProjectionMath.EyeUv(Direction(-101, 0), parameters, out bool outsideValid);

        Assert.True(forwardValid);
        Equal(new Vector2(0.45f, 0.55f), forward);
        Assert.Equal(0.55 - (0.4 * 50.0 / 100.0), up.Y, 1e-5); // 50° of 100° half field of view
        Assert.True(beyondValid);
        Assert.Equal(0.45 + (0.4 * EyeHeight / EyeWidth * 95.0 / 100.0), beyondSide.X, 1e-5);
        Assert.False(outsideValid);
    }

    [Fact]
    public void EyeUv_Equirect360_IsTheEquirect360Function()
    {
        ProjectionParameters parameters = Parameters(ProjectionType.Equirect360, CubemapLayout.Grid3x2);
        Vector3 direction = Direction(123, -17);

        Vector2 uv = ProjectionMath.EyeUv(direction, parameters, out bool valid);

        Assert.True(valid);
        Equal(ProjectionMath.Equirect360Uv(direction), uv);
    }

    [Fact]
    public void EyeSourceRect_WithoutWrap_ClampsUHalfATexelInside()
    {
        EyeSourceRect rect = ProjectionMath.CreateEyeSourceRect(StereoLayout.Mono, Eye.Left, 200, 100, wrapsAround: false);

        Equal(new Vector2(0.5f / 200, 0.5f), ProjectionMath.SourceUv(new Vector2(-0.3f, 0.5f), rect));
        Equal(new Vector2(1f - (0.5f / 200), 0.5f), ProjectionMath.SourceUv(new Vector2(1.2f, 0.5f), rect));
    }

    [Theory]
    [InlineData(StereoLayout.Mono, 800, 400, 800, 400)]
    [InlineData(StereoLayout.TopBottom, 800, 800, 800, 400)]
    [InlineData(StereoLayout.SideBySide, 800, 400, 400, 400)]
    public void EyeSize_HalvesTheStereoAxis(StereoLayout layout, int width, int height, int eyeWidth, int eyeHeight)
    {
        Assert.Equal((eyeWidth, eyeHeight), ProjectionMath.EyeSize(layout, width, height));
    }

    [Theory]
    [InlineData(StereoLayout.TopBottom, false, Eye.Left, Eye.Left)]
    [InlineData(StereoLayout.TopBottom, true, Eye.Left, Eye.Right)]
    [InlineData(StereoLayout.SideBySide, true, Eye.Right, Eye.Left)]
    [InlineData(StereoLayout.Mono, true, Eye.Right, Eye.Right)]
    public void SwapEyes_SwapsOnlyStereo(StereoLayout layout, bool swap, Eye eye, Eye expected)
    {
        VideoFormat format = VideoFormat.Default with { Layout = layout, SwapEyes = swap };

        Assert.Equal(expected, format.SourceEye(eye));
    }

    private static ProjectionParameters Parameters(ProjectionType projection, CubemapLayout layout, FisheyeParameters? fisheye = null)
    {
        VideoFormat format = VideoFormat.Default with
        {
            Projection = projection,
            Cubemap = layout,
            Fisheye = fisheye ?? FisheyeParameters.Default,
        };
        return ProjectionMath.CreateProjectionParameters(format, EyeWidth, EyeHeight);
    }

    private static int Sign(float value) => Math.Abs(value) < 1e-4f ? 0 : Math.Sign(value);
}
