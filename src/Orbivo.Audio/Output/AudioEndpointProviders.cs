using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Orbivo.Audio.Output;

/// <summary>A sound output device the operator can choose (F24).</summary>
/// <param name="Id">Windows endpoint id.</param>
/// <param name="Name">Name as Windows shows it.</param>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Lists the active Windows sound output devices for the operator UI.</summary>
public static class AudioDeviceCatalog
{
    /// <summary>All active output devices, sorted by name. Empty if Windows has none or the list fails (logged).</summary>
    public static IReadOnlyList<AudioDeviceInfo> ListOutputs(ILogger logger)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            var result = new List<AudioDeviceInfo>(devices.Count);
            foreach (MMDevice device in devices)
            {
                using (device)
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                }
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Listing the sound output devices failed");
            return [];
        }
    }
}

/// <summary>Opens device streams and tells about device changes. Belongs to the audio output thread.</summary>
internal interface IAudioEndpointProvider : IDisposable
{
    /// <summary>Opens the device <paramref name="deviceId"/> (null = Windows default); throws if that is not possible.</summary>
    IAudioEndpoint Open(string? deviceId);

    /// <summary>Id of the current Windows default device, or null if there is none.</summary>
    string? GetDefaultDeviceId();

    /// <summary>Name of the device <paramref name="deviceId"/>, or null if it is unknown.</summary>
    string? GetDeviceName(string deviceId);

    /// <summary>Grows by one with each device change (added, removed, state, default). Any thread.</summary>
    int ChangeCount { get; }
}

/// <summary>The Windows devices (WASAPI, shared mode).</summary>
internal sealed class WasapiEndpointProvider : IAudioEndpointProvider
{
    private readonly ILogger _logger;
    private readonly float? _streamVolume;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private int _changeCount;

    /// <param name="logger">Log.</param>
    /// <param name="streamVolume">Volume of the streams opened here (not the Windows volume); the device test plays at 0.</param>
    public WasapiEndpointProvider(ILogger logger, float? streamVolume = null)
    {
        _logger = logger;
        _streamVolume = streamVolume;

        // Raised on a Windows audio worker thread that holds a lock: only count, the output thread does the rest.
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DeviceAdded += (_, _) => Interlocked.Increment(ref _changeCount);
        _notifications.DeviceRemoved += (_, _) => Interlocked.Increment(ref _changeCount);
        _notifications.DeviceStateChanged += (_, _) => Interlocked.Increment(ref _changeCount);
        _notifications.DefaultDeviceChanged += (_, args) =>
        {
            if (args.Flow == DataFlow.Render && args.Role == Role.Multimedia)
            {
                Interlocked.Increment(ref _changeCount);
            }
        };
    }

    public int ChangeCount => Volatile.Read(ref _changeCount);

    public IAudioEndpoint Open(string? deviceId)
    {
        MMDevice device = deviceId is null
            ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : _enumerator.GetDevice(deviceId);
        if (device.State != DeviceState.Active)
        {
            string name = device.FriendlyName;
            DeviceState state = device.State;
            device.Dispose();
            throw new InvalidOperationException($"Device {name} is {state}.");
        }

        WasapiAudioEndpoint endpoint = WasapiAudioEndpoint.Open(device, _logger);
        if (_streamVolume is float volume)
        {
            endpoint.SetStreamVolume(volume);
        }

        return endpoint;
    }

    public string? GetDefaultDeviceId()
    {
        if (!_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? device) || device is null)
        {
            return null;
        }

        using (device)
        {
            return device.ID;
        }
    }

    public string? GetDeviceName(string deviceId)
    {
        try
        {
            using MMDevice device = _enumerator.GetDevice(deviceId);
            return device.FriendlyName;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Device {DeviceId} is unknown", deviceId);
            return null;
        }
    }

    public void Dispose()
    {
        _notifications.Dispose();
        _enumerator.Dispose();
    }
}

/// <summary>Simulated devices for tests: nothing is heard.</summary>
internal sealed class SimulatedEndpointProvider : IAudioEndpointProvider
{
    private readonly ConcurrentDictionary<string, SimulatedEndpointOptions> _devices = new();
    private readonly ConcurrentQueue<SimulatedAudioEndpoint> _opened = new();
    private volatile string? _defaultDeviceId;
    private int _changeCount;

    public SimulatedEndpointProvider(params SimulatedEndpointOptions[] devices)
    {
        foreach (SimulatedEndpointOptions device in devices)
        {
            _devices[device.DeviceId] = device;
        }

        _defaultDeviceId = devices.Length > 0 ? devices[0].DeviceId : null;
    }

    public int ChangeCount => Volatile.Read(ref _changeCount);

    /// <summary>Endpoints opened so far, oldest first.</summary>
    public IReadOnlyList<SimulatedAudioEndpoint> Opened => [.. _opened];

    public SimulatedAudioEndpoint? Latest => _opened.LastOrDefault();

    /// <summary>Plugs a device in (or changes its settings).</summary>
    public void Add(SimulatedEndpointOptions device)
    {
        _devices[device.DeviceId] = device;
        Interlocked.Increment(ref _changeCount);
    }

    /// <summary>Unplugs a device: open streams of it fail, it cannot be opened.</summary>
    public void Remove(string deviceId)
    {
        _devices.TryRemove(deviceId, out _);
        foreach (SimulatedAudioEndpoint endpoint in _opened)
        {
            if (endpoint.DeviceId == deviceId)
            {
                endpoint.Fail();
            }
        }

        Interlocked.Increment(ref _changeCount);
    }

    public void SetDefault(string? deviceId)
    {
        _defaultDeviceId = deviceId;
        Interlocked.Increment(ref _changeCount);
    }

    public IAudioEndpoint Open(string? deviceId)
    {
        string? id = deviceId ?? _defaultDeviceId;
        if (id is null || !_devices.TryGetValue(id, out SimulatedEndpointOptions? options))
        {
            throw new InvalidOperationException($"Simulated device {id ?? "(default)"} is not there.");
        }

        var endpoint = new SimulatedAudioEndpoint(options);
        _opened.Enqueue(endpoint);
        return endpoint;
    }

    public string? GetDefaultDeviceId() => _defaultDeviceId;

    public string? GetDeviceName(string deviceId) => _devices.ContainsKey(deviceId) ? $"Simuliert ({deviceId})" : null;

    public void Dispose()
    {
    }
}
