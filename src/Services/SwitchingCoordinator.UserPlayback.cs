using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed partial class SwitchingCoordinator
{
    private volatile bool _userPauseRequested;
    private int _userPlaybackCommandActive;
    private TrackSnapshot? _userPausedTrack;
    private readonly PlaybackCommandTracker _playbackCommands = new();
    private SwitchingStatus? _lastPublishedStatus;

    // Acceptance of a remote command is not confirmation of its eventual effect.
    // Keep an accepted Play pending until Playing was actually observed. In particular,
    // a stale Paused snapshot cannot make cancellation followed by unmute safe.
    private MediaTransportState ReadPlaybackState() => _playbackCommands.Read(_mediaTransportController.GetPlaybackState);

    private bool HasUnconfirmedPlaybackCommand => _playbackCommands.HasUnconfirmed;

    private async Task<bool> InvokeTrackedPlaybackCommandAsync(
        Func<CancellationToken, Task<bool>> dispatch, MediaTransportPlaybackStatus expectedState, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_playbackCommands.HasPendingExpected)
        {
            // A normal recovery nudge can wait between Pause and Play without another
            // reader. Confirm that previous effect now before classifying it as unknown.
            ReadPlaybackState();
        }
        var generation = _playbackCommands.Begin(expectedState);
        var accepted = false;
        var outcomeKnown = false;
        try
        {
            accepted = await dispatch(token);
            outcomeKnown = true;
        }
        finally { _playbackCommands.Complete(generation, accepted, outcomeKnown); }
        return accepted;
    }

    private bool HasUserPausedTrack()
    {
        lock (_processingSync) return _userPausedTrack is not null;
    }

    public UserPlaybackAvailability GetUserPlaybackAvailability()
    {
        var state = ReadPlaybackState();
        var stopped = _stopping || _manualRestartRequested;
        var busy = stopped || _userPauseRequested || Volatile.Read(ref _userPlaybackCommandActive) != 0 ||
            _switchLock.CurrentCount == 0;
        var known = state.PlaybackStatus is MediaTransportPlaybackStatus.Playing or
            MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped;
        return new(state.PlaybackStatus,
            !busy && !HasUnconfirmedPlaybackCommand && !_previousRestartSafetyHold && _manualTrackHold is null &&
                (state.PlaybackStatus is MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped) && state.CanPlay,
            !stopped && !_playbackCommands.IsUncertain && !_userPauseRequested && Volatile.Read(ref _userPlaybackCommandActive) == 0 && state.IsPlaying && state.CanPause,
            !busy && !HasUnconfirmedPlaybackCommand && !_previousRestartSafetyHold && known && state.CanPrevious,
            !busy && !HasUnconfirmedPlaybackCommand && !_previousRestartSafetyHold && known && state.CanNext,
            busy,
            _playbackCommands.IsUncertain || _previousRestartSafetyHold || _manualTrackHold is not null);
    }

    /// <summary>
    /// Explicit UI actions share the switch lock. Navigation and Play never queue behind
    /// a switch; Pause cancels old work before confirming the player's settled state.
    /// </summary>
    public async Task<UserPlaybackResult> ExecuteUserPlaybackAsync(UserPlaybackAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _userPlaybackCommandActive, 1, 0) != 0)
            return new(false, "Playback controls are temporarily unavailable.");
        try
        {
            if (_stopping || _manualRestartRequested)
                return new(false, "Playback controls are unavailable during restart.");
            if (action == UserPlaybackAction.Pause)
                return await PauseForUserAsync(token);
            if (_previousRestartSafetyHold || (action == UserPlaybackAction.Play && _manualTrackHold is not null))
                return new(false, "Playback is held for safety. Follow the recovery guidance.");
            if (_userPauseRequested || !await _switchLock.WaitAsync(0, token))
                return new(false, "Playback controls are temporarily unavailable.");
            TrackSnapshot? resumeTrack = null;
            try
            {
                if (_stopping || _manualRestartRequested || _previousRestartSafetyHold || _previousRestartGuardActive ||
                    (action == UserPlaybackAction.Play && _manualTrackHold is not null))
                    return new(false, "Playback is held for safety. Follow the recovery guidance.");
                var state = ReadPlaybackState();
                if (HasUnconfirmedPlaybackCommand)
                    return new(false, "Playback is held for safety. Follow the recovery guidance.");
                var enabled = action switch
                {
                    UserPlaybackAction.Play => state.CanPlay && (state.PlaybackStatus is MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped),
                    UserPlaybackAction.Previous => state.CanPrevious,
                    UserPlaybackAction.Next => state.CanNext,
                    _ => false,
                };
                if (!enabled || state.PlaybackStatus is MediaTransportPlaybackStatus.Unknown or MediaTransportPlaybackStatus.Closed or MediaTransportPlaybackStatus.Changing)
                    return new(false, "Apple Music does not currently support this control.");
                Func<CancellationToken, Task<bool>> dispatch = action switch
                {
                    UserPlaybackAction.Play => _mediaTransportController.TryPlayAsync,
                    UserPlaybackAction.Previous => _mediaTransportController.TrySkipPreviousAsync,
                    UserPlaybackAction.Next => _mediaTransportController.TrySkipNextAsync,
                    _ => throw new ArgumentOutOfRangeException(nameof(action)),
                };
                var accepted = await InvokeTrackedPlaybackCommandAsync(
                    ct => BoundedMediaCommand.InvokeAsync(dispatch, action.ToString(), ct),
                    action == UserPlaybackAction.Play ? MediaTransportPlaybackStatus.Playing : MediaTransportPlaybackStatus.Unknown, token);
                if (!accepted) return new(false, "Apple Music did not accept this command.");
                if (action == UserPlaybackAction.Play)
                {
                    lock (_processingSync)
                    {
                        resumeTrack = _userPausedTrack;
                        _userPausedTrack = null;
                    }
                }
                return new(true, "Command sent to Apple Music.");
            }
            catch (TimeoutException)
            {
                HoldAfterUserCommand("A user playback command timed out and may still be queued; it was not retried.");
                return new(false, "Playback command timed out. Pause Apple Music and restart the tool.");
            }
            catch (OperationCanceledException)
            {
                HoldAfterUserCommand("A user playback command was canceled with an uncertain remote result; no retry is safe.");
                return new(false, "Playback is held for safety. Follow the recovery guidance.");
            }
            finally
            {
                _switchLock.Release();
                // A pause may have canceled resolution before it finished. An explicit Play
                // re-enters ordinary same-track processing rather than bypassing its guards.
                if (resumeTrack is not null && !_stopping && !_manualRestartRequested)
                    OnTrackChanged(this, resumeTrack);
            }
        }
        finally { Volatile.Write(ref _userPlaybackCommandActive, 0); }
    }

    private async Task<UserPlaybackResult> PauseForUserAsync(CancellationToken token)
    {
        _userPauseRequested = true;
        var acquired = false;
        try
        {
            lock (_processingSync)
            {
                _userPausedTrack = _currentTrackSnapshot;
                _currentTrackCts?.Cancel();
                _deferredRateSwitch = null;
                _suppressWatchdogUntilNextTrack = true;
            }
            acquired = await _switchLock.WaitAsync(TimeSpan.FromSeconds(5), token);
            if (!acquired)
            {
                HoldAfterUserCommand("Canceled processing did not stop before a user pause could be verified.");
                return new(false, "Pause could not be confirmed. Follow the recovery guidance.");
            }
            if (_stopping || _manualRestartRequested)
                return new(false, "Playback controls are unavailable during restart.");
            var state = ReadPlaybackState();
            if (HasUnconfirmedPlaybackCommand)
            {
                HoldAfterUserCommand("An earlier playback command may still execute. A reported pause cannot authorize unmuting or restarting.");
                return new(false, "Pause could not be confirmed. Follow the recovery guidance.");
            }
            if (!IsUserPauseSafe(state))
            {
                if (!state.IsPlaying || !state.CanPause ||
                    !await InvokeTrackedPlaybackCommandAsync(
                        ct => BoundedMediaCommand.InvokeAsync(_mediaTransportController.TryPauseAsync, "Pause", ct),
                        MediaTransportPlaybackStatus.Paused, token))
                {
                    HoldAfterUserCommand("The player's pause request was rejected or unavailable.");
                    return new(false, "Pause could not be confirmed. Follow the recovery guidance.");
                }
            }
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            var consecutiveSafeReads = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                consecutiveSafeReads = IsUserPauseSafe(ReadPlaybackState()) ? consecutiveSafeReads + 1 : 0;
                if (consecutiveSafeReads >= 3)
                {
                    _transportPausedByCoordinator = false;
                    if (!_previousRestartSafetyHold && _manualTrackHold is null)
                        UnmuteTargetDeviceAfterProcessing(allowUserPause: true);
                    var previous = Volatile.Read(ref _lastPublishedStatus);
                    var device = GetCurrentTargetDeviceFormat();
                    PublishStatusIfCurrent(Volatile.Read(ref _currentTrackGeneration), new SwitchingStatus(
                        "Resolver: Paused by user", device.DeviceName ?? previous?.ActiveDeviceName,
                        _currentTrackSnapshot ?? previous?.Track, previous?.RequestedFormat,
                        device.Format ?? previous?.AppliedFormat, null));
                    return new(true, "Playback paused.");
                }
                await Task.Delay(50, token);
            }
            HoldAfterUserCommand("The player did not report a stable paused state after the user's request.");
            return new(false, "Pause could not be confirmed. Follow the recovery guidance.");
        }
        catch (TimeoutException)
        {
            HoldAfterUserCommand("A user pause timed out and may still be queued; it was not retried.");
            return new(false, "Playback command timed out. Pause Apple Music and restart the tool.");
        }
        catch (OperationCanceledException)
        {
            HoldAfterUserCommand("User pause preparation was interrupted before a safe pause was confirmed.");
            return new(false, "Pause could not be confirmed. Follow the recovery guidance.");
        }
        finally
        {
            if (acquired) _switchLock.Release();
            _userPauseRequested = false;
        }
    }

    private static bool IsUserPauseSafe(MediaTransportState state) => state.PlaybackStatus is
        MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed;

    private void HoldAfterUserCommand(string detail)
    {
        var correlation = new DiagnosticsLogger.LogCorrelation(Volatile.Read(ref _currentTrackGeneration), _currentTrackKey);
        MuteTargetDeviceForProcessing(_lastTargetDeviceId, correlation);
        SetPreviousRestartSafetyHold(correlation, detail);
        PublishStatusIfCurrent(Volatile.Read(ref _currentTrackGeneration), new SwitchingStatus(
            "Resolver: Failed", null, _currentTrackSnapshot, null, null, GetGlobalHoldMessage(), Recovery: RecoveryCategory.UnsafeState));
    }
}
