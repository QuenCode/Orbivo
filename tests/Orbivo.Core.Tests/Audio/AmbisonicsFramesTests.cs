using Orbivo.Core.Audio;

namespace Orbivo.Core.Tests.Audio;

public sealed class AmbisonicsFramesTests
{
    [Fact]
    public void AmbiX_IsCopied()
    {
        float[] source = [1, 2, 3, 4, 5, 6, 7, 8];
        var destination = new float[8];

        AmbisonicsFrames.ToAmbiX(source, 4, destination, 4, 2, fuMa: false);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void ExtraChannels_AreDropped()
    {
        // First order plus a head-locked stereo pair (L, R) per frame.
        float[] source = [1, 2, 3, 4, 90, 91, 5, 6, 7, 8, 92, 93];
        var destination = new float[8];

        AmbisonicsFrames.ToAmbiX(source, 6, destination, 4, 2, fuMa: false);

        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], destination);
    }

    [Fact]
    public void FuMa_IsReorderedAndWIsRaisedBy3dB()
    {
        // FuMa W X Y Z of a source straight ahead (+X) at full level: W = 1/√2, X = 1.
        float w = 1f / MathF.Sqrt(2f);
        float[] source = [w, 1, 0.25f, -0.5f];
        var destination = new float[4];

        AmbisonicsFrames.ToAmbiX(source, 4, destination, 4, 1, fuMa: true);

        Assert.Equal(1f, destination[0], 6); // W (SN3D)
        Assert.Equal(0.25f, destination[1]); // Y
        Assert.Equal(-0.5f, destination[2]); // Z
        Assert.Equal(1f, destination[3]); // X
    }

    [Fact]
    public void FuMa_AboveFirstOrder_Throws()
    {
        Assert.Throws<ArgumentException>(() => AmbisonicsFrames.ToAmbiX(new float[9], 9, new float[9], 9, 1, fuMa: true));
    }
}
