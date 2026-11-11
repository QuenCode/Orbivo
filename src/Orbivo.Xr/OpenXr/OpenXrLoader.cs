using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Localization;
using Orbivo.Core.Status;

namespace Orbivo.Xr.OpenXr;

/// <summary>
/// Locates the Khronos OpenXR loader (openxr_loader.dll, see native/README.md). Silk.NET.OpenXR does not ship it.
/// The loader finds the active runtime (Varjo OpenXR) through the registry.
/// </summary>
public static class OpenXrLoader
{
    public const string ComponentName = "OpenXR-Loader";

    /// <summary>Keep in sync with build/Native.targets.</summary>
    public const string LibraryFileName = "openxr_loader.dll";

    public static Version ExpectedVersion { get; } = new(1, 1, 65);

    /// <summary>
    /// Checks that openxr_loader.dll is present. Another version is reported as a warning in the log only:
    /// the loader ABI is stable and Orbivo asks for OpenXR 1.0.
    /// </summary>
    public static NativeComponentStatus Check(string libraryDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(libraryDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        string path = Path.Combine(libraryDirectory, LibraryFileName);
        if (!File.Exists(path))
        {
            logger.LogError("OpenXR loader missing: {Path}. See native/README.md.", path);
            return new NativeComponentStatus(ComponentName, false, Loc.Format(CoreText.NativeMissing, LibraryFileName));
        }

        string? fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (Version.TryParse(fileVersion, out Version? actual)
            && (actual.Major, actual.Minor, actual.Build) != (ExpectedVersion.Major, ExpectedVersion.Minor, ExpectedVersion.Build))
        {
            logger.LogWarning("OpenXR loader {Path} has version {Actual}, pinned is {Expected}.", path, fileVersion, ExpectedVersion);
        }
        else
        {
            logger.LogInformation("OpenXR loader found: {Path} (file version {FileVersion}).", path, fileVersion ?? "unknown");
        }

        return new NativeComponentStatus(ComponentName, true, $"{LibraryFileName} {fileVersion ?? CoreText.NativeVersionUnknown}");
    }
}
