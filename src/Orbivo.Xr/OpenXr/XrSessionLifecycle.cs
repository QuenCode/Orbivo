using Silk.NET.OpenXR;

namespace Orbivo.Xr.OpenXr;

/// <summary>What the render thread does after a session state change.</summary>
internal enum XrLifecycleAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Call <c>xrBeginSession</c>; frames start.</summary>
    BeginSession,

    /// <summary>Call <c>xrEndSession</c>; frames stop, the session may become ready again later.</summary>
    EndSession,

    /// <summary>Destroy the session and connect again after a pause (runtime closed it or lost it).</summary>
    Reconnect,

    /// <summary>Destroy the session; Orbivo itself asked to exit.</summary>
    Finish,
}

/// <summary>
/// Session state machine of OpenXR (docs/ARCHITECTURE.md, "OpenXR-Lebenszyklus"), without any OpenXR calls,
/// so it can be unit tested. The render thread feeds every <c>XrEventDataSessionStateChanged</c> into it.
/// </summary>
internal sealed class XrSessionLifecycle
{
    public SessionState State { get; private set; } = SessionState.Unknown;

    /// <summary>True between <c>xrBeginSession</c> and <c>xrEndSession</c>: the frame loop runs.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Set by <see cref="RequestExit"/> (the app is closing).</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>Marks that Orbivo called <c>xrRequestExitSession</c>; a later EXITING finishes instead of reconnecting.</summary>
    public void RequestExit()
    {
        ExitRequested = true;
    }

    /// <summary>
    /// Records the new state and returns what to do. <see cref="IsRunning"/> is updated as if the action succeeded.
    /// </summary>
    public XrLifecycleAction OnStateChanged(SessionState state)
    {
        State = state;
        switch (state)
        {
            case SessionState.Ready when !IsRunning && !ExitRequested:
                IsRunning = true;
                return XrLifecycleAction.BeginSession;

            case SessionState.Stopping when IsRunning:
                IsRunning = false;
                return XrLifecycleAction.EndSession;

            case SessionState.Exiting:
                IsRunning = false;
                return ExitRequested ? XrLifecycleAction.Finish : XrLifecycleAction.Reconnect;

            case SessionState.LossPending:
                IsRunning = false;
                return ExitRequested ? XrLifecycleAction.Finish : XrLifecycleAction.Reconnect;

            default:
                // IDLE, SYNCHRONIZED, VISIBLE, FOCUSED: frames continue (or wait) as they are.
                return XrLifecycleAction.None;
        }
    }
}
