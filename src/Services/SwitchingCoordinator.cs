using System.Diagnostics;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class SwitchingCoordinator : IAsyncDisposable
{

    // How often to re-sample playback status and master peak while confirming the pause settled.
    private static readonly TimeSpan PausedConfirmationPollInterval = TimeSpan.FromMilliseconds(50);

    // Master peak at/below this counts as silence (render buffer drained). Mirrors
    // PlaybackRestorePeakThreshold so idle-bus noise / digital silence isn't read as active output.
    private const float PauseSilenceThreshold = 0.001f;

    // Consecutive silent peak reads required before trusting the endpoint is drained. Guards against
    // a single inter-buffer dip being mistaken for a fully drained stream.
    private const int PauseSilenceConsecutiveReads = 3;

    // How often to re-read the device format while waiting for it to stabilize after a switch.
    private static readonly TimeSpan DeviceReadyPollInterval = TimeSpan.FromMilliseconds(100);

    // How often to sample playback state and audio peak when confirming that output has resumed.
    private static readonly TimeSpan PlaybackRestorePollInterval = TimeSpan.FromMilliseconds(100);

    // Total observation window after resume. Keeps the coordinator in the "confirming" state
    // long enough to detect audio output even if there is a brief buffering delay at song start.
    private static readonly TimeSpan PlaybackRestoreObservationWindow = TimeSpan.FromSeconds(4);

    // Minimum timeline advance required to count as confirmed playback progress.
    // 250 ms filters out timer jitter while still catching real forward motion quickly.
    private static readonly TimeSpan TimelineAdvanceThreshold = TimeSpan.FromMilliseconds(250);

    // Require two consecutive device-format reads to agree before declaring the device ready.
    // One read is insufficient because the audio engine briefly reports the old format after a switch.
    private const int DeviceReadyMatchThreshold = 2;

    // Minimum master peak value that confirms audio is flowing. 0.001 (0.1 %) filters out
    // digital silence and idle bus noise while catching any real audio output.
    private const float PlaybackRestorePeakThreshold = 0.001f;

    // Hard ceiling on how long we wait for device-format confirmation before resuming anyway.
    private const int FormatSwitchPauseTimeoutSeconds = 5;

    // Max pause→play "nudge" cycles attempted when playback restore cannot be confirmed after a
    // format switch. The device format change sometimes kills Apple Music's render stream while
    // GSMTC still reports Playing; a pause/play cycle forces it to rebuild the stream.
    private const int MaxPlaybackRecoveryNudges = 2;

    // Gap between the nudge pause and the follow-up play, giving Apple Music time to tear down
    // the dead render stream before recreating it on the new device format.
    private static readonly TimeSpan PlaybackRecoveryNudgeDelay = TimeSpan.FromMilliseconds(400);

    // Re-observation window after each recovery nudge. Live testing showed Apple Music can take
    // a little over 2 s to rebuild its render stream after a high-rate (384 kHz) format switch,
    // so this matches the initial observation window rather than cutting recovery short.
    private static readonly TimeSpan PlaybackRecoveryObservationWindow = TimeSpan.FromSeconds(4);

    // Bounded retry for the resume command. GSMTC occasionally rejects the first play issued
    // right after a device format change; a couple of quick retries recover without visible delay.
    private const int ResumePlayAttemptLimit = 3;

    // Delay between resume attempts.
    private static readonly TimeSpan ResumePlayRetryDelay = TimeSpan.FromMilliseconds(250);

    // Apple Music renders audio through Apple's media agent process, which keeps its WASAPI
    // render stream RUNNING for ~5 s after a pause before stopping it. Changing the device
    // format while that stream is still running invalidates it mid-pump, which is what produced
    // "zombie" playback (SMTC reports Playing, nothing renders, timeline frozen). Waiting for
    // the agent's audio session to go inactive before applying the format removes the cause.
    private const string AppleMediaAgentProcessName = "AMPLibraryAgent";

    // How often to re-check the agent's session state while waiting for the stream to stop.
    private static readonly TimeSpan RenderStreamReleasePollInterval = TimeSpan.FromMilliseconds(200);

    // Window to wait for the new track's render stream to spin up (while playing muted) BEFORE
    // switching. Skipping to a track — particularly a local file Apple Music must download —
    // can leave the stream cold for several seconds; switching into that state zombies, but
    // declaring the track dead too early turns a slow load into a skipped song.
    private static readonly TimeSpan RenderStreamStartupTimeout = TimeSpan.FromSeconds(6);

    // Playback health check for tracks handled WITHOUT a format switch (already-active format,
    // skipped switches). Apple Music can wedge entirely on its own during rapid track skips:
    // SMTC reports Playing while the media agent never starts a render stream. Sustained
    // playing-with-no-stream for this long is the zombie signature — a genuinely quiet track
    // keeps an ACTIVE session, so silent intros do not false-positive. Generous enough to
    // tolerate slow local-file loads (cloud downloads can take many seconds to start
    // rendering); acting too early turns a slow start into a skipped song.
    private static readonly TimeSpan PlaybackHealthZombieThreshold = TimeSpan.FromSeconds(10);

    // How often to sample transport state and the agent's session during the health check.
    // A healthy track exits on the first active reading, so the check adds no latency.
    private static readonly TimeSpan PlaybackHealthPollInterval = TimeSpan.FromMilliseconds(400);

    // Final recovery escalation: when the pause/play nudges cannot revive a hard-wedged renderer
    // (proven live — some wedges survive every nudge), the only reliable cure is a track change,
    // which forces Apple Music to rebuild its playback pipeline. The wedged track produces no
    // audio anyway, so skipping forward beats permanent silence. The cooldown guarantees this
    // can never runaway-skip through a playlist if Apple Music stays wedged.
    private static readonly TimeSpan RecoverySkipCooldown = TimeSpan.FromSeconds(30);

    // Relaxed skip cooldown once CONSECUTIVE tracks stall without an applied switch. That
    // pattern is not renderer damage — it is a run of tracks Apple Music itself cannot play
    // (bad local files: renderer never starts), and each one already costs ~15-20 s of stall
    // detection before its skip can fire, so detection latency alone bounds the skip rate.
    // The full cooldown would strand the queue in silence between dead tracks; this floor
    // only guards against back-to-back command bursts.
    private static readonly TimeSpan ConsecutiveNoSwitchStallSkipCooldown = TimeSpan.FromSeconds(5);

    // A track that never loaded needs Apple Music to redo the load, which takes longer than the
    // mid-stream nudge settle; the observation window matches, since audio arrives late here.
    private static readonly TimeSpan PatientRetrySettleDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PatientRetryObservationWindow = TimeSpan.FromSeconds(6);

    // The continuous watchdog samples playback at this interval whenever no track is being
    // processed. The per-path health checks only run at track-processing time, but a wedge can
    // strike at ANY moment (proven live: a track passed its processing-time check, then died
    // minutes later and sat silent at 0:00 with nothing watching).
    private static readonly TimeSpan WatchdogPollInterval = TimeSpan.FromSeconds(5);

    // After a watchdog-triggered recovery FAILS, wait this long before the watchdog may trigger
    // recovery again, so a persistent wedge is not hammered with nudges every few seconds.
    private static readonly TimeSpan WatchdogFailureCooldown = TimeSpan.FromSeconds(60);

    // Nudge cycles on the NO-SWITCH wedge paths (health check, watchdog). Live evidence across
    // every observed cascade: a no-switch wedge that survives one nudge survives them all, and
    // each extra nudge adds ~5 s of dead air before the escalation that actually works.
    // Post-switch restores keep MaxPlaybackRecoveryNudges — nudges demonstrably succeed there.
    private const int NoSwitchRecoveryNudgeLimit = 1;

    // Cascade detection. Nudges and the skip-next escalation handle ISOLATED wedges, but once
    // Apple Music's renderer enters its degraded state, consecutive tracks wedge, nudges fail
    // 100% of the time, and skipping just moves the wedge forward through the queue (proven
    // live, 01:51–02:15 cascades). Repeated recovery failures in a short window are that
    // state's signature, and the only cure ever observed is restarting Apple Music.
    private static readonly TimeSpan CascadeFailureWindow = TimeSpan.FromMinutes(3);
    private const int CascadeFailureThreshold = 3;

    // Floor between automatic Apple Music restarts, so the escalation can never loop even if
    // the degradation somehow survives a restart.
    private static readonly TimeSpan AppleMusicRestartCooldown = TimeSpan.FromMinutes(10);

    // After the relaunch: how long to wait for Apple Music's media session to reappear, how
    // often to poll for it, how long to let the app settle before issuing play, and how long
    // to watch for real audio (cold app start + stream spin-up takes longer than a resume).
    private static readonly TimeSpan PostRestartSessionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PostRestartSessionPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PostRestartPlayDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostRestartRestoreObservationWindow = TimeSpan.FromSeconds(10);

    // The pre-pause gate must see the stream active CONTINUOUSLY for this long before trusting
    // it. Right after a track change the active stream is usually the OLD track's dying stream
    // (it stops within ~1 s); pausing into it and switching is what zombies playback. The new
    // track's stream survives this window, the old one cannot. This replaces the old absolute
    // drain-duration law, which proved agent-instance-specific (an aged agent held its stream
    // ~5 s after pause; a freshly spawned one releases in ~1.1 s — both healthy).
    private static readonly TimeSpan SustainedStreamActiveDuration = TimeSpan.FromMilliseconds(1500);

    // After a live format change the agent's rebuilt stream typically produces audio within
    // ~4 s (verified live for same- and cross-family switches); margin for slow local files.
    private static readonly TimeSpan StreamRebuildTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StreamRebuildPollInterval = TimeSpan.FromMilliseconds(150);

    // Previous can navigate the queue. Wait away from the beginning, then delegate the
    // single command and fresh identity verification to the guarded transport.
    private static readonly TimeSpan RestartPauseConfirmationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartConfirmationPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PreviousRestartMinimumPosition = GuardedPreviousRestart.MinimumPosition;
    private static readonly TimeSpan PreviousRestartReadinessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PausedSwitchRestartReadinessTimeout = TimeSpan.FromSeconds(15);
    private const string PreviousRestartManualRecoveryMessage = "Playback manual recovery required: a safe restart could not be confirmed. Automatic switching is suspended. Pause Apple Music, exit the switcher, select the intended song, then reopen the switcher.";


    private readonly ITrackSource _trackSource;
    private readonly IMediaTransportController _mediaTransportController;
    private readonly IPlaybackStateNotifier? _playbackStateNotifier;
    private readonly ResolverChain _resolverChain;
    private readonly FormatCacheVerificationService? _formatCacheVerificationService;
    private readonly IAudioEndpointController _audioEndpointController;
    private readonly DiagnosticsLogger _logger;
    private readonly IAppleMusicProcessController? _appleMusicProcessController;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly object _processingSync = new();
    private readonly object _activeTaskSync = new();
    private readonly object _cacheVerificationSync = new();
    private readonly object _muteOwnershipSync = new();
    private readonly HashSet<Task> _activeTrackTasks = [];
    private readonly Dictionary<string, Task> _activeCacheVerifications = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cacheVerificationLifetimeCts = new();
    private CancellationTokenSource? _currentTrackCts;
    private string? _currentTrackKey;
    private TrackSnapshot? _currentTrackSnapshot;
    private TrackSnapshot? _precedingTrackSnapshot;
    private long _currentTrackGeneration;
    private volatile bool _previousRestartGuardActive;
    private volatile bool _previousRestartSafetyHold;
    private string? _previousRestartSafetyHoldDetail;
    private volatile ManualTrackHold? _manualTrackHold;
    private DeferredRateSwitch? _deferredRateSwitch;
    private volatile bool _manualRestartRequested;
    private bool _waitForUserPlaybackAfterRestart;
    private RecoveryCategory _globalRecoveryCategory = RecoveryCategory.UnsafeState;
    private volatile bool _stopping;
    private bool _cacheVerificationStopping;

    // Device muted by the coordinator to silence pre-switch playback. Ownership transfers to a
    // superseding track's processing; cleared on unmute. Only touched while holding _switchLock
    // (or during disposal after all track tasks have completed).
    private string? _mutedDeviceId;

    // True while playback is paused by the coordinator (not by the user). Lets a superseding
    // track's processing inherit the paused state and guarantee a resume once it finishes.
    private bool _transportPausedByCoordinator;

    // Target device from the previous track processing, used to mute instantly on the next
    // track change before the (slower) device selection runs.
    private string? _lastTargetDeviceId;

    // Last time recovery escalated to a skip-next, enforcing RecoverySkipCooldown.
    private DateTimeOffset _lastRecoverySkipUtc = DateTimeOffset.MinValue;

    // True when the last recovery skip was for a stall with no applied switch. Consecutive
    // no-switch stalls (a cluster of tracks Apple Music cannot play) relax the skip cooldown
    // so the queue moves through the cluster quickly. Cleared when a recovery succeeds or a
    // switch-applied recovery skips. Only touched while holding _switchLock.
    private bool _lastRecoverySkipWasNoSwitchStall;

    // Whether a format switch has been applied since the current track began (track
    // processing or a manual original-format restore). Scopes recovery escalation: stalls
    // after an applied switch count toward the Apple Music restart threshold (switch-induced
    // renderer damage); stalls without one only ever skip forward.
    private bool _currentTrackSwitchApplied;
    private bool _patientRetryUsedForTrack;

    // Continuous playback watchdog (started in StartAsync, stopped on disposal).
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdogTask;
    private DateTimeOffset _lastWatchdogRecoveryFailureUtc = DateTimeOffset.MinValue;

    // Recent failed recoveries (cascade detection) and the last automatic Apple Music restart.
    // Only touched while holding _switchLock — every recovery path runs under it.
    private readonly List<DateTimeOffset> _recentRecoveryFailures = [];
    private DateTimeOffset _lastAppleMusicRestartUtc = DateTimeOffset.MinValue;

    // After a restart that could not resume playback, Apple Music sits queue-less but its media
    // session can report a phantom Playing-with-no-stream — the exact wedge signature. Recovery
    // is pointless there (verified live: the watchdog re-fired forever); stand down until a real
    // track change proves the user resumed. Cleared in BeginTrackProcessing.
    private bool _suppressWatchdogUntilNextTrack;

    public SwitchingCoordinator(
        ITrackSource trackSource,
        IMediaTransportController mediaTransportController,
        ResolverChain resolverChain,
        IAudioEndpointController audioEndpointController,
        DiagnosticsLogger logger,
        IAppleMusicProcessController? appleMusicProcessController = null,
        FormatCacheStore? formatCacheStore = null,
        IFormatResolver? catalogResolverForCacheVerification = null)
    {
        _trackSource = trackSource;
        _mediaTransportController = mediaTransportController;
        _playbackStateNotifier = mediaTransportController as IPlaybackStateNotifier ?? trackSource as IPlaybackStateNotifier;
        _resolverChain = resolverChain;
        if (formatCacheStore is not null && catalogResolverForCacheVerification is not null)
        {
            _formatCacheVerificationService = new FormatCacheVerificationService(
                formatCacheStore,
                catalogResolverForCacheVerification,
                logger);
        }

        _audioEndpointController = audioEndpointController;
        _logger = logger;
        _appleMusicProcessController = appleMusicProcessController;
    }

    public event EventHandler<SwitchingStatus>? StatusChanged;

    public event EventHandler<FormatCacheUpdateEventArgs>? FormatCacheUpdated;

    public AppSettings Settings { get; private set; } = new();

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Settings = settings;
        PublishStatus(new SwitchingStatus("Resolver: Waiting for Apple Music", null, null, null, null, null));
        _trackSource.TrackChanged += OnTrackChanged;
        if (_playbackStateNotifier is not null)
            _playbackStateNotifier.PlaybackStateChanged += OnPlaybackStateChanged;
        await _trackSource.StartAsync(cancellationToken);

        _watchdogCts = new CancellationTokenSource();
        _watchdogTask = Task.Run(() => RunPlaybackWatchdogAsync(_watchdogCts.Token), CancellationToken.None);
    }

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => _audioEndpointController.GetRenderDevices();

    public CurrentTargetDeviceFormatSnapshot GetCurrentTargetDeviceFormat()
    {
        var targetDevice = GetCurrentTargetDevice();
        if (targetDevice is null)
        {
            return new CurrentTargetDeviceFormatSnapshot(null, null, null);
        }

        return new CurrentTargetDeviceFormatSnapshot(
            targetDevice.Id,
            targetDevice.FriendlyName,
            _audioEndpointController.GetCurrentDeviceFormat(targetDevice.Id));
    }

    public async Task<FormatRestoreResult> RestoreOriginalFormatAsync(
        string deviceId,
        string? deviceName,
        AudioFormatCandidate format,
        CancellationToken cancellationToken)
    {
        await _switchLock.WaitAsync(cancellationToken);
        try
        {
            var targetDevice = _audioEndpointController.GetRenderDevices()
                .FirstOrDefault(device => device.Id == deviceId);
            var targetDeviceName = targetDevice?.FriendlyName ?? deviceName ?? "captured device";

            if (targetDevice is null)
            {
                var message = $"Captured device {targetDeviceName} is not currently available.";
                _logger.Warn(message);
                PublishStatus(new SwitchingStatus("Restore original format failed", targetDeviceName, null, null, null, message));
                return new FormatRestoreResult(false, message, null);
            }

            var variant = _audioEndpointController.DescribeSupportedFormat(targetDevice.Id, format) ?? "unknown native format";
            _logger.Info($"Restoring original format {format.DisplayName} on {targetDevice.FriendlyName} using {variant}.");
            PublishStatus(new SwitchingStatus("Restoring original format", targetDevice.FriendlyName, null, null, null, null));

            if (!_audioEndpointController.TryApplyFormat(targetDevice.Id, format, out var verifiedDeviceFormat, out var failure))
            {
                var diagnostics = _audioEndpointController.GetLastApplyDiagnostics(targetDevice.Id);
                var message = $"Failed to restore {format.DisplayName} on {targetDevice.FriendlyName}: {failure}" +
                    (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : $" | {diagnostics}");
                _logger.Warn(message);
                PublishStatus(new SwitchingStatus("Restore original format failed", targetDevice.FriendlyName, null, null, null, message));
                return new FormatRestoreResult(false, message, null);
            }

            Volatile.Write(ref _currentTrackSwitchApplied, true);
            var restoredFormat = verifiedDeviceFormat ?? format;
            var successMessage = $"Restored {AudioFormatTextFormatter.Format(restoredFormat)} on {targetDevice.FriendlyName}.";
            _logger.Info(successMessage);
            PublishStatus(new SwitchingStatus("Restored original format", targetDevice.FriendlyName, null, null, restoredFormat, null));
            return new FormatRestoreResult(true, successMessage, restoredFormat);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public CurrentTargetDeviceCapabilitiesSnapshot GetCurrentTargetDeviceCapabilities(bool forceRefresh = false)
    {
        var targetDevice = GetCurrentTargetDevice();
        if (targetDevice is null)
        {
            return new CurrentTargetDeviceCapabilitiesSnapshot(null, []);
        }

        var supportedFormats = _audioEndpointController.GetSupportedFormats(targetDevice.Id, forceRefresh);
        var probeDiagnostics = _audioEndpointController.GetLastProbeDiagnostics(targetDevice.Id);
        if (!string.IsNullOrWhiteSpace(probeDiagnostics))
        {
            _logger.Info($"Probed formats on {targetDevice.FriendlyName}: {probeDiagnostics}");
        }

        return new CurrentTargetDeviceCapabilitiesSnapshot(
            targetDevice.FriendlyName,
            supportedFormats,
            probeDiagnostics);
    }

    public void UpdateSettings(AppSettings settings)
    {
        Settings = settings;
    }

    /// <summary>Hold the new instance until Play without changing a persisted option.</summary>
    public void WaitForUserPlaybackAfterManualRestart() => _waitForUserPlaybackAfterRestart = true;

    /// <summary>Quiesce old work before releasing owned mute or starting a helper.</summary>
    public async Task<bool> PrepareForManualRestartAsync(CancellationToken token, bool noPlayerProcess = false)
    {
        _manualRestartRequested = true;
        _watchdogCts?.Cancel();
        CancelCurrentTrackProcessing();
        if (!await _switchLock.WaitAsync(TimeSpan.FromSeconds(5), token)) return false;
        try
        {
            // No old processing can resume while this flag is set. A failed preparation
            // remains stopped; the user can pause manually and explicitly retry.
            return await ManualRestartSafety.ConfirmPausedAsync(_mediaTransportController, token, noPlayerProcess);
        }
        finally { _switchLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // WPF OnExit is async void: release owned mute before the first asynchronous wait.
        // Serialize with mute acquisition so canceled work cannot re-mute during shutdown.
        lock (_muteOwnershipSync)
        {
            _stopping = true;
            UnmuteTargetDeviceAfterProcessing();
        }
        _trackSource.TrackChanged -= OnTrackChanged;
        if (_playbackStateNotifier is not null)
            _playbackStateNotifier.PlaybackStateChanged -= OnPlaybackStateChanged;
        Task[] cacheVerificationTasks;
        lock (_cacheVerificationSync)
        {
            _cacheVerificationStopping = true;
            cacheVerificationTasks = _activeCacheVerifications.Values.ToArray();
        }

        _cacheVerificationLifetimeCts.Cancel();
        _watchdogCts?.Cancel();
        CancelCurrentTrackProcessing();
        await _trackSource.DisposeAsync();
        await AwaitActiveTrackTasksAsync();
        await AwaitCacheVerificationTasksAsync(cacheVerificationTasks);
        if (_watchdogTask is not null)
        {
            try
            {
                await _watchdogTask;
            }
            catch (OperationCanceledException)
            {
                // Watchdog canceled as part of disposal — expected.
            }
        }

        _watchdogCts?.Dispose();
        _cacheVerificationLifetimeCts.Dispose();

        // Never exit with the user's device muted by us.
        UnmuteTargetDeviceAfterProcessing();
        _switchLock.Dispose();
    }

    private async Task RunPlaybackWatchdogAsync(CancellationToken cancellationToken)
    {
        var inactiveSince = default(DateTimeOffset?);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchdogPollInterval, cancellationToken);

                // Stay out of the way while a track is being processed: the pipeline's own
                // checks own those windows, and pause/mute states would read as wedges here.
                if (_previousRestartSafetyHold || _manualTrackHold is not null ||
                    _switchLock.CurrentCount == 0 || HasActiveTrackTasks())
                {
                    inactiveSince = null;
                    continue;
                }

                if (HasDeferredRateSwitch())
                {
                    // Event delivery is not guaranteed on every Windows build. A census is
                    // a fallback observer only; it never sends Play to end the user's pause.
                    OnPlaybackStateChanged(this, _mediaTransportController.GetPlaybackState());
                    inactiveSince = null;
                    continue;
                }

                var deviceId = _lastTargetDeviceId;
                if (deviceId is null)
                {
                    continue;
                }

                if (DateTimeOffset.UtcNow - _lastWatchdogRecoveryFailureUtc < WatchdogFailureCooldown)
                {
                    continue;
                }

                if (Volatile.Read(ref _suppressWatchdogUntilNextTrack))
                {
                    inactiveSince = null;
                    continue;
                }

                var state = _mediaTransportController.GetPlaybackState();
                if (!state.IsPlaying ||
                    _audioEndpointController.IsProcessSessionActive(deviceId, AppleMediaAgentProcessName) != false)
                {
                    inactiveSince = null;
                    continue;
                }

                inactiveSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - inactiveSince < PlaybackHealthZombieThreshold)
                {
                    continue;
                }

                inactiveSince = null;
                await RunWatchdogRecoveryAsync(deviceId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TimeoutException ex) when (Settings.RestartTrackAfterSampleRateChange)
            {
                var correlation = new DiagnosticsLogger.LogCorrelation(Volatile.Read(ref _currentTrackGeneration), _currentTrackKey ?? "unknown");
                MuteTargetDeviceForProcessing(_lastTargetDeviceId, correlation);
                SetPreviousRestartSafetyHold(correlation, "A playback command timed out and may still be queued. Automatic retries stopped. " + ex.Message);
                PublishStatusIfCurrent(Volatile.Read(ref _currentTrackGeneration), new SwitchingStatus("Resolver: Failed", null,
                    _currentTrackSnapshot, null, null, GetGlobalHoldMessage()));
                inactiveSince = null;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Playback watchdog cycle failed: {ex.Message}");
                inactiveSince = null;
            }
        }
    }

    private async Task RunWatchdogRecoveryAsync(string deviceId, CancellationToken watchdogToken)
    {
        // Take the switch lock so the recovery cannot fight a track that arrives mid-recovery;
        // an incoming track supersedes us through the linked current-track token.
        await _switchLock.WaitAsync(watchdogToken);
        try
        {
            if (_manualRestartRequested || _previousRestartSafetyHold || _manualTrackHold is not null || HasDeferredRateSwitch())
            {
                return;
            }
            var generation = Volatile.Read(ref _currentTrackGeneration);
            var trackKey = _currentTrackKey ?? "unknown";
            var correlation = new DiagnosticsLogger.LogCorrelation(generation, trackKey);

            // Re-verify under the lock — the state may have changed while we waited.
            var state = _mediaTransportController.GetPlaybackState();
            if (!state.IsPlaying ||
                _audioEndpointController.IsProcessSessionActive(deviceId, AppleMediaAgentProcessName) != false)
            {
                return;
            }

            _logger.Warn(
                $"Watchdog: playback reports Playing but the media agent has had no render stream for {PlaybackHealthZombieThreshold.TotalSeconds:0} s (mid-track stall); starting recovery.",
                correlation);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                watchdogToken,
                _currentTrackCts?.Token ?? CancellationToken.None);
            var recovered = await TryRecoverPlaybackWithNudgesAsync(
                deviceId, null, generation, null, null, null, correlation, linked.Token,
                NoSwitchRecoveryNudgeLimit, switchApplied: Volatile.Read(ref _currentTrackSwitchApplied));
            if (recovered)
            {
                _logger.Info("Watchdog: playback recovered.", correlation);
            }
            else
            {
                _lastWatchdogRecoveryFailureUtc = DateTimeOffset.UtcNow;
                _logger.Warn(Settings.RestartTrackAfterSampleRateChange
                    ? "Watchdog: recovery did not restore audio; automatic retries are stopped for the current track."
                    : "Watchdog: recovery did not restore audio; backing off before the next attempt.", correlation);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a new track or disposal — the newer work owns playback now.
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private bool HasActiveTrackTasks()
    {
        lock (_activeTaskSync)
        {
            return _activeTrackTasks.Count > 0;
        }
    }

    private async void OnTrackChanged(object? sender, TrackSnapshot track)
    {
        await HandleTrackNotificationAsync(track);
    }

    private async Task HandleTrackNotificationAsync(TrackSnapshot track, long? deferredGeneration = null)
    {
        if (_stopping || _manualRestartRequested)
        {
            return;
        }
        var manualHold = _manualTrackHold;
        if (manualHold is not null &&
            (manualHold.Track.UniqueKey == track.UniqueKey || AppleMusicTrackSource.IsPlaceholderTrack(track)))
        {
            _logger.Info("Current-track recovery remains held; duplicate or transient metadata cannot authorize playback.");
            PublishStatusIfCurrent(manualHold.Generation, new SwitchingStatus(manualHold.Status, null, manualHold.Track,
                null, null, manualHold.Reason));
            return;
        }
        if (_previousRestartGuardActive)
        {
            _previousRestartSafetyHold = true;
            _transportPausedByCoordinator = false;
            _logger.Warn("Previous-restart: a different track arrived during the guarded operation; automatic processing is suspended.");
        }
        var (generation, cancellationToken) = BeginTrackProcessing(track, deferredGeneration);
        if (generation == 0)
            return;
        var correlation = CreateCorrelation(generation, track);
        PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Detecting track", null, track, null, null, null));
        var processingTask = ProcessTrackAsync(track, generation, cancellationToken);
        TrackTaskStarted(processingTask);

        try
        {
            await processingTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("Track change handler observed a superseded track cancellation.", correlation);
        }
        catch (Exception ex)
        {
            _logger.Error("Track change handler failed unexpectedly.", ex, correlation);
            PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Error", null, track, null, null, ex.Message));
        }
        finally
        {
            TrackTaskCompleted(processingTask);
        }
    }

    private bool HasDeferredRateSwitch()
    {
        lock (_processingSync)
            return _deferredRateSwitch is not null;
    }

    private async void OnPlaybackStateChanged(object? sender, MediaTransportState state)
    {
        if (_stopping || _manualRestartRequested || _previousRestartSafetyHold || _manualTrackHold is not null)
            return;
        if (!state.IsPlaying)
        {
            if (state.PlaybackStatus is MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed)
            {
                try { await ReleaseDeferredPauseMuteAsync(); }
                catch (ObjectDisposedException) when (_stopping) { }
                catch (Exception ex) { _logger.Warn("Deferred pause cleanup could not complete: " + ex.Message); }
            }
            return;
        }
        DeferredRateSwitch? pending;
        lock (_processingSync)
        {
            pending = _deferredRateSwitch;
            if (pending is null || pending.Generation != _currentTrackGeneration || pending.Track.UniqueKey != _currentTrackKey)
                return;
            // Claim once before any await: repeated Playing notifications cannot schedule
            // several switches for one user resume.
            _deferredRateSwitch = null;
        }
        var task = ResumeDeferredRateSwitchAsync(pending);
        TrackTaskStarted(task);
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_stopping && Volatile.Read(ref _currentTrackGeneration) == pending.Generation)
            {
                var correlation = CreateCorrelation(pending.Generation, pending.Track);
                SetPreviousRestartSafetyHold(correlation, "Deferred switch failed before a safe resume: " + ex.Message);
                PublishStatusIfCurrent(pending.Generation, new SwitchingStatus("Resolver: Failed", null, pending.Track,
                    null, null, GetGlobalHoldMessage()));
            }
        }
        finally { TrackTaskCompleted(task); }
    }

    private async Task ReleaseDeferredPauseMuteAsync()
    {
        // Never release mute belonging to a real switch. A non-blocking acquisition
        // leaves active processing alone; the observer will check again next cycle.
        if (_stopping || !HasDeferredRateSwitch() || !await _switchLock.WaitAsync(0))
            return;
        try
        {
            DeferredRateSwitch? pending;
            lock (_processingSync)
            {
                if (_stopping || _previousRestartSafetyHold || _manualTrackHold is not null ||
                    _deferredRateSwitch is null || _deferredRateSwitch.Generation != _currentTrackGeneration)
                    return;
                pending = _deferredRateSwitch;
            }
            // Media-session calls can marshal across apartments; do not make them
            // while holding the coordinator's generation lock.
            var state = _mediaTransportController.GetPlaybackState();
            if (state.PlaybackStatus is MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed)
            {
                lock (_processingSync)
                    if (!_stopping && !_previousRestartSafetyHold && _manualTrackHold is null &&
                        ReferenceEquals(pending, _deferredRateSwitch) && pending.Generation == _currentTrackGeneration)
                    UnmuteTargetDeviceAfterProcessing(CreateCorrelation(pending.Generation, pending.Track));
            }
        }
        finally
        {
            try { _switchLock.Release(); }
            catch (ObjectDisposedException) when (_stopping) { }
        }
    }

    private async Task ResumeDeferredRateSwitchAsync(DeferredRateSwitch pending)
    {
        var resume = false;
        await _switchLock.WaitAsync(pending.CancellationToken);
        try
        {
            EnsureRestartIsCurrent(pending.Generation, pending.CancellationToken);
            if (_previousRestartSafetyHold || _manualTrackHold is not null)
                return;
            var state = _mediaTransportController.GetPlaybackState();
            if (!state.IsPlaying)
            {
                // The user paused again before we acquired the lock. Keep waiting without
                // touching the device or issuing Play.
                if (state.PlaybackStatus == MediaTransportPlaybackStatus.Paused)
                {
                    lock (_processingSync)
                        if (!_stopping && pending.Generation == _currentTrackGeneration)
                            _deferredRateSwitch = pending;
                    UnmuteTargetDeviceAfterProcessing(CreateCorrelation(pending.Generation, pending.Track));
                }
                return;
            }
            var correlation = CreateCorrelation(pending.Generation, pending.Track);
            // A Playing event can precede resolved metadata. No device or navigation
            // command has been sent here; allow one later fresh check on this generation.
            MuteTargetDeviceForProcessing(_lastTargetDeviceId, correlation);
            var matching = _mediaTransportController is ITrackIdentityVerifier verifier &&
                await verifier.IsCurrentTrackAsync(pending.Track, pending.CancellationToken);
            EnsureRestartIsCurrent(pending.Generation, pending.CancellationToken);
            if (!matching)
            {
                if (pending.VerificationRetries == 0)
                {
                    lock (_processingSync)
                        if (!_stopping && pending.Generation == _currentTrackGeneration)
                            _deferredRateSwitch = pending with { VerificationRetries = 1 };
                    if (_mediaTransportController.GetPlaybackState().PlaybackStatus == MediaTransportPlaybackStatus.Paused)
                        UnmuteTargetDeviceAfterProcessing(correlation);
                    _logger.Info("Deferred rate switch: resumed playback metadata is not ready; one fresh verification will be attempted without issuing playback or format commands.", correlation);
                    PublishStatusIfCurrent(pending.Generation, new SwitchingStatus("Switching: Waiting for user playback", null,
                        pending.Track, null, null, "Playback resumed; waiting for a fresh same-track check before switching."));
                    return;
                }
                SetPreviousRestartSafetyHold(correlation, "Deferred switch could not verify the expected resumed track after the bounded fresh check. No format or navigation command was sent.");
                PublishStatusIfCurrent(pending.Generation, new SwitchingStatus("Resolver: Failed", null, pending.Track,
                    null, null, GetGlobalHoldMessage()));
                return;
            }
            EnsureRestartIsCurrent(pending.Generation, pending.CancellationToken);
            resume = _mediaTransportController.GetPlaybackState().IsPlaying;
            if (!resume)
            {
                lock (_processingSync)
                    if (!_stopping && pending.Generation == _currentTrackGeneration)
                        _deferredRateSwitch = pending;
                UnmuteTargetDeviceAfterProcessing(correlation);
            }
        }
        finally { _switchLock.Release(); }
        if (resume)
        {
            _logger.Info("Deferred rate switch: user playback confirmed; re-evaluating the same current track and device before switching.",
                CreateCorrelation(pending.Generation, pending.Track));
            await HandleTrackNotificationAsync(pending.Track, pending.Generation);
        }
    }

    private async Task ProcessTrackAsync(TrackSnapshot track, long generation, CancellationToken cancellationToken)
    {
        var pausedPlayback = false;
        var resumeAttempted = false;
        var lockAcquired = false;
        string? targetDeviceName = null;
        var stage = TrackProcessingStage.BeforeLockAcquisition;
        var correlation = CreateCorrelation(generation, track);
        var lockWait = Stopwatch.StartNew();

        try
        {
            _logger.Info("Waiting for switch lock.", correlation);
            await _switchLock.WaitAsync(cancellationToken);
            lockAcquired = true;
            _logger.Info($"Acquired switch lock after {lockWait.ElapsedMilliseconds} ms.", correlation);

            cancellationToken.ThrowIfCancellationRequested();
            if (_manualRestartRequested || _previousRestartSafetyHold || _stopping)
            {
                PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", null, track, null, null, GetGlobalHoldMessage()));
                return;
            }
            if (_manualTrackHold is { } manualHold && manualHold.Track.UniqueKey == track.UniqueKey)
            {
                PublishStatusIfCurrent(generation, new SwitchingStatus(manualHold.Status, null, track, null, null, manualHold.Reason));
                return;
            }
            _logger.Info($"Processing track {track.UniqueKey} detected at {track.DetectedAtUtc:O}.", correlation);

            if (_waitForUserPlaybackAfterRestart)
            {
                if (!_mediaTransportController.GetPlaybackState().IsPlaying)
                {
                    lock (_processingSync)
                        if (!_stopping && generation == _currentTrackGeneration)
                            _deferredRateSwitch = new DeferredRateSwitch(track, generation, cancellationToken);
                    PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Waiting for user playback",
                        null, track, null, null, "Restart completed. Press Play in Apple Music to resume monitoring.",
                        Recovery: RecoveryCategory.WaitingForPlayback));
                    return;
                }
                _waitForUserPlaybackAfterRestart = false;
            }

            // Silence the new track immediately — before the (slow) format resolution — so it
            // never plays audibly at the previous track's format. We MUTE rather than pause:
            // muting is silent but lets Apple Music's render stream finish starting up, which is
            // required for a clean format switch later. The pause + drain happens at the switch
            // point once the stream is established.
            stage = TrackProcessingStage.TrackChangePause;
            MuteOnTrackChange(track, generation, correlation);
            if (_transportPausedByCoordinator)
            {
                pausedPlayback = true;
                _logger.Info("Inheriting coordinator-paused playback from a superseded track.", correlation);
            }

            _logger.Info("Starting target-device selection.", correlation);
            var targetSelectionStopwatch = Stopwatch.StartNew();
            var devices = _audioEndpointController.GetRenderDevices();
            var defaultDevice = _audioEndpointController.GetDefaultRenderDevice();
            var targetDevice = DeviceTargetingPolicy.SelectTargetDevice(Settings, devices, defaultDevice);
            _logger.Info(
                $"Target-device selection completed after {targetSelectionStopwatch.ElapsedMilliseconds} ms. " +
                $"result={(targetDevice?.FriendlyName ?? "none")}.",
                correlation);

            if (targetDevice is null)
            {
                var failureReason = "No active target device.";
                (resumeAttempted, failureReason) = await ResumeIfNeededAsync(
                    pausedPlayback,
                    resumeAttempted,
                    null,
                    failureReason,
                    CancellationToken.None);
                PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: No target device", null, track, null, null, failureReason, Recovery: RecoveryCategory.DeviceUnavailable));
                return;
            }

            targetDeviceName = targetDevice.FriendlyName;
            _lastTargetDeviceId = targetDevice.Id;
            if (_mutedDeviceId is not null && !string.Equals(_mutedDeviceId, targetDevice.Id, StringComparison.Ordinal))
            {
                // The cached device we muted is no longer the target (device changed since the
                // last switch). Move the mute to the real target.
                UnmuteTargetDeviceAfterProcessing(correlation);
                MuteTargetDeviceForProcessing(targetDevice.Id, correlation);
            }

            PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Resolving format", targetDevice.FriendlyName, track, null, null, null));
            stage = TrackProcessingStage.ResolvingFormat;
            var resolved = await ResolveFormatAsync(track, cancellationToken);
            if (resolved?.Source == AudioFormatSource.CachedCatalog)
            {
                MaybeScheduleCacheVerification(track, resolved);
            }

            if (resolved is null)
            {
                var failureReason = "No format resolver returned a result.";
                (resumeAttempted, failureReason) = await ResumeIfNeededAsync(
                    pausedPlayback,
                    resumeAttempted,
                    targetDevice.FriendlyName,
                    failureReason,
                    CancellationToken.None);
                PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", targetDevice.FriendlyName, track, resolved, null, failureReason, Recovery: RecoveryCategory.MetadataUnavailable));
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var decision = EvaluateResolvedFormat(targetDevice, resolved);
            if (decision.SelectedFormat is null)
            {
                var failureReason = decision.FailureReason;
                if (!string.IsNullOrWhiteSpace(failureReason))
                {
                    _logger.Warn(failureReason);
                }

                (resumeAttempted, failureReason) = await ResumeIfNeededAsync(
                    pausedPlayback,
                    resumeAttempted,
                    targetDevice.FriendlyName,
                    failureReason,
                    CancellationToken.None);
                var skipHealth = await VerifyPlaybackHealthAfterNoSwitchAsync(
                    targetDevice.Id, targetDevice.FriendlyName, generation, track, resolved, decision.CurrentDeviceFormat, correlation, cancellationToken);
                resumeAttempted = resumeAttempted || skipHealth != PlaybackHealthOutcome.Healthy;
                failureReason = AppendHealthNote(failureReason, skipHealth);
                PublishStatusIfCurrent(generation, new SwitchingStatus($"Resolver: {resolved!.Source}", targetDevice.FriendlyName, track, resolved, null, failureReason, Recovery: RecoveryCategory.UnsupportedFormat));
                return;
            }

            if (decision.CurrentDeviceFormat == decision.SelectedFormat)
            {
                var activeFormatNote = "Target format already active.";
                (resumeAttempted, activeFormatNote) = await ResumeIfNeededAsync(
                    pausedPlayback,
                    resumeAttempted,
                    targetDevice.FriendlyName,
                    activeFormatNote,
                    cancellationToken);
                var activeHealth = await VerifyPlaybackHealthAfterNoSwitchAsync(
                    targetDevice.Id, targetDevice.FriendlyName, generation, track, resolved, decision.CurrentDeviceFormat, correlation, cancellationToken);
                resumeAttempted = resumeAttempted || activeHealth != PlaybackHealthOutcome.Healthy;
                activeFormatNote = AppendHealthNote(activeFormatNote, activeHealth);
                PublishStatusIfCurrent(generation, new SwitchingStatus($"Resolver: {resolved!.Source}", targetDevice.FriendlyName, track, resolved, decision.CurrentDeviceFormat, activeFormatNote));
                return;
            }

            // The experimental paused-switch path is restricted to a known, real rate
            // change and a transport that can freshly verify the current item. Resolution
            // stays before this point so same-rate tracks gain no pause or navigation.
            if (Settings.RestartTrackAfterSampleRateChange && decision.CurrentDeviceFormat is { } beforePauseSwitch &&
                beforePauseSwitch.SampleRateHz != decision.SelectedFormat.SampleRateHz &&
                _mediaTransportController is ITrackIdentityVerifier)
            {
                stage = TrackProcessingStage.PauseBeforeSwitch;
                await PauseSwitchAndRestartAsync(targetDevice, track, resolved, decision, generation, correlation, cancellationToken);
                return;
            }

            if (pausedPlayback && _transportPausedByCoordinator)
            {
                // A canceled restart can hand us its pause. Restore our own pause while still
                // muted before another live format change; never invalidate that dormant stream.
                cancellationToken.ThrowIfCancellationRequested();
                resumeAttempted = true;
                var inheritedResume = await TryPlayWithRetriesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!inheritedResume)
                {
                    PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", targetDevice.FriendlyName, track, resolved, decision.CurrentDeviceFormat,
                        "Could not resume the inherited coordinator pause; skipped the next format change."));
                    return;
                }
                pausedPlayback = false;
                _logger.Info("Resumed the inherited coordinator pause while muted before the next live format change.", correlation);
            }

            // Live-switch rule, proven decisively: applying the device format while Apple Music's
            // render stream is ACTIVELY RENDERING invalidates the stream, the media agent notices
            // within ~3 s and rebuilds it on its own — audio resumes with no transport command at
            // all (verified for same- and cross-family switches). Pausing first is what zombied:
            // a stream invalidated while dormant is silently reused on resume and never renders.
            // So the only requirement before switching live is a genuinely active stream; if it
            // never comes up, SKIP the switch — a track playing at a slightly-wrong rate is far
            // better than a silent zombie. Genuinely idle playback switches directly (a stream
            // created later starts on the new format and needs no rebuild).
            stage = TrackProcessingStage.PauseBeforeSwitch;
            var liveStream = false;
            if (!pausedPlayback && _mediaTransportController.GetPlaybackState().IsPlaying)
            {
                liveStream = await WaitForRenderStreamActiveAsync(targetDevice.Id, correlation, cancellationToken);
                if (!liveStream)
                {
                    var skipReason = "Render stream not active; skipped format switch to avoid zombie playback.";
                    _logger.Warn(skipReason, correlation);

                    // No switch will happen, so the track-change mute has nothing left to
                    // protect. Release it NOW: the health check + recovery below can hold this
                    // state for ~20 s, and a track whose stream comes up late would render into
                    // enforced silence for that whole window.
                    UnmuteTargetDeviceAfterProcessing(correlation);
                    var gateHealth = await VerifyPlaybackHealthAfterNoSwitchAsync(
                        targetDevice.Id, targetDevice.FriendlyName, generation, track, resolved, decision.CurrentDeviceFormat, correlation, cancellationToken);
                    resumeAttempted = resumeAttempted || gateHealth != PlaybackHealthOutcome.Healthy;
                    var gateNote = AppendHealthNote(skipReason, gateHealth);
                    PublishStatusIfCurrent(generation, new SwitchingStatus($"Resolver: {resolved.Source}", targetDevice.FriendlyName, track, resolved, decision.CurrentDeviceFormat, gateNote));
                    return;
                }
            }
            else
            {
                // Transport reports paused/stopped — but trust the endpoint over GSMTC: audio
                // still flowing means the stream is live (stale transport state at startup), and
                // a live stream takes the live-switch path like any other.
                var (silent, peakReadable, maxPeak) = await SampleEndpointSilenceAsync(targetDevice.Id, cancellationToken);
                liveStream = peakReadable && !silent;
                _logger.Info(
                    liveStream
                        ? $"Playback reported not-playing but the endpoint shows audio flowing (maxPeak={maxPeak:F6}); switching live."
                        : "Playback idle; applying the format directly (the next stream starts on it).",
                    correlation);
            }

            if (TryCapturePlaybackProbe(targetDevice.Id) is { } applyProbe)
            {
                _logger.Verbose($"Intro-trace: about to apply format; liveStream={liveStream}; {applyProbe}.", correlation);
            }

            var variant = _audioEndpointController.DescribeSupportedFormat(targetDevice.Id, decision.SelectedFormat) ?? "unknown native format";
            _logger.Info(
                $"Applying {decision.SelectedFormat.DisplayName} on {targetDevice.FriendlyName} using {variant}. " +
                $"deviceFormatBefore={(decision.CurrentDeviceFormat?.DisplayName ?? "unknown")}, mixFormatBefore={(decision.CurrentMixFormat?.DisplayName ?? "unknown")}.");
            PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Applying format", targetDevice.FriendlyName, track, resolved, null, null));

            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                return;
            }
            if (!_audioEndpointController.TryApplyFormat(targetDevice.Id, decision.SelectedFormat, out var verifiedDeviceFormat, out var failure))
            {
                var diagnostics = _audioEndpointController.GetLastApplyDiagnostics(targetDevice.Id);
                _logger.Warn(
                    $"Failed to apply {decision.SelectedFormat.DisplayName} on {targetDevice.FriendlyName}: {failure}" +
                    (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : $" | {diagnostics}"));
                PublishStatusIfCurrent(generation, new SwitchingStatus($"Resolver: {resolved!.Source}", targetDevice.FriendlyName, track, resolved, null, failure, Recovery: RecoveryCategory.FormatApplyFailed));
                return;
            }

            Volatile.Write(ref _currentTrackSwitchApplied, true);
            var applyDiagnostics = _audioEndpointController.GetLastApplyDiagnostics(targetDevice.Id);
            _logger.Info(
                $"Applied {decision.SelectedFormat.DisplayName} to {targetDevice.FriendlyName} using {resolved!.Source}. " +
                $"deviceFormatAfter={(verifiedDeviceFormat?.DisplayName ?? "unknown")}. " +
                (string.IsNullOrWhiteSpace(applyDiagnostics) ? string.Empty : applyDiagnostics));

            string? completionNote = null;
            if (liveStream)
            {
                PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Waiting for device", targetDevice.FriendlyName, track, resolved, verifiedDeviceFormat ?? decision.SelectedFormat, null));
                stage = TrackProcessingStage.DeviceReadyWait;
                var waitResult = await WaitForTargetFormatAsync(targetDevice.Id, decision.SelectedFormat, verifiedDeviceFormat, cancellationToken);
                verifiedDeviceFormat = waitResult.VerifiedFormat ?? verifiedDeviceFormat ?? decision.SelectedFormat;
                if (!waitResult.IsReady)
                {
                    completionNote = $"The device switch could not be confirmed twice in a row within {FormatSwitchPauseTimeoutSeconds} seconds.";
                    _logger.Warn($"{targetDevice.FriendlyName} did not report stable {decision.SelectedFormat.DisplayName} before timeout.");
                }

                // The format change invalidated the live stream; the media agent rebuilds it on
                // its own (~3 s). Wait for real audio at the new format.
                stage = TrackProcessingStage.PlaybackRestoreObservation;
                var rebuilt = await ObserveStreamRebuildAsync(targetDevice.Id, generation, targetDevice.FriendlyName, track, resolved, verifiedDeviceFormat, correlation, cancellationToken);
                if (rebuilt)
                {
                    // Realign audio with the timeline. The timeline kept advancing while the
                    // stream rebuilt, so the audio now lags it by the rebuild duration and Apple
                    // Music ends the track when the TIMELINE finishes — cutting that many seconds
                    // off the end of the song. A single muted pause/play on the healthy rebuilt
                    // stream makes Apple Music resume at the timeline position with a fresh
                    // end-of-track schedule. (No format change happens across this pause, so the
                    // invalidated-while-dormant zombie trap does not apply; unlike the track
                    // restart this is not a transition, so a quick user skip cannot wedge it.)
                    rebuilt = await TryRealignScheduleAfterRebuildAsync(targetDevice.Id, correlation, cancellationToken);
                }

                if (!rebuilt)
                {
                    stage = TrackProcessingStage.PlaybackRecovery;
                    rebuilt = await TryRecoverPlaybackWithNudgesAsync(
                        targetDevice.Id, targetDevice.FriendlyName, generation, track, resolved, verifiedDeviceFormat, correlation, cancellationToken);
                }

                if (rebuilt)
                {
                    // Do NOT restart the track to recover the muted intro: the skip-previous
                    // re-buffers the track and creates a second transition, and a user skip
                    // landing inside that window reliably wedges Apple Music's renderer
                    // (proven live — every wedge cascade followed a restart + quick skip).
                    // Losing a few muted seconds of intro is far cheaper than a wedge.
                    PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Playback restored", targetDevice.FriendlyName, track, resolved, verifiedDeviceFormat, completionNote, WasFormatChanged: true));
                    return;
                }

                completionNote = CombineFailureReason(
                    completionNote,
                    "The audio pipeline did not come back after the switch and recovery could not confirm audio.");
            }

            var publishedStatus = new SwitchingStatus(
                $"Resolver: {resolved.Source}",
                targetDevice.FriendlyName,
                track,
                resolved,
                verifiedDeviceFormat ?? decision.SelectedFormat,
                completionNote,
                WasFormatChanged: true);
            PublishStatusIfCurrent(generation, publishedStatus);
        }
        catch (OperationCanceledException) when (_stopping || cancellationToken.IsCancellationRequested || Volatile.Read(ref _currentTrackGeneration) != generation)
        {
            _logger.Info($"Track processing canceled during {DescribeStage(stage)}.", correlation);
            var superseded = Volatile.Read(ref _currentTrackGeneration) != generation;
            if (pausedPlayback && !resumeAttempted)
            {
                if (superseded)
                {
                    // The successor inherits the paused/muted state via
                    // _transportPausedByCoordinator and _mutedDeviceId; resuming here would
                    // leak the old format between our resume and the successor's pause.
                    _logger.Info("Leaving playback paused for the superseding track processing to manage.", correlation);
                }
                else
                {
                    await ResumePlaybackAfterPauseAsync(targetDeviceName, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException && Settings.RestartTrackAfterSampleRateChange)
            {
                MuteTargetDeviceForProcessing(_lastTargetDeviceId, correlation);
                SetPreviousRestartSafetyHold(correlation, "A playback command timed out and may still be queued. No automatic retry is safe. " + ex.Message);
            }
            if (pausedPlayback && !resumeAttempted)
            {
                await ResumePlaybackAfterPauseAsync(targetDeviceName, CancellationToken.None);
            }

            _logger.Error("Track processing failed.", ex);
            PublishStatusIfCurrent(generation, new SwitchingStatus(_previousRestartSafetyHold ? "Resolver: Failed" : "Resolver: Error", targetDeviceName, track, null, null,
                _previousRestartSafetyHold ? PreviousRestartManualRecoveryMessage + " " + ex.Message : ex.Message));
        }
        finally
        {
            if (lockAcquired)
            {
                // Never leave the device muted unless a superseding track's processing now
                // owns the mute and will release it itself.
                if (!_manualRestartRequested && !_previousRestartSafetyHold && _manualTrackHold is null && Volatile.Read(ref _currentTrackGeneration) == generation)
                {
                    UnmuteTargetDeviceAfterProcessing(correlation);
                }

                _switchLock.Release();
            }
        }
    }

    private async Task<PlaybackHealthOutcome> VerifyPlaybackHealthAfterNoSwitchAsync(
        string deviceId,
        string? targetDeviceName,
        long generation,
        TrackSnapshot track,
        ResolvedAudioFormat resolved,
        AudioFormatCandidate? currentFormat,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken)
    {
        var inactiveSince = default(DateTimeOffset?);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = _mediaTransportController.GetPlaybackState();
            if (!state.IsPlaying)
            {
                // Paused/stopped is a user state, not a stall.
                return PlaybackHealthOutcome.Healthy;
            }

            var active = _audioEndpointController.IsProcessSessionActive(deviceId, AppleMediaAgentProcessName);
            if (active != false)
            {
                // Stream running (or state unreadable — assume the best): playback is healthy.
                return PlaybackHealthOutcome.Healthy;
            }

            inactiveSince ??= DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - inactiveSince >= PlaybackHealthZombieThreshold)
            {
                _logger.Warn(
                    $"Playback reports Playing but the media agent has had no render stream for {PlaybackHealthZombieThreshold.TotalSeconds:0} s (stalled without a format switch); starting recovery.",
                    correlation);
                var recovered = await TryRecoverPlaybackWithNudgesAsync(
                    deviceId, targetDeviceName, generation, track, resolved, currentFormat, correlation, cancellationToken,
                    NoSwitchRecoveryNudgeLimit, switchApplied: false);
                return recovered ? PlaybackHealthOutcome.Recovered : PlaybackHealthOutcome.RecoveryFailed;
            }

            await Task.Delay(PlaybackHealthPollInterval, cancellationToken);
        }
    }

    private static string? AppendHealthNote(string? note, PlaybackHealthOutcome outcome) =>
        outcome switch
        {
            PlaybackHealthOutcome.Recovered => CombineFailureReason(note, "Playback stalled and was recovered automatically."),
            PlaybackHealthOutcome.RecoveryFailed => CombineFailureReason(note, "Playback stalled and could not be recovered automatically."),
            _ => note,
        };

    


    private void MuteOnTrackChange(TrackSnapshot track, long generation, DiagnosticsLogger.LogCorrelation correlation)
    {
        var state = _mediaTransportController.GetPlaybackState();
        if (!state.IsPlaying)
        {
            return;
        }

        // Mute (do NOT pause) the instant a track change is detected. The mute is silent and
        // near-instant, so the new track is inaudible at the previous format — but Apple Music
        // keeps playing, which lets its render stream fully spin up. Pausing here instead would
        // kill the stream before it activates; switching the format into that cold-start state
        // is what produces zombie playback (proven live: a switch after a 1 ms "release" zombies,
        // a switch after the stream actually drained recovers). The real pause + drain happens
        // later, at the switch point, once the stream is established.
        MuteTargetDeviceForProcessing(_lastTargetDeviceId, correlation);
        if (TryCapturePlaybackProbe(_lastTargetDeviceId, state) is { } muteProbe)
        {
            _logger.Verbose($"Intro-trace: muted on track change; {muteProbe}.", correlation);
        }

        PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Preparing", null, track, null, null, null));
    }

    // Diagnostic snapshot of "where the timeline is" vs "is the agent actually outputting audio",
    // tagged "Intro-trace" so a log can be followed step-by-step through the switch/rebuild/realign.
    // The lost-intro symptom reproduces only on some machines, so this captures whether (and where)
    // the timeline position jumps forward of the audio across the rebuild + schedule realign.
    // Returns null (and does no work) when verbose diagnostics are off: the peak probe enumerates
    // audio sessions over COM, which must not tax the switch path for an unwanted log line. Pass an
    // already-read transport state where one exists so the logged value is the value the caller
    // decided on, not a second read that may differ.
    private string? TryCapturePlaybackProbe(string? deviceId, MediaTransportState? state = null)
    {
        if (!_logger.VerboseEnabled)
        {
            return null;
        }

        var timeline = (state ?? _mediaTransportController.GetPlaybackState()).TimelinePosition;
        var peak = string.IsNullOrEmpty(deviceId)
            ? null
            : _audioEndpointController.GetProcessSessionPeak(deviceId, AppleMediaAgentProcessName);
        return $"timeline={(timeline?.ToString() ?? "n/a")}, agentPeak={(peak?.ToString("F6") ?? "n/a")}";
    }

    
    
    private async Task<bool> WaitForRenderStreamActiveAsync(
        string deviceId,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow + RenderStreamStartupTimeout + SustainedStreamActiveDuration;
        var activeSince = default(DateTimeOffset?);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var active = _audioEndpointController.IsProcessSessionActive(deviceId, AppleMediaAgentProcessName);
            if (active == true)
            {
                activeSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - activeSince >= SustainedStreamActiveDuration)
                {
                    // Sustained activity proves this is the NEW track's stream: the old track's
                    // dying stream stops within ~1 s of the track change and cannot pass this.
                    _logger.Info($"Render stream active and sustained after {stopwatch.ElapsedMilliseconds} ms; proceeding to pause + switch.", correlation);
                    return true;
                }
            }
            else if (active is null)
            {
                // Session state unreadable — fall through and let the rest of the pipeline cope.
                return false;
            }
            else
            {
                activeSince = null;
            }

            await Task.Delay(RenderStreamReleasePollInterval, cancellationToken);
        }

        _logger.Warn(
            $"Render stream did not sustain activity within {RenderStreamStartupTimeout.TotalSeconds:0} s; proceeding anyway (may still be loading).",
            correlation);
        return false;
    }

    
    private void MuteTargetDeviceForProcessing(string? deviceId, DiagnosticsLogger.LogCorrelation correlation)
    {
        lock (_muteOwnershipSync)
        {
            if (_stopping || deviceId is null || _mutedDeviceId is not null)
            {
                return;
            }

            // Respect a user-set mute (or an unreadable state): never toggle what we did not set.
            var existingMute = _audioEndpointController.GetMasterMute(deviceId);
            if (existingMute != false)
            {
                return;
            }

            if (_audioEndpointController.TrySetMasterMute(deviceId, true))
            {
                _mutedDeviceId = deviceId;
                _logger.Info("Muted target device to silence pre-switch playback.", correlation);
            }
            else
            {
                _logger.Warn("Could not mute target device; pre-switch playback may be briefly audible.", correlation);
            }
        }
    }

    private void UnmuteTargetDeviceAfterProcessing(DiagnosticsLogger.LogCorrelation? correlation = null)
    {
        lock (_muteOwnershipSync)
        {
            if (_mutedDeviceId is null)
            {
                return;
            }

            if (_audioEndpointController.TrySetMasterMute(_mutedDeviceId, false))
            {
                LogWithOptionalCorrelation(warn: false, "Unmuted target device after switch processing.", correlation);
                _mutedDeviceId = null;
            }
            else
            {
                LogWithOptionalCorrelation(warn: true, "Could not unmute target device; will retry on the next opportunity.", correlation);
            }
        }
    }

    private void LogWithOptionalCorrelation(bool warn, string message, DiagnosticsLogger.LogCorrelation? correlation)
    {
        if (correlation is { } resolvedCorrelation)
        {
            if (warn)
            {
                _logger.Warn(message, resolvedCorrelation);
            }
            else
            {
                _logger.Info(message, resolvedCorrelation);
            }
        }
        else if (warn)
        {
            _logger.Warn(message);
        }
        else
        {
            _logger.Info(message);
        }
    }

    
    private async Task<(bool Silent, bool PeakReadable, float MaxPeak)> SampleEndpointSilenceAsync(string deviceId, CancellationToken cancellationToken)
    {
        var maxPeak = 0f;
        var peakReadable = true;
        var silent = true;
        for (var read = 0; read < PauseSilenceConsecutiveReads; read++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The agent's own session peak, NOT the endpoint master meter: other applications'
            // audio on the same device would otherwise read as Apple Music playing.
            var peak = _audioEndpointController.GetProcessSessionPeak(deviceId, AppleMediaAgentProcessName);
            if (peak is null)
            {
                peakReadable = false;
                break;
            }

            if (peak.Value > maxPeak)
            {
                maxPeak = peak.Value;
            }

            if (peak.Value > PauseSilenceThreshold)
            {
                silent = false;
                break;
            }

            if (read < PauseSilenceConsecutiveReads - 1)
            {
                await Task.Delay(PausedConfirmationPollInterval, cancellationToken);
            }
        }

        return (silent, peakReadable, maxPeak);
    }

    private async Task<DeviceReadyWaitResult> WaitForTargetFormatAsync(
        string deviceId,
        AudioFormatCandidate targetFormat,
        AudioFormatCandidate? initialFormat,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(FormatSwitchPauseTimeoutSeconds);
        var consecutiveMatches = initialFormat == targetFormat ? 1 : 0;
        var lastObservedFormat = initialFormat;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(DeviceReadyPollInterval, cancellationToken);

            var currentFormat = _audioEndpointController.GetCurrentDeviceFormat(deviceId);
            lastObservedFormat = currentFormat ?? lastObservedFormat;

            if (currentFormat == targetFormat)
            {
                consecutiveMatches++;
                if (consecutiveMatches >= DeviceReadyMatchThreshold)
                {
                    return new DeviceReadyWaitResult(true, currentFormat);
                }
            }
            else
            {
                consecutiveMatches = 0;
            }
        }

        return new DeviceReadyWaitResult(false, lastObservedFormat);
    }

    private async Task<bool> ResumePlaybackAfterPauseAsync(string? targetDeviceName, CancellationToken cancellationToken)
    {
        if (_stopping || _manualRestartRequested || _previousRestartSafetyHold || _manualTrackHold is not null)
        {
            return false;
        }
        // Unmute before issuing play so resumed audio is audible immediately and the
        // restore-observation peak meter sees real output.
        UnmuteTargetDeviceAfterProcessing();
        return await TryPlayWithRetriesAsync(targetDeviceName, cancellationToken);
    }

    // Play with bounded retries, WITHOUT unmuting — used mid-pipeline (e.g. the drain retry after
    // a mid-transition pause) where playback must continue silently while the switch is pending.
    private async Task<bool> TryPlayWithRetriesAsync(CancellationToken cancellationToken) =>
        await TryPlayWithRetriesAsync(null, cancellationToken);

    private async Task<bool> TryPlayWithRetriesAsync(string? targetDeviceName, CancellationToken cancellationToken)
    {
        try
        {
            // Resume without seeking. Seeking back to the captured position reset Apple Music's
            // timeline to the start for tracks paused near 0:00 (local files just begun), which made
            // the duration display as "0 seconds" until the next manual pause/play. Any drift from the
            // brief pause is negligible, so we simply resume at the current position.
            for (var attempt = 1; attempt <= ResumePlayAttemptLimit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_stopping || _manualRestartRequested || _previousRestartSafetyHold || _manualTrackHold is not null)
                {
                    return false;
                }
                var resumed = await _mediaTransportController.TryPlayAsync(cancellationToken);
                if (_stopping)
                {
                    return false;
                }
                if (resumed)
                {
                    _transportPausedByCoordinator = false;
                    return true;
                }

                var currentState = _mediaTransportController.GetPlaybackState();
                if (currentState.IsPlaying)
                {
                    _logger.Info($"Apple Music is already playing for {targetDeviceName ?? "unknown device"}; no resume needed.");
                    _transportPausedByCoordinator = false;
                    return true;
                }

                if (attempt < ResumePlayAttemptLimit)
                {
                    _logger.Warn($"Apple Music resume attempt {attempt}/{ResumePlayAttemptLimit} failed; retrying in {ResumePlayRetryDelay.TotalMilliseconds:0} ms.");
                    await Task.Delay(ResumePlayRetryDelay, cancellationToken);
                }
            }

            _logger.Warn($"Apple Music playback could not be resumed automatically for {targetDeviceName ?? "unknown device"} after {ResumePlayAttemptLimit} attempts.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) when (Settings.RestartTrackAfterSampleRateChange)
        {
            // The remote Play may still execute; do not treat timeout as rejection.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Unexpected error while resuming Apple Music playback: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ObserveStreamRebuildAsync(
        string deviceId,
        long generation,
        string? targetDeviceName,
        TrackSnapshot track,
        ResolvedAudioFormat resolved,
        AudioFormatCandidate? verifiedDeviceFormat,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken)
    {
        PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Rebuilding audio pipeline", targetDeviceName, track, resolved, verifiedDeviceFormat, null));
        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow + StreamRebuildTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = _mediaTransportController.GetPlaybackState();
            if (state.PlaybackStatus == MediaTransportPlaybackStatus.Paused)
            {
                // The user paused mid-rebuild — their call. Recovery would override it.
                _logger.Info("Playback was paused during the stream rebuild; leaving it to the user.", correlation);
                return true;
            }

            var active = _audioEndpointController.IsProcessSessionActive(deviceId, AppleMediaAgentProcessName);
            // The agent's own session peak, NOT the endpoint master meter: other applications'
            // audio on the same device would otherwise confirm a rebuild that never happened.
            var peak = _audioEndpointController.GetProcessSessionPeak(deviceId, AppleMediaAgentProcessName) ?? 0f;
            if (active == true && peak > PlaybackRestorePeakThreshold)
            {
                _logger.Info($"Render stream rebuilt with audio {stopwatch.ElapsedMilliseconds} ms after the live format change; timeline={(state.TimelinePosition?.ToString() ?? "n/a")}, agentPeak={peak:F6}.", correlation);
                return true;
            }

            await Task.Delay(StreamRebuildPollInterval, cancellationToken);
        }

        _logger.Warn($"Render stream did not rebuild within {StreamRebuildTimeout.TotalSeconds:0} s of the live format change.", correlation);
        return false;
    }


    private async Task<bool> VerifyExpectedTrackAsync(
        TrackSnapshot track, long generation, DiagnosticsLogger.LogCorrelation correlation, CancellationToken cancellationToken)
    {
        EnsureRestartIsCurrent(generation, cancellationToken);
        var verified = _mediaTransportController is ITrackIdentityVerifier verifier &&
            await verifier.IsCurrentTrackAsync(track, cancellationToken);
        EnsureRestartIsCurrent(generation, cancellationToken);
        if (!verified)
        {
            MuteTargetDeviceForProcessing(_lastTargetDeviceId, correlation);
            SetPreviousRestartSafetyHold(correlation, "Fresh track/session identity could not be verified; no further playback command was sent.");
        }
        return verified;
    }

    private async Task PauseSwitchAndRestartAsync(
        AudioDeviceInfo targetDevice, TrackSnapshot track, ResolvedAudioFormat resolved, ResolvedTrackDecision decision,
        long generation, DiagnosticsLogger.LogCorrelation correlation, CancellationToken cancellationToken)
    {
        AudioFormatCandidate? appliedFormat = null;
        void Hold(string reason, RecoveryCategory category = RecoveryCategory.UnsafeState)
        {
            MuteTargetDeviceForProcessing(targetDevice.Id, correlation);
            if (!_previousRestartSafetyHold)
                SetPreviousRestartSafetyHold(correlation, reason, category);
            PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", targetDevice.FriendlyName, track,
                resolved, appliedFormat, GetGlobalHoldMessage(),
                WasFormatChanged: appliedFormat is not null));
        }

        try
        {
            var entryState = _mediaTransportController.GetPlaybackState();
            var wasUserPaused = entryState.PlaybackStatus == MediaTransportPlaybackStatus.Paused && !_transportPausedByCoordinator;
            if (!wasUserPaused)
            {
                MuteTargetDeviceForProcessing(targetDevice.Id, correlation);
                if (_audioEndpointController.GetMasterMute(targetDevice.Id) != true)
                {
                    Hold("Pause-switch canceled: endpoint mute could not be verified; no format or navigation command was sent.");
                    return;
                }
            }
            if (!await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken))
            {
                Hold("Pause-switch canceled: current track was not verified before pausing.");
                return;
            }

            var state = _mediaTransportController.GetPlaybackState();
            var transitionTimer = Stopwatch.StartNew();
            while (state.PlaybackStatus == MediaTransportPlaybackStatus.Changing && transitionTimer.Elapsed < RestartPauseConfirmationTimeout)
            {
                await Task.Delay(RestartConfirmationPollInterval, cancellationToken);
                EnsureRestartIsCurrent(generation, cancellationToken);
                state = _mediaTransportController.GetPlaybackState();
            }
            if (state.PlaybackStatus == MediaTransportPlaybackStatus.Paused && !_transportPausedByCoordinator)
            {
                EnsureRestartIsCurrent(generation, cancellationToken);
                lock (_processingSync)
                {
                    if (!_stopping && generation == _currentTrackGeneration)
                        _deferredRateSwitch = new DeferredRateSwitch(track, generation, cancellationToken);
                }
                _logger.Info("Deferred rate switch: the user already paused this track; keeping the device format unchanged until user playback is observed.", correlation);
                PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Waiting for user playback", targetDevice.FriendlyName,
                    track, resolved, decision.CurrentDeviceFormat,
                    "Playback is paused by the user. The device format will be re-evaluated after the user presses Play."));
                return;
            }
            MuteTargetDeviceForProcessing(targetDevice.Id, correlation);
            if (_audioEndpointController.GetMasterMute(targetDevice.Id) != true)
            {
                Hold("Pause-switch canceled: endpoint mute could not be verified before pausing.");
                return;
            }
            if (state.IsPlaying)
            {
                _transportPausedByCoordinator = await _mediaTransportController.TryPauseAsync(cancellationToken);
                EnsureRestartIsCurrent(generation, cancellationToken);
                if (!_transportPausedByCoordinator)
                {
                    Hold("Pause-switch canceled: Apple Music rejected the initial pause.");
                    return;
                }
            }
            else if (state.PlaybackStatus != MediaTransportPlaybackStatus.Paused)
            {
                Hold("Pause-switch canceled: playback was neither playing nor confirmed paused.");
                return;
            }

            PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Paused before format change", targetDevice.FriendlyName,
                track, resolved, decision.CurrentDeviceFormat, null));
            var pauseTimer = Stopwatch.StartNew();
            while ((state = _mediaTransportController.GetPlaybackState()).PlaybackStatus != MediaTransportPlaybackStatus.Paused &&
                   pauseTimer.Elapsed < RestartPauseConfirmationTimeout)
            {
                await Task.Delay(RestartConfirmationPollInterval, cancellationToken);
                EnsureRestartIsCurrent(generation, cancellationToken);
            }
            if (state.PlaybackStatus != MediaTransportPlaybackStatus.Paused)
            {
                Hold("Pause-switch canceled: the initial pause was not confirmed.");
                return;
            }
            _logger.Info($"Pause-switch: paused before format apply; timeline={state.TimelinePosition}; " +
                "pause is not proof that the renderer released its audio client.", correlation);
            await Task.Delay(PlaybackRecoveryNudgeDelay, cancellationToken);
            if (!await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken))
            {
                Hold("Pause-switch canceled: current item changed before format apply.");
                return;
            }
            if (_mediaTransportController.GetPlaybackState().PlaybackStatus != MediaTransportPlaybackStatus.Paused)
            {
                Hold("Pause-switch canceled: the paused state changed before format apply.");
                return;
            }

            EnsureRestartIsCurrent(generation, cancellationToken);
            _logger.Info($"Pause-switch: applying {decision.SelectedFormat!.DisplayName} while the verified current track is paused.", correlation);
            if (!_audioEndpointController.TryApplyFormat(targetDevice.Id, decision.SelectedFormat!, out appliedFormat, out var failure))
            {
                Hold("Pause-switch format apply failed: " + failure, RecoveryCategory.FormatApplyFailed);
                return;
            }
            Volatile.Write(ref _currentTrackSwitchApplied, true);
            var ready = await WaitForTargetFormatAsync(targetDevice.Id, decision.SelectedFormat!, appliedFormat, cancellationToken);
            appliedFormat = ready.VerifiedFormat ?? appliedFormat;
            if (!ready.IsReady)
            {
                Hold("Pause-switch: target format did not become stable; playback remains held.", RecoveryCategory.FormatNotStable);
                return;
            }
            _logger.Info($"Pause-switch: target format confirmed {appliedFormat?.DisplayName}; endpoint remains muted.", correlation);
            if (!await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken))
            {
                Hold("Pause-switch: current item was not verified before muted warmup.");
                return;
            }
            _logger.Info("Pause-switch: beginning muted renderer warmup; no audible resume is permitted before same-track 0:00 confirmation.", correlation);
            if (!await TryPlayWithRetriesAsync(cancellationToken))
            {
                Hold("Pause-switch: muted Play was rejected after format confirmation.");
                return;
            }
            PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Muted warmup before 0:00", targetDevice.FriendlyName,
                track, resolved, appliedFormat, null, WasFormatChanged: true));
            var warmup = await ObserveMutedWarmupAsync(targetDevice.Id, generation, correlation, StreamRebuildTimeout, cancellationToken);
            if (warmup.Outcome == MutedWarmupOutcome.TransportUnconfirmed)
            {
                Hold("Pause-switch: muted Play did not reach a confirmed Playing state; no additional command will be sent.");
                return;
            }
            if (warmup.Outcome == MutedWarmupOutcome.UserPaused)
            {
                _logger.Info("Pause-switch canceled: the user paused during muted warmup; no automatic resume or Previous.", correlation);
                PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Playback paused", targetDevice.FriendlyName,
                    track, resolved, appliedFormat, "User paused; restart canceled.", WasFormatChanged: true));
                return;
            }
            if (warmup.Outcome != MutedWarmupOutcome.AudioReady)
            {
                // A dormant client can stay invalidated after a paused format change. Try
                // one mute-preserving pause/play; never skip a track or restart Apple Music.
                if (!await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken))
                {
                    Hold("Pause-switch: current item was not verified before the single renderer recovery.");
                    return;
                }
                _logger.Warn("Pause-switch: no renderer audio after paused apply; attempting one muted pause/play recovery.", correlation);
                _transportPausedByCoordinator = await _mediaTransportController.TryPauseAsync(cancellationToken);
                EnsureRestartIsCurrent(generation, cancellationToken);
                if (!_transportPausedByCoordinator)
                {
                    Hold("Pause-switch: the single renderer recovery pause was rejected.");
                    return;
                }
                await Task.Delay(PlaybackRecoveryNudgeDelay, cancellationToken);
                if (!await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken) ||
                    _mediaTransportController.GetPlaybackState().PlaybackStatus != MediaTransportPlaybackStatus.Paused ||
                    !await TryPlayWithRetriesAsync(cancellationToken))
                {
                    Hold("Pause-switch: the single renderer recovery could not safely resume.");
                    return;
                }
                warmup = await ObserveMutedWarmupAsync(targetDevice.Id, generation, correlation, PlaybackRecoveryObservationWindow, cancellationToken);
                if (warmup.Outcome == MutedWarmupOutcome.UserPaused)
                {
                    PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Playback paused", targetDevice.FriendlyName,
                        track, resolved, appliedFormat, "User paused; restart canceled.", WasFormatChanged: true));
                    return;
                }
                if (warmup.Outcome != MutedWarmupOutcome.AudioReady)
                {
                    Hold("Pause-switch: the paused format change did not rebuild audio after one recovery. Pause Apple Music and exit the switcher.");
                    return;
                }
            }
            if (!await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken))
            {
                Hold("Pause-switch: current item was not verified before returning to 0:00.");
                return;
            }
            var restart = await TryRestartTrackAfterRateChangeAsync(targetDevice.Id, track, generation, correlation, cancellationToken,
                requireConfirmedZero: true, mutedPlayingTimer: warmup.PlayingTimer, expectedFormat: decision.SelectedFormat);
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (!restart.PlaybackRestored)
            {
                if (_manualTrackHold is { } held && held.Generation == generation)
                    return;
                Hold(restart.Note ?? "Pause-switch: same-track 0:00 and audible playback could not be confirmed.");
                return;
            }
            PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Playback restored", targetDevice.FriendlyName,
                track, resolved, appliedFormat, restart.Note, WasFormatChanged: true));
        }
        catch (OperationCanceledException) when (_stopping || cancellationToken.IsCancellationRequested || Volatile.Read(ref _currentTrackGeneration) != generation)
        {
            // The successor inherits only our own pause/mute; disposal releases owned mute.
            // A guard that already dispatched navigation has its own stricter safety hold.
            _logger.Info("Pause-switch canceled; no old-track automatic Play will be issued.", correlation);
            throw;
        }
        catch (Exception ex)
        {
            Hold("Pause-switch failed: " + ex.Message);
            throw;
        }
    }

    private enum MutedWarmupOutcome { AudioReady, UserPaused, TimedOut, TransportUnconfirmed }
    private sealed record MutedWarmupResult(MutedWarmupOutcome Outcome, Stopwatch? PlayingTimer);

    private async Task<MutedWarmupResult> ObserveMutedWarmupAsync(
        string deviceId, long generation, DiagnosticsLogger.LogCorrelation correlation, TimeSpan window, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        Stopwatch? playingTimer = null;
        while (timer.Elapsed < window)
        {
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (_audioEndpointController.GetMasterMute(deviceId) != true)
                throw new InvalidOperationException("Endpoint mute was lost during renderer warmup.");
            var state = _mediaTransportController.GetPlaybackState();
            if (state.IsPlaying)
                playingTimer ??= Stopwatch.StartNew();
            else if (state.PlaybackStatus is MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Closed or MediaTransportPlaybackStatus.Stopped)
            {
                if (playingTimer is not null)
                    return new(MutedWarmupOutcome.UserPaused, playingTimer);
                // Play acceptance precedes GSMTC state propagation. A stale initial
                // Paused is not a user pause and must never release the endpoint mute.
                if (timer.Elapsed >= RestartPauseConfirmationTimeout)
                    return new(MutedWarmupOutcome.TransportUnconfirmed, null);
            }
            var active = _audioEndpointController.IsProcessSessionActive(deviceId, AppleMediaAgentProcessName);
            var peak = _audioEndpointController.GetProcessSessionPeak(deviceId, AppleMediaAgentProcessName) ?? 0f;
            if (state.IsPlaying && active == true && peak > PlaybackRestorePeakThreshold)
            {
                _logger.Info($"Pause-switch: muted renderer audio confirmed after {timer.ElapsedMilliseconds} ms; " +
                    $"timeline={state.TimelinePosition}, agentPeak={peak:F6}.", correlation);
                return new(MutedWarmupOutcome.AudioReady, playingTimer);
            }
            await Task.Delay(StreamRebuildPollInterval, cancellationToken);
        }
        return new(playingTimer is null ? MutedWarmupOutcome.TransportUnconfirmed : MutedWarmupOutcome.TimedOut, playingTimer);
    }

    private async Task<RestartAtZeroResult> TryRestartTrackAfterRateChangeAsync(
        string deviceId,
        TrackSnapshot track,
        long generation,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken,
        bool requireConfirmedZero = false,
        Stopwatch? mutedPlayingTimer = null,
        AudioFormatCandidate? expectedFormat = null)
    {
        EnsureRestartIsCurrent(generation, cancellationToken);
        RestartAtZeroResult Unconfirmed(string reason)
        {
            _logger.Warn(reason, correlation);
            if (requireConfirmedZero)
                SetPreviousRestartSafetyHold(correlation, reason);
            return new(!requireConfirmedZero, reason);
        }
        if (_mediaTransportController is not IPreviousTrackRestarter restarter)
        {
            return Unconfirmed("Previous-restart skipped: guarded track identity verification is unavailable.");
        }

        TrackSnapshot? precedingTrack;
        lock (_processingSync)
        {
            precedingTrack = _precedingTrackSnapshot;
        }

        var state = _mediaTransportController.GetPlaybackState();
        EnsureRestartIsCurrent(generation, cancellationToken);
        if (!state.IsPlaying)
        {
            if (requireConfirmedZero && state.PlaybackStatus is not (MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed))
                return Unconfirmed("Previous-restart canceled: transport was unavailable before the 0:00 guard.");
            _logger.Info("Previous-restart skipped: playback is no longer playing; respecting the user's pause.", correlation);
            return new(true, null);
        }

        MuteTargetDeviceForProcessing(deviceId, correlation);
        if (_audioEndpointController.GetMasterMute(deviceId) != true)
        {
            const string muteNote = "Previous-restart skipped: endpoint mute could not be verified.";
            return Unconfirmed(muteNote);
        }

        var readiness = Stopwatch.StartNew();
        while (state.TimelinePosition is { } position &&
               (position < PreviousRestartMinimumPosition ||
                (requireConfirmedZero && (mutedPlayingTimer is null || mutedPlayingTimer.Elapsed < PreviousRestartMinimumPosition))) &&
               readiness.Elapsed < (requireConfirmedZero ? PausedSwitchRestartReadinessTimeout : PreviousRestartReadinessTimeout))
        {
            EnsureRestartIsCurrent(generation, cancellationToken);
            await Task.Delay(RestartConfirmationPollInterval, cancellationToken);
            state = _mediaTransportController.GetPlaybackState();
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (!state.IsPlaying)
            {
                if (requireConfirmedZero && state.PlaybackStatus is not (MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed))
                    return Unconfirmed("Previous-restart canceled: transport became unavailable during muted preparation.");
                return new(true, null);
            }
        }
        if (state.TimelinePosition is not { } readyPosition || readyPosition < PreviousRestartMinimumPosition ||
            (requireConfirmedZero && (mutedPlayingTimer is null || mutedPlayingTimer.Elapsed < PreviousRestartMinimumPosition)))
        {
            const string positionNote = "Previous-restart skipped: position is unknown or still below the conservative 10-second threshold.";
            return Unconfirmed(positionNote);
        }

        var ownsPause = false;
        var resumeAttempted = false;
        try
        {
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (requireConfirmedZero && !await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken))
                return new(false, PreviousRestartManualRecoveryMessage);
            ownsPause = await _mediaTransportController.TryPauseAsync(cancellationToken);
            if (ownsPause)
            {
                _transportPausedByCoordinator = true;
            }
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (!ownsPause)
            {
                const string pauseNote = "Previous-restart skipped: Apple Music rejected pause; no Previous command was sent.";
                return Unconfirmed(pauseNote);
            }

            var pauseTimer = Stopwatch.StartNew();
            state = _mediaTransportController.GetPlaybackState();
            while (state.PlaybackStatus != MediaTransportPlaybackStatus.Paused &&
                   pauseTimer.Elapsed < RestartPauseConfirmationTimeout)
            {
                EnsureRestartIsCurrent(generation, cancellationToken);
                if (state.PlaybackStatus is MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed)
                {
                    ownsPause = _transportPausedByCoordinator = false;
                    return new(true, "Previous-restart skipped: playback was stopped.");
                }
                await Task.Delay(RestartConfirmationPollInterval, cancellationToken);
                state = _mediaTransportController.GetPlaybackState();
            }
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (state.PlaybackStatus != MediaTransportPlaybackStatus.Paused)
            {
                if (requireConfirmedZero)
                    return Unconfirmed("Previous-restart skipped: pause could not be confirmed; no Previous command was sent.");
                return new(false, "Previous-restart skipped: pause could not be confirmed; no Previous command was sent.");
            }

            await Task.Delay(PlaybackRecoveryNudgeDelay, cancellationToken);
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (_mediaTransportController.GetPlaybackState().PlaybackStatus != MediaTransportPlaybackStatus.Paused)
            {
                ownsPause = _transportPausedByCoordinator = false;
                return Unconfirmed("Previous-restart skipped: transport changed before the guarded command.");
            }

            // If a different track is published during this operation it must never inherit an
            // automatic Play. Unsafe results hold the mute and stop all automatic processing.
            _transportPausedByCoordinator = false;
            _previousRestartGuardActive = true;
            PreviousRestartResult result;
            try
            {
                _logger.Info("Previous-restart: starting the guarded single-Previous operation.", correlation);
                result = await restarter.TryRestartWithPreviousAsync(track, precedingTrack, cancellationToken);
                EnsureRestartIsCurrent(generation, cancellationToken);
                if (result.CanRecoverOnNewTrack)
                {
                    // The transport has freshly verified the paused item and explicitly
                    // proved that no navigation was dispatched. Do not turn this into a
                    // permanent global lock or audible resume from ten seconds.
                    if (_mediaTransportController.GetPlaybackState().PlaybackStatus != MediaTransportPlaybackStatus.Paused)
                    {
                        SetPreviousRestartSafetyHold(correlation, "The verified item was no longer paused after the no-navigation capability check.");
                        ownsPause = false;
                        return new(false, GetGlobalHoldMessage());
                    }
                    // The adapter proved that no navigation was sent. A new user-selected
                    // track after publishing the recoverable status must not be mistaken
                    // for a replacement during an in-flight navigation command.
                    _previousRestartGuardActive = false;
                    SetManualTrackHold(track, generation, "Switching: Restart unavailable",
                        "Apple Music's Previous control is unavailable. No navigation was sent and the track remains paused. " +
                        "Select a different song to continue, or pause Apple Music and exit the switcher before restarting this song manually.", correlation,
                        expectedFormat ?? _audioEndpointController.GetCurrentDeviceFormat(deviceId), wasFormatChanged: true, category: RecoveryCategory.PreviousUnavailable);
                    ownsPause = false;
                    return new(false, result.Detail);
                }
                if (!result.SafeToResume || (requireConfirmedZero && !result.Restarted))
                {
                    SetPreviousRestartSafetyHold(correlation, result.Detail);
                    ownsPause = false;
                    return new(false, PreviousRestartManualRecoveryMessage + " " + result.Detail);
                }
            }
            catch
            {
                SetPreviousRestartSafetyHold(correlation, "The guarded operation did not return a verified current track.");
                ownsPause = false;
                throw;
            }
            finally
            {
                _previousRestartGuardActive = false;
            }

            EnsureRestartIsCurrent(generation, cancellationToken);
            if (requireConfirmedZero && !await VerifyExpectedTrackAsync(track, generation, correlation, cancellationToken))
            {
                ownsPause = false;
                return new(false, PreviousRestartManualRecoveryMessage);
            }
            state = _mediaTransportController.GetPlaybackState();
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (state.PlaybackStatus is MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed)
            {
                ownsPause = _transportPausedByCoordinator = false;
                return new(true, "Previous-restart: playback was stopped before resume.");
            }
            if (!state.IsPlaying && state.PlaybackStatus != MediaTransportPlaybackStatus.Paused)
            {
                SetPreviousRestartSafetyHold(correlation, "Transport state became unavailable before resume.");
                ownsPause = false;
                return new(false, PreviousRestartManualRecoveryMessage);
            }
            if (requireConfirmedZero &&
                (state.PlaybackStatus != MediaTransportPlaybackStatus.Paused ||
                 state.TimelinePosition is not { } zeroPosition || zeroPosition < TimeSpan.Zero ||
                 zeroPosition > GuardedPreviousRestart.PositionTolerance ||
                 _audioEndpointController.GetMasterMute(deviceId) != true ||
                 (expectedFormat is not null && _audioEndpointController.GetCurrentDeviceFormat(deviceId) != expectedFormat)))
            {
                ownsPause = false;
                return Unconfirmed("Same-track paused 0:00, endpoint mute or target format no longer held at the final audible-resume gate.");
            }

            string? note = result.Restarted ? null : "Previous-restart was not confirmed; keeping the verified current track. " + result.Detail;
            ownsPause = _transportPausedByCoordinator = state.PlaybackStatus == MediaTransportPlaybackStatus.Paused;
            resumeAttempted = true;
            if (ownsPause)
            {
                // Release mute before Play so confirmation does not consume the intro again.
                if (!await ResumePlaybackAfterPauseAsync(null, cancellationToken))
                {
                    return new(false, CombineFailureReason(note, "Playback could not resume; use Play manually."));
                }
            }
            else
            {
                UnmuteTargetDeviceAfterProcessing(correlation);
            }
            EnsureRestartIsCurrent(generation, cancellationToken);

            var restored = await ObservePlaybackRestoredAsync(deviceId, PlaybackRestoreObservationWindow, cancellationToken);
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (!restored)
            {
                return new(false, CombineFailureReason(note, "Audio after the guarded restart was not confirmed; no further navigation was attempted."));
            }

            _logger.Info(
                result.Restarted
                    ? "Previous-restart confirmed: the same current track returned to 0:00 and playback resumed unmuted."
                    : "Previous-restart fallback: the verified current track resumed without a confirmed rewind.",
                correlation);
            return new(true, note);
        }
        finally
        {
            if (ownsPause && !resumeAttempted && !_previousRestartSafetyHold && _manualTrackHold is null && !_stopping)
            {
                if (Volatile.Read(ref _currentTrackGeneration) == generation)
                {
                    if (requireConfirmedZero)
                        SetPreviousRestartSafetyHold(correlation, "Restart ended before paused 0:00 was confirmed; automatic audible resume is forbidden.");
                    else
                        await ResumePlaybackAfterPauseAsync(null, CancellationToken.None);
                }
                else
                {
                    _logger.Info("Previous-restart canceled before the guarded command; the successor inherits the coordinator pause.", correlation);
                }
            }
        }
    }

    private void SetPreviousRestartSafetyHold(DiagnosticsLogger.LogCorrelation correlation, string detail, RecoveryCategory category = RecoveryCategory.UnsafeState)
    {
        var prefix = PreviousRestartManualRecoveryMessage;
        if (detail.StartsWith(prefix, StringComparison.Ordinal))
            detail = detail[prefix.Length..].Trim();
        if (!_previousRestartSafetyHold || string.IsNullOrWhiteSpace(_previousRestartSafetyHoldDetail))
        {
            _previousRestartSafetyHoldDetail = detail;
            _globalRecoveryCategory = category;
        }
        _previousRestartSafetyHold = true;
        _transportPausedByCoordinator = false;
        _manualTrackHold = null;
        lock (_processingSync)
            _deferredRateSwitch = null;
        _logger.Warn(GetGlobalHoldMessage(), correlation);
    }

    private string GetGlobalHoldMessage() => string.IsNullOrWhiteSpace(_previousRestartSafetyHoldDetail)
        ? PreviousRestartManualRecoveryMessage
        : PreviousRestartManualRecoveryMessage + " " + _previousRestartSafetyHoldDetail;

    private void SetManualTrackHold(TrackSnapshot track, long generation, string status, string reason,
        DiagnosticsLogger.LogCorrelation correlation, AudioFormatCandidate? format = null, string? deviceName = null,
        ResolvedAudioFormat? resolved = null, bool wasFormatChanged = false, RecoveryCategory category = RecoveryCategory.AudioRecoveryFailed)
    {
        if (_stopping || generation != Volatile.Read(ref _currentTrackGeneration) || _previousRestartSafetyHold)
            return;
        lock (_processingSync)
        {
            if (_stopping || generation != _currentTrackGeneration || _previousRestartSafetyHold)
                return;
            _manualTrackHold = new ManualTrackHold(track, generation, status, reason, format, deviceName, resolved, wasFormatChanged, category);
            _deferredRateSwitch = null;
            _transportPausedByCoordinator = false;
        }
        _logger.Warn(reason, correlation);
        PublishStatusIfCurrent(generation, new SwitchingStatus(status, deviceName, track, resolved, format, reason, wasFormatChanged, category));
    }
    private void EnsureRestartIsCurrent(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopping || _manualRestartRequested || Volatile.Read(ref _currentTrackGeneration) != generation)
        {
            throw new OperationCanceledException("Restart superseded by a newer track.", cancellationToken);
        }
    }

    private async Task<bool> TryRealignScheduleAfterRebuildAsync(
        string deviceId,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken)
    {
        var state = _mediaTransportController.GetPlaybackState();
        if (TryCapturePlaybackProbe(deviceId, state) is { } startProbe)
        {
            _logger.Verbose($"Intro-trace: realign start; {startProbe}.", correlation);
        }

        if (!state.IsPlaying)
        {
            // The user paused meanwhile — never play over them. Their next resume realigns
            // the schedule on its own.
            return true;
        }

        // Captured before the pause: the probe enumerates COM audio sessions and must not sit
        // inside the pause->delay->play window whose duration is exactly what this trace measures.
        var pauseProbe = TryCapturePlaybackProbe(deviceId, state);

        // Mark the pause as ours so a track arriving mid-realign inherits and resumes it.
        _transportPausedByCoordinator = await _mediaTransportController.TryPauseAsync(cancellationToken);
        if (!_transportPausedByCoordinator)
        {
            _logger.Info("Schedule realign pause was rejected; the end of the track may be cut short by the rebuild duration.", correlation);
            return true; // playback itself is healthy — keep it
        }

        if (pauseProbe is not null)
        {
            _logger.Verbose($"Intro-trace: realign pausing; probed before the pause: {pauseProbe}.", correlation);
        }

        await Task.Delay(PlaybackRecoveryNudgeDelay, cancellationToken);
        var resumed = await TryPlayWithRetriesAsync(cancellationToken);
        if (!resumed)
        {
            _logger.Warn("Schedule realign play failed; running recovery.", correlation);
            return false;
        }

        if (TryCapturePlaybackProbe(deviceId) is { } resumeProbe)
        {
            _logger.Verbose($"Intro-trace: realign resumed; {resumeProbe}.", correlation);
        }

        var restored = await ObservePlaybackRestoredAsync(deviceId, PlaybackRestoreObservationWindow, cancellationToken);
        _logger.Info(
            restored
                ? "Schedule realigned after the rebuild: audio resumed at the timeline position with a fresh end-of-track schedule."
                : "Schedule realign could not confirm audio; running recovery.",
            correlation);
        return restored;
    }

    private async Task<bool> ObservePlaybackRestoredAsync(string deviceId, TimeSpan observationWindow, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + observationWindow;
        var baselinePosition = default(TimeSpan?);
        var sawPeak = false;
        var maxPeakValue = 0f;
        var lastPlaybackStatus = MediaTransportPlaybackStatus.Unknown;
        var lastTimelinePosition = default(TimeSpan?);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var playbackState = _mediaTransportController.GetPlaybackState();
            lastPlaybackStatus = playbackState.PlaybackStatus;
            lastTimelinePosition = playbackState.TimelinePosition;

            if (baselinePosition is null && playbackState.TimelinePosition is not null)
            {
                baselinePosition = playbackState.TimelinePosition;
            }

            // The agent's own session peak, NOT the endpoint master meter: other applications'
            // audio on the same device falsely confirmed dead playback as restored (proven live,
            // with the timeline frozen at 0:00 while another app made the master meter move).
            var peakValue = _audioEndpointController.GetProcessSessionPeak(deviceId, AppleMediaAgentProcessName);
            var currentPeak = peakValue ?? 0f;
            if (currentPeak > maxPeakValue)
            {
                maxPeakValue = currentPeak;
            }

            if (currentPeak > PlaybackRestorePeakThreshold)
            {
                sawPeak = true;
            }

            var timelineAdvanced = baselinePosition is not null &&
                                   playbackState.TimelinePosition is not null &&
                                   playbackState.TimelinePosition.Value - baselinePosition.Value >= TimelineAdvanceThreshold;

            // Only a real audio peak proves playback restored. Apple Music advances its timeline
            // even when the render stream is dead (verified live: position moving, endpoint
            // unmuted, meter flat zero), so timeline advance alone must never confirm restore.
            if (playbackState.IsPlaying && sawPeak)
            {
                _logger.Info(
                    $"Playback restore confirmed: isPlaying={playbackState.IsPlaying}, sawPeak={sawPeak} (maxPeak={maxPeakValue:F6}), " +
                    $"timelineAdvanced={timelineAdvanced}, baseline={baselinePosition}, current={playbackState.TimelinePosition}.");
                return true;
            }

            await Task.Delay(PlaybackRestorePollInterval, cancellationToken);
        }

        _logger.Warn(
            $"Playback restore observation timed out after {observationWindow.TotalSeconds:0.#}s: " +
            $"lastStatus={lastPlaybackStatus}, sawPeak={sawPeak} (maxPeak={maxPeakValue:F6}), " +
            $"baselinePosition={baselinePosition}, lastTimelinePosition={lastTimelinePosition}.");
        return false;
    }

    private async Task<bool> TryRecoverPlaybackWithNudgesAsync(
        string deviceId,
        string? targetDeviceName,
        long generation,
        TrackSnapshot? track,
        ResolvedAudioFormat? resolved,
        AudioFormatCandidate? verifiedDeviceFormat,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken,
        int maxNudges = MaxPlaybackRecoveryNudges,
        bool switchApplied = true)
    {
        for (var nudge = 1; nudge <= maxNudges; nudge++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Info("Playback recovery abandoned because a newer track superseded this one.", correlation);
                return false;
            }

            _logger.Warn($"Playback restore unconfirmed; starting recovery nudge {nudge}/{maxNudges} (pause→play cycle).", correlation);
            PublishStatusIfCurrent(generation, new SwitchingStatus("Switching: Recovering playback", targetDeviceName, track, resolved, verifiedDeviceFormat, null));

            // A failed pause here is expected when GSMTC has already fallen back to Paused
            // (IsPauseEnabled goes false); the follow-up play is still worth attempting.
            var paused = await _mediaTransportController.TryPauseAsync(cancellationToken);
            if (!paused)
            {
                _logger.Info($"Recovery nudge {nudge}/{maxNudges}: pause command was rejected; continuing with play.", correlation);
            }

            await Task.Delay(PlaybackRecoveryNudgeDelay, cancellationToken);

            if (nudge == maxNudges)
            {
                // Final escalation: plain pause/play cycles sometimes cannot revive a render
                // stream killed by the device format change, but a timeline operation forces
                // Apple Music to rebuild its playback pipeline (a track change always recovers
                // it; a seek is the closest non-destructive equivalent). The timeline is frozen
                // in this state, so seeking to the current position loses nothing.
                var seekPosition = _mediaTransportController.GetPlaybackState().TimelinePosition ?? TimeSpan.Zero;
                var seeked = await _mediaTransportController.TryChangePlaybackPositionAsync(seekPosition, cancellationToken);
                _logger.Info(
                    seeked
                        ? $"Recovery nudge {nudge}/{maxNudges}: seeked to {seekPosition} to force a playback pipeline rebuild."
                        : $"Recovery nudge {nudge}/{maxNudges}: seek to {seekPosition} was rejected; continuing with play.",
                    correlation);
            }

            var resumed = await ResumePlaybackAfterPauseAsync(targetDeviceName, cancellationToken);
            if (!resumed)
            {
                _logger.Warn($"Recovery nudge {nudge}/{maxNudges}: play command failed.", correlation);
                continue;
            }

            if (await ObservePlaybackRestoredAsync(deviceId, PlaybackRecoveryObservationWindow, cancellationToken))
            {
                _logger.Info($"Playback recovered after nudge {nudge}/{maxNudges}.", correlation);
                _recentRecoveryFailures.Clear();
                _lastRecoverySkipWasNoSwitchStall = false;
                return true;
            }
        }

        _logger.Warn($"Playback recovery gave up after {maxNudges} nudge(s).", correlation);

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (Settings.RestartTrackAfterSampleRateChange)
        {
            await HoldFailedRecoveryAsync(deviceId, targetDeviceName, generation, track, resolved,
                verifiedDeviceFormat, correlation, cancellationToken, wasFormatChanged: switchApplied);
            return false;
        }

        if (!switchApplied)
        {
            // No format switch was applied, so this is not switch-induced renderer damage —
            // Apple Music simply never started the track. Before giving the song up, try once
            // more in place with a longer settle than the nudge loop uses: some of these
            // recover on a second attempt, and skipping costs the user the song outright.
            if (await TryPatientRetryAsync(deviceId, correlation, cancellationToken))
            {
                return true;
            }

            // Still nothing. Restarting Apple Music would not help and must not be primed
            // either: these stalls never count toward the cascade threshold. Skip forward only,
            // and once a cluster is underway relax the cooldown so consecutive dead tracks are
            // skipped through quickly instead of stranding the queue in silence.
            var cooldown = _lastRecoverySkipWasNoSwitchStall
                ? ConsecutiveNoSwitchStallSkipCooldown
                : RecoverySkipCooldown;
            await TryEscalateSkipNextAsync(cooldown, isNoSwitchStall: true, correlation, cancellationToken);
            return false;
        }

        // Repeated failed recoveries in a short window mean the renderer itself is degraded —
        // skipping would just move the wedge to the next track (proven live). Restart Apple
        // Music instead: the only cure ever observed for that state.
        RecordRecoveryFailure();
        if (ShouldEscalateToAppleMusicRestart())
        {
            var restarted = await TryRestartAppleMusicEscalationAsync(
                deviceId, targetDeviceName, generation, track, resolved, verifiedDeviceFormat, correlation, cancellationToken);
            if (restarted)
            {
                _recentRecoveryFailures.Clear();
                _lastRecoverySkipWasNoSwitchStall = false;
                return true;
            }

            // The restart ran (or failed); skipping on top of it would not help.
            return false;
        }

        // Hard-wedged renderer: nudges cannot revive it, but a track change always rebuilds
        // Apple Music's playback pipeline. Skip forward (bounded by a cooldown so a persistent
        // wedge can never runaway-skip through the queue). The resulting track change supersedes
        // this processing and the next track is handled — and health-checked — normally.
        await TryEscalateSkipNextAsync(RecoverySkipCooldown, isNoSwitchStall: false, correlation, cancellationToken);
        return false;
    }

    /// <summary>
    /// Last chance for a track Apple Music never started: one pause / longer settle / play cycle.
    /// The nudge loop's 400 ms settle is tuned for reviving a wedged renderer mid-stream; a track
    /// that never loaded needs Apple Music to redo the load, which takes longer. Runs at most once
    /// per track (a cluster of unplayable tracks must not each pay the extra wait twice) and is
    /// deliberately seek-free — see the skip-previous warning on the restart path.
    /// </summary>
    private async Task<bool> TryPatientRetryAsync(
        string deviceId,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _patientRetryUsedForTrack))
        {
            return false;
        }

        Volatile.Write(ref _patientRetryUsedForTrack, true);
        _logger.Info("Track never started; retrying it in place before skipping.", correlation);

        await _mediaTransportController.TryPauseAsync(cancellationToken);
        await Task.Delay(PatientRetrySettleDelay, cancellationToken);
        if (!await ResumePlaybackAfterPauseAsync(deviceId, cancellationToken))
        {
            return false;
        }

        if (!await ObservePlaybackRestoredAsync(deviceId, PatientRetryObservationWindow, cancellationToken))
        {
            _logger.Warn("In-place retry did not start the track either.", correlation);
            return false;
        }

        _logger.Info("In-place retry started the track; keeping it instead of skipping.", correlation);
        _recentRecoveryFailures.Clear();
        _lastRecoverySkipWasNoSwitchStall = false;
        return true;
    }

    private async Task TryEscalateSkipNextAsync(
        TimeSpan cooldown,
        bool isNoSwitchStall,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastRecoverySkipUtc < cooldown)
        {
            _logger.Warn("Skipping the skip-next escalation: a recovery skip already fired within the cooldown window.", correlation);
            return;
        }

        _lastRecoverySkipUtc = DateTimeOffset.UtcNow;
        _lastRecoverySkipWasNoSwitchStall = isNoSwitchStall;
        var skipped = await _mediaTransportController.TrySkipNextAsync(cancellationToken);
        _logger.Warn(
            skipped
                ? "Recovery escalated: skipped to the next track to force Apple Music to rebuild its playback pipeline."
                : "Recovery escalation failed: the skip-next command was rejected.",
            correlation);
    }

    private async Task HoldFailedRecoveryAsync(string deviceId, string? targetDeviceName, long generation,
        TrackSnapshot? track, ResolvedAudioFormat? resolved, AudioFormatCandidate? verifiedDeviceFormat,
        DiagnosticsLogger.LogCorrelation correlation, CancellationToken cancellationToken, bool wasFormatChanged)
    {
        EnsureRestartIsCurrent(generation, cancellationToken);
        track ??= _currentTrackSnapshot;
        MuteTargetDeviceForProcessing(deviceId, correlation);
        if (track is null || _audioEndpointController.GetMasterMute(deviceId) != true)
        {
            SetPreviousRestartSafetyHold(correlation, "Playback recovery failed and a current track with muted output could not be confirmed. No track was skipped or player restarted.");
            PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", targetDeviceName, track, resolved,
                verifiedDeviceFormat, GetGlobalHoldMessage()));
            return;
        }
        if (_mediaTransportController is ITrackIdentityVerifier verifier &&
            !await verifier.IsCurrentTrackAsync(track, cancellationToken))
        {
            EnsureRestartIsCurrent(generation, cancellationToken);
            SetPreviousRestartSafetyHold(correlation, "Playback recovery failed and current item identity changed before the final pause. No navigation was sent.");
            PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", targetDeviceName, track, resolved,
                verifiedDeviceFormat, GetGlobalHoldMessage()));
            return;
        }
        EnsureRestartIsCurrent(generation, cancellationToken);
        var state = _mediaTransportController.GetPlaybackState();
        if (state.IsPlaying)
        {
            _transportPausedByCoordinator = await _mediaTransportController.TryPauseAsync(cancellationToken);
            EnsureRestartIsCurrent(generation, cancellationToken);
            if (!_transportPausedByCoordinator)
            {
                SetPreviousRestartSafetyHold(correlation, "Playback recovery failed; the final safety pause was rejected. Output remains held, without skipping or restarting Apple Music.");
                PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", targetDeviceName, track, resolved,
                    verifiedDeviceFormat, GetGlobalHoldMessage()));
                return;
            }
            var pauseTimer = Stopwatch.StartNew();
            do
            {
                state = _mediaTransportController.GetPlaybackState();
                if (state.PlaybackStatus == MediaTransportPlaybackStatus.Paused)
                    break;
                await Task.Delay(RestartConfirmationPollInterval, cancellationToken);
                EnsureRestartIsCurrent(generation, cancellationToken);
            } while (pauseTimer.Elapsed < RestartPauseConfirmationTimeout);
        }
        if (state.PlaybackStatus is not (MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed))
        {
            SetPreviousRestartSafetyHold(correlation, "Playback recovery failed; a final pause could not be confirmed. No track was skipped or player restarted.");
            PublishStatusIfCurrent(generation, new SwitchingStatus("Resolver: Failed", targetDeviceName, track, resolved,
                verifiedDeviceFormat, GetGlobalHoldMessage()));
            return;
        }
        EnsureRestartIsCurrent(generation, cancellationToken);
        SetManualTrackHold(track, generation, "Switching: Manual recovery needed",
            "Playback recovery could not confirm audio. The current track is held and automatic retries are stopped. " +
            "No track was skipped or player restarted. Select a different song to continue, or pause Apple Music and exit the switcher to recover manually.", correlation,
            verifiedDeviceFormat ?? _audioEndpointController.GetCurrentDeviceFormat(deviceId), targetDeviceName, resolved,
            wasFormatChanged: wasFormatChanged);
    }

    private void RecordRecoveryFailure()
    {
        var cutoff = DateTimeOffset.UtcNow - CascadeFailureWindow;
        _recentRecoveryFailures.RemoveAll(timestamp => timestamp < cutoff);
        _recentRecoveryFailures.Add(DateTimeOffset.UtcNow);
    }

    private bool ShouldEscalateToAppleMusicRestart()
    {
        if (_appleMusicProcessController is null || !Settings.RestartAppleMusicOnPlaybackFailure)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - _lastAppleMusicRestartUtc < AppleMusicRestartCooldown)
        {
            return false;
        }

        return _recentRecoveryFailures.Count >= CascadeFailureThreshold;
    }

    private async Task<bool> TryRestartAppleMusicEscalationAsync(
        string deviceId,
        string? targetDeviceName,
        long generation,
        TrackSnapshot? track,
        ResolvedAudioFormat? resolved,
        AudioFormatCandidate? verifiedDeviceFormat,
        DiagnosticsLogger.LogCorrelation correlation,
        CancellationToken cancellationToken)
    {
        _lastAppleMusicRestartUtc = DateTimeOffset.UtcNow;
        _logger.Warn(
            $"Recovery escalated: {_recentRecoveryFailures.Count} failed recoveries within {CascadeFailureWindow.TotalMinutes:0} minutes — restarting Apple Music to rebuild its degraded renderer.",
            correlation);
        PublishStatusIfCurrent(generation, new SwitchingStatus(
            "Switching: Restarting Apple Music", targetDeviceName, track, resolved, verifiedDeviceFormat,
            "Playback wedged repeatedly; restarting Apple Music to recover."));

        // Our pause bookkeeping is void once the process dies.
        _transportPausedByCoordinator = false;

        var restarted = await _appleMusicProcessController!.TryRestartAsync(cancellationToken);
        if (!restarted)
        {
            _logger.Warn("Apple Music restart escalation failed: the app could not be restarted.", correlation);
            return false;
        }

        var sessionDeadline = DateTimeOffset.UtcNow + PostRestartSessionTimeout;
        while (_mediaTransportController.GetPlaybackState().PlaybackStatus == MediaTransportPlaybackStatus.Unknown)
        {
            if (DateTimeOffset.UtcNow >= sessionDeadline)
            {
                _logger.Warn("Apple Music restarted but its media session did not reappear; leaving playback to the user.", correlation);
                PublishRestartedButNotResumed(generation, targetDeviceName, track, resolved, verifiedDeviceFormat);
                return false;
            }

            await Task.Delay(PostRestartSessionPollInterval, cancellationToken);
        }

        await Task.Delay(PostRestartPlayDelay, cancellationToken);

        var resumed = await ResumePlaybackAfterPauseAsync(targetDeviceName, cancellationToken);
        var restored = resumed && await ObservePlaybackRestoredAsync(deviceId, PostRestartRestoreObservationWindow, cancellationToken);
        if (restored)
        {
            _logger.Info("Apple Music restarted and playback recovered.", correlation);
            PublishStatusIfCurrent(generation, new SwitchingStatus(
                "Switching: Playback restored", targetDeviceName, track, resolved, verifiedDeviceFormat,
                "Apple Music was restarted to recover playback."));
        }
        else
        {
            // Apple Music does not restore its play queue across restarts (platform limitation,
            // verified live), so a healthy-but-silent outcome is the expected common case here.
            // The renderer is fixed; the user just has to start the music again.
            _logger.Warn("Apple Music restarted with a healthy renderer, but it cannot restore its play queue; the user must press Play.", correlation);
            PublishRestartedButNotResumed(generation, targetDeviceName, track, resolved, verifiedDeviceFormat);
        }

        return restored;
    }

    private void PublishRestartedButNotResumed(
        long generation,
        string? targetDeviceName,
        TrackSnapshot? track,
        ResolvedAudioFormat? resolved,
        AudioFormatCandidate? verifiedDeviceFormat)
    {
        Volatile.Write(ref _suppressWatchdogUntilNextTrack, true);
        PublishStatusIfCurrent(generation, new SwitchingStatus(
            "Switching: Apple Music restarted", targetDeviceName, track, resolved, verifiedDeviceFormat,
            "Apple Music was restarted after repeated playback failures. Press Play in Apple Music to resume your music."));
    }

    private void PublishStatus(SwitchingStatus status) => StatusChanged?.Invoke(this, status);

    private (long Generation, CancellationToken CancellationToken) BeginTrackProcessing(TrackSnapshot track, long? deferredGeneration = null)
    {
        lock (_processingSync)
        {
            if (_manualTrackHold is { } held &&
                (held.Track.UniqueKey == track.UniqueKey || AppleMusicTrackSource.IsPlaceholderTrack(track)))
                return (0, new CancellationToken(canceled: true));
            if (deferredGeneration is { } expected &&
                (_stopping || _previousRestartSafetyHold || _manualTrackHold is not null ||
                 _currentTrackGeneration != expected || _currentTrackKey != track.UniqueKey))
                return (0, new CancellationToken(canceled: true));
            var supersededGeneration = _currentTrackGeneration;
            var supersededTrackKey = _currentTrackKey;

            _currentTrackGeneration++;
            var generation = _currentTrackGeneration;
            _currentTrackCts?.Cancel();
            _currentTrackCts?.Dispose();
            if (!string.IsNullOrWhiteSpace(supersededTrackKey))
            {
                _logger.Info(
                    $"Canceling superseded track processing because {track.UniqueKey} arrived next.",
                    new DiagnosticsLogger.LogCorrelation(supersededGeneration, supersededTrackKey));
            }

            _currentTrackCts = new CancellationTokenSource();
            if (_currentTrackSnapshot?.UniqueKey != track.UniqueKey)
            {
                _precedingTrackSnapshot = _currentTrackSnapshot;
            }
            _currentTrackSnapshot = track;
            _currentTrackKey = track.UniqueKey;
            _deferredRateSwitch = null;
            if (_manualTrackHold is { } previousHold && previousHold.Track.UniqueKey != track.UniqueKey)
            {
                _logger.Info("A new current track cleared a no-navigation manual recovery hold; keeping owned mute until its processing completes.");
                _manualTrackHold = null;
                _transportPausedByCoordinator = false;
                _lastWatchdogRecoveryFailureUtc = DateTimeOffset.MinValue;
            }
            Volatile.Write(ref _suppressWatchdogUntilNextTrack, false);
            Volatile.Write(ref _currentTrackSwitchApplied, false);
            Volatile.Write(ref _patientRetryUsedForTrack, false);
            _logger.Info(
                string.IsNullOrWhiteSpace(supersededTrackKey)
                    ? "Starting track processing."
                    : $"Starting track processing. Supersedes {supersededTrackKey}.",
                new DiagnosticsLogger.LogCorrelation(generation, track.UniqueKey));
            return (generation, _currentTrackCts.Token);
        }
    }

    private void CancelCurrentTrackProcessing()
    {
        lock (_processingSync)
        {
            _currentTrackCts?.Cancel();
            _currentTrackCts?.Dispose();
            _currentTrackCts = null;
            _currentTrackKey = null;
            _deferredRateSwitch = null;
        }
    }

    private void TrackTaskStarted(Task task)
    {
        lock (_activeTaskSync)
        {
            _activeTrackTasks.Add(task);
        }
    }

    private void TrackTaskCompleted(Task task)
    {
        lock (_activeTaskSync)
        {
            _activeTrackTasks.Remove(task);
        }
    }

    private async Task AwaitActiveTrackTasksAsync()
    {
        Task[] tasks;
        lock (_activeTaskSync)
        {
            tasks = _activeTrackTasks.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Tasks are canceled as part of disposal — expected.
        }
        catch (Exception)
        {
            // Background task failures do not propagate through DisposeAsync; the app is shutting down.
        }
    }

    private void PublishStatusIfCurrent(long generation, SwitchingStatus status)
    {
        if (generation != Volatile.Read(ref _currentTrackGeneration))
        {
            return;
        }

        if (_previousRestartSafetyHold)
        {
            status = status with { ResolverStatusText = "Resolver: Failed", FailureReason = GetGlobalHoldMessage(), Recovery = _globalRecoveryCategory };
        }
        else if (_manualTrackHold is { } hold && hold.Generation == generation)
        {
            status = status with
            {
                ResolverStatusText = hold.Status,
                FailureReason = hold.Reason,
                ActiveDeviceName = status.ActiveDeviceName ?? hold.DeviceName,
                RequestedFormat = status.RequestedFormat ?? hold.Resolved,
                AppliedFormat = status.AppliedFormat ?? hold.Format,
                WasFormatChanged = status.WasFormatChanged || hold.WasFormatChanged,
                Recovery = hold.Category,
            };
        }
        PublishStatus(status);
    }

    private Task<ResolvedAudioFormat?> ResolveFormatAsync(TrackSnapshot track, CancellationToken cancellationToken) =>
        _resolverChain.ResolveAsync(track, cancellationToken);

    private void MaybeScheduleCacheVerification(TrackSnapshot track, ResolvedAudioFormat resolved)
    {
        var entry = resolved.CachedCatalogEntry;
        if (_formatCacheVerificationService is null ||
            entry is null ||
            !FormatCacheVerificationService.IsVerificationDue(entry, Settings.FormatCacheRefreshDays))
        {
            return;
        }

        Task task;
        lock (_cacheVerificationSync)
        {
            if (_cacheVerificationStopping || _activeCacheVerifications.ContainsKey(entry.UniqueKey))
            {
                return;
            }

            task = PublishCacheVerificationResultAsync(
                _formatCacheVerificationService,
                track,
                entry,
                _cacheVerificationLifetimeCts.Token);
            _activeCacheVerifications.Add(entry.UniqueKey, task);
        }

        _ = task.ContinueWith(
            _ => CacheVerificationCompleted(entry.UniqueKey, task),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PublishCacheVerificationResultAsync(
        FormatCacheVerificationService verificationService,
        TrackSnapshot track,
        FormatCacheEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var update = await verificationService.VerifyAsync(track, entry, cancellationToken);
            if (update is not null && !cancellationToken.IsCancellationRequested)
            {
                FormatCacheUpdated?.Invoke(this, update);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Coordinator disposal cancels outstanding cache verification.
        }
        catch (Exception ex)
        {
            _logger.Warn($"Format cache verification task failed for {track.UniqueKey}: {ex.Message}");
        }
    }

    private void CacheVerificationCompleted(string uniqueKey, Task task)
    {
        lock (_cacheVerificationSync)
        {
            if (_activeCacheVerifications.TryGetValue(uniqueKey, out var current) &&
                ReferenceEquals(current, task))
            {
                _activeCacheVerifications.Remove(uniqueKey);
            }
        }
    }

    private static async Task AwaitCacheVerificationTasksAsync(Task[] tasks)
    {
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Each owned task observes and logs its own failure before completing.
        }
    }

    private AudioDeviceInfo? GetCurrentTargetDevice()
    {
        var devices = _audioEndpointController.GetRenderDevices();
        var defaultDevice = _audioEndpointController.GetDefaultRenderDevice();
        return DeviceTargetingPolicy.SelectTargetDevice(Settings, devices, defaultDevice);
    }

    private ResolvedTrackDecision EvaluateResolvedFormat(AudioDeviceInfo targetDevice, ResolvedAudioFormat resolved)
    {
        var currentDeviceFormat = _audioEndpointController.GetCurrentDeviceFormat(targetDevice.Id);
        var currentMixFormat = _audioEndpointController.GetCurrentMixFormat(targetDevice.Id);
        var supportedFormats = _audioEndpointController.GetSupportedFormats(targetDevice.Id);
        var supportedFormatDescriptions = supportedFormats
            .Select(format =>
            {
                var description = _audioEndpointController.DescribeSupportedFormat(targetDevice.Id, format);
                return description is null ? format.DisplayName : $"{format.DisplayName} [{description}]";
            })
            .ToList();
        _logger.Info(
            $"Target device {targetDevice.FriendlyName}: requested={resolved.BitDepth}/{resolved.SampleRateHz}, " +
            $"deviceFormat={(currentDeviceFormat?.DisplayName ?? "unknown")}, mixFormat={(currentMixFormat?.DisplayName ?? "unknown")}, " +
            $"supported={(supportedFormatDescriptions.Count == 0 ? "none" : string.Join(", ", supportedFormatDescriptions))}.");

        // Tier-fallback results apply like any other resolution: the value derives from the
        // user's Apple Music quality setting, the live switch makes low-confidence changes
        // safe, and a constant fallback means consecutive unidentified tracks are no-ops.
        // (The old "never switch on tier fallback" rule guarded the pause/drain pipeline,
        // where a wrong switch could zombie playback — obsolete since the live-switch
        // redesign, and it left the resolved and applied formats visibly mismatched.)
        var selectedFormat = FormatSelectionPolicy.SelectBest(
            resolved,
            currentDeviceFormat,
            supportedFormats,
            Settings.SwitchBitDepth,
            Settings.DefaultBitDepth,
            Settings.PreferClosestSampleRateMultiple,
            Settings.AllowedSampleRates,
            Settings.AllowedBitDepths);

        if (selectedFormat is not null)
        {
            return new ResolvedTrackDecision(currentDeviceFormat, currentMixFormat, selectedFormat, null);
        }

        var failureReason = $"No supported shared-mode formats were detected for the requested format {resolved.BitDepth}/{resolved.SampleRateHz / 1000.0:0.###}.";
        return new ResolvedTrackDecision(currentDeviceFormat, currentMixFormat, null, failureReason);
    }

    private async Task<(bool ResumeAttempted, string? FailureReason)> ResumeIfNeededAsync(
        bool pausedPlayback,
        bool resumeAttempted,
        string? targetDeviceName,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        if (!pausedPlayback || resumeAttempted)
        {
            return (resumeAttempted, failureReason);
        }

        var resumed = await ResumePlaybackAfterPauseAsync(targetDeviceName, cancellationToken);
        if (!resumed)
        {
            failureReason = CombineFailureReason(failureReason, "Apple Music could not be resumed automatically.");
        }

        return (true, failureReason);
    }

    private static string CombineFailureReason(string? existing, string addition)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return addition;
        }

        return $"{existing} {addition}";
    }

    
    
    private static DiagnosticsLogger.LogCorrelation CreateCorrelation(long generation, TrackSnapshot track) =>
        new(generation, track.UniqueKey);

    private static string DescribeStage(TrackProcessingStage stage) =>
        stage switch
        {
            TrackProcessingStage.BeforeLockAcquisition => "lock acquisition",
            TrackProcessingStage.TrackChangePause => "track-change pause",
            TrackProcessingStage.ResolvingFormat => "format resolution",
            TrackProcessingStage.PauseBeforeSwitch => "pause settle delay",
            TrackProcessingStage.DeviceReadyWait => "device-ready wait",
            TrackProcessingStage.ResumeAfterSwitch => "playback resume",
            TrackProcessingStage.PlaybackRestoreObservation => "playback restore observation",
            TrackProcessingStage.PlaybackRecovery => "playback recovery",
            TrackProcessingStage.RestartFromBeginning => "restart from 0:00",
            _ => "track processing",
        };

    private enum TrackProcessingStage
    {
        BeforeLockAcquisition,
        TrackChangePause,
        ResolvingFormat,
        PauseBeforeSwitch,
        DeviceReadyWait,
        ResumeAfterSwitch,
        PlaybackRestoreObservation,
        PlaybackRecovery,
        RestartFromBeginning,
    }

    private enum PlaybackHealthOutcome
    {
        // Playback is rendering (or not playing at all) — nothing to do.
        Healthy,

        // The zombie signature was detected and the recovery nudges revived playback.
        Recovered,

        // The zombie signature was detected but recovery did not restore audio.
        RecoveryFailed,
    }




    private sealed record DeviceReadyWaitResult(bool IsReady, AudioFormatCandidate? VerifiedFormat);

    private sealed record RestartAtZeroResult(bool PlaybackRestored, string? Note);

    private sealed record DeferredRateSwitch(TrackSnapshot Track, long Generation, CancellationToken CancellationToken,
        int VerificationRetries = 0);

    private sealed record ManualTrackHold(TrackSnapshot Track, long Generation, string Status, string Reason,
        AudioFormatCandidate? Format, string? DeviceName, ResolvedAudioFormat? Resolved, bool WasFormatChanged, RecoveryCategory Category);

    private sealed record ResolvedTrackDecision(
        AudioFormatCandidate? CurrentDeviceFormat,
        AudioFormatCandidate? CurrentMixFormat,
        AudioFormatCandidate? SelectedFormat,
        string? FailureReason);
}

public sealed record FormatRestoreResult(bool Succeeded, string Message, AudioFormatCandidate? VerifiedFormat);
