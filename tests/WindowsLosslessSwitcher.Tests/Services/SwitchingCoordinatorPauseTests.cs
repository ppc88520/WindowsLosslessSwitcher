using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using System.Diagnostics;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed partial class SwitchingCoordinatorTests
{
    [Fact]
    public async Task PauseSwitch_DefaultOffPreservesTheUpstreamLiveSwitchWithoutPrevious()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
        var settings = CreateSettings();
        Assert.False(settings.RestartTrackAfterSampleRateChange);

        await coordinator.StartAsync(settings, CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await completed.WaitAsync(DefaultTimeout);

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(1, transport.PauseCallCount); // upstream schedule realignment only
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.VerifyCallCount);
        Assert.Equal(TimeSpan.FromSeconds(12), transport.TimelinePosition);
    }

    [Fact]
    public async Task PauseSwitch_EnabledDoesNotRestartForABitDepthOnlyChange()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var sameRate = new AudioFormatCandidate(CurrentFormat.SampleRateHz, 24, 2);
        var endpoint = new TestAudioEndpointController(DefaultDevice, CurrentFormat, [CurrentFormat, sameRate])
        {
            MasterPeakValue = 0.02f,
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(sameRate));
        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await completed.WaitAsync(DefaultTimeout);

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.VerifyCallCount);
        Assert.Equal(TimeSpan.FromSeconds(12), transport.TimelinePosition);
    }

    private static readonly TimeSpan PausePathTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task PauseSwitch_PausesBeforeApplyAndKeepsEveryWarmupPlayMutedUntilVerifiedZero()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var endpoint = PauseEndpoint();
        var events = new List<string>();
        transport.CommandObserver = command =>
        {
            events.Add(command);
            if (command == "play" && transport.GuardedRestartCallCount == 0)
                Assert.True(endpoint.MasterMute);
        };
        endpoint.ApplyInvoked = () =>
        {
            events.Add("apply");
            Assert.False(transport.IsPlaying);
            Assert.True(endpoint.MasterMute);
        };
        endpoint.MuteInvoked = muted =>
        {
            if (!muted)
            {
                events.Add("unmute");
                Assert.Equal(1, transport.GuardedRestartCallCount);
                Assert.Equal(TimeSpan.Zero, transport.TimelinePosition);
            }
        };
        transport.GuardedRestartHandler = (_, _, _) =>
        {
            Assert.True(endpoint.MasterMute);
            Assert.False(transport.IsPlaying);
            return Task.FromResult(new PreviousRestartResult(true, true, "Same track held at zero."));
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        var elapsed = Stopwatch.StartNew();
        source.RaiseTrackChanged(CreateTrack());
        var status = await completed.WaitAsync(PausePathTimeout);

        Assert.True(status.WasFormatChanged);
        Assert.Equal(LosslessFormat, status.AppliedFormat);
        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(2, transport.PauseCallCount); // initial pause + hold at warmup guard position
        Assert.Equal(2, transport.PlayCallCount); // muted renderer warmup + audible play from zero
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.ChangePlaybackPositionCallCount);
        Assert.False(endpoint.MasterMute);
        Assert.True(events.IndexOf("pause") < events.IndexOf("apply"));
        Assert.True(events.IndexOf("apply") < events.IndexOf("play"));
        Assert.True(events.IndexOf("guarded-previous") < events.IndexOf("unmute"));
        Assert.True(events.IndexOf("unmute") < events.LastIndexOf("play"));
        Assert.True(transport.VerifyCallCount >= 5);
        // The fake timeline begins at the preceding song's stale 12-second position.
        // It must not bypass the new song's ten seconds of confirmed muted Playing.
        Assert.True(elapsed.Elapsed >= GuardedPreviousRestart.MinimumPosition);
    }

    [Fact]
    public async Task PauseSwitch_SameRateDoesNotAddPauseWarmupOrPrevious()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(CurrentFormat));
        var completed = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await completed.WaitAsync(PausePathTimeout);

        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, transport.VerifyCallCount);
    }

    [Fact]
    public async Task PauseSwitch_AcceptedWarmupPlayCanReportPausedBrieflyWithoutExtraResume()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var endpoint = PauseEndpoint();
        var warmupPlaySent = false;
        var readsAfterWarmupPlay = 0;
        transport.PlayInvoked = () =>
        {
            if (transport.PlayCallCount == 1)
            {
                warmupPlaySent = true;
                Assert.True(endpoint.MasterMute);
            }
        };
        transport.PlaybackStateRead = () =>
        {
            if (warmupPlaySent && readsAfterWarmupPlay < 3)
            {
                readsAfterWarmupPlay++;
                transport.IsPlaying = readsAfterWarmupPlay == 3;
            }
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await completed.WaitAsync(PausePathTimeout);

        Assert.Equal(3, readsAfterWarmupPlay);
        Assert.Equal(2, transport.PauseCallCount);
        Assert.Equal(2, transport.PlayCallCount);
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.False(endpoint.MasterMute);
    }

    [Fact]
    public async Task PauseSwitch_AcceptedWarmupPlayWithoutPlayingConfirmationHoldsWithoutAnotherPlay()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var endpoint = PauseEndpoint();
        transport.PlaybackStateRead = () =>
        {
            if (transport.PlayCallCount > 0)
                transport.IsPlaying = false;
        };
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed" &&
            status.FailureReason?.Contains("muted Play did not reach a confirmed Playing state") == true);

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await held.WaitAsync(PausePathTimeout);

        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.True(endpoint.MasterMute);
        Assert.DoesNotContain(false, endpoint.MuteCalls);
    }

    [Fact]
    public async Task PauseSwitch_DoesNotPlayOrNavigateWhenTheUserWasAlreadyPaused()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Format changed; playback paused");

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        var status = await completed.WaitAsync(PausePathTimeout);

        Assert.True(status.WasFormatChanged);
        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.False(transport.IsPlaying);
        Assert.Equal(0, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
    }

    [Fact]
    public async Task PauseSwitch_FreshIdentityMismatchBeforeApplyHoldsWithoutFormatOrPlay()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        transport.VerifyHandler = (_, _) => Task.FromResult(transport.VerifyCallCount == 1);
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed" &&
            status.FailureReason?.Contains("manual recovery required") == true);

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await held.WaitAsync(PausePathTimeout);

        Assert.Equal(2, transport.VerifyCallCount);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.True(endpoint.MasterMute);
        Assert.DoesNotContain(false, endpoint.MuteCalls);
    }

    [Fact]
    public async Task PauseSwitch_SafeOriginalWithoutConfirmedZeroCannotLeakAnAudibleFallback()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport
        {
            GuardedRestartResult = new(false, true, "Original identity known but zero was not confirmed."),
        };
        var endpoint = PauseEndpoint();
        transport.PlayInvoked = () => Assert.True(endpoint.MasterMute);
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed" &&
            status.FailureReason?.Contains("manual recovery required") == true);

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await held.WaitAsync(PausePathTimeout);

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(1, transport.PlayCallCount); // warmup only, never an audible fallback
        Assert.True(endpoint.MasterMute);
        Assert.False(transport.IsPlaying);
        Assert.DoesNotContain(false, endpoint.MuteCalls);
        Assert.Equal(0, transport.SkipNextCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseSwitch_FinalGateRechecksPausedZeroAfterTheFreshIdentityAwait(bool startedPlaying)
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        transport.VerifyHandler = (_, _) =>
        {
            if (transport.GuardedRestartCallCount > 0)
            {
                transport.TimelinePosition = startedPlaying ? TimeSpan.Zero : TimeSpan.FromSeconds(10);
                transport.IsPlaying = startedPlaying;
            }
            return Task.FromResult(true);
        };
        var endpoint = PauseEndpoint();
        transport.PlayInvoked = () => Assert.True(endpoint.MasterMute);
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed" &&
            status.FailureReason?.Contains("final audible-resume gate") == true);

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await held.WaitAsync(PausePathTimeout);

        Assert.Equal(1, transport.GuardedRestartCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.True(endpoint.MasterMute);
        Assert.DoesNotContain(false, endpoint.MuteCalls);
    }

    [Fact]
    public async Task PauseSwitch_AcceptedGuardPauseWithoutConfirmationCannotResumeThroughFinally()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        transport.PauseAccepted = () =>
        {
            if (transport.PauseCallCount == 2)
                transport.PlaybackStatusSequence.Enqueue(MediaTransportPlaybackStatus.Playing);
        };
        var endpoint = PauseEndpoint();
        transport.PlayInvoked = () => Assert.True(endpoint.MasterMute);
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed" &&
            status.FailureReason?.Contains("manual recovery required") == true);

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await held.WaitAsync(PausePathTimeout);

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(2, transport.PauseCallCount);
        Assert.Equal(1, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.True(endpoint.MasterMute);
        Assert.DoesNotContain(false, endpoint.MuteCalls);
    }

    [Fact]
    public async Task PauseSwitch_SupersedingTrackCannotReceiveAnOldTrackPlayAfterIdentityAwait()
    {
        var source = new TestTrackSource();
        var oldTrack = CreateTrack("Old track");
        var nextTrack = CreateTrack("New track");
        var transport = new VerifiedTestTransport();
        var endpoint = PauseEndpoint();
        var awaitingIdentity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.VerifyHandler = async (track, cancellationToken) =>
        {
            if (track.UniqueKey == oldTrack.UniqueKey && transport.VerifyCallCount == 3)
            {
                awaitingIdentity.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return true;
        };
        var currentTrack = oldTrack.UniqueKey;
        var playTracks = new List<string>();
        transport.PlayInvoked = () => playTracks.Add(currentTrack);
        var resolver = new DelegateResolver((track, _) => Task.FromResult<ResolvedAudioFormat?>(
            CreateResolvedFormat(LosslessFormat)));
        await using var coordinator = CreateCoordinator(source, transport, endpoint, resolver);
        var completed = WaitForStatusAsync(coordinator, status => status.Track?.UniqueKey == nextTrack.UniqueKey &&
            status.FailureReason?.Contains("Target format already active.") == true);

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(oldTrack);
        await awaitingIdentity.Task.WaitAsync(PausePathTimeout);
        currentTrack = nextTrack.UniqueKey;
        source.RaiseTrackChanged(nextTrack);
        await completed.WaitAsync(PausePathTimeout);

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.DoesNotContain(oldTrack.UniqueKey, playTracks);
        Assert.All(playTracks, key => Assert.Equal(nextTrack.UniqueKey, key));
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
    }

    [Fact]
    public async Task PauseSwitch_SilentRendererUsesOnlyOneMutedPausePlayRecoveryThenHolds()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var endpoint = PauseEndpoint();
        endpoint.MasterPeakValue = 0f;
        var process = new TestAppleMusicProcessController();
        transport.PlayInvoked = () => Assert.True(endpoint.MasterMute);
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat), process);
        var held = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Resolver: Failed" &&
            status.FailureReason?.Contains("did not rebuild audio after one recovery") == true);

        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack());
        await held.WaitAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(2, transport.PauseCallCount);
        Assert.Equal(2, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, process.RestartCallCount);
        Assert.True(endpoint.MasterMute);
        Assert.DoesNotContain(false, endpoint.MuteCalls);
    }

    private static AppSettings CreatePauseSettings()
    {
        var settings = CreateSettings();
        settings.RestartTrackAfterSampleRateChange = true;
        return settings;
    }

    private static TestAudioEndpointController PauseEndpoint() => new(DefaultDevice, CurrentFormat, [CurrentFormat, LosslessFormat])
    {
        MasterPeakValue = 0.02f,
    };

    private static IFormatResolver PauseResolver(AudioFormatCandidate format) => new DelegateResolver((_, _) =>
        Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(format)));

    private sealed class VerifiedTestTransport : TestMediaTransportController, ITrackIdentityVerifier
    {
        public int VerifyCallCount { get; private set; }
        public Func<TrackSnapshot, CancellationToken, Task<bool>>? VerifyHandler { get; set; }

        public async Task<bool> IsCurrentTrackAsync(TrackSnapshot expectedTrack, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCallCount++;
            var verified = VerifyHandler is null || await VerifyHandler(expectedTrack, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return verified;
        }
    }
}
