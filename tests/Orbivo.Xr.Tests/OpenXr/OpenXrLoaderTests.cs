using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Orbivo.Core.Status;
using Orbivo.Xr.OpenXr;

namespace Orbivo.Xr.Tests.OpenXr;

public sealed class OpenXrLoaderTests
{
    [Fact]
    public void MissingLoader_IsReportedAsUnavailable()
    {
        string directory = Directory.CreateTempSubdirectory("orbivo-loader-test").FullName;
        try
        {
            NativeComponentStatus status = OpenXrLoader.Check(directory, NullLogger.Instance);

            Assert.False(status.IsAvailable);
            Assert.Contains(OpenXrLoader.LibraryFileName, status.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PresentLoader_IsReportedAsAvailable()
    {
        string directory = Directory.CreateTempSubdirectory("orbivo-loader-test").FullName;
        try
        {
            // A file without version resource is enough for the presence check.
            File.WriteAllBytes(Path.Combine(directory, OpenXrLoader.LibraryFileName), [0]);

            NativeComponentStatus status = OpenXrLoader.Check(directory, NullLogger.Instance);

            Assert.True(status.IsAvailable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
