using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Playback;

public sealed class PlaybackStateMachineTests
{
    [Fact]
    public void Open_HidesVideoUntilPlay()
    {
        var machine = new PlaybackStateMachine();

        PlaybackActions actions = machine.Open();
        Assert.Equal(PlaybackState.Opening, machine.State);
        Assert.True(actions.HasFlag(PlaybackActions.OpenMedia));
        Assert.True(actions.HasFlag(PlaybackActions.HideVideo));

        Assert.Equal(PlaybackActions.None, machine.MediaReady());
        Assert.Equal(PlaybackState.Ready, machine.State);
    }

    [Fact]
    public void Play_FromReady_StartsClockAndShowsVideo()
    {
        PlaybackStateMachine machine = ReadyMachine();

        Assert.Equal(PlaybackActions.StartClock | PlaybackActions.ShowVideo, machine.Play());
        Assert.Equal(PlaybackState.Playing, machine.State);
    }

    [Fact]
    public void Play_WhileOpening_StartsAsSoonAsReady()
    {
        var machine = new PlaybackStateMachine();
        machine.Open();

        Assert.Equal(PlaybackActions.None, machine.Play());
        Assert.True(machine.PlayPending);
        Assert.Equal(PlaybackActions.StartClock | PlaybackActions.ShowVideo, machine.MediaReady());
        Assert.Equal(PlaybackState.Playing, machine.State);
    }

    [Fact]
    public void Pause_WhileOpening_CancelsThePendingPlay()
    {
        var machine = new PlaybackStateMachine();
        machine.Open();
        machine.Play();
        machine.Pause();

        Assert.Equal(PlaybackActions.None, machine.MediaReady());
        Assert.Equal(PlaybackState.Ready, machine.State);
    }

    [Fact]
    public void PauseAndResume()
    {
        PlaybackStateMachine machine = ReadyMachine();
        machine.Play();

        Assert.Equal(PlaybackActions.PauseClock, machine.Pause());
        Assert.Equal(PlaybackState.Paused, machine.State);
        Assert.Equal(PlaybackActions.None, machine.Pause());
        Assert.Equal(PlaybackActions.ResumeClock, machine.Play());
        Assert.Equal(PlaybackState.Playing, machine.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_GoesBackToTheBeginningAndShowsBlack(bool paused)
    {
        PlaybackStateMachine machine = ReadyMachine();
        machine.Play();
        if (paused)
        {
            machine.Pause();
        }

        PlaybackActions actions = machine.Stop();
        Assert.Equal(PlaybackState.Opening, machine.State);
        Assert.True(actions.HasFlag(PlaybackActions.RestartMedia));
        Assert.True(actions.HasFlag(PlaybackActions.HideVideo));
        Assert.True(actions.HasFlag(PlaybackActions.StopClock));

        Assert.Equal(PlaybackActions.None, machine.MediaReady());
        Assert.Equal(PlaybackState.Ready, machine.State);
    }

    [Fact]
    public void Stop_WhenReady_DoesNothing()
    {
        PlaybackStateMachine machine = ReadyMachine();
        Assert.Equal(PlaybackActions.None, machine.Stop());
        Assert.Equal(PlaybackState.Ready, machine.State);
    }

    [Fact]
    public void End_HoldsTheLastFrame_AndPlayStartsAgain()
    {
        PlaybackStateMachine machine = ReadyMachine();
        machine.Play();

        Assert.Equal(PlaybackActions.PauseClock, machine.EndReached());
        Assert.Equal(PlaybackState.Ended, machine.State);

        PlaybackActions actions = machine.Play();
        Assert.True(actions.HasFlag(PlaybackActions.RestartMedia));
        Assert.Equal(PlaybackState.Opening, machine.State);
        Assert.Equal(PlaybackActions.StartClock | PlaybackActions.ShowVideo, machine.MediaReady());
    }

    [Fact]
    public void EndReached_OnlyWhilePlaying()
    {
        PlaybackStateMachine machine = ReadyMachine();
        machine.Play();
        machine.Pause();

        Assert.Equal(PlaybackActions.None, machine.EndReached());
        Assert.Equal(PlaybackState.Paused, machine.State);
    }

    [Fact]
    public void Failure_HidesVideo_AndTransportIsIgnoredAfterwards()
    {
        PlaybackStateMachine machine = ReadyMachine();
        machine.Play();

        Assert.True(machine.MediaFailed().HasFlag(PlaybackActions.HideVideo));
        Assert.Equal(PlaybackState.Failed, machine.State);
        Assert.Equal(PlaybackActions.None, machine.Play());
        Assert.Equal(PlaybackActions.None, machine.Stop());
    }

    [Fact]
    public void WithoutMedia_TransportDoesNothing()
    {
        var machine = new PlaybackStateMachine();
        Assert.Equal(PlaybackActions.None, machine.Play());
        Assert.Equal(PlaybackActions.None, machine.Pause());
        Assert.Equal(PlaybackActions.None, machine.Stop());
        Assert.Equal(PlaybackState.NoMedia, machine.State);
    }

    private static PlaybackStateMachine ReadyMachine()
    {
        var machine = new PlaybackStateMachine();
        machine.Open();
        machine.MediaReady();
        return machine;
    }
}
