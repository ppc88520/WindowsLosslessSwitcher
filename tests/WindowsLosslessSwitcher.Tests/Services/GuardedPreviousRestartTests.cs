using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class GuardedPreviousRestartTests : IDisposable
{
    private static readonly TrackSnapshot Expected = Track("Current");
    private static readonly TrackSnapshot Preceding = Track("Preceding");
    private static readonly TrackSnapshot Unknown = Track("Unrelated");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.PreviousGuard.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Previous_RestartsOnlyAfterSameTrackAndPausedZeroAreObservedTwice()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };
        var result = await RunAsync(session);

        Assert.True(result.Restarted);
        Assert.True(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(0, session.PauseCount);
        Assert.Equal(0, session.PlayCount);
        Assert.True(session.ReadCount >= 4); // initial + pre-dispatch + two independent confirmations
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(9d)]
    [InlineData(null)]
    public async Task Previous_IsNeverSentAtAnEarlyOrUnknownPosition(double? seconds)
    {
        var session = new FakeSession { Snapshot = Snapshot(position: seconds is { } value ? TimeSpan.FromSeconds(value) : null) };
        var result = await RunAsync(session);

        Assert.False(result.Restarted);
        Assert.Equal(0, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_IsNeverSentWhenExpectedMetadataDoesNotMatch()
    {
        var session = new FakeSession { Snapshot = Snapshot() with { Track = Unknown } };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(0, session.PreviousCount);
    }

    [Fact]
    public async Task Previous_IsNeverSentWhenCapabilityIsDisabled()
    {
        var session = new FakeSession { Snapshot = Snapshot() with { CanPrevious = false } };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.True(result.SafeToResume);
        Assert.Equal(0, session.PreviousCount);
    }

    [Fact]
    public async Task Previous_IsNeverSentIfPlaybackWasNotPaused()
    {
        var session = new FakeSession { Snapshot = Snapshot() with { PlaybackStatus = MediaTransportPlaybackStatus.Playing } };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.Equal(0, session.PreviousCount);
    }

    [Fact]
    public async Task Previous_RechecksTrackImmediatelyBeforeDispatch()
    {
        var session = new FakeSession();
        session.ReadInvoked = () =>
        {
            if (session.ReadCount == 2)
                session.Snapshot = session.Snapshot! with { Track = Unknown };
        };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(0, session.PreviousCount);
    }

    [Fact]
    public async Task Previous_IsNotSentIfThePositionReturnsToZeroBeforeDispatch()
    {
        var session = new FakeSession();
        session.ReadInvoked = () =>
        {
            if (session.ReadCount == 2)
                session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };
        };
        var result = await RunAsync(session);

        Assert.False(result.Restarted);
        Assert.True(result.SafeToResume);
        Assert.Equal(0, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_RejectedRequestDoesNotRetryAndCanResumeOnlyVerifiedOriginal()
    {
        var session = new FakeSession { PreviousResult = false };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.True(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_RejectedRequestWithChangedTrackCannotResumeAutomatically()
    {
        var session = new FakeSession { PreviousResult = false };
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Track = Unknown };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_FreezesAnAutoPlayingRestartBeforeConfirmingZero()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with
        {
            Position = TimeSpan.Zero,
            PlaybackStatus = MediaTransportPlaybackStatus.Playing,
        };
        var result = await RunAsync(session);
        Assert.True(result.Restarted);
        Assert.True(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.PauseCount);
        Assert.Equal(MediaTransportPlaybackStatus.Paused, session.Snapshot!.PlaybackStatus);
    }

    [Fact]
    public async Task Previous_ZeroFromAnotherTrackIsNotAConfirmedRestart()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with
        {
            Track = Unknown,
            Position = TimeSpan.Zero,
            PlaybackStatus = MediaTransportPlaybackStatus.Playing,
        };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(1, session.PauseCount);
    }

    [Fact]
    public async Task Previous_KnownPredecessorCanBeCorrectedWithOneNextAndVerified()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Track = Preceding, Position = TimeSpan.Zero };
        session.NextInvoked = () => session.Snapshot = session.Snapshot! with { Track = Expected, Position = TimeSpan.Zero };
        var result = await RunAsync(session);
        Assert.True(result.Restarted);
        Assert.True(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.NextCount);
        Assert.Equal(Expected.UniqueKey, session.Snapshot!.Track.UniqueKey);
    }

    [Fact]
    public async Task Previous_DoesNotGuessACorrectionWhenNoPredecessorWasKnown()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Track = Preceding, Position = TimeSpan.Zero };
        var result = await CreateGuard(session).TryRestartWithPreviousAsync(Expected, null, CancellationToken.None);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(0, session.NextCount);
    }

    [Theory]
    [InlineData(true, MediaTransportRepeatMode.Off)]
    [InlineData(null, MediaTransportRepeatMode.Off)]
    [InlineData(false, MediaTransportRepeatMode.One)]
    [InlineData(false, MediaTransportRepeatMode.Unknown)]
    public async Task Previous_DoesNotCorrectThroughAnUnsafeOrUnknownQueueMode(bool? shuffle, MediaTransportRepeatMode repeat)
    {
        var session = new FakeSession { Snapshot = Snapshot() with { IsShuffleActive = shuffle, RepeatMode = repeat } };
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Track = Preceding, Position = TimeSpan.Zero };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_ARejectedCorrectionIsNeverRepeated()
    {
        var session = new FakeSession { NextResult = false };
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Track = Preceding, Position = TimeSpan.Zero };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.NextCount);
    }

    [Fact]
    public async Task Previous_AWrongCorrectionDoesNotCauseFurtherNavigation()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Track = Preceding, Position = TimeSpan.Zero };
        session.NextInvoked = () => session.Snapshot = session.Snapshot! with { Track = Unknown, Position = TimeSpan.Zero };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.NextCount);
    }

    [Fact]
    public async Task Previous_DoesNotActOnAReplacementSession()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { SessionId = "replacement-session", Position = TimeSpan.Zero };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(0, session.PauseCount);
    }

    [Fact]
    public async Task Previous_DoesNotCorrectAfterObservedUserNavigation()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with
        {
            Track = Preceding,
            Position = TimeSpan.Zero,
            NavigationRevision = 1,
        };
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(0, session.PauseCount);
    }

    [Fact]
    public async Task Previous_AcceptanceWithoutTimelineResetIsNotSuccessAndDoesNotRetry()
    {
        var session = new FakeSession();
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(1, session.PlayCount);
        Assert.Equal(1, session.PauseCount);
    }

    [Fact]
    public async Task Previous_OneTransientZeroReadDoesNotConfirmRestart()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };
        session.ReadInvoked = () =>
        {
            // Read 3 is the first zero observation; a stale zero must not survive read 4.
            if (session.ReadCount == 4)
                session.Snapshot = session.Snapshot! with { Position = TimeSpan.FromSeconds(2) };
        };
        var result = await RunAsync(session);

        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_DelayedWrongMetadataAfterTwoEarlyZeroReadsCannotConfirmSuccess()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };
        session.ReadInvoked = () =>
        {
            // The timeline can reset before title/artist metadata catches up. Two early
            // matching zero reads must not permit unmute before this delayed item change.
            if (session.ReadCount == 5)
                session.Snapshot = session.Snapshot! with { Track = Unknown };
        };
        var guard = new GuardedPreviousRestart(
            session, new DiagnosticsLogger(_directory), TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(1));
        var result = await guard.TryRestartWithPreviousAsync(Expected, Preceding, CancellationToken.None);

        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
        Assert.True(session.ReadCount >= 5);
    }

    [Fact]
    public async Task Previous_UnknownMetadataAfterTheRequestCannotBeResumed()
    {
        var session = new FakeSession();
        session.PreviousInvoked = () => session.Snapshot = null;
        var result = await RunAsync(session);
        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_DeferredResetIsActivatedOnceWhileMutedAndFrozenAtZero()
    {
        var session = new FakeSession();
        session.PlayInvoked = () => session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };

        var result = await RunAsync(session);

        Assert.True(result.Restarted, result.Detail);
        Assert.True(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.PlayCount);
        Assert.Equal(1, session.PauseCount);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(TimeSpan.Zero, session.Snapshot!.Position);
        Assert.Equal(MediaTransportPlaybackStatus.Paused, session.Snapshot.PlaybackStatus);
    }

    [Fact]
    public async Task Previous_ActivationDoesNotPauseOldPositionBeforePendingResetArrives()
    {
        var session = new FakeSession();
        var afterPlayReads = 0;
        session.ReadInvoked = () =>
        {
            if (session.PlayCount == 1 && ++afterPlayReads == 3)
                session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };
        };

        var result = await RunAsync(session);

        Assert.True(result.Restarted);
        Assert.True(result.SafeToResume);
        Assert.Equal(1, session.PlayCount);
        Assert.Single(session.PausePositions);
        Assert.Equal(TimeSpan.Zero, session.PausePositions[0]);
    }

    [Fact]
    public async Task Previous_DeferredActivationCannotResumeAnUnknownTrack()
    {
        var session = new FakeSession();
        session.PlayInvoked = () => session.Snapshot = session.Snapshot! with { Track = Unknown, Position = TimeSpan.Zero };

        var result = await RunAsync(session);

        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.PlayCount);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(1, session.PauseCount);
    }

    [Fact]
    public async Task Previous_ActivatedZeroWaitsForAsynchronousPauseConfirmation()
    {
        var session = new FakeSession();
        var afterPauseReads = 0;
        session.PlayInvoked = () => session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };
        session.PauseInvoked = () => session.Snapshot = session.Snapshot! with { PlaybackStatus = MediaTransportPlaybackStatus.Playing };
        session.ReadInvoked = () =>
        {
            if (session.PauseCount == 1 && ++afterPauseReads == 3)
                session.Snapshot = session.Snapshot! with { PlaybackStatus = MediaTransportPlaybackStatus.Paused };
        };

        var result = await RunAsync(session);

        Assert.True(result.Restarted, result.Detail);
        Assert.True(result.SafeToResume);
        Assert.Equal(1, session.PauseCount);
        Assert.Equal(1, session.PlayCount);
        Assert.True(afterPauseReads >= 3);
    }

    [Fact]
    public async Task Previous_DeferredActivationRechecksIdentityBeforePlay()
    {
        var session = new FakeSession();
        session.ReadInvoked = () =>
        {
            if (session.ReadCount == 4)
                session.Snapshot = session.Snapshot! with { SessionId = "replacement-session" };
        };
        var guard = new GuardedPreviousRestart(session, new DiagnosticsLogger(_directory),
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.Zero);

        var result = await guard.TryRestartWithPreviousAsync(Expected, Preceding, CancellationToken.None);

        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.PlayCount);
        Assert.Equal(0, session.PauseCount);
    }

    [Fact]
    public async Task Previous_DeferredActivationRejectionKeepsPendingNavigationUnsafe()
    {
        var session = new FakeSession { PlayResult = false };
        var result = await RunAsync(session);

        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.PlayCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_DeferredActivationRequiresAPauseThatActuallySucceeds()
    {
        var session = new FakeSession { PauseResult = false };
        session.PlayInvoked = () => session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero };
        var result = await RunAsync(session);

        Assert.False(result.Restarted);
        Assert.False(result.SafeToResume);
        Assert.Equal(1, session.PlayCount);
        Assert.Equal(1, session.PauseCount);
    }

    [Fact]
    public async Task Previous_LatePlayCompletionCannotPauseOrNavigateAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { PlayHandler = _ => { started.TrySetResult(); return completion.Task; } };
        var attempt = CreateGuard(session).TryRestartWithPreviousAsync(Expected, Preceding, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        session.Snapshot = session.Snapshot! with { Position = TimeSpan.Zero, PlaybackStatus = MediaTransportPlaybackStatus.Playing };
        completion.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(1, session.PlayCount);
        Assert.Equal(0, session.PauseCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_CancellationBeforeDispatchSendsNoCommand()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = new FakeSession();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateGuard(session).TryRestartWithPreviousAsync(Expected, Preceding, cancellation.Token));
        Assert.Equal(0, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    [Fact]
    public async Task Previous_NonCooperativeInFlightCompletionCannotTriggerCorrectionAfterCancel()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { PreviousHandler = _ => { started.TrySetResult(); return completion.Task; } };
        var attempt = CreateGuard(session).TryRestartWithPreviousAsync(Expected, Preceding, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        session.Snapshot = session.Snapshot! with { Track = Preceding, Position = TimeSpan.Zero };
        completion.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Equal(1, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
        Assert.Equal(0, session.PauseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Previous_NonCompletingRemoteTaskIsBoundedAndCannotContinueNavigation(bool blockCommand)
    {
        var session = new FakeSession();
        if (blockCommand)
            session.PreviousHandler = _ => new TaskCompletionSource<bool>().Task;
        else
            session.ReadHandler = _ => new TaskCompletionSource<PreviousRestartSnapshot?>().Task;

        // This case tests the guard deadline itself; keep it short independently of
        // the normal state-sequence tests' scheduler/logging margin.
        var guard = new GuardedPreviousRestart(session, new DiagnosticsLogger(_directory),
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20));
        var attempt = guard.TryRestartWithPreviousAsync(Expected, Preceding, CancellationToken.None);
        var completed = await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(attempt, completed);
        try
        {
            var result = await attempt;
            Assert.False(result.Restarted);
            Assert.False(result.SafeToResume);
        }
        catch (TimeoutException)
        {
            // A timeout is also an unsafe result at the coordinator boundary.
        }
        Assert.Equal(blockCommand ? 1 : 0, session.PreviousCount);
        Assert.Equal(0, session.NextCount);
    }

    private GuardedPreviousRestart CreateGuard(FakeSession session) => new(
        session,
        new DiagnosticsLogger(_directory),
        // Several state reads and synchronous diagnostic writes occur in this window.
        // Full-suite parallel scheduling must not turn those behavior tests into
        // accidental tests of a 150 ms deadline. The production window remains 3 s.
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(20));

    private Task<PreviousRestartResult> RunAsync(FakeSession session) =>
        CreateGuard(session).TryRestartWithPreviousAsync(Expected, Preceding, CancellationToken.None);

    private static TrackSnapshot Track(string title) => new("AppleMusic", null, title, "Artist", "Album", "test", DateTimeOffset.UtcNow);

    private static PreviousRestartSnapshot Snapshot() => Snapshot(TimeSpan.FromSeconds(12));

    private static PreviousRestartSnapshot Snapshot(TimeSpan? position) => new(
        "session-a", Expected, MediaTransportPlaybackStatus.Paused, position,
        true, true, true, false, MediaTransportRepeatMode.Off, 0);

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.PreviousGuard.Tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_directory);
        Assert.StartsWith(parent, target, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(target, recursive: true);
    }

    private sealed class FakeSession : IPreviousRestartSession
    {
        public PreviousRestartSnapshot? Snapshot { get; set; } = GuardedPreviousRestartTests.Snapshot(TimeSpan.FromSeconds(12));
        public int ReadCount { get; private set; }
        public int PreviousCount { get; private set; }
        public int NextCount { get; private set; }
        public int PauseCount { get; private set; }
        public int PlayCount { get; private set; }
        public List<TimeSpan?> PausePositions { get; } = [];
        public bool PreviousResult { get; set; } = true;
        public bool NextResult { get; set; } = true;
        public bool PauseResult { get; set; } = true;
        public bool PlayResult { get; set; } = true;
        public Action? ReadInvoked { get; set; }
        public Action? PreviousInvoked { get; set; }
        public Action? NextInvoked { get; set; }
        public Action? PlayInvoked { get; set; }
        public Action? PauseInvoked { get; set; }
        public Func<CancellationToken, Task<PreviousRestartSnapshot?>>? ReadHandler { get; set; }
        public Func<CancellationToken, Task<bool>>? PreviousHandler { get; set; }
        public Func<CancellationToken, Task<bool>>? PlayHandler { get; set; }

        public Task<PreviousRestartSnapshot?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            ReadInvoked?.Invoke();
            return ReadHandler?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
        }

        public Task<bool> PreviousAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreviousCount++;
            PreviousInvoked?.Invoke();
            return PreviousHandler?.Invoke(cancellationToken) ?? Task.FromResult(PreviousResult);
        }

        public Task<bool> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NextCount++;
            NextInvoked?.Invoke();
            return Task.FromResult(NextResult);
        }

        public Task<bool> PauseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PauseCount++;
            PausePositions.Add(Snapshot?.Position);
            if (PauseResult && Snapshot is { } state)
                Snapshot = state with { PlaybackStatus = MediaTransportPlaybackStatus.Paused };
            PauseInvoked?.Invoke();
            return Task.FromResult(PauseResult);
        }

        public Task<bool> PlayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlayCount++;
            if (PlayResult && Snapshot is { } state)
                Snapshot = state with { PlaybackStatus = MediaTransportPlaybackStatus.Playing };
            PlayInvoked?.Invoke();
            return PlayHandler?.Invoke(cancellationToken) ?? Task.FromResult(PlayResult);
        }
    }
}
