using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed partial class SwitchingCoordinatorTests
{
    [Fact]
    public async Task UserControlsAndRestartRemainAvailableAfterConfirmedRecoveryNudge()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController { CanNext = true, CanPrevious = true };
        var endpoint = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
        };
        transport.PlayInvoked = () =>
        {
            if (transport.PlayCallCount >= 2) endpoint.MasterPeakValue = 0.01f;
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var restored = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Confirmed pause-play recovery"));
        await restored.WaitAsync(RecoveryTimeout);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (coordinator.GetUserPlaybackAvailability().IsBusy && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        var availability = coordinator.GetUserPlaybackAvailability();
        Assert.False(availability.IsSafetyHeld);
        Assert.True(availability.CanNext);
        Assert.True(availability.CanPrevious);
        Assert.True(availability.CanPause);
        Assert.Equal(2, transport.PauseCallCount);
        Assert.Equal(2, transport.PlayCallCount);
        Assert.True(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        Assert.False(transport.IsPlaying);
        Assert.Equal(3, transport.PauseCallCount);
        Assert.Equal(2, transport.PlayCallCount);
    }

    [Fact]
    public async Task UserPlayAcceptedWithoutObservedPlayingDoesNotAuthorizeRestart()
    {
        var transport = new TestMediaTransportController
        {
            IsPlaying = false,
            PlayHandler = _ => Task.FromResult(true),
        };
        await using var coordinator = CreateCoordinator(new TestTrackSource(), transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        Assert.True((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Play, CancellationToken.None)).Succeeded);
        Assert.False(transport.IsPlaying);
        Assert.False(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        Assert.True(coordinator.GetUserPlaybackAvailability().IsSafetyHeld);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UserPauseAndManualRestartDoNotReleaseMuteWhileEarlierPlayMayExecute(bool playCommandCompleted, bool manualRestart)
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { CanNext = true, CanPrevious = true };
        var endpoint = PauseEndpoint();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PlayHandler = token =>
        {
            // Simulate Apple Music accepting a Play into its remote queue while it still
            // reports Paused. Canceling this local wait cannot withdraw that remote Play.
            dispatched.TrySetResult();
            return playCommandCompleted ? Task.FromResult(true) : remoteCompletion.Task.WaitAsync(token);
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Late queued warmup Play"));
        await dispatched.Task.WaitAsync(PausePathTimeout);
        Assert.False(transport.IsPlaying);
        Assert.True(endpoint.MasterMute);
        var succeeded = manualRestart
            ? await coordinator.PrepareForManualRestartAsync(CancellationToken.None)
            : (await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Pause, CancellationToken.None)).Succeeded;
        Assert.False(succeeded);
        Assert.True(endpoint.MasterMute);
        Assert.Equal(1, transport.PauseCallCount); // Only the original guarded switch pause.
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        // The remote player can begin Playing after the local cancellation completed.
        // It must still be inaudible, with no extra Pause/Play/navigation sent by the tool.
        transport.IsPlaying = true;
        remoteCompletion.TrySetResult(true);
        Assert.True(endpoint.MasterMute);
        Assert.True(coordinator.GetUserPlaybackAvailability().IsSafetyHeld);
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Next, CancellationToken.None)).Succeeded);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(1, transport.PlayCallCount);
    }

    [Fact]
    public async Task UserControlsReadCapabilitiesAndNeverDispatchAnUnavailableNavigation()
    {
        var transport = new TestMediaTransportController { IsPlaying = false, CanPrevious = true };
        await using var coordinator = CreateCoordinator(new TestTrackSource(), transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        var state = coordinator.GetUserPlaybackAvailability();
        Assert.True(state.CanPlay);
        Assert.False(state.CanPause);
        Assert.True(state.CanPrevious);
        Assert.False(state.CanNext);
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Next, CancellationToken.None)).Succeeded);
        Assert.True((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Previous, CancellationToken.None)).Succeeded);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(1, transport.SkipPreviousCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.PlayCallCount);
    }

    [Fact]
    public async Task UserPauseCancelsResolutionAndDoesNotResumeOrNavigate()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController { CanNext = true, CanPrevious = true };
        var endpoint = PauseEndpoint();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IFormatResolver resolver = new DelegateResolver(async (track, token) =>
        {
            if (track.Title == "Blocked user pause")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return CreateResolvedFormat(CurrentFormat);
        });
        await using var coordinator = CreateCoordinator(source, transport, endpoint, resolver);
        SwitchingStatus? lastStatus = null;
        coordinator.StatusChanged += (_, status) => lastStatus = status;
        var seeded = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Seed target for user pause"));
        await seeded.WaitAsync(PausePathTimeout);
        source.RaiseTrackChanged(CreateTrack("Blocked user pause"));
        await entered.Task.WaitAsync(PausePathTimeout);
        Assert.True(endpoint.MasterMute);
        transport.PauseAccepted = () => Assert.True(endpoint.MasterMute);
        var busy = coordinator.GetUserPlaybackAvailability();
        Assert.True(busy.CanPause);
        Assert.False(busy.CanNext);
        Assert.False(busy.CanPrevious);
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Next, CancellationToken.None)).Succeeded);
        Assert.True((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Pause, CancellationToken.None)).Succeeded);
        Assert.False(transport.IsPlaying);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.False(endpoint.MasterMute);
        Assert.NotNull(lastStatus);
        Assert.Equal("Resolver: Paused by user", lastStatus.ResolverStatusText);
        Assert.Equal("Blocked user pause", lastStatus.Track?.Title);
        Assert.Equal(CurrentFormat, lastStatus.AppliedFormat);
        Assert.Null(lastStatus.FailureReason);
        Assert.Equal(RecoveryCategory.None, lastStatus.Recovery);
        Assert.Equal(SwitchPhase.Idle, MainWindowViewModel.DerivePhase(lastStatus));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserPausedResolutionReentersProcessingOnceAfterEitherPlaySource(bool useTool)
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutions = 0;
        IFormatResolver resolver = new DelegateResolver(async (_, token) =>
        {
            if (Interlocked.Increment(ref resolutions) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return CreateResolvedFormat(CurrentFormat);
        });
        await using var coordinator = CreateCoordinator(source, transport, PauseEndpoint(), resolver);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Resume after UI pause"));
        await entered.Task.WaitAsync(PausePathTimeout);
        Assert.True((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Pause, CancellationToken.None)).Succeeded);
        var resumed = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");
        if (useTool)
            Assert.True((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Play, CancellationToken.None)).Succeeded);
        else
            transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await resumed.WaitAsync(PausePathTimeout);
        Assert.Equal(2, resolutions);
        Assert.Equal(useTool ? 1 : 0, transport.PlayCallCount);
        Assert.Equal(1, transport.PauseCallCount);
    }

    [Fact]
    public async Task UserPauseFailureKeepsPlaybackHeldAndRejectsPlayOrNavigation()
    {
        var transport = new TestMediaTransportController { PauseResult = false, CanNext = true, CanPrevious = true };
        await using var coordinator = CreateCoordinator(new TestTrackSource(), transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Pause, CancellationToken.None)).Succeeded);
        var state = coordinator.GetUserPlaybackAvailability();
        Assert.True(state.IsSafetyHeld);
        Assert.True(state.CanPause);
        Assert.False(state.CanPlay);
        Assert.False(state.CanNext);
        Assert.False(state.CanPrevious);
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Next, CancellationToken.None)).Succeeded);
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Play, CancellationToken.None)).Succeeded);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
    }

    [Fact]
    public async Task UserNavigationTimeoutDoesNotRetryAndRetainsGlobalSafetyHold()
    {
        var transport = new TestMediaTransportController
        {
            CanNext = true,
            CanPrevious = true,
            NextHandler = _ => throw new TimeoutException("Uncertain command result"),
        };
        await using var coordinator = CreateCoordinator(new TestTrackSource(), transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Next, CancellationToken.None)).Succeeded);
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Next, CancellationToken.None)).Succeeded);
        Assert.Equal(1, transport.SkipNextCallCount);
        Assert.True(coordinator.GetUserPlaybackAvailability().IsSafetyHeld);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, transport.PlayCallCount);
    }

    [Fact]
    public async Task UserControlsRejectADuplicateClickInsteadOfQueueingIt()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new TestMediaTransportController { CanNext = true, CanPrevious = true, NextHandler = _ => pending.Task };
        await using var coordinator = CreateCoordinator(new TestTrackSource(), transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        var first = coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Next, CancellationToken.None);
        Assert.False((await coordinator.ExecuteUserPlaybackAsync(UserPlaybackAction.Previous, CancellationToken.None)).Succeeded);
        Assert.Equal(1, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        pending.SetResult(true);
        Assert.True((await first).Succeeded);
    }

    [Fact]
    public async Task ManualRestartPreparationStopsAllUserPlaybackActions()
    {
        var transport = new TestMediaTransportController { IsPlaying = false, CanNext = true, CanPrevious = true };
        await using var coordinator = CreateCoordinator(new TestTrackSource(), transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        Assert.True(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        foreach (var action in Enum.GetValues<UserPlaybackAction>())
            Assert.False((await coordinator.ExecuteUserPlaybackAsync(action, CancellationToken.None)).Succeeded);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
    }

    [Fact]
    public async Task PlaybackViewModelOpenUsesOnlyOpenAndRestartBusyDisablesAllCommands()
    {
        var process = new OpenOnlyProcessController();
        var transport = new TestMediaTransportController { CanNext = true, CanPrevious = true };
        await using var coordinator = CreateCoordinator(new TestTrackSource(), transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        using var vm = new PlaybackControlsViewModel();
        Assert.False(vm.OpenAppleMusicCommand.CanExecute(null));
        Assert.False(vm.TogglePlaybackCommand.CanExecute(null));
        vm.Configure(coordinator, process);
        Assert.True(vm.TogglePlaybackCommand.CanExecute(null));
        vm.OpenAppleMusicCommand.Execute(null);
        Assert.Equal(1, process.OpenCalls);
        Assert.Equal(0, process.RestartCalls);
        Assert.Equal(0, transport.PlayCallCount);
        vm.SetRestartBusy(true);
        Assert.False(vm.TogglePlaybackCommand.CanExecute(null));
        Assert.False(vm.PreviousTrackCommand.CanExecute(null));
        Assert.False(vm.NextTrackCommand.CanExecute(null));
        Assert.False(vm.OpenAppleMusicCommand.CanExecute(null));
    }

    private sealed class OpenOnlyProcessController : IAppleMusicProcessController
    {
        public int OpenCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public Task<bool> TryOpenAsync(CancellationToken token) { OpenCalls++; return Task.FromResult(true); }
        public Task<bool> TryRestartAsync(CancellationToken token) { RestartCalls++; return Task.FromResult(false); }
    }
}
