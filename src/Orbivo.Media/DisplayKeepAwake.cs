using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Orbivo.Media;

/// <summary>
/// Keeps the monitor on and the PC awake while a video plays or is paused, like other video players do
/// (<c>SetThreadExecutionState</c>, kernel32). Changes no Windows setting; the request ends with <see cref="Update"/>
/// (false) or when the thread ends.
/// </summary>
/// <remarks>
/// When Windows switches a monitor off or on, the GPU holds up video decoding for 2–5 s on the development PC; the sound
/// and the clock run on, the headset would show a frozen picture (docs/DECISIONS.md, M5). The request belongs to the
/// calling thread, so only the player control thread uses one instance.
/// </remarks>
internal sealed partial class DisplayKeepAwake(ILogger logger)
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private bool _active;

    /// <summary>Player control thread: requests or releases. Does nothing if the wish has not changed.</summary>
    public void Update(bool keepAwake)
    {
        if (keepAwake == _active)
        {
            return;
        }

        _active = keepAwake;
        uint flags = keepAwake ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous;
        if (SetThreadExecutionState(flags) == 0)
        {
            logger.LogWarning("Windows refused to keep the display on during playback (SetThreadExecutionState).");
            return;
        }

        logger.LogInformation(keepAwake ? "Display and system kept awake while the video plays." : "Display and system may sleep again.");
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
