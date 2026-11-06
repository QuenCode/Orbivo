using System.Runtime.InteropServices;
using Orbivo.Core.Formats;
using Orbivo.Media;
using Orbivo.Xr.Graphics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using VideoColor = Orbivo.Core.Formats.VideoColor;

namespace Orbivo.Xr.Tests.Support;

/// <summary>
/// An NV12 or P010 texture made on the CPU, in the form the decoder delivers it: the upper half filled with one
/// YUV code triple, the lower half with another. Stands in for a decoded frame in GPU tests without a decoder.
/// </summary>
internal sealed class TestVideoFrame : IDisposable
{
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11ShaderResourceView _luma;
    private readonly ID3D11ShaderResourceView _chroma;

    public TestVideoFrame(
        GraphicsDevice device,
        int width,
        int height,
        int bitDepth,
        (int Y, int Cb, int Cr) top,
        (int Y, int Cb, int Cr) bottom,
        StereoLayout layout,
        VideoColorFormat color)
    {
        bool tenBit = bitDepth == 10;
        int bytesPerSample = tenBit ? 2 : 1;
        int pitch = width * bytesPerSample;

        // D3D11 initial data for planar formats: the luma plane, directly followed by the interleaved chroma plane.
        byte[] data = new byte[pitch * height * 3 / 2];
        for (int y = 0; y < height; y++)
        {
            int code = y < height / 2 ? top.Y : bottom.Y;
            for (int x = 0; x < width; x++)
            {
                Write(data, (y * pitch) + (x * bytesPerSample), code, tenBit);
            }
        }

        int chromaStart = pitch * height;
        for (int y = 0; y < height / 2; y++)
        {
            (int cb, int cr) = y < height / 4 ? (top.Cb, top.Cr) : (bottom.Cb, bottom.Cr);
            for (int x = 0; x < width / 2; x++)
            {
                int offset = chromaStart + (y * pitch) + (x * 2 * bytesPerSample);
                Write(data, offset, cb, tenBit);
                Write(data, offset + bytesPerSample, cr, tenBit);
            }
        }

        Format format = tenBit ? Format.P010 : Format.NV12;
        GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            _texture = device.Device.CreateTexture2D(
                new Texture2DDescription(format, (uint)width, (uint)height, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.ShaderResource),
                new SubresourceData(pinned.AddrOfPinnedObject(), (uint)pitch));
        }
        finally
        {
            pinned.Free();
        }

        _luma = device.Device.CreateShaderResourceView(
            _texture,
            new ShaderResourceViewDescription(_texture, ShaderResourceViewDimension.Texture2D, tenBit ? Format.R16_UNorm : Format.R8_UNorm));
        _chroma = device.Device.CreateShaderResourceView(
            _texture,
            new ShaderResourceViewDescription(_texture, ShaderResourceViewDimension.Texture2D, tenBit ? Format.R16G16_UNorm : Format.R8G8_UNorm));

        Constants = VideoColor.CreateConstants(color with { BitDepth = bitDepth });
        Frame = new VideoFrame(_luma, _chroma, width, height, layout, Constants, 0);
    }

    public VideoColorConstants Constants { get; }

    public VideoFrame Frame { get; }

    public void Dispose()
    {
        _chroma.Dispose();
        _luma.Dispose();
        _texture.Dispose();
    }

    /// <summary>Texture samples as the shader reads them, for the C# reference.</summary>
    public static System.Numerics.Vector3 Samples((int Y, int Cb, int Cr) codes, int bitDepth)
    {
        return bitDepth == 10
            ? new System.Numerics.Vector3(codes.Y << 6, codes.Cb << 6, codes.Cr << 6) / 65535f
            : new System.Numerics.Vector3(codes.Y, codes.Cb, codes.Cr) / 255f;
    }

    private static void Write(byte[] data, int offset, int code, bool tenBit)
    {
        if (tenBit)
        {
            // P010: 10-bit value in the high bits of a little-endian 16-bit word.
            int value = code << 6;
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        }
        else
        {
            data[offset] = (byte)code;
        }
    }
}
