using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Orbivo.Xr.Graphics;

/// <summary>
/// The Direct3D 11 device of the player. Renderer and (from M3) FFmpeg share it, so it is multithread protected
/// (docs/ARCHITECTURE.md, "Datenfluss Video").
/// </summary>
/// <remarks>
/// Resources may be created on any thread. <see cref="ImmediateContext"/> belongs to the render thread.
/// </remarks>
public sealed class GraphicsDevice : IDisposable
{
    private static readonly FeatureLevel[] SupportedFeatureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    private bool _disposed;

    private GraphicsDevice(
        ID3D11Device device,
        ID3D11DeviceContext immediateContext,
        string adapterName,
        ulong dedicatedVideoMemoryBytes,
        ulong adapterLuid,
        bool isSoftware,
        bool debugLayerEnabled)
    {
        Device = device;
        ImmediateContext = immediateContext;
        AdapterName = adapterName;
        AdapterLuid = adapterLuid;
        DedicatedVideoMemoryBytes = dedicatedVideoMemoryBytes;
        IsSoftware = isSoftware;
        DebugLayerEnabled = debugLayerEnabled;
    }

    public ID3D11Device Device { get; }

    /// <summary>Render thread only.</summary>
    public ID3D11DeviceContext ImmediateContext { get; }

    public string AdapterName { get; }

    public ulong DedicatedVideoMemoryBytes { get; }

    /// <summary>Locally unique id of the adapter, in the 64-bit form OpenXR uses (<c>XrGraphicsRequirementsD3D11KHR</c>).</summary>
    public ulong AdapterLuid { get; }

    public FeatureLevel FeatureLevel => Device.FeatureLevel;

    /// <summary>True for the WARP software rasterizer (tests).</summary>
    public bool IsSoftware { get; }

    public bool DebugLayerEnabled { get; }

    /// <summary>
    /// Creates the device on the most powerful hardware GPU (matters on laptops with an integrated GPU).
    /// Used for the desktop debug viewport; with the headset OpenXR prescribes the adapter (<see cref="CreateOnAdapter"/>).
    /// </summary>
    /// <param name="logger">Receives the device details.</param>
    /// <param name="requestDebugLayer">Enables the D3D11 debug layer if it is installed (Windows feature "Graphics Tools").</param>
    public static GraphicsDevice CreateOnHighPerformanceGpu(ILogger logger, bool requestDebugLayer)
    {
        ArgumentNullException.ThrowIfNull(logger);

        using IDXGIFactory6 factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(false);
        using IDXGIAdapter1 adapter = FindHighPerformanceHardwareAdapter(factory)
            ?? throw new InvalidOperationException("No hardware GPU with Direct3D 11 support was found.");

        // Video support is required for D3D11VA decoding on the shared device (M3).
        return Create(adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport, requestDebugLayer, logger);
    }

    /// <summary>
    /// Creates the device on the adapter the OpenXR runtime requires (the GPU the headset is connected to).
    /// </summary>
    /// <param name="adapterLuid">Adapter LUID from <c>xrGetD3D11GraphicsRequirementsKHR</c>.</param>
    /// <param name="minimumFeatureLevel">Minimum feature level from the same call (a D3D_FEATURE_LEVEL value).</param>
    /// <param name="logger">Receives the device details.</param>
    /// <param name="requestDebugLayer">Enables the D3D11 debug layer if it is installed.</param>
    public static GraphicsDevice CreateOnAdapter(ulong adapterLuid, uint minimumFeatureLevel, ILogger logger, bool requestDebugLayer)
    {
        ArgumentNullException.ThrowIfNull(logger);

        using IDXGIFactory4 factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        // LUID is { uint LowPart; int HighPart; }, which is exactly the little-endian 64-bit value OpenXR uses.
        Luid luid = Unsafe.BitCast<ulong, Luid>(adapterLuid);
        using IDXGIAdapter1 adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>(luid);

        GraphicsDevice device = Create(adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport, requestDebugLayer, logger);
        if ((uint)device.FeatureLevel < minimumFeatureLevel)
        {
            FeatureLevel actual = device.FeatureLevel;
            device.Dispose();
            throw new InvalidOperationException(
                $"The OpenXR runtime requires feature level 0x{minimumFeatureLevel:X}, but {adapter.Description1.Description} only offers {actual}.");
        }

        return device;
    }

    /// <summary>Creates a device on WARP, the software rasterizer that is part of Windows (used by the GPU tests).</summary>
    public static GraphicsDevice CreateWarp(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return Create(null, DriverType.Warp, DeviceCreationFlags.None, requestDebugLayer: false, logger);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ImmediateContext.ClearState();
        ImmediateContext.Flush();
        ImmediateContext.Dispose();
        Device.Dispose();
    }

    private static IDXGIAdapter1? FindHighPerformanceHardwareAdapter(IDXGIFactory6 factory)
    {
        for (uint index = 0; ; index++)
        {
            if (factory.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Failure
                || adapter is null)
            {
                return null;
            }

            if ((adapter.Description1.Flags & AdapterFlags.Software) == 0)
            {
                return adapter;
            }

            adapter.Dispose();
        }
    }

    private static GraphicsDevice Create(
        IDXGIAdapter? adapter,
        DriverType driverType,
        DeviceCreationFlags flags,
        bool requestDebugLayer,
        ILogger logger)
    {
        bool debugLayer = requestDebugLayer && D3D11.SdkLayersAvailable();
        if (requestDebugLayer && !debugLayer)
        {
            logger.LogInformation("D3D11 debug layer requested but not installed (Windows optional feature \"Graphics Tools\").");
        }

        if (debugLayer)
        {
            flags |= DeviceCreationFlags.Debug;
        }

        D3D11.D3D11CreateDevice(
            adapter,
            driverType,
            flags,
            SupportedFeatureLevels,
            out ID3D11Device? device,
            out FeatureLevel featureLevel,
            out ID3D11DeviceContext? immediateContext).CheckError();

        if (device is null || immediateContext is null)
        {
            throw new InvalidOperationException("D3D11CreateDevice succeeded but returned no device.");
        }

        try
        {
            using (ID3D11Multithread multithread = device.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true);
            }

            string adapterName;
            ulong videoMemory;
            ulong adapterLuid;
            using (IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>())
            using (IDXGIAdapter dxgiAdapter = dxgiDevice.GetAdapter())
            {
                AdapterDescription description = dxgiAdapter.Description;
                adapterName = description.Description;
                videoMemory = description.DedicatedVideoMemory;
                adapterLuid = Unsafe.BitCast<Luid, ulong>(description.Luid);
            }

            logger.LogInformation(
                "Direct3D 11 device created on {Adapter} ({VideoMemoryMiB} MiB dedicated video memory), feature level {FeatureLevel}, flags {Flags}, multithread protected.",
                adapterName,
                videoMemory / (1024 * 1024),
                featureLevel,
                flags);

            return new GraphicsDevice(device, immediateContext, adapterName, videoMemory, adapterLuid, driverType == DriverType.Warp, debugLayer);
        }
        catch
        {
            immediateContext.Dispose();
            device.Dispose();
            throw;
        }
    }
}
