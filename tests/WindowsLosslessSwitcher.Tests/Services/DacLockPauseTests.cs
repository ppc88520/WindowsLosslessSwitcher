using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed partial class SwitchingCoordinatorTests
{
    [Fact]
    public async Task DacLock_StartupPausedNeverCensusesResolvesProbesOrControlsHealthyPlayback()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw);
        var resolves = 0;
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint, new DelegateResolver((_, _) =>
        { resolves++; return Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat)); }));
        coordinator.InitializeOutputLockPaused(true);
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        var observed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "DAC lock paused" && status.Track is not null);
        source.RaiseTrackChanged(CreateTrack("Playing through another output"));
        await observed.WaitAsync(DefaultTimeout);
        for (var i = 0; i < 500; i++) Assert.False(coordinator.RefreshAudioOutputSafety()!.IsReady);
        Assert.Empty(coordinator.GetCurrentTargetDeviceCapabilities(true).SupportedFormats);
        Assert.False((await coordinator.RestoreOriginalFormatAsync("monitor", "Monitor", CurrentFormat, CancellationToken.None)).Succeeded);
        Assert.Equal(0, endpoint.EnsureCalls);
        Assert.Equal(0, endpoint.ProbeCalls);
        Assert.Equal(0, resolves);
        Assert.Equal(0, raw.TryApplyFormatCallCount);
        Assert.Empty(raw.MuteCalls);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.True(transport.IsPlaying);
    }

    [Fact]
    public async Task DacLock_HealthyPlaybackPauseDoesNotPauseThePlayerAndMissingDacResumeStaysPaused()
    {
        var transport = new TestMediaTransportController();
        var endpoint = new GuardedTestEndpoint(PauseEndpoint());
        await using var coordinator = CreateDacCoordinator(new TestTrackSource(), transport, endpoint, PauseResolver(CurrentFormat));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        var pause = coordinator.SetOutputLockPausedAsync(true, CancellationToken.None);
        Assert.True(coordinator.IsDacLockPaused); // the caller's synchronous prefix, before awaiting.
        Assert.True(endpoint.IsRoutingPaused);
        Assert.True((await pause).Succeeded);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.True(transport.IsPlaying);
        var censuses = endpoint.EnsureCalls;
        endpoint.Ready = false;
        for (var i = 0; i < 20; i++) coordinator.RefreshAudioOutputSafety();
        Assert.Equal(censuses, endpoint.EnsureCalls);
        var missing = await coordinator.SetOutputLockPausedAsync(false, CancellationToken.None);
        Assert.False(missing.Succeeded);
        Assert.True(missing.IsPaused);
        Assert.True(endpoint.IsRoutingPaused);
        endpoint.Ready = true;
        transport.IsPlaying = false;
        Assert.True((await coordinator.SetOutputLockPausedAsync(false, CancellationToken.None)).Succeeded);
        Assert.False(coordinator.IsDacLockPaused);
        Assert.False(endpoint.IsRoutingPaused);
        Assert.Equal(0, transport.PlayCallCount);
    }

    [Fact]
    public async Task DacLock_CancelsResolutionAndReentersOnceOnlyAfterUserPlayback()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolves = 0;
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint, new DelegateResolver(async (_, token) =>
        {
            if (Interlocked.Increment(ref resolves) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            }
            return CreateResolvedFormat(CurrentFormat);
        }));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Paused lock during lookup"));
        await entered.Task.WaitAsync(DefaultTimeout);
        Assert.True((await coordinator.SetOutputLockPausedAsync(true, CancellationToken.None)).Succeeded);
        await canceled.Task.WaitAsync(DefaultTimeout);
        transport.IsPlaying = false;
        Assert.True((await coordinator.SetOutputLockPausedAsync(false, CancellationToken.None)).Succeeded);
        Assert.Equal(1, resolves);
        Assert.Equal(0, transport.PlayCallCount);
        var complete = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");
        transport.IsPlaying = true;
        coordinator.RefreshAudioOutputSafety();
        await complete.WaitAsync(DefaultTimeout);
        await WaitForDacProcessingToStop(coordinator);
        for (var i = 0; i < 5; i++) coordinator.RefreshAudioOutputSafety();
        Assert.Equal(2, resolves);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, raw.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task DacLock_PauseAfterApplyReleasesOwnedMuteWithoutWarmupPlayOrNavigation()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw);
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var requested = new TaskCompletionSource<Task<DacLockChangeResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.ApplyInvoked = () => requested.TrySetResult(coordinator.SetOutputLockPausedAsync(true, CancellationToken.None));
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Pause lock after apply"));
        var operation = await requested.Task.WaitAsync(PausePathTimeout);
        Assert.True((await operation.WaitAsync(DefaultTimeout)).Succeeded);
        Assert.Equal(1, raw.TryApplyFormatCallCount);
        Assert.False(raw.MasterMute);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
    }

    [Fact]
    public async Task DacLock_PauseDuringConfirmedWarmupSettlesPlaybackAndResumeDoesNotPlay()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw);
        var warmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PlayInvoked = () => warmup.TrySetResult();
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Pause lock while warming"));
        await warmup.Task.WaitAsync(PausePathTimeout);
        Assert.True((await coordinator.SetOutputLockPausedAsync(true, CancellationToken.None)).Succeeded);
        Assert.False(raw.MasterMute);
        Assert.False(transport.IsPlaying);
        Assert.Equal(2, transport.PauseCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.True((await coordinator.SetOutputLockPausedAsync(false, CancellationToken.None)).Succeeded);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.True(coordinator.GetUserPlaybackAvailability().CanPlay);
    }

    [Fact]
    public async Task DacLock_UnconfirmedQueuedPlayKeepsOwnedMuteAndRefusesResume()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PlayHandler = _ => { dispatched.TrySetResult(); return Task.FromResult(true); };
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Unconfirmed queued Play"));
        await dispatched.Task.WaitAsync(PausePathTimeout);
        var pause = await coordinator.SetOutputLockPausedAsync(true, CancellationToken.None);
        Assert.False(pause.Succeeded);
        Assert.True(pause.IsPaused);
        Assert.True(endpoint.IsRoutingPaused);
        Assert.True(raw.MasterMute);
        Assert.False((await coordinator.SetOutputLockPausedAsync(false, CancellationToken.None)).Succeeded);
        Assert.True(raw.MasterMute);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
    }

    [Fact]
    public async Task DacLock_PausedToolOnlyRestartDoesNotPauseHealthySpeakerPlayback()
    {
        var transport = new TestMediaTransportController();
        var endpoint = new GuardedTestEndpoint(PauseEndpoint());
        await using var coordinator = CreateDacCoordinator(new TestTrackSource(), transport, endpoint, PauseResolver(CurrentFormat));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        Assert.True((await coordinator.SetOutputLockPausedAsync(true, CancellationToken.None)).Succeeded);
        Assert.True(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        Assert.True(transport.IsPlaying);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
    }

    [Fact]
    public async Task DacLock_DuplicateTransitionIsRejectedRatherThanQueued()
    {
        var endpoint = new GuardedTestEndpoint(PauseEndpoint());
        var transport = new TestMediaTransportController();
        await using var coordinator = CreateDacCoordinator(new TestTrackSource(), transport, endpoint, PauseResolver(CurrentFormat));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        Assert.True((await coordinator.SetOutputLockPausedAsync(true, CancellationToken.None)).Succeeded);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        endpoint.OnEnsureReady = () =>
        {
            endpoint.OnEnsureReady = null;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        };
        var resume = coordinator.SetOutputLockPausedAsync(false, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(DefaultTimeout);
            var duplicate = await coordinator.SetOutputLockPausedAsync(true, CancellationToken.None);
            Assert.False(duplicate.Succeeded);
            Assert.Contains("already in progress", duplicate.Message);
        }
        finally { release.Set(); }
        Assert.True((await resume.WaitAsync(DefaultTimeout)).Succeeded);
        Assert.False(coordinator.IsDacLockPaused);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.PauseCallCount);
    }

    [Fact]
    public async Task DacLock_ShutdownDuringResumeKeepsRoutingPaused()
    {
        var endpoint = new GuardedTestEndpoint(PauseEndpoint());
        var transport = new TestMediaTransportController();
        await using var coordinator = CreateDacCoordinator(new TestTrackSource(), transport, endpoint, PauseResolver(CurrentFormat));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        Assert.True((await coordinator.SetOutputLockPausedAsync(true, CancellationToken.None)).Succeeded);
        endpoint.OnEnsureReady = coordinator.RequestShutdown;
        var resume = await coordinator.SetOutputLockPausedAsync(false, CancellationToken.None);
        Assert.False(resume.Succeeded);
        Assert.True(resume.IsPaused);
        Assert.True(endpoint.IsRoutingPaused);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.PauseCallCount);
    }
}
