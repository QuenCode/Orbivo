using Orbivo.Xr.OpenXr;
using Silk.NET.OpenXR;

namespace Orbivo.Xr.Tests.OpenXr;

public sealed class XrSessionLifecycleTests
{
    [Fact]
    public void NormalRun_BeginsAtReady_AndEndsAtStopping()
    {
        var lifecycle = new XrSessionLifecycle();

        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Idle));
        Assert.False(lifecycle.IsRunning);

        Assert.Equal(XrLifecycleAction.BeginSession, lifecycle.OnStateChanged(SessionState.Ready));
        Assert.True(lifecycle.IsRunning);

        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Synchronized));
        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Visible));
        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Focused));
        Assert.True(lifecycle.IsRunning);

        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Visible));
        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Synchronized));
        Assert.Equal(XrLifecycleAction.EndSession, lifecycle.OnStateChanged(SessionState.Stopping));
        Assert.False(lifecycle.IsRunning);

        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Idle));
        Assert.Equal(SessionState.Idle, lifecycle.State);
    }

    [Fact]
    public void ReadyAgainAfterStopping_BeginsAgain()
    {
        // Varjo Base can pause a session (headset taken off, another app in front) and resume it.
        var lifecycle = new XrSessionLifecycle();
        lifecycle.OnStateChanged(SessionState.Ready);
        lifecycle.OnStateChanged(SessionState.Stopping);
        lifecycle.OnStateChanged(SessionState.Idle);

        Assert.Equal(XrLifecycleAction.BeginSession, lifecycle.OnStateChanged(SessionState.Ready));
        Assert.True(lifecycle.IsRunning);
    }

    [Fact]
    public void ReadyTwice_BeginsOnlyOnce()
    {
        var lifecycle = new XrSessionLifecycle();
        lifecycle.OnStateChanged(SessionState.Ready);

        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Ready));
    }

    [Fact]
    public void StoppingWithoutRunning_DoesNotEnd()
    {
        var lifecycle = new XrSessionLifecycle();

        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Stopping));
    }

    [Theory]
    [InlineData(SessionState.Exiting)]
    [InlineData(SessionState.LossPending)]
    public void RuntimeClosesSession_Reconnects(SessionState state)
    {
        var lifecycle = new XrSessionLifecycle();
        lifecycle.OnStateChanged(SessionState.Ready);

        Assert.Equal(XrLifecycleAction.Reconnect, lifecycle.OnStateChanged(state));
        Assert.False(lifecycle.IsRunning);
    }

    [Theory]
    [InlineData(SessionState.Exiting)]
    [InlineData(SessionState.LossPending)]
    public void AfterOwnExitRequest_Finishes(SessionState state)
    {
        var lifecycle = new XrSessionLifecycle();
        lifecycle.OnStateChanged(SessionState.Ready);
        lifecycle.RequestExit();

        Assert.Equal(XrLifecycleAction.EndSession, lifecycle.OnStateChanged(SessionState.Stopping));
        Assert.Equal(XrLifecycleAction.Finish, lifecycle.OnStateChanged(state));
    }

    [Fact]
    public void AfterOwnExitRequest_DoesNotBeginAgain()
    {
        var lifecycle = new XrSessionLifecycle();
        lifecycle.RequestExit();

        Assert.Equal(XrLifecycleAction.None, lifecycle.OnStateChanged(SessionState.Ready));
        Assert.False(lifecycle.IsRunning);
    }
}
