using System.ComponentModel;
using System.Runtime.InteropServices;
using static Orbivo.Xr.DebugViewport.NativeMethods;

namespace Orbivo.Xr.DebugViewport;

/// <summary>
/// The Win32 window of the debug viewport. It is created on the render thread and its messages are processed there,
/// so mouse input reaches <see cref="MouseViewSource"/> without crossing threads.
/// </summary>
/// <remarks>
/// The window has no close button: like the headset it stays until the operator window closes.
/// </remarks>
internal sealed unsafe class DebugViewportWindow : IDisposable
{
    private const string ClassName = "Orbivo.DebugViewport";

    [ThreadStatic]
    private static DebugViewportWindow? t_current;

    private readonly MouseViewSource _view;
    private readonly nint _instance;
    private Exception? _messageException;
    private bool _disposed;

    public DebugViewportWindow(string title, int clientWidth, int clientHeight, MouseViewSource view)
    {
        _view = view;
        _instance = GetModuleHandleW(null);
        RegisterWindowClass(_instance);

        var rect = new RECT { Right = clientWidth, Bottom = clientHeight };
        if (!AdjustWindowRectEx(ref rect, WS_OVERLAPPEDWINDOW, false, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AdjustWindowRectEx failed.");
        }

        t_current = this;
        Handle = CreateWindowExW(
            0,
            ClassName,
            title,
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            0,
            0,
            _instance,
            0);
        if (Handle == 0)
        {
            t_current = null;
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowExW failed.");
        }

        // Do not take the focus away from the operator window.
        ShowWindow(Handle, SW_SHOWNOACTIVATE);

        if (!GetClientRect(Handle, out RECT client))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetClientRect failed.");
        }

        ClientWidth = Math.Max(1, client.Right - client.Left);
        ClientHeight = Math.Max(1, client.Bottom - client.Top);
        _view.SetViewportSize(ClientWidth, ClientHeight);

        // The swap chain is created with this size; WM_SIZE during creation needs no resize.
        SizeChanged = false;
    }

    public nint Handle { get; private set; }

    public int ClientWidth { get; private set; }

    public int ClientHeight { get; private set; }

    /// <summary>Set when the client size changed; the render loop resizes the swap chain and clears it.</summary>
    public bool SizeChanged { get; set; }

    public bool IsMinimized { get; private set; }

    /// <summary>
    /// Waits until <paramref name="frameHandle"/> is signaled or a window message arrives, at most
    /// <paramref name="timeoutMilliseconds"/>. Returns true if the frame handle was signaled.
    /// Pass 0 as handle to wait for messages only.
    /// </summary>
    public static bool WaitForFrameOrMessage(nint frameHandle, uint timeoutMilliseconds)
    {
        uint result = frameHandle == 0
            ? MsgWaitForMultipleObjectsEx(0, null, timeoutMilliseconds, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
            : MsgWaitForMultipleObjectsEx(1, &frameHandle, timeoutMilliseconds, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

        if (result == WAIT_FAILED)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "MsgWaitForMultipleObjectsEx failed.");
        }

        return frameHandle != 0 && result == WAIT_OBJECT_0;
    }

    /// <summary>Processes all queued messages of this thread without waiting.</summary>
    public void PumpMessages()
    {
        while (PeekMessageW(out MSG message, 0, 0, 0, PM_REMOVE))
        {
            TranslateMessage(message);
            DispatchMessageW(message);
        }

        if (_messageException is not null)
        {
            Exception exception = _messageException;
            _messageException = null;
            throw new InvalidOperationException("Handling a window message failed.", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Handle != 0)
        {
            DestroyWindow(Handle);
            Handle = 0;
        }

        t_current = null;
        UnregisterClassW(ClassName, _instance);
    }

    private static void RegisterWindowClass(nint instance)
    {
        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_HREDRAW | CS_VREDRAW | CS_NOCLOSE,
                lpfnWndProc = &WindowProcedure,
                hInstance = instance,
                hCursor = LoadCursorW(0, IDC_ARROW),
                lpszClassName = className,
            };

            if (RegisterClassExW(windowClass) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw new Win32Exception(error, "RegisterClassExW failed.");
                }
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        DebugViewportWindow? current = t_current;
        if (current is not null)
        {
            try
            {
                if (current.HandleMessage(window, message, wParam, lParam, out nint result))
                {
                    return result;
                }
            }
            catch (Exception exception)
            {
                // Exceptions must not cross the native boundary; PumpMessages rethrows it on the render loop.
                current._messageException ??= exception;
                return 0;
            }
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private bool HandleMessage(nint window, uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case WM_SIZE:
                IsMinimized = wParam == SIZE_MINIMIZED;
                if (!IsMinimized)
                {
                    int width = Math.Max(1, UnsignedLowWord(lParam));
                    int height = Math.Max(1, UnsignedHighWord(lParam));
                    if (width != ClientWidth || height != ClientHeight)
                    {
                        ClientWidth = width;
                        ClientHeight = height;
                        SizeChanged = true;
                        _view.SetViewportSize(width, height);
                    }
                }

                return true;

            case WM_LBUTTONDOWN:
                SetCapture(window);
                _view.BeginDrag(SignedLowWord(lParam), SignedHighWord(lParam));
                return true;

            case WM_MOUSEMOVE:
                _view.DragTo(SignedLowWord(lParam), SignedHighWord(lParam));
                return true;

            case WM_LBUTTONUP:
                _view.EndDrag();
                ReleaseCapture();
                return true;

            case WM_CAPTURECHANGED:
                _view.EndDrag();
                return true;

            case WM_MOUSEWHEEL:
                _view.Zoom(SignedHighWord(wParam));
                return true;

            case WM_KEYDOWN when wParam == VK_HOME:
                _view.Reset();
                return true;

            case WM_CLOSE:
                // Ignored (Alt+F4): the viewport closes together with the operator window.
                return true;

            case WM_ERASEBKGND:
                // The swap chain covers the whole client area.
                result = 1;
                return true;

            default:
                return false;
        }
    }
}
