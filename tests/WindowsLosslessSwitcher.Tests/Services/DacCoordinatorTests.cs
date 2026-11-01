using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed partial class SwitchingCoordinatorTests
{
    [Fact]
    public async Task DacGuard_NoDacBlocksResolutionCapabilitiesRestoreAndStartingPlayback()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController { IsPlaying = false, CanNext = true, CanPrevious = true };
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw) { Ready = false };
        var resolves = 0;
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint,
            new DelegateResolver((_, _) => { resolves++; return Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(LosslessFormat)); }));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        var held = WaitForStatusAsync(coordinator, s => s.ResolverStatusText == "Waiting for a supported DAC" && s.Track is not null);
        source.RaiseTrackChanged(CreateTrack("No DAC"));
        await held.WaitAsync(DefaultTimeout);
        Assert.Null(coordinator.GetCurrentTargetDeviceFormat().DeviceId);
        Assert.Empty(coordinator.GetCurrentTargetDeviceCapabilities(forceRefresh: true).SupportedFormats);
        Assert.False((await coordinator.RestoreOriginalFormatAsync("monitor", "Monitor speakers", CurrentFormat, CancellationToken.None)).Succeeded);
        foreach (var action in new[] { UserPlaybackAction.Play, UserPlaybackAction.Previous, UserPlaybackAction.Next })
            Assert.False((await coordinator.ExecuteUserPlaybackAsync(action, CancellationToken.None)).Succeeded);
        Assert.Equal(0, resolves);
        Assert.Equal(0, raw.TryApplyFormatCallCount);
        Assert.Empty(raw.MuteCalls);
        Assert.Equal(0, endpoint.ProbeCalls);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
    }

    [Fact]
    public async Task DacGuard_VerifiedDefaultOverridesPersistedPinnedSpeaker()
    {
        var endpoint = new GuardedTestEndpoint(PauseEndpoint());
        await using var coordinator = CreateDacCoordinator(new TestTrackSource(), new TestMediaTransportController(), endpoint, PauseResolver(CurrentFormat));
        var settings = CreateSettings();
        settings.DeviceSelectionMode = DeviceSelectionMode.PinnedDevice;
        settings.PinnedDeviceId = "monitor";
        await coordinator.StartAsync(settings, CancellationToken.None);
        var target = coordinator.GetCurrentTargetDeviceFormat();
        Assert.Equal(DefaultDevice.Id, target.DeviceId);
        Assert.True(endpoint.EnsureCalls > 0);
        Assert.False((await coordinator.RestoreOriginalFormatAsync("monitor", "Monitor", CurrentFormat, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task DacGuard_HardwareChangeDiscoveredDuringRefreshCancelsOldResolutionEvenWhenDefaultsStillMatch()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint,
            new DelegateResolver(async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                return null;
            }));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Changed physical identity"));
        await entered.Task.WaitAsync(DefaultTimeout);
        endpoint.OnEnsureReady = () => { endpoint.DeviceChangeVersion++; endpoint.OnEnsureReady = null; };
        var state = coordinator.RefreshAudioOutputSafety();
        Assert.False(state!.IsReady);
        await canceled.Task.WaitAsync(DefaultTimeout);
        Assert.True(endpoint.Ready);
        Assert.Equal(0, raw.TryApplyFormatCallCount);
        Assert.Empty(raw.MuteCalls);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
    }

    [Fact]
    public async Task DacGuard_DisconnectDuringResolutionCancelsBeforeAnyFormatOrTransportMutation()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var raw = PauseEndpoint();
        var endpoint = new GuardedTestEndpoint(raw);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint,
            new DelegateResolver(async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                return null;
            }));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Unplug during resolve"));
        await entered.Task.WaitAsync(DefaultTimeout);
        endpoint.Ready = false;
        var result = coordinator.RefreshAudioOutputSafety();
        Assert.NotNull(result);
        Assert.False(result.IsReady);
        await canceled.Task.WaitAsync(DefaultTimeout);
        Assert.Equal(0, raw.TryApplyFormatCallCount);
        Assert.Empty(raw.MuteCalls);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.PauseCallCount);
    }

    [Fact]
    public async Task DacGuard_LossAfterPausedFormatApplyDoesNotPlayOrNavigateInCancellationCleanup()
    {
        var source = new TestTrackSource();
        var transport = new VerifiedTestTransport();
        var raw = PauseEndpoint();
        raw.RenderSessionActiveProvider = () => transport.IsPlaying;
        var endpoint = new GuardedTestEndpoint(raw);
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint, PauseResolver(LosslessFormat));
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.ApplyInvoked = () =>
        {
            Assert.False(transport.IsPlaying);
            endpoint.Ready = false;
            coordinator.RefreshAudioOutputSafety();
            lost.TrySetResult();
        };
        await coordinator.StartAsync(CreatePauseSettings(), CancellationToken.None);
        source.RaiseTrackChanged(CreateTrack("Unplug after apply"));
        await lost.Task.WaitAsync(PausePathTimeout);
        await WaitForDacProcessingToStop(coordinator);
        Assert.Equal(1, raw.TryApplyFormatCallCount);
        Assert.Equal(1, transport.PauseCallCount);
        Assert.Equal(0, transport.PlayCallCount);
        Assert.Equal(0, transport.GuardedRestartCallCount);
        Assert.Equal(0, transport.SkipNextCallCount);
        Assert.Equal(0, transport.SkipPreviousCallCount);
        Assert.False(transport.IsPlaying);
    }

    [Fact]
    public async Task DacGuard_ReconnectProcessesCurrentTrackOnceWithFreshResolution()
    {
        var source = new TestTrackSource();
        var transport = new TestMediaTransportController();
        var endpoint = new GuardedTestEndpoint(PauseEndpoint()) { Ready = false };
        var resolves = 0;
        await using var coordinator = CreateDacCoordinator(source, transport, endpoint,
            new DelegateResolver((_, _) =>
            {
                Interlocked.Increment(ref resolves);
                return Task.FromResult<ResolvedAudioFormat?>(CreateResolvedFormat(CurrentFormat));
            }));
        await coordinator.StartAsync(CreateSettings(), CancellationToken.None);
        var blocked = WaitForStatusAsync(coordinator, s => s.ResolverStatusText == "Waiting for a supported DAC" && s.Track is not null);
        source.RaiseTrackChanged(CreateTrack("Connect after start"));
        await blocked.WaitAsync(DefaultTimeout);
        await WaitForDacProcessingToStop(coordinator);
        var completed = WaitForStatusAsync(coordinator, s => s.FailureReason == "Target format already active.");
        endpoint.Ready = true;
        Assert.True(coordinator.RefreshAudioOutputSafety()?.IsReady);
        await completed.WaitAsync(DefaultTimeout);
        await WaitForDacProcessingToStop(coordinator);
        for (var i = 0; i < 5; i++) coordinator.RefreshAudioOutputSafety();
        Assert.Equal(1, resolves);
        Assert.Equal(0, transport.PlayCallCount);
    }

    [Fact]
    public async Task DacTransport_RechecksEveryDispatchAndAllowsOnlyPauseAfterLoss()
    {
        var inner = new TestMediaTransportController { CanNext = true, CanPrevious = true };
        var ready = true;
        var guarded = new DacGuardedMediaTransportController(inner, () => ready);
        Assert.True(guarded.GetPlaybackState().CanNext);
        ready = false;
        Assert.False(guarded.GetPlaybackState().CanPlay);
        Assert.False(await guarded.TryPlayAsync(CancellationToken.None));
        Assert.False(await guarded.TrySkipNextAsync(CancellationToken.None));
        Assert.False(await guarded.TrySkipPreviousAsync(CancellationToken.None));
        Assert.False(await guarded.TryChangePlaybackPositionAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.True(await guarded.TryPauseAsync(CancellationToken.None));
        Assert.False(await guarded.TryTogglePlayPauseAsync(CancellationToken.None));
        Assert.Equal(1, inner.PauseCallCount);
        Assert.Equal(0, inner.PlayCallCount);
        Assert.Equal(0, inner.ChangePlaybackPositionCallCount);
        Assert.Equal(0, inner.SkipNextCallCount);
        Assert.Equal(0, inner.SkipPreviousCallCount);
    }

    [Fact]
    public async Task DacProcessGuard_RefusesAutomaticRestartAfterLoss()
    {
        var inner = new TestAppleMusicProcessController();
        var guarded = new DacGuardedAppleMusicProcessController(inner, () => false);
        Assert.False(await guarded.TryRestartAsync(CancellationToken.None));
        Assert.Equal(0, inner.RestartCallCount);
    }

    private static SwitchingCoordinator CreateDacCoordinator(
        TestTrackSource source, TestMediaTransportController transport, GuardedTestEndpoint endpoint, IFormatResolver resolver)
    {
        endpoint.ConfigureSession(transport);
        var logger = new DiagnosticsLogger(Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.Tests", Guid.NewGuid().ToString("N")));
        return new(source, transport, new ResolverChain([resolver]), endpoint, logger);
    }

    private static async Task WaitForDacProcessingToStop(SwitchingCoordinator coordinator)
    {
        var deadline = DateTimeOffset.UtcNow + DefaultTimeout;
        while (coordinator.GetUserPlaybackAvailability().IsBusy && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.False(coordinator.GetUserPlaybackAvailability().IsBusy);
    }

    private sealed class GuardedTestEndpoint(TestAudioEndpointController inner) : IAudioEndpointController, IAudioOutputGuard
    {
        public void ConfigureSession(TestMediaTransportController transport) =>
            inner.RenderSessionActiveProvider ??= () => transport.IsPlaying;
        public bool Ready { get; set; } = true;
        public int EnsureCalls { get; private set; }
        public int ProbeCalls { get; private set; }
        public long DeviceChangeVersion { get; set; }
        public Action? OnEnsureReady { get; set; }
        public bool IsRoutingPaused { get; private set; }
        public void SetRoutingPaused(bool paused) { if (IsRoutingPaused != paused) DeviceChangeVersion++; IsRoutingPaused = paused; }
        public bool IsReady => Ready && !IsRoutingPaused;
        public AudioOutputGuardState CurrentState => new(IsReady, IsReady ? DefaultDevice : null, IsRoutingPaused ? "DAC lock is paused." : Ready ? "DAC output is ready." : "No supported DAC is connected.");
        public AudioOutputGuardState EnsureReady() { EnsureCalls++; OnEnsureReady?.Invoke(); return CurrentState; }
        public void ResetRoutingAttempt() { }
        public bool IsReadyForDevice(string id) => IsReady && id == DefaultDevice.Id;
        public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => [DefaultDevice, new("monitor", "Monitor speakers", false)];
        public AudioDeviceInfo? GetDefaultRenderDevice() => Ready ? DefaultDevice : new("monitor", "Monitor speakers", true);
        public AudioFormatCandidate? GetCurrentDeviceFormat(string id) => IsReadyForDevice(id) ? inner.GetCurrentDeviceFormat(id) : null;
        public AudioFormatCandidate? GetCurrentMixFormat(string id) => IsReadyForDevice(id) ? inner.GetCurrentMixFormat(id) : null;
        public IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string id, bool forceRefresh = false)
        { if (!IsReadyForDevice(id)) return []; ProbeCalls++; return inner.GetSupportedFormats(id, forceRefresh); }
        public string? DescribeSupportedFormat(string id, AudioFormatCandidate format) => IsReadyForDevice(id) ? inner.DescribeSupportedFormat(id, format) : null;
        public string? GetLastApplyDiagnostics(string id) => null;
        public string? GetLastProbeDiagnostics(string id) => null;
        public float? GetMasterPeakValue(string id) => IsReadyForDevice(id) ? inner.GetMasterPeakValue(id) : null;
        public bool? IsProcessSessionActive(string id, string name) => IsReadyForDevice(id) ? inner.IsProcessSessionActive(id, name) : null;
        public float? GetProcessSessionPeak(string id, string name) => IsReadyForDevice(id) ? inner.GetProcessSessionPeak(id, name) : null;
        public bool? GetMasterMute(string id) => IsReadyForDevice(id) ? inner.GetMasterMute(id) : null;
        public bool TrySetMasterMute(string id, bool mute) => Ready && id == DefaultDevice.Id && (!mute || !IsRoutingPaused) && inner.TrySetMasterMute(id, mute);
        public bool TryApplyFormat(string id, AudioFormatCandidate format, out AudioFormatCandidate? verified, out string reason)
        {
            if (IsReadyForDevice(id)) return inner.TryApplyFormat(id, format, out verified, out reason);
            verified = null;
            reason = "DAC unavailable";
            return false;
        }
    }
}
