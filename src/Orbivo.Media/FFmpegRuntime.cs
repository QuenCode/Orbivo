using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Status;

namespace Orbivo.Media;

/// <summary>
/// Locates and checks the FFmpeg shared libraries (LGPL build, see native/README.md).
/// </summary>
public static class FFmpegRuntime
{
    public const string ComponentName = "FFmpeg";

    /// <summary>avcodec major version expected by FFmpeg.AutoGen 9.0.1.1 (FFmpeg 9.0).</summary>
    public const int ExpectedAvcodecMajor = 63;

    /// <summary>DLLs needed at runtime. Keep in sync with build/Native.targets.</summary>
    public static IReadOnlyList<string> ExpectedLibraries { get; } =
        ["avcodec-63.dll", "avformat-63.dll", "avutil-61.dll", "swresample-7.dll", "swscale-10.dll"];

    /// <summary>
    /// Points FFmpeg.AutoGen at <paramref name="libraryDirectory"/>, loads the libraries and checks
    /// version and license. Call once at startup, before any other FFmpeg use.
    /// </summary>
    public static NativeComponentStatus Initialize(string libraryDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(libraryDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        string[] missing = ExpectedLibraries
            .Where(name => !File.Exists(Path.Combine(libraryDirectory, name)))
            .ToArray();
        if (missing.Length > 0)
        {
            logger.LogError(
                "FFmpeg libraries missing in {Directory}: {Missing}. See native/README.md.",
                libraryDirectory,
                string.Join(", ", missing));
            return new NativeComponentStatus(ComponentName, false, $"Fehlt: {string.Join(", ", missing)}");
        }

        try
        {
            DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            ffmpeg.RootPath = libraryDirectory;

            string version = ffmpeg.av_version_info();
            int avcodecMajor = (int)(ffmpeg.avcodec_version() >> 16);
            string license = ffmpeg.avcodec_license();

            if (avcodecMajor != ExpectedAvcodecMajor)
            {
                logger.LogError(
                    "FFmpeg {Version} has avcodec major {Actual}, but the bindings expect {Expected}.",
                    version,
                    avcodecMajor,
                    ExpectedAvcodecMajor);
                return new NativeComponentStatus(
                    ComponentName,
                    false,
                    $"Falsche Version {version} (avcodec {avcodecMajor}, erwartet {ExpectedAvcodecMajor})");
            }

            if (!license.Contains("LGPL", StringComparison.Ordinal))
            {
                logger.LogError("FFmpeg {Version} is licensed '{License}'; only LGPL builds are allowed (N5).", version, license);
                return new NativeComponentStatus(ComponentName, false, $"Kein LGPL-Build: {license}");
            }

            logger.LogInformation(
                "FFmpeg {Version} loaded from {Directory} (avcodec {Major}, license: {License}).",
                version,
                libraryDirectory,
                avcodecMajor,
                license);
            return new NativeComponentStatus(ComponentName, true, $"{version} ({license})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                       or TypeInitializationException or NotSupportedException)
        {
            logger.LogError(ex, "FFmpeg could not be loaded from {Directory}.", libraryDirectory);
            return new NativeComponentStatus(ComponentName, false, $"Laden fehlgeschlagen: {ex.Message}");
        }
    }
}
