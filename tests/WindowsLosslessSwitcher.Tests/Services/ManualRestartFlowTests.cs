using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class ManualRestartFlowTests
{
    [Fact]
    public async Task ReadySignalIsPublishedAfterItsWriterIsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wls-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "request.json");
        try
        {
            await ManualRestartHandoff.SignalReadyAsync(path, "nonce");
            Assert.Equal("nonce", await File.ReadAllTextAsync(path + ".ready"));
            Assert.False(File.Exists(path + ".ready.tmp"));
        }
        finally
        {
            File.Delete(path + ".ready");
            File.Delete(path + ".ready.tmp");
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task ToolOnlyWaitsForOldExitAndNeverRestartsThePlayer()
    {
        var calls = new List<string>();
        Assert.True(await ManualRestartHandoff.CompleteAsync(ManualRestartMode.ToolOnly,
            () => { calls.Add("wait"); return Task.FromResult(true); },
            () => throw new InvalidOperationException("Player restart must not be called"),
            _ => calls.Add("launch")));
        Assert.Equal(new[] { "wait", "launch" }, calls);
    }

    [Fact]
    public async Task CombinedRestartRunsOnlyAfterOldExitAndBeforeToolLaunch()
    {
        var calls = new List<string>();
        await ManualRestartHandoff.CompleteAsync(ManualRestartMode.AppleMusicAndTool,
            () => { calls.Add("wait"); return Task.FromResult(true); },
            () => { calls.Add("player"); return Task.FromResult(true); },
            success => { Assert.True(success); calls.Add("launch"); });
        Assert.Equal(new[] { "wait", "player", "launch" }, calls);
    }

    [Fact]
    public async Task OldProcessTimeoutDoesNotRestartOrLaunchAnything()
    {
        Assert.False(await ManualRestartHandoff.CompleteAsync(ManualRestartMode.AppleMusicAndTool,
            () => Task.FromResult(false), () => throw new Exception("Unexpected player restart"),
            _ => throw new Exception("Unexpected second tool")));
    }

    [Fact]
    public async Task PlayerFailureStillAllowsTheToolToExplainTheFailure()
    {
        bool? result = null;
        await ManualRestartHandoff.CompleteAsync(ManualRestartMode.AppleMusicAndTool,
            () => Task.FromResult(true), () => Task.FromResult(false), success => result = success);
        Assert.False(result);
    }

    [Fact]
    public async Task UncertainPlayerTimeoutDoesNotLaunchWhileTheRemoteOperationMayBePending()
    {
        var launched = false;
        await Assert.ThrowsAsync<TimeoutException>(() => ManualRestartHandoff.CompleteAsync(
            ManualRestartMode.AppleMusicAndTool, () => Task.FromResult(true),
            () => throw new TimeoutException(), _ => launched = true));
        Assert.False(launched);
    }

    [Fact]
    public void RequestRequiresTheSameExecutableProfileDirectoryAndFreshNonce()
    {
        var now = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid().ToString("N");
        var executable = Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.exe");
        var request = new ManualRestartRequest(nonce, 123, 100, executable, ManualRestartMode.ToolOnly, now, ManualRestartHandoff.ProfileIdentity);
        var path = Path.Combine(ManualRestartHandoff.RequestsDirectory, nonce + ".json");
        Assert.True(ManualRestartHandoff.Validate(request, path, executable, now));
        Assert.False(ManualRestartHandoff.Validate(request with { Profile = "WrongProfile" }, path, executable, now));
        Assert.False(ManualRestartHandoff.Validate(request with { CreatedUtc = now.AddMinutes(-3) }, path, executable, now));
        Assert.False(ManualRestartHandoff.Validate(request, Path.Combine(Path.GetTempPath(), nonce + ".json"), executable, now));
        Assert.False(ManualRestartHandoff.Validate(request with { Executable = "C:\\other.exe" }, path, executable, now));
        Assert.False(ManualRestartHandoff.Validate(request with { Mode = (ManualRestartMode)999 }, path, executable, now));
    }

    [Theory]
    [InlineData(MediaTransportPlaybackStatus.Paused)]
    [InlineData(MediaTransportPlaybackStatus.Stopped)]
    [InlineData(MediaTransportPlaybackStatus.Closed)]
    public async Task AlreadySafePlaybackSendsNoCommands(MediaTransportPlaybackStatus state)
    {
        var transport = new PauseTransport { State = state };
        Assert.True(await ManualRestartSafety.ConfirmPausedAsync(transport, CancellationToken.None));
        Assert.Equal(0, transport.PauseCalls);
    }

    [Fact]
    public async Task PlayingMustReachAPausedState()
    {
        var transport = new PauseTransport();
        Assert.True(await ManualRestartSafety.ConfirmPausedAsync(transport, CancellationToken.None));
        Assert.Equal(1, transport.PauseCalls);
        Assert.Equal(MediaTransportPlaybackStatus.Paused, transport.State);
    }

    [Fact]
    public async Task RejectedPauseAndUnknownStateDoNotAuthorizeRestart()
    {
        var transport = new PauseTransport { AcceptPause = false };
        Assert.False(await ManualRestartSafety.ConfirmPausedAsync(transport, CancellationToken.None));
        transport.State = MediaTransportPlaybackStatus.Unknown;
        Assert.False(await ManualRestartSafety.ConfirmPausedAsync(transport, CancellationToken.None));
        Assert.True(await ManualRestartSafety.ConfirmPausedAsync(transport, CancellationToken.None, noPlayerProcess: true));
    }

    [Fact]
    public async Task AcceptedButUnconfirmedPauseDoesNotAuthorizeRestart()
    {
        var transport = new PauseTransport { UpdateState = false };
        Assert.False(await ManualRestartSafety.ConfirmPausedAsync(transport, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotDispatchAPause()
    {
        var transport = new PauseTransport();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualRestartSafety.ConfirmPausedAsync(transport, new CancellationToken(true)));
        Assert.Equal(0, transport.PauseCalls);
    }

    private sealed class PauseTransport : IMediaTransportController
    {
        public MediaTransportPlaybackStatus State = MediaTransportPlaybackStatus.Playing;
        public bool AcceptPause = true;
        public bool UpdateState = true;
        public int PauseCalls;
        public MediaTransportState GetPlaybackState() => new(State, true, true, TimeSpan.Zero, DateTimeOffset.UtcNow);
        public Task<bool> TryPauseAsync(CancellationToken token)
        {
            PauseCalls++;
            if (AcceptPause && UpdateState) State = MediaTransportPlaybackStatus.Paused;
            return Task.FromResult(AcceptPause);
        }
        public Task<bool> TryPlayAsync(CancellationToken token) => throw new Exception("No Play during restart");
        public Task<bool> TryTogglePlayPauseAsync(CancellationToken token) => throw new Exception("No toggle during restart");
        public Task<bool> TryChangePlaybackPositionAsync(TimeSpan value, CancellationToken token) => throw new Exception("No seek during restart");
        public Task<bool> TrySkipNextAsync(CancellationToken token) => throw new Exception("No Next during restart");
        public Task<bool> TrySkipPreviousAsync(CancellationToken token) => throw new Exception("No Previous during restart");
    }
}
