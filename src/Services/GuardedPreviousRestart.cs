using System.Diagnostics;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Conservative Previous policy, separated from WinRT so queue changes and cancellation can be tested.
/// Previous has player-defined semantics: this policy never claims that sending it alone is success.
/// </summary>
public sealed class GuardedPreviousRestart : IPreviousTrackRestarter
{
    public static readonly TimeSpan MinimumPosition = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PositionTolerance = TimeSpan.FromMilliseconds(250);
    private readonly IPreviousRestartSession _session;
    private readonly DiagnosticsLogger _logger;
    private readonly TimeSpan _observationTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _confirmationWindow;
    private readonly TimeSpan _deferredPlayDelay;

    public GuardedPreviousRestart(
        IPreviousRestartSession session,
        DiagnosticsLogger logger,
        TimeSpan? observationTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? confirmationWindow = null,
        TimeSpan? deferredPlayDelay = null)
    {
        _session = session;
        _logger = logger;
        _observationTimeout = observationTimeout ?? TimeSpan.FromSeconds(3);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        _confirmationWindow = confirmationWindow ?? TimeSpan.FromMilliseconds(500);
        _deferredPlayDelay = deferredPlayDelay ?? TimeSpan.FromMilliseconds(500);
        if (_observationTimeout <= TimeSpan.Zero || _pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(observationTimeout));
        if (_confirmationWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(confirmationWindow));
        if (_deferredPlayDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(deferredPlayDelay));
    }

    public async Task<PreviousRestartResult> TryRestartWithPreviousAsync(
        TrackSnapshot expectedTrack,
        TrackSnapshot? precedingTrack,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromTicks(_observationTimeout.Ticks * 3));
        try
        {
            // WaitAsync also bounds a session implementation that ignores cancellation. The
            // core checks the canceled token after every remote await before another command.
            return await TryRestartCoreAsync(expectedTrack, precedingTrack, deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(false, false, "guard deadline expired; no further navigation or automatic resume is allowed.");
        }
    }

    private async Task<PreviousRestartResult> TryRestartCoreAsync(
        TrackSnapshot expectedTrack,
        TrackSnapshot? precedingTrack,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var initial = await ReadAsync(cancellationToken);
        if (initial is null || !SameTrack(initial, expectedTrack) || string.IsNullOrEmpty(initial.SessionId))
            return Result(false, false, "preflight could not verify the expected current track; no Previous was sent.");
        Trace("preflight", initial);
        if (initial.PlaybackStatus != MediaTransportPlaybackStatus.Paused)
            return Result(false, true, "preflight was not paused; no Previous was sent.");
        if (initial.Position is null || initial.Position < MinimumPosition)
            return Result(false, true, "position was unknown or below the 10-second guard; no Previous was sent.");
        if (!initial.CanPrevious)
            return Result(false, true, "Previous control was disabled; no Previous was sent.");

        // Recheck immediately before dispatch. No command goes to a newly selected session/item.
        var before = await ReadAsync(cancellationToken);
        if (!SameOperation(before, initial) || !SameTrack(before, expectedTrack))
            return Result(false, false, "track, session, or user navigation changed before dispatch; no Previous was sent.");
        if (before!.PlaybackStatus != MediaTransportPlaybackStatus.Paused ||
            before.Position is null || before.Position < MinimumPosition || !before.CanPrevious)
            return Result(false, true, "pause, position, or Previous capability changed before dispatch; no Previous was sent.");

        cancellationToken.ThrowIfCancellationRequested();
        _logger.Info("Previous-restart: dispatching one Previous request on the captured session.");
        var accepted = await _session.PreviousAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!accepted)
        {
            var rejectedState = await ReadAsync(cancellationToken);
            var safe = SameOperation(rejectedState, initial) && SameTrack(rejectedState, expectedTrack);
            if (!safe)
                await PauseIfSafeAsync(rejectedState, initial, cancellationToken);
            return Result(false, safe, "Previous request was rejected; no retry was sent.");
        }

        var timer = Stopwatch.StartNew();
        var consecutiveMatches = 0;
        Stopwatch? stableMatchTimer = null;
        var pauseUsed = false;
        var rollbackUsed = false;
        var playActivationUsed = false;
        PreviousRestartSnapshot? last = before;
        string? lastTrace = null;
        while (timer.Elapsed < _observationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await ReadAsync(cancellationToken);
            if (!SameOperation(last, initial))
            {
                await PauseIfSafeAsync(last, initial, cancellationToken);
                return Result(false, false, "session or observed user navigation changed after Previous; automatic replay stopped.");
            }

            var trace = $"{last!.Track.UniqueKey}|{last.PlaybackStatus}|{last.Position}";
            if (trace != lastTrace)
            {
                Trace(rollbackUsed ? "rollback-observation" : "observation", last);
                lastTrace = trace;
            }

            if (SameTrack(last, expectedTrack))
            {
                if (last.PlaybackStatus == MediaTransportPlaybackStatus.Playing &&
                    last.Position is { } playingPosition && playingPosition >= TimeSpan.Zero && playingPosition <= PositionTolerance)
                {
                    if (!pauseUsed)
                    {
                        if (!last.CanPause)
                            return Result(false, false, "same-track 0:00 was playing but could not be held paused; endpoint must remain muted.");
                        pauseUsed = true;
                        var paused = await _session.PauseAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!paused)
                            return Result(false, false, "could not pause the activated restart at 0:00; endpoint must remain muted.");
                    }
                    // An accepted Pause may take a few observations to appear in GSMTC.
                    // Keep waiting muted; never repeat the command or accept Playing as zero.
                    consecutiveMatches = 0;
                    stableMatchTimer = null;
                }
                else if (last.PlaybackStatus == MediaTransportPlaybackStatus.Paused &&
                    last.Position is { } position && position >= TimeSpan.Zero && position <= PositionTolerance)
                {
                    // Metadata can lag a timeline reset. Hold paused at the beginning long
                    // enough to catch that transition instead of accepting two quick samples.
                    stableMatchTimer ??= Stopwatch.StartNew();
                    if (++consecutiveMatches >= 2 && stableMatchTimer.Elapsed >= _confirmationWindow)
                        return Result(true, true, rollbackUsed
                            ? $"one Next restored the original track; same-track paused 0:00 held for {_confirmationWindow.TotalMilliseconds:0} ms with multiple reads."
                            : $"same-track paused 0:00 held for {_confirmationWindow.TotalMilliseconds:0} ms with multiple reads after one Previous.");
                }
                else
                {
                    consecutiveMatches = 0;
                    stableMatchTimer = null;
                    // Some Apple Music sessions accept Previous while paused but defer the
                    // timeline reset until Play. The endpoint must remain muted throughout
                    // this policy. A single activation can flush that pending command; it is
                    // never permission to unmute at the old position or send Previous again.
                    if (!rollbackUsed && !playActivationUsed &&
                        last.PlaybackStatus == MediaTransportPlaybackStatus.Paused &&
                        last.Position == before!.Position && last.Position >= MinimumPosition &&
                        timer.Elapsed >= _deferredPlayDelay)
                    {
                        var activation = await ReadAsync(cancellationToken);
                        if (!SameOperation(activation, initial) || !SameTrack(activation, expectedTrack))
                        {
                            await PauseIfSafeAsync(activation, initial, cancellationToken);
                            return Result(false, false, "track or session changed before the muted Play activation; no Play was sent.");
                        }
                        if (activation!.PlaybackStatus == MediaTransportPlaybackStatus.Paused &&
                            activation.Position == before.Position && activation.Position >= MinimumPosition)
                        {
                            playActivationUsed = true;
                            _logger.Info("Previous-restart: accepted Previous is still pending while paused; dispatching one Play activation with the endpoint kept muted.");
                            var activated = await _session.PlayAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!activated)
                            {
                                await PauseIfSafeAsync(await ReadAsync(cancellationToken), initial, cancellationToken);
                                return Result(false, false, "muted Play activation was rejected while Previous may still be pending; automatic unmute is unsafe.");
                            }
                            // Allow a bounded observation window after activation; the outer
                            // deadline also bounds a provider that never completes its task.
                            timer.Restart();
                        }
                    }
                }
            }
            else
            {
                consecutiveMatches = 0;
                stableMatchTimer = null;
                var isKnownPredecessor = precedingTrack is not null &&
                    precedingTrack.UniqueKey != expectedTrack.UniqueKey && SameTrack(last, precedingTrack);
                if (!isKnownPredecessor)
                {
                    await PauseIfSafeAsync(last, initial, cancellationToken);
                    return Result(false, false, "Previous exposed an unknown track; stopped without guessing a queue correction.");
                }

                if (!rollbackUsed)
                {
                    if (!CanRollback(initial, last))
                    {
                        await PauseIfSafeAsync(last, initial, cancellationToken);
                        return Result(false, false, "Previous moved to the known preceding track, but queue conditions were unsafe for a correction; stopped.");
                    }
                    // Pause the unintended item before correcting it. Only a fresh, still-matching
                    // predecessor permits Next; we never navigate forward from an unknown item.
                    if (last.PlaybackStatus == MediaTransportPlaybackStatus.Playing)
                    {
                        if (!last.CanPause || !await _session.PauseAsync(cancellationToken))
                            return Result(false, false, "could not pause the unintended preceding track; correction canceled.");
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    var correctionState = await ReadAsync(cancellationToken);
                    if (!SameOperation(correctionState, initial) || !SameTrack(correctionState, precedingTrack!) ||
                        !CanRollback(initial, correctionState!) || correctionState!.PlaybackStatus != MediaTransportPlaybackStatus.Paused)
                        return Result(false, false, "track or queue changed before correction; no Next was sent.");

                    rollbackUsed = true;
                    _logger.Warn("Previous-restart: detected the known preceding track; dispatching one guarded Next to restore the original item.");
                    var corrected = await _session.NextAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!corrected)
                    {
                        await PauseIfSafeAsync(correctionState, initial, cancellationToken);
                        return Result(false, false, "the single Next correction was rejected; stopped without further navigation.");
                    }
                    // One bounded window per command, at most Previous + one correcting Next.
                    timer.Restart();
                    pauseUsed = false;
                }
            }
            await Task.Delay(_pollInterval, cancellationToken);
        }

        // An accepted navigation command may still execute later. Seeing the original
        // metadata is not proof that it is safe to play/unmute at its old position.
        if (!pauseUsed)
            await PauseIfSafeAsync(last, initial, cancellationToken);
        return Result(false, false, rollbackUsed
            ? "the single correction could not verify original-track 0:00 before timeout; no further navigation was sent."
            : "same-track paused 0:00 was not confirmed before timeout; Previous was not repeated and the endpoint must remain muted.");
    }

    private async Task<PreviousRestartSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _session.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    private async Task PauseIfSafeAsync(PreviousRestartSnapshot? current, PreviousRestartSnapshot initial, CancellationToken cancellationToken)
    {
        // Do not act on a replacement session or overwrite a locally observed user command.
        if (current is not null && SameOperation(current, initial) &&
            current.PlaybackStatus == MediaTransportPlaybackStatus.Playing && current.CanPause)
        {
            await _session.PauseAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool SameOperation(PreviousRestartSnapshot? current, PreviousRestartSnapshot initial) =>
        current is not null && current.SessionId == initial.SessionId && current.NavigationRevision == initial.NavigationRevision;

    private static bool SameTrack(PreviousRestartSnapshot? current, TrackSnapshot expected) =>
        current is not null && current.Track.UniqueKey == expected.UniqueKey;

    private static bool CanRollback(PreviousRestartSnapshot initial, PreviousRestartSnapshot current) =>
        current.CanNext && initial.IsShuffleActive == false && current.IsShuffleActive == false &&
        initial.RepeatMode is MediaTransportRepeatMode.Off or MediaTransportRepeatMode.All &&
        current.RepeatMode is MediaTransportRepeatMode.Off or MediaTransportRepeatMode.All;

    private PreviousRestartResult Result(bool restarted, bool safe, string detail)
    {
        if (restarted) _logger.Info($"Previous-restart confirmed: {detail}");
        else _logger.Warn($"Previous-restart skipped/failed: {detail} SafeToResume={safe}.");
        return new(restarted, safe, detail);
    }

    private void Trace(string stage, PreviousRestartSnapshot snapshot) =>
        _logger.Info($"Previous-restart {stage}: session={snapshot.SessionId}, track={snapshot.Track.UniqueKey}, " +
            $"state={snapshot.PlaybackStatus}, position={snapshot.Position}, previous={snapshot.CanPrevious}, " +
            $"next={snapshot.CanNext}, shuffle={snapshot.IsShuffleActive}, repeat={snapshot.RepeatMode}, navigationRevision={snapshot.NavigationRevision}.");
}
