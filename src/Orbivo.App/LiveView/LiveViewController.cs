using Microsoft.Extensions.Logging;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.LiveView;

namespace Orbivo.App.LiveView;

/// <summary>
/// Starts and stops the live view presenter (F42) when everything it needs is there: the operator switched it on, the
/// child window exists, and the render thread's device and frames exist. UI thread only.
/// </summary>
public sealed class LiveViewController : IDisposable
{
    private readonly ILogger _logger;
    private GraphicsDevice? _device;
    private LiveViewFrames? _frames;
    private nint _window;
    private bool _enabled;
    private LiveViewPresenter? _presenter;

    public LiveViewController(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>True once the render thread's device exists (the headset was found, or the preview runs).</summary>
    public bool IsAvailable => _frames is not null;

    /// <summary>The render thread draws into <paramref name="frames"/> on <paramref name="device"/>.</summary>
    public void Attach(GraphicsDevice device, LiveViewFrames frames)
    {
        _device = device;
        _frames = frames;
        Update();
    }

    public void SetWindow(nint window)
    {
        _window = window;
        Update();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Update();
    }

    /// <summary>Presenter state, or null while the live view is off or not available yet.</summary>
    public LiveViewStatus? GetStatus() => _presenter?.GetStatus();

    public void Dispose()
    {
        _enabled = false;
        Update();
    }

    private void Update()
    {
        bool run = _enabled && _window != 0 && _device is not null && _frames is not null;
        if (run && _presenter is null)
        {
            _presenter = new LiveViewPresenter(_device!, _frames!, _window, _logger);
            _presenter.Start();
        }
        else if (!run && _presenter is not null)
        {
            _presenter.Dispose();
            _presenter = null;
        }
    }
}
