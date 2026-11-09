namespace Orbivo.Media.Tests.Support;

/// <summary>Test videos from <c>python tools/testmedia/make_testmedia.py</c> (600 frames at 30 fps each).</summary>
internal static class TestVideos
{
    public const int FrameCount = 600;

    public static string Mono4K => Require("equirect360_mono_4096x2048_hevc_8bit_30fps.mp4");

    public static string TopBottom8K => Require("equirect360_tb_8192x8192_hevc_8bit_30fps.mp4");

    public static string MonoImage => Require("equirect360_mono_4096x2048.png");

    /// <summary>Frames of the short videos for the software decoder (4 s, <c>make_testmedia.py --what decoder</c>).</summary>
    public const int DecoderVideoFrameCount = 120;

    /// <summary>H.264 4320 × 2160: larger than NVDEC decodes H.264 (4096), so it needs the software decoder.</summary>
    public static string H264AboveGpuLimit => Require("equirect360_mono_4320x2160_h264_8bit_30fps_4s.mp4");

    /// <summary>Ambisonics direction test (M10), first order AmbiX as AAC with an <c>SA3D</c> box.</summary>
    public static string AmbisonicsSa3d => Require("ambisonics_richtungstest_equirect360_mono_4096x2048_hevc_8bit_30fps.mp4");

    /// <summary>The same as AAC without any Ambisonics metadata.</summary>
    public static string AmbisonicsWithoutMetadata => Require("ambisonics_richtungstest_equirect360_mono_4096x2048_hevc_8bit_30fps_ohne_metadaten.mp4");

    /// <summary>The same in FuMa (W X Y Z, W −3 dB) without metadata; its sidecar says <c>"audioMode": "FuMa"</c>.</summary>
    public static string AmbisonicsFuMa => Require("ambisonics_richtungstest_equirect360_mono_4096x2048_hevc_8bit_30fps_fuma.mp4");

    /// <summary>Third order AmbiX, 16 channels PCM in Matroska.</summary>
    public static string AmbisonicsThirdOrder => Require("ambisonics_richtungstest_equirect360_mono_4096x2048_hevc_8bit_30fps_3o_16ch.mkv");

    /// <summary>10 minutes, for the A/V long test (<c>make_testmedia.py --long</c>).</summary>
    public static string Mono4KLong => Require("equirect360_mono_4096x2048_hevc_8bit_30fps_600s.mp4");

    public static string OutputDirectory
    {
        get
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Orbivo.sln")))
                {
                    return Path.Combine(directory.FullName, "tools", "testmedia", "out");
                }
            }

            throw new InvalidOperationException("Repository root (Orbivo.sln) not found.");
        }
    }

    private static string Require(string fileName)
    {
        string path = Path.Combine(OutputDirectory, fileName);
        if (!File.Exists(path))
        {
            Assert.Skip($"{fileName} missing, run python tools/testmedia/make_testmedia.py.");
        }

        return path;
    }
}
