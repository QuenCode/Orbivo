using System.Runtime.InteropServices;

namespace Orbivo.Audio.Output;

/// <summary>
/// Registers the calling thread with the Multimedia Class Scheduler ("Pro Audio"), so Windows runs it on time even
/// under load. avrt.dll is part of Windows.
/// </summary>
internal sealed partial class Mmcss : IDisposable
{
    private IntPtr _handle;

    private Mmcss(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>Registers the calling thread; null if Windows refused (the error code is returned).</summary>
    public static Mmcss? Enter(string task, out int error)
    {
        uint taskIndex = 0;
        IntPtr handle = AvSetMmThreadCharacteristics(task, ref taskIndex);
        error = handle == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
        return handle == IntPtr.Zero ? null : new Mmcss(handle);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            AvRevertMmThreadCharacteristics(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
