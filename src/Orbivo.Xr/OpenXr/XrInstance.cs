using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;

namespace Orbivo.Xr.OpenXr;

/// <summary>Headset properties needed to create a session.</summary>
/// <param name="SystemId">OpenXR system id of the headset.</param>
/// <param name="SystemName">Name reported by the runtime.</param>
/// <param name="EyeWidth">Recommended image width per eye.</param>
/// <param name="EyeHeight">Recommended image height per eye.</param>
/// <param name="AdapterLuid">GPU the runtime requires.</param>
/// <param name="MinimumFeatureLevel">Minimum D3D feature level the runtime requires.</param>
internal readonly record struct XrSystemInfo(
    ulong SystemId,
    string SystemName,
    int EyeWidth,
    int EyeHeight,
    ulong AdapterLuid,
    uint MinimumFeatureLevel);

/// <summary>
/// An OpenXR instance with the D3D11 extension (docs/ARCHITECTURE.md, "OpenXR-Lebenszyklus").
/// Owned by the XR render thread.
/// </summary>
internal sealed unsafe class XrInstance : IDisposable
{
    public const string D3D11ExtensionName = "XR_KHR_D3D11_enable";
    public const ViewConfigurationType ViewConfiguration = ViewConfigurationType.PrimaryStereo;
    public const int EyeCount = 2;

    private bool _disposed;

    private XrInstance(XR xr, Instance handle, KhrD3D11Enable d3d11, string runtimeName, string runtimeVersion)
    {
        Xr = xr;
        Handle = handle;
        D3D11 = d3d11;
        RuntimeName = runtimeName;
        RuntimeVersion = runtimeVersion;
    }

    public XR Xr { get; }

    public Instance Handle { get; }

    public KhrD3D11Enable D3D11 { get; }

    public string RuntimeName { get; }

    public string RuntimeVersion { get; }

    /// <summary>
    /// Creates the instance. Throws <see cref="OpenXrException"/> with
    /// <see cref="Result.ErrorRuntimeUnavailable"/> when no runtime is reachable.
    /// </summary>
    public static XrInstance Create(XR xr, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(xr);
        ArgumentNullException.ThrowIfNull(logger);

        if (!RuntimeSupportsExtension(xr, D3D11ExtensionName))
        {
            throw new OpenXrException($"The OpenXR runtime does not support {D3D11ExtensionName}.");
        }

        var applicationInfo = new ApplicationInfo(
            applicationVersion: 1,
            engineVersion: 1,
            apiVersion: XrConversions.MakeVersion(1, 0, 0));
        WriteUtf8("Orbivo", applicationInfo.ApplicationName, (int)XR.MaxApplicationNameSize);
        WriteUtf8("Orbivo", applicationInfo.EngineName, (int)XR.MaxEngineNameSize);

        Instance handle;
        fixed (byte* extensionName = "XR_KHR_D3D11_enable\0"u8)
        {
            byte* extensionNames = extensionName;
            var createInfo = new InstanceCreateInfo(
                type: StructureType.InstanceCreateInfo,
                applicationInfo: applicationInfo,
                enabledExtensionCount: 1,
                enabledExtensionNames: &extensionNames);
            handle = default;
            xr.CreateInstance(&createInfo, &handle).Check("xrCreateInstance");
        }

        try
        {
            var properties = new InstanceProperties(type: StructureType.InstanceProperties);
            xr.GetInstanceProperties(handle, &properties).Check("xrGetInstanceProperties");
            string runtimeName = Marshal.PtrToStringUTF8((nint)properties.RuntimeName) ?? "unknown";
            string runtimeVersion = XrConversions.FormatVersion(properties.RuntimeVersion);

            if (!xr.TryGetInstanceExtension(null, handle, out KhrD3D11Enable d3d11))
            {
                throw new OpenXrException($"Could not load the functions of {D3D11ExtensionName}.");
            }

            logger.LogInformation("OpenXR instance created: runtime {Runtime} {Version}.", runtimeName, runtimeVersion);
            return new XrInstance(xr, handle, d3d11, runtimeName, runtimeVersion);
        }
        catch
        {
            xr.DestroyInstance(handle);
            throw;
        }
    }

    /// <summary>
    /// Looks for a head-mounted display. Returns false while none is connected
    /// (<see cref="Result.ErrorFormFactorUnavailable"/>); the caller polls again later with the same instance.
    /// </summary>
    public bool TryGetSystem(out XrSystemInfo system)
    {
        system = default;
        var getInfo = new SystemGetInfo(type: StructureType.SystemGetInfo, formFactor: FormFactor.HeadMountedDisplay);
        ulong systemId = 0;
        Result result = Xr.GetSystem(Handle, &getInfo, &systemId);
        if (result == Result.ErrorFormFactorUnavailable)
        {
            return false;
        }

        result.Check("xrGetSystem");

        var properties = new SystemProperties(type: StructureType.SystemProperties);
        Xr.GetSystemProperties(Handle, systemId, &properties).Check("xrGetSystemProperties");
        string systemName = Marshal.PtrToStringUTF8((nint)properties.SystemName) ?? string.Empty;

        uint viewCount = 0;
        Xr.EnumerateViewConfigurationView(Handle, systemId, ViewConfiguration, 0, &viewCount, (ViewConfigurationView*)null)
            .Check("xrEnumerateViewConfigurationViews");
        if (viewCount != EyeCount)
        {
            throw new OpenXrException($"Expected {EyeCount} views for PRIMARY_STEREO, the runtime reports {viewCount}.");
        }

        ViewConfigurationView* views = stackalloc ViewConfigurationView[EyeCount];
        for (int i = 0; i < EyeCount; i++)
        {
            views[i] = new ViewConfigurationView(type: StructureType.ViewConfigurationView);
        }

        Xr.EnumerateViewConfigurationView(Handle, systemId, ViewConfiguration, viewCount, &viewCount, views)
            .Check("xrEnumerateViewConfigurationViews");

        var requirements = new GraphicsRequirementsD3D11KHR(type: StructureType.GraphicsRequirementsD3D11Khr);
        D3D11.GetD3D11GraphicsRequirements(Handle, systemId, &requirements).Check("xrGetD3D11GraphicsRequirementsKHR");

        // Both eyes share one size; Varjo reports the same for both.
        int width = (int)Math.Max(views[0].RecommendedImageRectWidth, views[1].RecommendedImageRectWidth);
        int height = (int)Math.Max(views[0].RecommendedImageRectHeight, views[1].RecommendedImageRectHeight);
        system = new XrSystemInfo(systemId, systemName, width, height, requirements.AdapterLuid, requirements.MinFeatureLevel);
        return true;
    }

    /// <summary>Takes the next event from the queue. Returns false if the queue is empty. Does not allocate.</summary>
    public bool PollEvent(ref EventDataBuffer buffer)
    {
        buffer.Type = StructureType.EventDataBuffer;
        buffer.Next = null;
        Result result;
        fixed (EventDataBuffer* pointer = &buffer)
        {
            result = Xr.PollEvent(Handle, pointer);
        }

        if (result == Result.EventUnavailable)
        {
            return false;
        }

        result.Check("xrPollEvent");
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Xr.DestroyInstance(Handle);
    }

    private static bool RuntimeSupportsExtension(XR xr, string name)
    {
        uint count = 0;
        xr.EnumerateInstanceExtensionProperties((byte*)null, 0, &count, (ExtensionProperties*)null)
            .Check("xrEnumerateInstanceExtensionProperties");
        var properties = new ExtensionProperties[count];
        for (int i = 0; i < properties.Length; i++)
        {
            properties[i] = new ExtensionProperties(type: StructureType.ExtensionProperties);
        }

        fixed (ExtensionProperties* pointer = properties)
        {
            xr.EnumerateInstanceExtensionProperties((byte*)null, count, &count, pointer)
                .Check("xrEnumerateInstanceExtensionProperties");
            for (int i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUTF8((nint)pointer[i].ExtensionName) == name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void WriteUtf8(string text, byte* destination, int capacity)
    {
        var span = new Span<byte>(destination, capacity);
        span.Clear();
        int written = System.Text.Encoding.UTF8.GetBytes(text, span[..(capacity - 1)]);
        span[written] = 0;
    }
}
