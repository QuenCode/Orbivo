using System.IO;
using Orbivo.Core.Geometry;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests;

/// <summary>
/// Renders the test image from tools/testmedia (made by an independent Python implementation of the conventions)
/// and checks which colored cube face is visible. This is the automated form of the M1 acceptance test:
/// VORNE in front, OBEN above, nothing mirrored. Skipped if the test media have not been generated.
/// </summary>
public sealed class TestImageRenderTests(TestImageRenderTests.Fixture fixture) : IClassFixture<TestImageRenderTests.Fixture>
{
    private const int Width = 192;
    private const int Height = 108;

    /// <summary>Face colors from tools/testmedia/orbivo_testmedia/faces.py.</summary>
    private static readonly (string Label, byte R, byte G, byte B)[] Faces =
    [
        ("VORNE", 46, 125, 50),
        ("HINTEN", 183, 28, 28),
        ("LINKS", 21, 101, 192),
        ("RECHTS", 230, 100, 0),
        ("OBEN", 0, 121, 133),
        ("UNTEN", 106, 27, 154),
    ];

    [Theory]
    [InlineData(0, 0, "VORNE")]
    [InlineData(-90, 0, "RECHTS")]
    [InlineData(180, 0, "HINTEN")]
    [InlineData(90, 0, "LINKS")]
    [InlineData(0, 90, "OBEN")]
    [InlineData(0, -90, "UNTEN")]
    public void LookingAtAFace_ShowsThatFace(double yawDegrees, double pitchDegrees, string expectedFace)
    {
        byte[] bgra = Render($"face_{expectedFace}", TestViews.Create(yawDegrees, pitchDegrees, 40, 16.0 / 9.0));

        AssertMajority(bgra, 0, Width, 0, Height, expectedFace);
    }

    [Theory]
    [InlineData(45, 0, "LINKS", "VORNE")] // front left: LINKS on the left half
    [InlineData(-45, 0, "VORNE", "RECHTS")] // front right: RECHTS on the right half
    public void LookingAtAVerticalEdge_IsNotMirrored(double yawDegrees, double pitchDegrees, string leftFace, string rightFace)
    {
        byte[] bgra = Render($"edge_{leftFace}_{rightFace}", TestViews.Create(yawDegrees, pitchDegrees, 30, 16.0 / 9.0));

        AssertMajority(bgra, 0, Width / 2, 0, Height, leftFace);
        AssertMajority(bgra, Width / 2, Width, 0, Height, rightFace);
    }

    [Fact]
    public void LookingAtTheUpperEdge_OBENIsAboveVORNE()
    {
        byte[] bgra = Render("edge_OBEN_VORNE", TestViews.Create(0, 45, 40, 16.0 / 9.0));

        AssertMajority(bgra, 0, Width, 0, Height / 2, "OBEN");
        AssertMajority(bgra, 0, Width, Height / 2, Height, "VORNE");
    }

    [Theory]
    [InlineData(90, 0, 0, "VORNE")] // head 90° left, recenter: the front is where the head looks
    [InlineData(90, 0, -90, "RECHTS")] // then turning 90° right shows what lies right of the front
    [InlineData(-135, 0, 90, "LINKS")]
    [InlineData(60, 30, 0, "VORNE")] // the head looked up while recentering: only the yaw counts
    public void Recenter_MakesTheHeadDirectionTheFront(double headYaw, double headPitch, double turnAfterwards, string expectedFace)
    {
        var recenter = new RecenterControl();
        ViewParameters head = TestViews.Create(headYaw, headPitch, 40, 16.0 / 9.0);
        recenter.RequestRecenter();
        recenter.Update(head.Orientation, headTracked: true);

        // Afterwards the head turns on and looks straight ahead again.
        ViewParameters later = TestViews.Create(headYaw + turnAfterwards, 0, 40, 16.0 / 9.0);
        ViewParameters view = RecenterControl.Apply(later, recenter.Update(later.Orientation, headTracked: true));
        byte[] bgra = Render($"recenter_{headYaw}_{headPitch}_{turnAfterwards}", view);

        AssertMajority(bgra, 0, Width, 0, Height, expectedFace);
    }

    [Fact]
    public void FaceColor_PassesThroughTheSrgbPipelineUnchanged()
    {
        // Texture and render target are *_SRGB views: decode on sampling, encode on writing. If one of them were
        // missing, the green would come out noticeably lighter or darker.
        byte[] bgra = Render("gamma_VORNE", TestViews.Create(0, 0, 40, 16.0 / 9.0));

        (double r, double g, double b) = AverageColorOf(bgra, "VORNE");
        Assert.InRange(r, 46 - 2, 46 + 2);
        Assert.InRange(g, 125 - 2, 125 + 2);
        Assert.InRange(b, 50 - 2, 50 + 2);
    }

    private byte[] Render(string name, ViewParameters view)
    {
        StillImageTexture texture = fixture.GetTexture();
        using var target = new OffscreenTarget(fixture.Renderer.Device, Width, Height, Format.B8G8R8A8_UNorm_SRgb);
        fixture.Renderer.Renderer.Render(fixture.Renderer.Device.ImmediateContext, target.RenderTargetView, Width, Height, view, texture);
        byte[] bgra = target.Read<byte>();
        TestMedia.SavePng(Path.Combine(TestMedia.RenderedViewsDirectory, name + ".png"), Width, Height, bgra);
        return bgra;
    }

    private static void AssertMajority(byte[] bgra, int x0, int x1, int y0, int y1, string expectedFace)
    {
        var counts = new Dictionary<string, int>();
        int total = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                total++;
                string? face = Classify(bgra, (y * Width) + x);
                if (face is not null)
                {
                    counts[face] = counts.GetValueOrDefault(face) + 1;
                }
            }
        }

        string summary = string.Join(", ", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Key} {c.Value}"));
        int expectedCount = counts.GetValueOrDefault(expectedFace);
        Assert.True(
            expectedCount > total / 2 && counts.All(c => c.Key == expectedFace || c.Value < expectedCount),
            $"Expected mostly {expectedFace} in x {x0}..{x1}, y {y0}..{y1} ({total} px), got: {summary}");
    }

    private static (double R, double G, double B) AverageColorOf(byte[] bgra, string face)
    {
        double r = 0, g = 0, b = 0;
        int count = 0;
        for (int pixel = 0; pixel < Width * Height; pixel++)
        {
            if (Classify(bgra, pixel) == face)
            {
                b += bgra[pixel * 4];
                g += bgra[(pixel * 4) + 1];
                r += bgra[(pixel * 4) + 2];
                count++;
            }
        }

        Assert.True(count > 0, $"No {face} pixels found.");
        return (r / count, g / count, b / count);
    }

    private static string? Classify(byte[] bgra, int pixel)
    {
        int b = bgra[pixel * 4];
        int g = bgra[(pixel * 4) + 1];
        int r = bgra[(pixel * 4) + 2];
        foreach ((string label, byte fr, byte fg, byte fb) in Faces)
        {
            int dr = r - fr, dg = g - fg, db = b - fb;
            if ((dr * dr) + (dg * dg) + (db * db) < 30 * 30)
            {
                return label;
            }
        }

        return null;
    }

    public sealed class Fixture : IDisposable
    {
        private StillImageTexture? _texture;

        public WarpRendererFixture Renderer { get; } = new();

        public StillImageTexture GetTexture()
        {
            if (_texture is null)
            {
                string path = TestMedia.MonoTestImagePath;
                Assert.SkipUnless(
                    File.Exists(path),
                    $"Test image missing: {path}. Create it with: python tools/testmedia/make_testmedia.py --what images --sizes 4k --layouts mono");
                _texture = StillImageTexture.Create(Renderer.Device, TestMedia.LoadBgra32(path));
            }

            return _texture;
        }

        public void Dispose()
        {
            _texture?.Dispose();
            Renderer.Dispose();
        }
    }
}
