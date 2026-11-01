using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed partial class SwitchingCoordinator
{
    private readonly IAudioOutputGuard? _audioOutputGuard;
    private readonly object _audioOutputSafetySync = new();
    private volatile bool _audioOutputSafetyHold;
    private volatile string? _readyAudioDeviceId;
    private long _readyAudioDeviceVersion;
    private volatile bool _audioOutputReprocessPending;

    /// <summary>Memory-only barrier used before waiting for background UI queries at exit.</summary>
    public void RequestShutdown()
    {
        _stopping = true;
        _manualRestartRequested = true;
        _audioOutputGuard?.SetRoutingPaused(true);
    }

    /// <summary>
    /// Called at startup and by the UI's device monitor. Routing is confirmed before any
    /// capabilities, format changes or automatic playback are allowed. Old work is canceled
    /// even when routing can be repaired in this same census.
    /// </summary>
    public AudioOutputGuardState? RefreshAudioOutputSafety()
    {
        if (_audioOutputGuard is null) return null;
        TrackSnapshot? resumeTrack = null;
        AudioOutputGuardState state;
        lock (_audioOutputSafetySync)
        {
            if (_stopping) return _audioOutputGuard.CurrentState;
            if (_dacLockPaused)
            {
                PublishDacLockPaused();
                return new(false, null, DacLockStatusMessage);
            }
            if (_readyAudioDeviceId is not null &&
                (Volatile.Read(ref _readyAudioDeviceVersion) != _audioOutputGuard.DeviceChangeVersion ||
                 !_audioOutputGuard.IsReadyForDevice(_readyAudioDeviceId)))
                HoldForAudioOutputChange();
            var beforeCensusVersion = _audioOutputGuard.DeviceChangeVersion;
            state = _audioOutputGuard.EnsureReady();
            if (_readyAudioDeviceId is not null &&
                (beforeCensusVersion != _audioOutputGuard.DeviceChangeVersion ||
                 Volatile.Read(ref _readyAudioDeviceVersion) != _audioOutputGuard.DeviceChangeVersion))
                HoldForAudioOutputChange();
            if (!state.IsReady || state.Device is null)
            {
                HoldForAudioOutputChange();
                PublishAudioOutputWaiting(state.Detail);
                return state;
            }

            // Do not let the repaired default route revive cleanup in a canceled task.
            // Wait for that task to stop, then start a fresh generation on the verified DAC.
            if (_audioOutputSafetyHold && (HasActiveTrackTasks() || _switchLock.CurrentCount == 0))
                return state with { IsReady = false, Detail = "DAC output is ready. Waiting for canceled processing to stop." };

            _readyAudioDeviceId = state.Device.Id;
            Volatile.Write(ref _readyAudioDeviceVersion, _audioOutputGuard.DeviceChangeVersion);
            _audioOutputSafetyHold = false;
            if (_audioOutputReprocessPending && !HasActiveTrackTasks() && _switchLock.CurrentCount != 0 && !_manualRestartRequested && !_previousRestartSafetyHold &&
                _manualTrackHold is null && !_userPauseRequested && !HasUserPausedTrack() &&
                ReadPlaybackState().IsPlaying)
            {
                lock (_processingSync) resumeTrack = _currentTrackSnapshot;
                _audioOutputReprocessPending = false;
            }
        }
        if (resumeTrack is not null) OnTrackChanged(this, resumeTrack);
        return state;
    }

    private void HoldForAudioOutputChange()
    {
        _audioOutputSafetyHold = true;
        _readyAudioDeviceId = null;
        _lastTargetDeviceId = null;
        // Device loss must never inherit an automatic resume into the fallback speakers.
        _transportPausedByCoordinator = false;
        CancellationTokenSource? canceled;
        lock (_processingSync)
        {
            _audioOutputReprocessPending |= _currentTrackSnapshot is not null;
            canceled = _currentTrackCts;
            _deferredRateSwitch = null;
        }
        // Cancellation may synchronously run continuations; never do so while holding processing state.
        try { canceled?.Cancel(); } catch (ObjectDisposedException) { }
        if (_previousRestartGuardActive)
        {
            _previousRestartSafetyHold = true;
            _previousRestartSafetyHoldDetail = "DAC output changed during guarded navigation. Automatic playback remains suspended.";
        }
    }

    private bool IsAudioOutputReady()
    {
        if (_dacLockPaused)
        {
            PublishDacLockPaused();
            return false;
        }
        if (_audioOutputGuard is null) return true;
        var deviceId = _readyAudioDeviceId;
        if (!_audioOutputSafetyHold && deviceId is not null &&
            Volatile.Read(ref _readyAudioDeviceVersion) == _audioOutputGuard.DeviceChangeVersion &&
            _audioOutputGuard.IsReadyForDevice(deviceId))
            return true;
        lock (_audioOutputSafetySync)
        {
            HoldForAudioOutputChange();
            PublishAudioOutputWaiting(_audioOutputGuard.CurrentState.Detail);
        }
        return false;
    }

    private void PublishAudioOutputWaiting(string detail)
    {
        const string status = "Waiting for a supported DAC";
        var previous = Volatile.Read(ref _lastPublishedStatus);
        if (previous?.ResolverStatusText == status && previous.FailureReason == detail) return;
        PublishStatus(new SwitchingStatus(status, null, _currentTrackSnapshot, null, null, detail));
    }
}
