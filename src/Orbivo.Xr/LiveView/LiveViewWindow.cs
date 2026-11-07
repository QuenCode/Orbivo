using System.ComponentModel;
using System.Runtime.InteropServices;
using static Orbivo.Xr.DebugViewport.NativeMethods;

namespace Orbivo.Xr.LiveView;

/// <summary>
/// The child window the live view is presented into, embedded in the operator window (WPF <c>HwndHost</c>).
/// It is created and destroyed on the UI thread; the presenter only draws into it. Mouse clicks go through to the
/// operator window, the window never takes the keyboard focus.
/// </summary>
public static unsafe class LiveViewWindow
{
    private const string ClassName = "OrbivoLiveView";

    private static bool s_registered;

    /// <summary>Creates the child window. UI thread.</summary>
    /// <param name="parent">Window handle of the WPF host.</param>
    public static nint CreateChild(nint parent, int width, int height)
    {
        nint instance = GetModuleHandleW(null);
        if (!s_registered)
        {
            Register(instance);
            s_registered = true;
        }

        nint handle = CreateWindowExW(
            0,
            ClassName,
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0,
            0,
            Math.Max(1, width),
            Math.Max(1, height),
            parent,
            0,
            instance,
            0);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowExW for the live view failed.");
        }

        return handle;
    }

    /// <summary>Destroys the child window. UI thread; the presenter must be stopped before.</summary>
    public static void Destroy(nint handle)
    {
        if (handle != 0)
        {
            DestroyWindow(handle);
        }
    }

    private static void Register(nint instance)
    {
        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProcedure,
                hInstance = instance,
                hCursor = LoadCursorW(0, IDC_ARROW),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = className,
            };

            if (RegisterClassExW(windowClass) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw new Win32Exception(error, "RegisterClassExW for the live view failed.");
                }
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        // Clicks pass through to the operator window, so the live view never takes the keyboard focus.
        return message == WM_NCHITTEST ? HTTRANSPARENT : DefWindowProcW(window, message, wParam, lParam);
    }
}
