using System.Runtime.InteropServices;
using System.Windows.Interop;
using Orbivo.Xr.LiveView;

namespace Orbivo.App.LiveView;

/// <summary>
/// Embeds the live view child window (<see cref="LiveViewWindow"/>) in the operator window. The presenter draws into it
/// from its own thread; before the window is destroyed, <see cref="WindowDestroying"/> lets the owner stop it.
/// </summary>
public sealed class LiveViewHost : HwndHost
{
    public LiveViewHost()
    {
        Focusable = false;
    }

    /// <summary>The child window, 0 while it does not exist.</summary>
    public nint WindowHandle { get; private set; }

    /// <summary>The child window exists (<see cref="WindowHandle"/>).</summary>
    public event EventHandler? WindowCreated;

    /// <summary>The child window is about to be destroyed: stop drawing into it now.</summary>
    public event EventHandler? WindowDestroying;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        WindowHandle = LiveViewWindow.CreateChild(hwndParent.Handle, 1, 1);
        WindowCreated?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, WindowHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        WindowDestroying?.Invoke(this, EventArgs.Empty);
        LiveViewWindow.Destroy(hwnd.Handle);
        WindowHandle = 0;
    }
}
