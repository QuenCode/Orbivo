using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Orbivo.Core.Formats;

namespace Orbivo.Media;

/// <summary>
/// Reads the Spherical Video metadata of a video stream (F12): sv3d and st3d in MP4, the Matroska and SEI
/// equivalents, as FFmpeg's demuxer puts them into the stream's coded side data.
/// </summary>
/// <remarks>
/// FFmpeg.AutoGen has no types for <c>AVSphericalMapping</c> and <c>AVStereo3D</c>, so their first fields are read
/// through <see cref="SphericalMappingData"/> and <see cref="Stereo3DData"/> (layout as in libavutil/spherical.h and
/// stereo3d.h of FFmpeg 9; only fields at the start, which have kept their place since FFmpeg 3).
/// </remarks>
internal static unsafe class FormatMetadataReader
{
    private const double Fixed16 = 65536.0;
    private const double Fixed32 = 4294967296.0;
    private const int StereoFlagInvert = 1;

    public static EmbeddedFormatMetadata Read(AVStream* stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        AVCodecParameters* parameters = stream->codecpar;
        if (parameters is null || parameters->nb_coded_side_data <= 0)
        {
            return EmbeddedFormatMetadata.None;
        }

        AVPacketSideData* spherical = ffmpeg.av_packet_side_data_get(
            parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_SPHERICAL);
        AVPacketSideData* stereo = ffmpeg.av_packet_side_data_get(
            parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_STEREO3D);
        return new EmbeddedFormatMetadata(ReadSpherical(spherical), ReadStereo(stereo));
    }

    private static SphericalMapping? ReadSpherical(AVPacketSideData* sideData)
    {
        if (sideData is null || sideData->data is null || (long)sideData->size < sizeof(SphericalMappingData))
        {
            return null;
        }

        SphericalMappingData data = *(SphericalMappingData*)sideData->data;
        return FromData(data);
    }

    private static Stereo3DInfo? ReadStereo(AVPacketSideData* sideData)
    {
        if (sideData is null || sideData->data is null || (long)sideData->size < sizeof(Stereo3DData))
        {
            return null;
        }

        Stereo3DData data = *(Stereo3DData*)sideData->data;
        return FromData(data);
    }

    /// <summary>Converts the raw struct (fixed point) into degrees and fractions. Public to the tests.</summary>
    internal static SphericalMapping FromData(SphericalMappingData data)
    {
        return new SphericalMapping(
            (SphericalProjectionKind)data.Projection,
            data.Yaw / Fixed16,
            data.Pitch / Fixed16,
            data.Roll / Fixed16,
            data.BoundLeft / Fixed32,
            data.BoundTop / Fixed32,
            data.BoundRight / Fixed32,
            data.BoundBottom / Fixed32,
            (int)Math.Min(data.Padding, int.MaxValue));
    }

    internal static Stereo3DInfo FromData(Stereo3DData data)
    {
        return new Stereo3DInfo((Stereo3DKind)data.Type, (data.Flags & StereoFlagInvert) != 0);
    }

    /// <summary>The start of <c>AVSphericalMapping</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SphericalMappingData
    {
        public int Projection;
        public int Yaw;
        public int Pitch;
        public int Roll;
        public uint BoundLeft;
        public uint BoundTop;
        public uint BoundRight;
        public uint BoundBottom;
        public uint Padding;
    }

    /// <summary>The start of <c>AVStereo3D</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Stereo3DData
    {
        public int Type;
        public int Flags;
    }
}
