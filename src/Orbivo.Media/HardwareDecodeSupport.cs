using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Localization;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using DxgiFormat = Vortice.DXGI.Format;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using ID3D11VideoDecoder = Vortice.Direct3D11.ID3D11VideoDecoder;

namespace Orbivo.Media;

/// <summary>Which decoder the player uses (<c>--decoder auto|hw|sw</c>).</summary>
public enum DecoderPreference
{
    /// <summary>Hardware if the GPU can decode the video, else software (docs/DECISIONS.md).</summary>
    Auto,

    /// <summary>Hardware only; the video fails if the GPU cannot decode it.</summary>
    Hardware,

    /// <summary>Software only (for tests and comparisons).</summary>
    Software,
}

/// <summary>
/// Asks the GPU before opening whether it can decode a stream with D3D11VA: decoder profile, output format, and a
/// decoder of that size (NVDEC, for example, decodes H.264 only up to 4096 × 4096). Unknown codecs and profiles are not
/// judged; the hardware is tried then, and the player falls back to software if it fails before the first frame.
/// </summary>
internal static class HardwareDecodeSupport
{
    // D3D11_DECODER_PROFILE_* from d3d11.h.
    private static readonly Guid H264VldNoFgt = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    private static readonly Guid HevcMain = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
    private static readonly Guid HevcMain10 = new("107af0e0-ef1a-4d19-aba8-67a163073d13");
    private static readonly Guid Vp9Profile0 = new("463707f8-a1d0-4585-876d-83aa6d60b89e");
    private static readonly Guid Vp9Profile2 = new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7");
    private static readonly Guid Av1Profile0 = new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a");
    private static readonly Guid Mpeg2Vld = new("ee27417f-5e28-4e65-beea-1d26b508adc9");
    private static readonly Guid Mpeg2And1Vld = new("86695f12-340e-4f04-9fd3-9253dd327460");

    /// <summary>
    /// True if the GPU can decode the stream, false if it cannot (<paramref name="reason"/> says why, in German),
    /// null if this check does not know the codec or profile.
    /// </summary>
    /// <param name="device">The shared D3D11 device.</param>
    /// <param name="codecId">Codec of the stream.</param>
    /// <param name="profile">FFmpeg profile number (FF_PROFILE_*), or a negative value if unknown.</param>
    /// <param name="bitDepth">Bits per sample of the decoded picture.</param>
    /// <param name="chroma420">True for 4:2:0 chroma subsampling.</param>
    /// <param name="width">Picture width.</param>
    /// <param name="height">Picture height.</param>
    /// <param name="logger">Receives the details.</param>
    /// <param name="reason">Why the GPU cannot decode the stream; null otherwise.</param>
    public static bool? CanDecode(
        ID3D11Device device,
        AVCodecID codecId,
        int profile,
        int bitDepth,
        bool chroma420,
        int width,
        int height,
        ILogger logger,
        out OperatorText? reason)
    {
        reason = null;
        string codecName = ffmpeg.avcodec_get_name(codecId) ?? codecId.ToString();
        if (codecId == AVCodecID.AV_CODEC_ID_H264 && (bitDepth > 8 || !chroma420))
        {
            // DXVA has no H.264 profile beyond 8 bit 4:2:0 (High 10, 4:2:2 and 4:4:4 are software only).
            reason = chroma420
                ? new OperatorText(nameof(CoreText.HardwareNoH264), bitDepth)
                : new OperatorText(nameof(CoreText.HardwareNoH264Not420), bitDepth);
            return false;
        }

        Guid[] profiles = ProfilesFor(codecId, profile, bitDepth, chroma420);
        if (profiles.Length == 0)
        {
            logger.LogInformation(
                "No hardware decode check for {Codec} profile {Profile}, {BitDepth} bit; trying the hardware.", codecName, profile, bitDepth);
            return null;
        }

        using ID3D11VideoDevice? videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        if (videoDevice is null)
        {
            logger.LogInformation("The D3D11 device has no video decoding (ID3D11VideoDevice); trying the hardware anyway.");
            return null;
        }

        Guid? found = FindProfile(videoDevice, profiles);
        if (found is not Guid decoderProfile)
        {
            reason = new OperatorText(nameof(CoreText.HardwareNoProfile), codecName, bitDepth);
            logger.LogInformation("The GPU offers none of the decoder profiles {Profiles} for {Codec}.", string.Join(", ", profiles), codecName);
            return false;
        }

        DxgiFormat outputFormat = bitDepth > 8 ? DxgiFormat.P010 : DxgiFormat.NV12;
        Result formatResult = videoDevice.CheckVideoDecoderFormat(decoderProfile, outputFormat, out RawBool formatSupported);
        if (formatResult.Failure || !formatSupported)
        {
            reason = new OperatorText(nameof(CoreText.HardwareNoOutputFormat), codecName, outputFormat.ToString());
            logger.LogInformation("Decoder profile {Profile} does not output {Format} ({Result}).", decoderProfile, outputFormat, formatResult);
            return false;
        }

        // Like FFmpeg (dxva2.c): the decoder is created for the coded size, macroblock aligned for H.264 and MPEG-2.
        int alignment = codecId is AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_MPEG2VIDEO ? 16 : 1;
        var description = new VideoDecoderDescription
        {
            Guid = decoderProfile,
            SampleWidth = (uint)Align(width, alignment),
            SampleHeight = (uint)Align(height, alignment),
            OutputFormat = outputFormat,
        };

        var sizeReason = new OperatorText(nameof(CoreText.HardwareTooLarge), codecName, width, height);
        Result countResult = videoDevice.GetVideoDecoderConfigCount(ref description, out uint configCount);
        if (countResult.Failure || configCount == 0)
        {
            reason = sizeReason;
            logger.LogInformation(
                "No decoder configuration for {Codec} {Width}x{Height} {Format} ({Result}, {Count} configurations).",
                codecName, width, height, outputFormat, countResult, configCount);
            return false;
        }

        Result configResult = videoDevice.GetVideoDecoderConfig(ref description, 0, out VideoDecoderConfig config);
        if (configResult.Failure)
        {
            reason = sizeReason;
            logger.LogInformation("GetVideoDecoderConfig for {Codec} {Width}x{Height} failed ({Result}).", codecName, width, height, configResult);
            return false;
        }

        // The size limit shows for certain only when a decoder is created.
        Result createResult = videoDevice.CreateVideoDecoder(description, config, out ID3D11VideoDecoder? decoder);
        decoder?.Dispose();
        if (createResult.Failure)
        {
            reason = sizeReason;
            logger.LogInformation("CreateVideoDecoder for {Codec} {Width}x{Height} failed ({Result}).", codecName, width, height, createResult);
            return false;
        }

        logger.LogInformation(
            "The GPU decodes {Codec} {Width}x{Height} {Format} (decoder profile {Profile}).", codecName, width, height, outputFormat, decoderProfile);
        return true;
    }

    /// <summary>The decoder profiles FFmpeg's D3D11VA code uses for the stream (any one of them will do).</summary>
    private static Guid[] ProfilesFor(AVCodecID codecId, int profile, int bitDepth, bool chroma420)
    {
        if (!chroma420)
        {
            return [];
        }

        return codecId switch
        {
            AVCodecID.AV_CODEC_ID_H264 => [H264VldNoFgt],
            AVCodecID.AV_CODEC_ID_HEVC when bitDepth == 8 => [HevcMain],
            AVCodecID.AV_CODEC_ID_HEVC when bitDepth == 10 => [HevcMain10],
            AVCodecID.AV_CODEC_ID_VP9 when profile == 0 && bitDepth == 8 => [Vp9Profile0],
            AVCodecID.AV_CODEC_ID_VP9 when profile == 2 && bitDepth == 10 => [Vp9Profile2],
            AVCodecID.AV_CODEC_ID_AV1 when profile == 0 && bitDepth is 8 or 10 => [Av1Profile0],
            AVCodecID.AV_CODEC_ID_MPEG2VIDEO when bitDepth == 8 => [Mpeg2Vld, Mpeg2And1Vld],
            _ => [],
        };
    }

    private static Guid? FindProfile(ID3D11VideoDevice videoDevice, Guid[] wanted)
    {
        uint count = videoDevice.VideoDecoderProfileCount;
        for (uint i = 0; i < count; i++)
        {
            if (videoDevice.GetVideoDecoderProfile(i, out Guid profile).Success && Array.IndexOf(wanted, profile) >= 0)
            {
                return profile;
            }
        }

        return null;
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
