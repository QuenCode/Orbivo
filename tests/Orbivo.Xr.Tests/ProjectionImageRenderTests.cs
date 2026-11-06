using System.IO;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests;

/// <summary>
/// Renders the test images of the other projections (VR180, cubemap layouts, EAC, fisheye; made by
/// tools/testmedia from the same scene as the equirect 360 image) and compares each view with the same view of the
/// equirect 360 image. A wrong face order, a turned or mirrored face, a wrong fisheye circle or swapped eyes moves
/// labels and grid lines and shows up as differing pixels. The views are saved as PNG (reference next to it).
/// </summary>
public sealed class ProjectionImageRenderTests(ProjectionImageRenderTests.Fixture fixture) : IClassFixture<ProjectionImageRenderTests.Fixture>
{
    private const int Width = 192;
    private const int Height = 108;

    /// <summary>A pixel differs if one channel is off by more than this (resampling blur stays below).</summary>
    private const int DifferentChannel = 60;

    /// <summary>Share of differing pixels a view may have (edges of grid lines and text, the resolution patch).</summary>
    private const double MaxDifferentShare = 0.04;

    /// <summary>The whole sphere: every face, and edges with each turned EAC face (up, down, back).</summary>
    private static readonly (double Yaw, double Pitch, double Fov)[] SphereViews =
    [
        (0, 0, 40), (-90, 0, 40), (180, 0, 40), (90, 0, 40), (0, 90, 40), (0, -90, 40),
        (45, 0, 30), (135, 0, 30), (180, 45, 30), (180, -45, 30), (-90, 45, 30), (0, -45, 30), (30, 20, 30),
    ];

    /// <summary>The front half (VR180, fisheye 180°).</summary>
    private static readonly (double Yaw, double Pitch, double Fov)[] HemisphereViews =
    [
        (0, 0, 40), (-60, 0, 20), (60, 0, 20), (0, 60, 20), (0, -60, 20), (30, 20, 30), (-30, -20, 30),
    ];

    /// <summary>Beyond 90° from the front: only a 200° fisheye shows it.</summary>
    private static readonly (double Yaw, double Pitch, double Fov)[] BeyondHemisphereViews = [(92, 15, 8), (-92, -15, 8)];

    public static TheoryData<string, Eye> ProjectionImages()
    {
        var data = new TheoryData<string, Eye>();
        foreach ((string name, VideoFormat format) in Formats)
        {
            data.Add(name, Eye.Left);
            if (format.Layout != StereoLayout.Mono)
            {
                data.Add(name, Eye.Right);
            }
        }

        return data;
    }

    private static readonly (string Name, VideoFormat Format)[] Formats =
    [
        ("equirect180_sbs_4096x2048.png", Make(ProjectionType.Equirect180, StereoLayout.SideBySide)),
        ("cubemap3x2_mono_3072x2048.png", Make(ProjectionType.Cubemap, StereoLayout.Mono)),
        ("cubemap6x1_mono_6144x1024.png", Make(ProjectionType.Cubemap, StereoLayout.Mono, CubemapLayout.Strip6x1)),
        ("cubemap1x6_mono_1024x6144.png", Make(ProjectionType.Cubemap, StereoLayout.Mono, CubemapLayout.Strip1x6)),
        ("cubemap3x2_tb_3072x4096.png", Make(ProjectionType.Cubemap, StereoLayout.TopBottom)),
        ("eac_mono_3072x2048.png", Make(ProjectionType.Eac, StereoLayout.Mono, CubemapLayout.YouTubeEac)),
        ("eac_tb_3072x4096.png", Make(ProjectionType.Eac, StereoLayout.TopBottom, CubemapLayout.YouTubeEac)),
        ("fisheye180_mono_4096x4096.png", Make(ProjectionType.Fisheye, StereoLayout.Mono)),
        ("fisheye200_sbs_4096x2048.png", Make(ProjectionType.Fisheye, StereoLayout.SideBySide, fisheye: new FisheyeParameters(200, 0.48, 0.51, 0.46))),
    ];

    [Theory]
    [MemberData(nameof(ProjectionImages))]
    public void EveryView_MatchesTheEquirect360Image(string fileName, Eye eye)
    {
        VideoFormat format = Formats.Single(f => f.Name == fileName).Format;
        (double Yaw, double Pitch, double Fov)[] views = format.Projection switch
        {
            ProjectionType.Equirect180 => HemisphereViews,
            ProjectionType.Fisheye when format.Fisheye.FovDegrees > 190 => [.. HemisphereViews, .. BeyondHemisphereViews],
            ProjectionType.Fisheye => HemisphereViews,
            _ => SphereViews,
        };

        var failures = new List<string>();
        foreach ((double yaw, double pitch, double fov) in views)
        {
            ViewParameters view = TestViews.Create(yaw, pitch, fov, 16.0 / 9.0);
            string name = FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(fileName)}_{eye}_yaw{yaw}_pitch{pitch}");
            byte[] rendered = Render(name, fileName, format, eye, view);
            byte[] reference = RenderReference(name + "_reference", format.Layout != StereoLayout.Mono, eye, view);
            double share = DifferentShare(rendered, reference);
            if (share > MaxDifferentShare)
            {
                failures.Add(FormattableString.Invariant($"yaw {yaw} pitch {pitch}: {share:P1} of the pixels differ"));
            }
        }

        Assert.True(failures.Count == 0, $"{fileName} {eye} eye differs from the equirect 360 image: {string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData("equirect180_sbs_4096x2048.png")]
    [InlineData("fisheye180_mono_4096x4096.png")]
    public void Hemisphere_IsBlackBehind(string fileName)
    {
        VideoFormat format = Formats.Single(f => f.Name == fileName).Format;

        byte[] behind = Render(Path.GetFileNameWithoutExtension(fileName) + "_behind", fileName, format, Eye.Left, TestViews.Create(180, 0, 40, 16.0 / 9.0));

        Assert.All(Enumerable.Range(0, Width * Height), pixel => Assert.True(
            behind[pixel * 4] < 4 && behind[(pixel * 4) + 1] < 4 && behind[(pixel * 4) + 2] < 4,
            $"pixel {pixel} is not black"));
    }

    [Theory]
    [InlineData("equirect180_sbs_4096x2048.png")]
    [InlineData("cubemap3x2_tb_3072x4096.png")]
    [InlineData("eac_tb_3072x4096.png")]
    [InlineData("fisheye200_sbs_4096x2048.png")]
    public void DepthPlate_HasCrossedDisparity_AndSwapEyesTurnsItAround(string fileName)
    {
        VideoFormat format = Formats.Single(f => f.Name == fileName).Format;
        string name = Path.GetFileNameWithoutExtension(fileName);

        // The plate "TIEFE" (tools/testmedia, equirect.py): 30° right of the front, 15° up; 1° crossed disparity, so the
        // left eye sees it further right than the right eye.
        ViewParameters view = TestViews.Create(-30, 15, 12, 16.0 / 9.0);
        double left = PlateCenterX(Render(name + "_plate_Left", fileName, format, Eye.Left, view));
        double right = PlateCenterX(Render(name + "_plate_Right", fileName, format, Eye.Right, view));
        VideoFormat swapped = format with { SwapEyes = true };
        double swappedLeft = PlateCenterX(Render(name + "_plate_swapped_Left", fileName, swapped, Eye.Left, view));
        double swappedRight = PlateCenterX(Render(name + "_plate_swapped_Right", fileName, swapped, Eye.Right, view));

        // 1° at 192 px for about 21° is about 9 px.
        Assert.InRange(left - right, 5, 14);
        Assert.InRange(swappedRight - swappedLeft, 5, 14);
        Assert.Equal(left, swappedRight, 3);
    }

    private static VideoFormat Make(ProjectionType projection, StereoLayout layout, CubemapLayout? cubemap = null, FisheyeParameters? fisheye = null)
    {
        return VideoFormat.Equirect360(layout) with
        {
            Projection = projection,
            Cubemap = cubemap ?? CubemapLayout.Grid3x2,
            Fisheye = fisheye ?? FisheyeParameters.Default,
        };
    }

    private byte[] Render(string name, string fileName, VideoFormat format, Eye eye, ViewParameters view)
    {
        StillImageTexture texture = fixture.GetTexture(fileName);
        using var target = new OffscreenTarget(fixture.Renderer.Device, Width, Height, Format.B8G8R8A8_UNorm_SRgb);
        fixture.Renderer.Renderer.Render(
            fixture.Renderer.Device.ImmediateContext, target.RenderTargetView, Width, Height, view, eye, PanoramaSource.FromImage(texture, format));
        byte[] bgra = target.Read<byte>();
        TestMedia.SavePng(Path.Combine(TestMedia.RenderedViewsDirectory, "projections", name + ".png"), Width, Height, bgra);
        return bgra;
    }

    private byte[] RenderReference(string name, bool stereo, Eye eye, ViewParameters view)
    {
        return stereo
            ? Render(name, "equirect360_tb_4096x4096.png", VideoFormat.Equirect360(StereoLayout.TopBottom), eye, view)
            : Render(name, "equirect360_mono_4096x2048.png", VideoFormat.Default, Eye.Left, view);
    }

    private static double DifferentShare(byte[] a, byte[] b)
    {
        int different = 0;
        for (int pixel = 0; pixel < Width * Height; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                if (Math.Abs(a[(pixel * 4) + channel] - b[(pixel * 4) + channel]) > DifferentChannel)
                {
                    different++;
                    break;
                }
            }
        }

        return (double)different / (Width * Height);
    }

    /// <summary>Mean x of the plate's light gray (250, 250, 250) pixels.</summary>
    private static double PlateCenterX(byte[] bgra)
    {
        double sum = 0;
        int count = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int pixel = ((y * Width) + x) * 4;
                if (bgra[pixel] > 235 && bgra[pixel + 1] > 235 && bgra[pixel + 2] > 235)
                {
                    sum += x;
                    count++;
                }
            }
        }

        Assert.True(count > 200, $"plate not found ({count} light pixels)");
        return sum / count;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, StillImageTexture> _textures = [];

        public WarpRendererFixture Renderer { get; } = new();

        public StillImageTexture GetTexture(string fileName)
        {
            if (!_textures.TryGetValue(fileName, out StillImageTexture? texture))
            {
                string path = Path.Combine(TestMedia.OutputDirectory, fileName);
                Assert.SkipUnless(File.Exists(path), $"Test image missing: {path}. Create it with: python tools/testmedia/make_testmedia.py --what images --sizes 4k");
                texture = StillImageTexture.Create(Renderer.Device, TestMedia.LoadBgra32(path));
                _textures.Add(fileName, texture);
            }

            return texture;
        }

        public void Dispose()
        {
            foreach (StillImageTexture texture in _textures.Values)
            {
                texture.Dispose();
            }

            Renderer.Dispose();
        }
    }
}
