using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Status;

namespace Orbivo.Audio;

/// <summary>
/// Locates the Steam Audio library (phonon.dll, see native/README.md).
/// The P/Invoke declarations are written against exactly <see cref="ExpectedVersion"/>.
/// </summary>
public static class SteamAudioRuntime
{
    public const string ComponentName = "Steam Audio";

    /// <summary>Keep in sync with build/Native.targets.</summary>
    public const string LibraryFileName = "phonon.dll";

    public static Version ExpectedVersion { get; } = new(4, 8, 1);

    /// <summary>
    /// Checks that phonon.dll is present. The library itself is loaded on first use (M7),
    /// where the API version is verified by <c>iplContextCreate</c>.
    /// </summary>
    public static NativeComponentStatus Check(string libraryDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(libraryDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        string path = Path.Combine(libraryDirectory, LibraryFileName);
        if (!File.Exists(path))
        {
            logger.LogError("Steam Audio library missing: {Path}. See native/README.md.", path);
            return new NativeComponentStatus(ComponentName, false, $"Fehlt: {LibraryFileName}");
        }

        string? fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (Version.TryParse(fileVersion, out Version? actual)
            && (actual.Major, actual.Minor, actual.Build) != (ExpectedVersion.Major, ExpectedVersion.Minor, ExpectedVersion.Build))
        {
            logger.LogError(
                "Steam Audio library {Path} has version {Actual}, but the interop code expects {Expected}.",
                path,
                fileVersion,
                ExpectedVersion);
            return new NativeComponentStatus(ComponentName, false, $"Falsche Version {fileVersion} (erwartet {ExpectedVersion})");
        }

        logger.LogInformation(
            "Steam Audio library found: {Path} (file version {FileVersion}, interop expects {Expected}).",
            path,
            fileVersion ?? "unknown",
            ExpectedVersion);
        return new NativeComponentStatus(ComponentName, true, $"{LibraryFileName} gefunden (erwartet {ExpectedVersion})");
    }
}
