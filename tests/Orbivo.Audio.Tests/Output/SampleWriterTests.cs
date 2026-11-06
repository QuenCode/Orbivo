using System.Buffers.Binary;
using Orbivo.Audio.Output;

namespace Orbivo.Audio.Tests.Output;

public sealed class SampleWriterTests
{
    private static readonly float[] Stereo = [0.5f, -0.25f, 1.5f, -1.5f];

    [Fact]
    public void Float32Stereo_IsCopiedUnchanged()
    {
        var bytes = new byte[16];
        SampleWriter.Write(Stereo, 2, bytes, 2, SampleEncoding.Float32);
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes));
        Assert.Equal(-0.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8)));
    }

    [Fact]
    public void FurtherChannels_GetSilence()
    {
        var bytes = new byte[2 * 6 * 4];
        bytes.AsSpan().Fill(0xFF);
        SampleWriter.Write(Stereo, 2, bytes, 6, SampleEncoding.Float32);
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes));
        Assert.Equal(-0.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)));
        for (int channel = 2; channel < 6; channel++)
        {
            Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(channel * 4)));
        }

        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24)));
    }

    [Fact]
    public void MonoDevice_GetsTheMeanOfBothSides()
    {
        var bytes = new byte[8];
        SampleWriter.Write(Stereo, 2, bytes, 1, SampleEncoding.Float32);
        Assert.Equal(0.125f, BinaryPrimitives.ReadSingleLittleEndian(bytes));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void Int16_IsScaledAndClipped()
    {
        var bytes = new byte[8];
        SampleWriter.Write(Stereo, 2, bytes, 2, SampleEncoding.Int16);
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(bytes));
        Assert.Equal(-8192, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(6)));
    }

    [Fact]
    public void Int24_IsPackedInThreeBytes()
    {
        var bytes = new byte[12];
        SampleWriter.Write(Stereo, 2, bytes, 2, SampleEncoding.Int24);
        Assert.Equal(4194304, Read24(bytes, 0));
        Assert.Equal(-2097152, Read24(bytes, 3));
        Assert.Equal(8388607, Read24(bytes, 6));
        Assert.Equal(-8388608, Read24(bytes, 9));
    }

    [Fact]
    public void Int32_IsScaledAndClipped()
    {
        var bytes = new byte[16];
        SampleWriter.Write(Stereo, 2, bytes, 2, SampleEncoding.Int32);
        Assert.Equal(1073741824, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(int.MaxValue, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(int.MinValue, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)));
    }

    [Fact]
    public void Write_DoesNotAllocate()
    {
        var stereo = new float[2 * 480];
        var bytes = new byte[480 * 8 * 4];
        SampleEncoding[] encodings = [SampleEncoding.Float32, SampleEncoding.Int16, SampleEncoding.Int24, SampleEncoding.Int32];
        SampleWriter.Write(stereo, 480, bytes, 8, SampleEncoding.Int24);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (SampleEncoding encoding in encodings)
        {
            SampleWriter.Write(stereo, 480, bytes, 2, encoding);
            SampleWriter.Write(stereo, 480, bytes, 8, encoding);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static int Read24(byte[] bytes, int offset) => (bytes[offset] | (bytes[offset + 1] << 8) | ((sbyte)bytes[offset + 2] << 16));
}
