using WindowsLosslessSwitcher.Models;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed partial class SwitchingCoordinatorTests
{
    [Fact]
    public async Task DeferredRateChange_PausingAfterMetadataRetryReleasesOwnedMuteAndStillAllowsLaterResume()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Paused while resumed metadata catches up"));
        await deferred.WaitAsync(PausePathTimeout);
        Assert.Empty(endpoint.MuteCalls);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);

        var resumeVerifications = 0;
        transport.VerifyHandler = (_, _) => Task.FromResult(++resumeVerifications != 1);
        var retryQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedAfterRetry = false;
        EventHandler<WindowsLosslessSwitcher.Services.SwitchingStatus> onStatus = (_, status) =>
        {
            if (pausedAfterRetry || status.ResolverStatusText != "Switching: Waiting for user playback" ||
                status.FailureReason?.Contains("waiting for a fresh same-track check", StringComparison.OrdinalIgnoreCase) != true)
                return;
            pausedAfterRetry = true;
            Assert.True(endpoint.MasterMute);
            // Model a user pause after the retry was queued, with the Paused event lost.
            // The observer must release only this deferred-operation mute without Play.
            transport.IsPlaying = false;
            retryQueued.TrySetResult();
        };
        coordinator.StatusChanged += onStatus;
        try
        {
            transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
            await retryQueued.Task.WaitAsync(PausePathTimeout);
            var releaseDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
            while (endpoint.MasterMute == true && DateTimeOffset.UtcNow < releaseDeadline)
                await Task.Delay(20);

            Assert.True(pausedAfterRetry);
            Assert.False(endpoint.MasterMute);
            Assert.False(transport.IsPlaying);
            Assert.Equal(new[] { true, false }, endpoint.MuteCalls);
            Assert.Equal(0, endpoint.TryApplyFormatCallCount);
            Assert.Equal(0, transport.PauseCallCount);
            Assert.Equal(0, transport.PlayCallCount);
            Assert.Equal(0, transport.GuardedRestartCallCount);
            Assert.Equal(0, transport.SkipNextCallCount);
            Assert.Equal(0, transport.SkipPreviousCallCount);

            var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
            transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
            await completed.WaitAsync(TimeSpan.FromSeconds(40));

            Assert.Equal(1, endpoint.TryApplyFormatCallCount);
            Assert.Equal(1, transport.GuardedRestartCallCount);
            Assert.Equal(2, transport.PauseCallCount);
            Assert.Equal(2, transport.PlayCallCount);
            Assert.False(endpoint.MasterMute);
        }
        finally
        {
            coordinator.StatusChanged -= onStatus;
        }
    }

    [Fact]
    public async Task DeferredRateChange_UserPlayWithoutANotificationIsObservedOnceByTheWatchdog()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        endpoint.ApplyInvoked = () =>
        {
            Assert.False(transport.IsPlaying);
            Assert.True(endpoint.MasterMute);
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        var track = CreateTrack("User resumes without a media event");
        source.RaiseTrackChanged(track);
        await deferred.WaitAsync(PausePathTimeout);

        // The observer must never manufacture a Play while the user remains paused.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);

        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
        transport.IsPlaying = true; // deliberately do not raise PlaybackStateChanged
        await completed.WaitAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(track.UniqueKey, transport.LastRestartExpectedTrack?.UniqueKey);
        Assert.Equal(2, transport.PauseCallCount);
        Assert.Equal(2, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.False(endpoint.MasterMute);
    }

    [Fact]
    public async Task PreviousUnavailable_ReentrantNewTrackNotificationCanClearOnlyTheRecoverableHold()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport
        {
            GuardedRestartResult = new(false, true, "Previous remained unavailable; no navigation was dispatched.",
                PreviousRestartFailureKind.PreviousUnavailable, NavigationDispatched: false),
        };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var replacement = CreateTrack("Immediate new selection after the unavailable status");
        var triggered = false;
        EventHandler<WindowsLosslessSwitcher.Services.SwitchingStatus> onStatus = (_, status) =>
        {
            if (triggered || status.ResolverStatusText != "Switching: Restart unavailable") return;
            triggered = true;
            transport.IsPlaying = true;
            // Exercise the synchronous StatusChanged boundary while the old guard is unwinding.
            source.RaiseTrackChanged(replacement);
        };
        coordinator.StatusChanged += onStatus;
        try
        {
            var processed = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == replacement.UniqueKey &&
                status.FailureReason == "Target format already active.");
            await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
            source.RaiseTrackChanged(CreateTrack("Unavailable original song"));
            await processed.WaitAsync(PausePathTimeout);

            var unmuteDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            while (endpoint.MasterMute == true && DateTimeOffset.UtcNow < unmuteDeadline)
                await Task.Delay(10);
            Assert.True(triggered);
            Assert.Equal(1, endpoint.TryApplyFormatCallCount);
            Assert.Equal(1, transport.GuardedRestartCallCount);
            Assert.Equal(1, transport.PlayCallCount); // old track's muted warmup, never its audible restart
            Assert.Equal(0, transport.SkipNextCallCount);
            Assert.Equal(0, transport.SkipPreviousCallCount);
            Assert.False(endpoint.MasterMute);
        }
        finally
        {
            coordinator.StatusChanged -= onStatus;
        }
    }

    [Fact]
    public async Task DeferredRateChange_UserPlayStartsOneVerifiedSwitchDespiteDuplicateNotifications()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        endpoint.ApplyInvoked = () =>
        {
            Assert.False(transport.IsPlaying);
            Assert.True(endpoint.MasterMute);
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        var track = CreateTrack("Deferred current song");
        source.RaiseTrackChanged(track);
        await deferred.WaitAsync(PausePathTimeout);

        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Paused);
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Paused);
        // A stale Playing event is not permission to switch while the fresh state is Paused.
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing, updateState: false);
        await Task.Delay(100);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);

        var duplicateDeferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        source.RaiseTrackChanged(track);
        await duplicateDeferred.WaitAsync(PausePathTimeout);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PlayCallCount);

        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        // Duplicate notifications must not mutate the fake player's actual pause state.
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing, updateState: false);
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing, updateState: false);
        await completed.WaitAsync(PausePathTimeout);

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(track.UniqueKey, transport.LastRestartExpectedTrack?.UniqueKey);
        Assert.Equal(2, transport.PauseCallCount);
        Assert.Equal(2, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.False(endpoint.MasterMute);
    }

    [Fact]
    public async Task DeferredRateChange_DifferentTrackCancelsTheOldPendingFormat()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        var resolver = new DelegateResolver((track, _) => Task.FromResult<ResolvedAudioFormat?>(
            CreateResolvedFormat(track.Title == "Old deferred song" ? LosslessFormat : CurrentFormat)));
        await using var coordinator = CreateCoordinator(source, transport, endpoint, resolver);
        var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Old deferred song"));
        await deferred.WaitAsync(PausePathTimeout);

        var newTrack = CreateTrack("User selected another song");
        var processed = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == newTrack.UniqueKey &&
            status.FailureReason == "Target format already active.");
        transport.IsPlaying = true;
        source.RaiseTrackChanged(newTrack);
        await processed.WaitAsync(PausePathTimeout);
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await Task.Delay(200);

        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
    }

    [Fact]
    public async Task DeferredRateChange_ResumeRequiresFreshExpectedTrackIdentity()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await deferred.WaitAsync(PausePathTimeout);

        transport.VerifyHandler = (_, _) => Task.FromResult(false);
        var refused = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed");
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await refused.WaitAsync(PausePathTimeout);

        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
    }

    [Fact]
    public async Task DeferredRateChange_NewTrackDuringResumeVerificationCannotApplyTheOldFormat()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        var resolver = new DelegateResolver((track, _) => Task.FromResult<ResolvedAudioFormat?>(
            CreateResolvedFormat(track.Title == "Deferred old item" ? LosslessFormat : CurrentFormat)));
        await using var coordinator = CreateCoordinator(source, transport, endpoint, resolver);
        var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        var oldTrack = CreateTrack("Deferred old item");
        source.RaiseTrackChanged(oldTrack);
        await deferred.WaitAsync(PausePathTimeout);

        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.VerifyHandler = (track, _) =>
        {
            if (track.UniqueKey != oldTrack.UniqueKey) return Task.FromResult(true);
            verifying.TrySetResult();
            return release.Task; // deliberately completes after a superseding item arrives
        };
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await verifying.Task.WaitAsync(PausePathTimeout);

        var replacement = CreateTrack("User's actual current item");
        var processed = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == replacement.UniqueKey &&
            status.FailureReason == "Target format already active.");
        source.RaiseTrackChanged(replacement);
        release.TrySetResult(true);
        await processed.WaitAsync(PausePathTimeout);

        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
    }

    [Fact]
    public async Task DeferredRateChange_DisposeUnsubscribesAndRejectsAnAlreadyQueuedPlayingCallback()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        try
        {
            var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
            await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
            source.RaiseTrackChanged(CreateTrack());
            await deferred.WaitAsync(PausePathTimeout);
            Assert.Equal(1, transport.PlaybackStateSubscriberCount);
            var queued = transport.CapturePlaybackStateChangedCallback();

            await coordinator.DisposeAsync();
            Assert.Equal(0, transport.PlaybackStateSubscriberCount);
            transport.IsPlaying = true;
            queued(new(MediaTransportPlaybackStatus.Playing, true, true, TimeSpan.FromSeconds(12), DateTimeOffset.UtcNow));
            await Task.Delay(100);

            Assert.Equal(0, endpoint.TryApplyFormatCallCount);
            Assert.Equal(0, transport.PauseCallCount);
            Assert.Equal(0, transport.PlayCallCount);
            Assert.Equal(0, transport.GuardedRestartCallCount);
        }
        finally
        {
            // The explicit disposal above completes the subject's lifecycle.
            if (transport.PlaybackStateSubscriberCount > 0) await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviousUnavailable_HoldsOnlyThatTrackAndAllowsTheUsersDifferentSameRateTrack()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport
        {
            GuardedRestartResult = new(false, true, "Previous remained unavailable; no navigation was dispatched.",
                PreviousRestartFailureKind.PreviousUnavailable, NavigationDispatched: false),
        };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Restart unavailable");
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        var original = CreateTrack("Cannot restart this song");
        source.RaiseTrackChanged(original);
        var failure = await held.WaitAsync(PausePathTimeout);

        Assert.DoesNotContain("current song could not be verified", failure.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Previous", failure.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(endpoint.MasterMute);
        Assert.False(transport.IsPlaying);
        Assert.Equal(1, transport.PlayCallCount); // muted warmup only
        Assert.Equal(1, transport.GuardedRestartCallCount);
        var pauseCount = transport.PauseCallCount;

        // Neither a duplicate item nor an external same-item Play permits old-position auto-resume.
        source.RaiseTrackChanged(original);
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await Task.Delay(200);
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(pauseCount, transport.PauseCallCount);
        Assert.True(endpoint.MasterMute);

        // This different user-selected song needs no rate change: it should not inherit the failed restart.
        var next = CreateTrack("Fresh user selection");
        var processed = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == next.UniqueKey &&
            status.FailureReason == "Target format already active.");
        source.RaiseTrackChanged(next);
        await processed.WaitAsync(PausePathTimeout);
        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.False(endpoint.MasterMute);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PreviousUnavailable_WithIssuedNavigationOrUncertainIdentityStillBlocksReplacementTracks(
        bool navigationDispatched, bool safeToResume)
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport
        {
            GuardedRestartResult = new(false, safeToResume, "Uncertain navigation or identity.",
                PreviousRestartFailureKind.PreviousUnavailable, navigationDispatched),
        };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed");
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Uncertain original song"));
        await held.WaitAsync(PausePathTimeout);

        var replacement = CreateTrack("New selection cannot clear an unsafe navigation");
        var stillHeld = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == replacement.UniqueKey &&
            status.ResolverStatusText == "Resolver: Failed");
        transport.IsPlaying = true;
        source.RaiseTrackChanged(replacement);
        await stillHeld.WaitAsync(PausePathTimeout);

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.True(endpoint.MasterMute);
        Assert.DoesNotContain(false, endpoint.MuteCalls);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
    }

    [Fact]
    public async Task RecoveryFailure_WithConfirmedSafetyPauseHoldsTheItemUntilTheUserSelectsAnotherTrack()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var streamAlive = false;
        var endpoint = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
            RenderSessionActiveProvider = () => streamAlive,
        };
        var process = new TestAppleMusicProcessController();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(CurrentFormat), process);
        var settings = CreatePauseSettings();
        settings.RestartAppleMusicOnPlaybackFailure = true;
        var failed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Manual recovery needed");
        await coordinator.StartAsync(settings, CancellationToken.None);
        var original = CreateTrack("Stalled item with a confirmed safety pause");
        source.RaiseTrackChanged(original);
        await failed.WaitAsync(RecoveryTimeout);

        Assert.False(transport.IsPlaying);
        Assert.True(endpoint.MasterMute);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, process.RestartCallCount);
        var pauseCount = transport.PauseCallCount;
        var playCount = transport.PlayCallCount;

        source.RaiseTrackChanged(original);
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await Task.Delay(200);
        Assert.Equal(pauseCount, transport.PauseCallCount);
        Assert.Equal(playCount, transport.PlayCallCount);
        Assert.True(endpoint.MasterMute);

        // A different user-selected healthy item clears only the recoverable item hold.
        var replacement = CreateTrack("Healthy user selection after manual recovery");
        var processed = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == replacement.UniqueKey &&
            status.FailureReason == "Target format already active.");
        streamAlive = true;
        endpoint.MasterPeakValue = 0.02f;
        transport.IsPlaying = true;
        source.RaiseTrackChanged(replacement);
        await processed.WaitAsync(PausePathTimeout);
        var unmuteDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (endpoint.MasterMute == true && DateTimeOffset.UtcNow < unmuteDeadline)
            await Task.Delay(10);

        Assert.False(endpoint.MasterMute);
        Assert.Equal(pauseCount, transport.PauseCallCount);
        Assert.Equal(playCount, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, process.RestartCallCount);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
    }

    [Fact]
    public async Task FailedWatchdogRecovery_DoesNotRetryOrNavigateTheHeldTrack()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var endpoint = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0.01f,
        };
        var streamAlive = true;
        endpoint.RenderSessionActiveProvider = () => streamAlive;
        var process = new TestAppleMusicProcessController();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(CurrentFormat), process);
        var processed = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");
        var settings = CreatePauseSettings();
        settings.RestartAppleMusicOnPlaybackFailure = true;
        await coordinator.StartAsync(settings, CancellationToken.None);
        var track = CreateTrack("Mid-track stall held after failed watchdog recovery");
        source.RaiseTrackChanged(track);
        await processed.WaitAsync(DefaultTimeout);

        var failed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Manual recovery needed");
        streamAlive = false;
        endpoint.MasterPeakValue = 0f;
        await failed.WaitAsync(RecoveryTimeout);
        Assert.False(transport.IsPlaying);
        var pauseCount = transport.PauseCallCount;
        var playCount = transport.PlayCallCount;

        // Duplicate metadata and a same-item user Play must not start another recovery ladder.
        source.RaiseTrackChanged(track);
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await Task.Delay(TimeSpan.FromSeconds(27));
        Assert.Equal(pauseCount, transport.PauseCallCount);
        Assert.Equal(playCount, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, process.RestartCallCount);
        Assert.True(endpoint.MasterMute);
    }

    [Fact]
    public async Task RecoveryFailure_WithoutAConfirmedSafetyPauseKeepsTheGlobalHold()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController { PauseResult = false };
        var endpoint = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
        {
            MasterPeakValue = 0f,
            RenderSessionActiveProvider = () => false,
        };
        var process = new TestAppleMusicProcessController();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(CurrentFormat), process);
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed");
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Pause is unavailable"));
        await held.WaitAsync(RecoveryTimeout);

        var replacement = CreateTrack("Another song");
        var stillHeld = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == replacement.UniqueKey &&
            status.ResolverStatusText == "Resolver: Failed");
        source.RaiseTrackChanged(replacement);
        await stillHeld.WaitAsync(PausePathTimeout);

        Assert.True(endpoint.MasterMute);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, process.RestartCallCount);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
    }
}
