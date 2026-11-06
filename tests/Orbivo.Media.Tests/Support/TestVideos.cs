namespace Orbivo.Media.Tests.Support;

/// <summary>Test videos from <c>python tools/testmedia/make_testmedia.py</c> (600 frames at 30 fps each).</summary>
internal static class TestVideos
{
    public const int FrameCount = 600;

    public static string Mono4K => Require("equirect360_mono_4096x2048_hevc_8bit_30fps.mp4");

    public static string TopBottom8K => Require("equirect360_tb_8192x8192_hevc_8bit_30fps.mp4");

    public static string MonoImage => Require("equirect360_mono_4096x2048.png");

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
