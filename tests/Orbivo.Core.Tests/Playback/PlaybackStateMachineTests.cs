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

    [Theory]
    [InlineData(PlaybackState.Ready)]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Paused)]
    public void Seek_KeepsTheTransportState(PlaybackState state)
    {
        PlaybackStateMachine machine = MachineIn(state);

        Assert.Equal(PlaybackActions.SeekMedia, machine.Seek());
        Assert.Equal(state, machine.State);
        Assert.True(machine.Seeking);

        PlaybackActions completed = machine.SeekCompleted();
        Assert.Equal(state == PlaybackState.Playing ? PlaybackActions.ResumeClock : PlaybackActions.None, completed);
        Assert.Equal(state, machine.State);
        Assert.False(machine.Seeking);
    }

    [Fact]
    public void Seek_AfterTheEnd_Pauses()
    {
        PlaybackStateMachine machine = MachineIn(PlaybackState.Playing);
        machine.EndReached();

        Assert.Equal(PlaybackActions.SeekMedia, machine.Seek());
        Assert.Equal(PlaybackState.Paused, machine.State);
        Assert.Equal(PlaybackActions.None, machine.SeekCompleted());
        Assert.Equal(PlaybackActions.ResumeClock, machine.Play());
    }

    [Fact]
    public void Seek_WhileOpening_IsPartOfOpening()
    {
        var machine = new PlaybackStateMachine();
        machine.Open();

        Assert.Equal(PlaybackActions.SeekMedia, machine.Seek());
        Assert.False(machine.Seeking);
        Assert.Equal(PlaybackState.Opening, machine.State);
    }

    [Fact]
    public void Seek_WithoutMediaOrAfterFailure_DoesNothing()
    {
        var machine = new PlaybackStateMachine();
        Assert.Equal(PlaybackActions.None, machine.Seek());

        machine.Open();
        machine.MediaFailed();
        Assert.Equal(PlaybackActions.None, machine.Seek());
        Assert.False(machine.Seeking);
    }

    [Fact]
    public void PlayDuringSeek_StartsTheClockOnlyWhenTheSeekIsDone()
    {
        PlaybackStateMachine machine = ReadyMachine();
        machine.Seek();

        Assert.Equal(PlaybackActions.ShowVideo, machine.Play());
        Assert.Equal(PlaybackState.Playing, machine.State);
        Assert.Equal(PlaybackActions.ResumeClock, machine.SeekCompleted());
    }

    [Fact]
    public void PauseDuringSeek_KeepsTheClockStopped()
    {
        PlaybackStateMachine machine = MachineIn(PlaybackState.Playing);
        machine.Seek();

        Assert.Equal(PlaybackActions.None, machine.Pause());
        Assert.Equal(PlaybackState.Paused, machine.State);
        Assert.Equal(PlaybackActions.None, machine.SeekCompleted());
    }

    [Fact]
    public void EndReached_IsIgnoredDuringASeek()
    {
        PlaybackStateMachine machine = MachineIn(PlaybackState.Playing);
        machine.Seek();

        Assert.Equal(PlaybackActions.None, machine.EndReached());
        Assert.Equal(PlaybackState.Playing, machine.State);
    }

    [Fact]
    public void StopDuringSeek_EndsTheSeek()
    {
        PlaybackStateMachine machine = MachineIn(PlaybackState.Paused);
        machine.Seek();

        Assert.True(machine.Stop().HasFlag(PlaybackActions.RestartMedia));
        Assert.False(machine.Seeking);
        Assert.Equal(PlaybackActions.None, machine.SeekCompleted());
    }

    [Theory]
    [InlineData(PlaybackState.Opening)]
    [InlineData(PlaybackState.Ready)]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Ended)]
    [InlineData(PlaybackState.Failed)]
    public void Close_FromAnyState_ClosesTheFileAndShowsBlack(PlaybackState state)
    {
        PlaybackStateMachine machine = MachineIn(state);
        if (state == PlaybackState.Opening)
        {
            machine.Play();
        }

        PlaybackActions actions = machine.Close();

        Assert.Equal(PlaybackActions.CloseMedia | PlaybackActions.HideVideo | PlaybackActions.StopClock, actions);
        Assert.Equal(PlaybackState.NoMedia, machine.State);
        Assert.False(machine.PlayPending);
        Assert.False(machine.Seeking);
    }

    [Fact]
    public void Close_WithoutMedia_DoesNothing()
    {
        var machine = new PlaybackStateMachine();
        Assert.Equal(PlaybackActions.None, machine.Close());
        Assert.Equal(PlaybackState.NoMedia, machine.State);
    }

    [Fact]
    public void Close_DuringASeek_EndsTheSeek()
    {
        PlaybackStateMachine machine = ReadyMachine();
        machine.Play();
        machine.Seek();
        machine.Close();

        Assert.False(machine.Seeking);
        Assert.Equal(PlaybackActions.None, machine.SeekCompleted());
        Assert.Equal(PlaybackActions.None, machine.MediaReady());
        Assert.Equal(PlaybackState.NoMedia, machine.State);
    }

    private static PlaybackStateMachine MachineIn(PlaybackState state)
    {
        var machine = new PlaybackStateMachine();
        machine.Open();
        switch (state)
        {
            case PlaybackState.Ready:
                machine.MediaReady();
                break;
            case PlaybackState.Playing:
                machine.MediaReady();
                machine.Play();
                break;
            case PlaybackState.Paused:
                machine.MediaReady();
                machine.Play();
                machine.Pause();
                break;
            case PlaybackState.Ended:
                machine.MediaReady();
                machine.Play();
                machine.EndReached();
                break;
            case PlaybackState.Failed:
                machine.MediaFailed();
                break;
        }

        Assert.Equal(state, machine.State);
        return machine;
    }

    private static PlaybackStateMachine ReadyMachine()
    {
        var machine = new PlaybackStateMachine();
        machine.Open();
        machine.MediaReady();
        return machine;
    }
}
