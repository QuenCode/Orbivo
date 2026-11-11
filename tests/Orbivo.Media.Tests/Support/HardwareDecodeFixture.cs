using Microsoft.Extensions.Logging.Abstractions;
using Orbivo.Core.Status;
using Orbivo.Xr.Graphics;
using SharpGen.Runtime;

namespace Orbivo.Media.Tests.Support;

/// <summary>
/// FFmpeg plus the shared D3D11 device on the hardware GPU, like the app uses them. Tests call
/// <see cref="RequireHardware"/> first; it skips the test on machines without FFmpeg DLLs or a GPU.
/// </summary>
public sealed class HardwareDecodeFixture : IDisposable
{
    /// <summary>
    /// All decoder tests share one collection: they measure throughput and timing on the one video engine of the
    /// GPU, so they run one after another.
    /// </summary>
    public const string Collection = "Hardware decode";

    private readonly string? _unavailableReason;

    public HardwareDecodeFixture()
    {
        NativeComponentStatus ffmpeg = FFmpegRuntime.Initialize(AppContext.BaseDirectory, NullLogger.Instance);
        if (!ffmpeg.IsAvailable)
        {
            _unavailableReason = $"FFmpeg not available: {ffmpeg.Detail}";
            return;
        }

        try
        {
            Device = GraphicsDevice.CreateOnHighPerformanceGpu(NullLogger.Instance, requestDebugLayer: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SharpGenException)
        {
            // CI runners and remote sessions have no GPU with video support; the decoder tests skip there.
            _unavailableReason = $"No hardware GPU: {exception.Message}";
        }
    }

    public GraphicsDevice? Device { get; }

    public GraphicsDevice RequireHardware()
    {
        if (_unavailableReason is not null || Device is null)
        {
            Assert.Skip(_unavailableReason ?? "No hardware GPU.");
        }

        return Device;
    }

    public void Dispose()
    {
        Device?.Dispose();
    }
}

/// <summary>Test classes in this collection run sequentially and share one fixture.</summary>
[CollectionDefinition(HardwareDecodeFixture.Collection)]
public sealed class HardwareDecodeCollection : ICollectionFixture<HardwareDecodeFixture>;
