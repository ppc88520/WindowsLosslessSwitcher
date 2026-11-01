using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed partial class SwitchingCoordinator
{
    private volatile bool _dacLockPaused;
    private bool _dacLockNeedsPlaybackSettlement;
    private int _dacLockChangeActive;
    private string _dacLockStatusMessage = "DAC lock is active.";

    public bool IsDacLockPaused => _dacLockPaused;
    public bool IsDacLockChangeInProgress => Volatile.Read(ref _dacLockChangeActive) != 0;
    public string DacLockStatusMessage => Volatile.Read(ref _dacLockStatusMessage);

    /// <summary>Startup-only, memory-only policy initialization, before the first output census.</summary>
    public void InitializeOutputLockPaused(bool paused)
    {
        _dacLockPaused = paused;
        _audioOutputGuard?.SetRoutingPaused(paused);
        Volatile.Write(ref _dacLockStatusMessage, paused
            ? "DAC lock paused. You can select another output in Windows." : "DAC lock is active.");
    }

    /// <summary>
    /// The pause prefix is memory-only and takes effect on the caller immediately. Native
    /// settling and routing always run on a worker; duplicate transitions never queue.
    /// </summary>
    public Task<DacLockChangeResult> SetOutputLockPausedAsync(bool paused, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _dacLockChangeActive, 1, 0) != 0)
            return Task.FromResult(new DacLockChangeResult(false, _dacLockPaused, "A DAC lock change is already in progress."));
        if (paused)
        {
            _dacLockPaused = true;
            _audioOutputGuard?.SetRoutingPaused(true);
            Volatile.Write(ref _dacLockStatusMessage, "Pausing DAC lock. Waiting for existing audio work to stop.");
        }
        return Task.Run(() => ChangeOutputLockOnWorkerAsync(paused, token));
    }

    private async Task<DacLockChangeResult> ChangeOutputLockOnWorkerAsync(bool paused, CancellationToken token)
    {
        var acquired = false;
        var resumed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (_stopping || _manualRestartRequested)
                return LockChangeFailed("DAC lock could not be changed while the tool is stopping or restarting.");

            // This also waits out an observer already inside EnsureReady. The immediate
            // decorator gate stops any following role change while that old call settles.
            if (!Monitor.TryEnter(_audioOutputSafetySync, TimeSpan.FromSeconds(5)))
                return LockChangeFailed("DAC lock is paused, but the output observer has not stopped. Wait for device discovery to finish, then retry.");
            try
            {
                _dacLockNeedsPlaybackSettlement |= _transportPausedByCoordinator || _mutedDeviceId is not null;
                HoldForAudioOutputChange();
            }
            finally { Monitor.Exit(_audioOutputSafetySync); }
            acquired = await _switchLock.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            if (!acquired)
                return LockChangeFailed("DAC lock is paused, but existing audio work has not stopped. Protective mute has been kept. Pause Apple Music before retrying.");
            token.ThrowIfCancellationRequested();
            if (_stopping || _manualRestartRequested)
                return LockChangeFailed("DAC lock could not be changed while the tool is stopping or restarting.");

            if (!await SettleOwnedPlaybackForDacLockAsync(token).ConfigureAwait(false))
                return LockChangeFailed("DAC lock is paused, but a safe playback stop could not be confirmed. Protective mute has been kept. Pause Apple Music manually before retrying.");

            if (paused)
            {
                Volatile.Write(ref _dacLockStatusMessage, "DAC lock paused. You can select another output in Windows.");
                PublishDacLockPaused();
                return new(true, true, DacLockStatusMessage);
            }

            // Old processing has released the switch lock, all tracked remote commands are
            // settled, and the coordinator itself stays paused throughout output selection.
            token.ThrowIfCancellationRequested();
            if (_stopping || _manualRestartRequested)
                return LockChangeFailed("DAC lock could not be changed while the tool is stopping or restarting.");
            _audioOutputGuard?.SetRoutingPaused(false);
            token.ThrowIfCancellationRequested();
            if (_stopping || _manualRestartRequested)
                return LockChangeFailed("DAC lock could not be changed while the tool is stopping or restarting.");
            var ready = _audioOutputGuard?.EnsureReady();
            token.ThrowIfCancellationRequested();
            if (_stopping || _manualRestartRequested || ready is not { IsReady: true, Device: not null })
                return LockChangeFailed("DAC lock remains paused. Connect a supported DAC and confirm the default output before trying again.");
            lock (_audioOutputSafetySync)
            {
                _readyAudioDeviceId = ready.Device.Id;
                Volatile.Write(ref _readyAudioDeviceVersion, _audioOutputGuard!.DeviceChangeVersion);
                _audioOutputSafetyHold = false;
                _dacLockPaused = false;
            }
            Volatile.Write(ref _dacLockStatusMessage, "DAC lock resumed. Playback will not start automatically.");
            resumed = true;
            return new(true, false, DacLockStatusMessage);
        }
        catch (OperationCanceledException)
        {
            return LockChangeFailed("DAC lock is paused. The change was interrupted before safe completion; no playback command was retried.");
        }
        catch (TimeoutException)
        {
            return LockChangeFailed("DAC lock is paused, but a playback command may still be queued. Protective mute has been kept. Pause Apple Music and restart the tool before retrying.");
        }
        catch (Exception ex)
        {
            _logger.Warn("DAC lock change failed: " + ex.Message);
            return LockChangeFailed("DAC lock is paused because the change could not be completed safely. Pause Apple Music before retrying.");
        }
        finally
        {
            if (acquired) _switchLock.Release();
            Volatile.Write(ref _dacLockChangeActive, 0);
            // Resume ordinary processing only for a player which is already Playing.
            // A paused player remains paused until an explicit Play from the user.
            if (resumed && !_stopping && !_manualRestartRequested) RefreshAudioOutputSafety();
        }
    }

    private DacLockChangeResult LockChangeFailed(string message)
    {
        _dacLockPaused = true;
        _audioOutputGuard?.SetRoutingPaused(true);
        Volatile.Write(ref _dacLockStatusMessage, message);
        PublishDacLockPaused();
        return new(false, true, message);
    }

    private async Task<bool> SettleOwnedPlaybackForDacLockAsync(CancellationToken token)
    {
        var state = ReadPlaybackState();
        if (HasUnconfirmedPlaybackCommand) return false;
        // Releasing the routing policy is independent of the player's Play/Pause control.
        // Healthy ordinary playback is left alone; only tool-owned work needs settling.
        if (_mutedDeviceId is null && !_transportPausedByCoordinator && !_dacLockNeedsPlaybackSettlement) return true;
        _audioOutputGuard?.RefreshHardwareIdentityForCleanup();
        if (!IsUserPauseSafe(state))
        {
            if (!state.IsPlaying || !state.CanPause ||
                !await InvokeTrackedPlaybackCommandAsync(
                    ct => BoundedMediaCommand.InvokeAsync(_mediaTransportController.TryPauseAsync, "Pause for DAC lock", ct),
                    MediaTransportPlaybackStatus.Paused, token).ConfigureAwait(false)) return false;
        }
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        var stable = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            stable = IsUserPauseSafe(ReadPlaybackState()) && !HasUnconfirmedPlaybackCommand ? stable + 1 : 0;
            if (stable >= 3)
            {
                _transportPausedByCoordinator = false;
                _dacLockNeedsPlaybackSettlement = false;
                UnmuteTargetDeviceAfterProcessing(allowUserPause: true, allowDacLockCleanup: true);
                return _mutedDeviceId is null;
            }
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        return false;
    }

    private void PublishDacLockPaused()
    {
        var previous = Volatile.Read(ref _lastPublishedStatus);
        if (previous?.ResolverStatusText == "DAC lock paused" && previous.FailureReason == DacLockStatusMessage) return;
        PublishStatus(new SwitchingStatus("DAC lock paused", null, _currentTrackSnapshot, null, null, DacLockStatusMessage));
    }
}
