using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Abstractions;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed partial class SwitchingCoordinatorTests
{
    [Fact]
    public async Task ManualRestartStartupWaitsForPlayWithoutEnablingTheExperimentalOption()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var settings = CreateSettings();
        Assert.False(settings.RestartTrackAfterSampleRateChange);
        coordinator.WaitForUserPlaybackAfterManualRestart();
        var waiting = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(settings, CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Restarted with the option off"));
        await waiting.WaitAsync(PausePathTimeout);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        var completed = WaitForStatusAsync(coordinator, status => status.ResolverStatusText == "Switching: Playback restored");
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        await completed.WaitAsync(PausePathTimeout);
        Assert.Equal(1, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.False(settings.RestartTrackAfterSampleRateChange);
    }

    [Fact]
    public async Task ManualRestartCanBeRetriedAfterTheUserManuallyPauses()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController { PauseResult = false };
        await using var coordinator = CreateCoordinator(source, transport, PauseEndpoint(), PauseResolver(CurrentFormat));
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        Assert.False(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        transport.IsPlaying = false;
        Assert.True(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualRestartCancelsActiveResolutionWithoutReleasingOwnedMute(bool pauseAccepted)
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController { PauseResult = pauseAccepted };
        var endpoint = PauseEndpoint();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IFormatResolver resolver = new DelegateResolver(async (track, token) =>
        {
            if (track.Title == "Blocked resolution")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return CreateResolvedFormat(CurrentFormat);
        });
        await using var coordinator = CreateCoordinator(source, transport, endpoint, resolver);
        var first = WaitForStatusAsync(coordinator, status => status.FailureReason == "Target format already active.");
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Seed target"));
        await first.WaitAsync(PausePathTimeout);
        source.RaiseTrackChanged(CreateTrack("Blocked resolution"));
        await entered.Task.WaitAsync(PausePathTimeout);
        Assert.True(endpoint.MasterMute);
        Assert.Equal(pauseAccepted, await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        Assert.True(endpoint.MasterMute);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
    }

    [Fact]
    public async Task ManualRestartStopsDeferredSwitchesAndDoesNotResumeOnALaterPlayingEvent()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport { IsPlaying = false };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var deferred = WaitForStatusAsync(coordinator, IsDeferredSwitch);
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Paused before manual restart"));
        await deferred.WaitAsync(PausePathTimeout);
        Assert.True(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        transport.RaisePlaybackStateChanged(MediaTransportPlaybackStatus.Playing);
        source.RaiseTrackChanged(CreateTrack("Ignored during restart"));
        await Task.Delay(150);
        Assert.Equal(0, endpoint.TryApplyFormatCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
    }

    [Fact]
    public async Task ManualRestartRejectsAnUnconfirmedPauseWithoutPlayingOrNavigating()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController { PauseResult = false };
        var endpoint = PauseEndpoint();
        await using var coordinator = CreateCoordinator(source, transport, endpoint, PauseResolver(CurrentFormat));
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        Assert.False(await coordinator.PrepareForManualRestartAsync(CancellationToken.None));
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
    }
}
