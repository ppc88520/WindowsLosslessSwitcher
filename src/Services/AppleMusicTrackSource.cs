using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Windows.Foundation;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed class AppleMusicTrackSource : ITrackSource, IMediaTransportController, IPreviousTrackRestarter, ITrackIdentityVerifier, IPlaybackStateNotifier
{
    // AUMID prefix shared by all Apple Music for Windows package identities.
    private const string AppleMusicAumidPrefix = "AppleInc.AppleMusicWin";

    // On Windows 10, GSMTC attributes Apple Music's session to the bare executable name
    // instead of the package AUMID it reports on Windows 11 (verified live on 19045).
    private const string AppleMusicExecutableSessionId = "AppleMusic.exe";

    internal static bool IsAppleMusicSessionId(string? sourceAppUserModelId) =>
        !string.IsNullOrEmpty(sourceAppUserModelId) &&
        (sourceAppUserModelId.Contains(AppleMusicAumidPrefix, StringComparison.OrdinalIgnoreCase) ||
         sourceAppUserModelId.Equals(AppleMusicExecutableSessionId, StringComparison.OrdinalIgnoreCase));

    // Apple Music briefly emits "Connecting…" metadata while it resolves the track after a skip
    // or app resume. Debouncing for 2 seconds prevents a spurious format switch when the real
    // track metadata arrives within that window.
    private static readonly TimeSpan PlaceholderDebounce = TimeSpan.FromSeconds(2);

    // The GSMTC session can disappear for a moment during Apple Music restarts or system
    // sleep/wake cycles. Waiting 3 seconds before fully detaching avoids losing the session
    // during a transient blip.
    private static readonly TimeSpan SessionLossDebounce = TimeSpan.FromSeconds(3);

    // While no Apple Music session is attached, re-enumerate sessions on a slow cadence instead
    // of relying solely on SessionsChanged. Event delivery is not guaranteed on every OS build
    // (Windows 10 in particular is unverified), and the periodic census lines this produces in
    // the diagnostics log show whether Apple Music's session is visible at all.
    private static readonly TimeSpan UnattachedRepollInterval = TimeSpan.FromSeconds(30);

    private readonly DiagnosticsLogger _logger;
    private readonly TimeSpan _placeholderDebounce;
    private readonly TimeSpan _sessionLossDebounce;
    private readonly CancellationTokenSource _repollCts = new();
    private readonly object _sessionStateSync = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _attachedSession;
    private readonly SemaphoreSlim _sessionRefreshGate = new(1, 1);
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _mediaPropertiesHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackInfoHandler;
    private string? _lastTrackKey;
    private CancellationTokenSource? _placeholderPublishCts;
    private CancellationTokenSource? _sessionLossCts;
    private TrackSnapshot? _lastRealTrack;
    private MediaSessionSnapshot _sessionSnapshot = MediaSessionSnapshot.CreateUnavailable();
    private MediaArtworkSnapshot _artworkSnapshot = MediaArtworkSnapshot.CreateUnavailable();
    private MediaArtworkSnapshot? _lastAppliedRealArtwork;
    private long _snapshotGeneration;
    private long _propertiesRefreshRevision;
    private long _sessionEpoch;
    private long _navigationRevision;
    private readonly SemaphoreSlim _previousRestartGate = new(1, 1);
    private readonly CancellationTokenSource _previousRestartLifetime = new();
    private readonly object _previousGuardSync = new();
    private bool _previousGuardActive;
    private string? _previousGuardExpectedKey;
    private string? _previousGuardUnexpectedKey;
    private TrackSnapshot? _deferredTrack;
    private volatile bool _disposed;

    public AppleMusicTrackSource(DiagnosticsLogger logger)
        : this(logger, PlaceholderDebounce, SessionLossDebounce)
    {
    }

    internal AppleMusicTrackSource(
        DiagnosticsLogger logger,
        TimeSpan placeholderDebounce,
        TimeSpan sessionLossDebounce)
    {
        _logger = logger;
        _placeholderDebounce = placeholderDebounce;
        _sessionLossDebounce = sessionLossDebounce;
    }

    public event EventHandler<TrackSnapshot>? TrackChanged;
    public event EventHandler<MediaTransportState>? PlaybackStateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            // Without the session manager the app can still run (and switch manually); throwing
            // here would take down WPF startup. The log line is the diagnostic that matters.
            _logger.Error("Global media session manager is unavailable; track detection is disabled.", ex);
            return;
        }

        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        await RefreshAttachedSessionAsync(cancellationToken: cancellationToken);
        _ = RepollWhileUnattachedAsync(_repollCts.Token);
        _logger.Info("Apple Music track source started.");
    }

    private async Task RepollWhileUnattachedAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(UnattachedRepollInterval, cancellationToken);
                if (_attachedSession is not null)
                {
                    continue;
                }

                _logger.Info("No Apple Music session attached; re-enumerating GSMTC sessions.");
                await RefreshAttachedSessionAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"GSMTC re-poll loop stopped unexpectedly: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        _previousRestartLifetime.Cancel();
        await _previousRestartGate.WaitAsync();
        _previousRestartGate.Release();
        _previousRestartLifetime.Dispose();
        _repollCts.Cancel();
        _repollCts.Dispose();
        CancelSessionLossDebounce();
        CancelPlaceholderPublish();
        await DetachSessionAsync();
    }

    public async Task<bool> IsCurrentTrackAsync(TrackSnapshot expectedTrack, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) return false;
        // Re-enumeration is serialized and keeps the existing wrapper for the same native
        // session. This also notices an idle session that Windows has actually replaced.
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await RefreshAttachedSessionAsync(cancellationToken: deadline.Token);
            var epoch = Volatile.Read(ref _sessionEpoch);
            var session = _attachedSession;
            if (session is null || !IsAttachedSession(session, epoch)) return false;
            var properties = await ReadMediaPropertiesAsync(session, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!IsAttachedSession(session, epoch) || !NativeSessionIdentity.AreSame(GetPreferredCommandSession(), session))
            {
                _logger.Warn($"Track verification rejected a changed session. epoch={epoch}, currentEpoch={Volatile.Read(ref _sessionEpoch)}.");
                return false;
            }
            var actual = NormalizeSnapshot(BuildTrackSnapshot(session, properties, "fresh-track-verification"));
            var matches = !IsPlaceholderTrack(actual) && !string.IsNullOrWhiteSpace(actual.Title) && actual.UniqueKey == expectedTrack.UniqueKey;
            if (!matches) _logger.Warn($"Track verification mismatch: expected={expectedTrack.UniqueKey}, actual={actual.UniqueKey}, epoch={epoch}.");
            return matches;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Fresh track verification failed: {ex.Message}, HRESULT=0x{ex.HResult:X8}.");
            return false;
        }
    }

    public MediaTransportState GetPlaybackState()
    {
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return MediaTransportState.CreateUnavailable();
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playbackInfo.Controls;

            return new MediaTransportState(
                MapPlaybackStatus(playbackInfo.PlaybackStatus),
                controls?.IsPauseEnabled ?? false,
                controls?.IsPlayEnabled ?? false,
                timeline.Position,
                DateTimeOffset.UtcNow)
            {
                CanNext = controls?.IsNextEnabled == true,
                CanPrevious = controls?.IsPreviousEnabled == true,
            };
        }
        catch
        {
            return MediaTransportState.CreateUnavailable();
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionMediaProperties> ReadMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return properties;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { throw new TimeoutException("Apple Music media-properties read timed out; the current track could not be verified."); }
    }

    public MediaSessionSnapshot GetSessionSnapshot()
    {
        lock (_sessionStateSync)
        {
            return _sessionSnapshot;
        }
    }

    public MediaArtworkSnapshot GetArtworkSnapshot()
    {
        lock (_sessionStateSync)
        {
            return _artworkSnapshot;
        }
    }

    public async Task<bool> TryPauseAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigationRevision);
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPauseEnabled != true)
            {
                return false;
            }

            return await BoundedMediaCommand.InvokeAsync(ct => session.TryPauseAsync().AsTask(ct), "Pause", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to pause Apple Music playback: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryPlayAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigationRevision);
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPlayEnabled != true)
            {
                return false;
            }

            return await BoundedMediaCommand.InvokeAsync(ct => session.TryPlayAsync().AsTask(ct), "Play", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to resume Apple Music playback: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryTogglePlayPauseAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigationRevision);
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPlayPauseToggleEnabled != true)
            {
                return false;
            }

            return await BoundedMediaCommand.InvokeAsync(ct => session.TryTogglePlayPauseAsync().AsTask(ct), "Toggle", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to toggle Apple Music playback: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryChangePlaybackPositionAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigationRevision);
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPlaybackPositionEnabled != true)
            {
                return false;
            }

            return await BoundedMediaCommand.InvokeAsync(ct => session.TryChangePlaybackPositionAsync(position.Ticks).AsTask(ct), "Seek", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to change Apple Music playback position: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TrySkipNextAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigationRevision);
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsNextEnabled != true)
            {
                return false;
            }

            return await BoundedMediaCommand.InvokeAsync(ct => session.TrySkipNextAsync().AsTask(ct), "Next", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to skip Apple Music to the next track: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TrySkipPreviousAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigationRevision);
        var session = GetPreferredCommandSession();
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.Controls?.IsPreviousEnabled != true)
            {
                return false;
            }

            // Previous semantics are player-defined and can navigate to another item. Automatic
            // restart uses the separate guarded path; this method remains the ordinary UI control.
            return await BoundedMediaCommand.InvokeAsync(ct => session.TrySkipPreviousAsync().AsTask(ct), "Previous", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to restart the current Apple Music track: {ex.Message}");
            return false;
        }
    }

    public async Task<PreviousRestartResult> TryRestartWithPreviousAsync(
        TrackSnapshot expectedTrack,
        TrackSnapshot? precedingTrack,
        CancellationToken cancellationToken)
    {
        if (_disposed)
            return new(false, false, "Track source is disposed; Previous was not sent.");
        await _previousRestartGate.WaitAsync(cancellationToken);
        CapturedPreviousSession? captured = null;
        PreviousRestartSnapshot? finalSnapshot = null;
        long cleanupPublicationRevision = 0;
        var result = new PreviousRestartResult(false, false, "Guard did not start.");
        try
        {
            var session = _attachedSession;
            if (session is null || _disposed)
            {
                result = new(false, false, "No attached Apple Music session; Previous was not sent.");
            }
            else
            {
                captured = new CapturedPreviousSession(this, session, Volatile.Read(ref _sessionEpoch), expectedTrack, precedingTrack);
                lock (_previousGuardSync)
                {
                    Interlocked.Increment(ref _propertiesRefreshRevision);
                    _previousGuardActive = true;
                    _previousGuardExpectedKey = expectedTrack.UniqueKey;
                    _previousGuardUnexpectedKey = null;
                    _deferredTrack = null;
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _previousRestartLifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    var initial = await captured.ReadAsync(deadline.Token);
                    if (initial is null && !captured.NavigationDispatched)
                    {
                        // An idle/replaced session may fail before any command. One fresh
                        // attachment is safe here; never rebind a command already in flight.
                        _logger.Warn("Previous-restart preflight: no navigation was dispatched; refreshing the session once before a fresh same-track paused check.");
                        await RefreshAttachedSessionAsync(forceReattach: true, cancellationToken: deadline.Token);
                        deadline.Token.ThrowIfCancellationRequested();
                        var refreshedSession = _attachedSession;
                        if (refreshedSession is not null)
                        {
                            captured = new CapturedPreviousSession(this, refreshedSession, Volatile.Read(ref _sessionEpoch), expectedTrack, precedingTrack);
                            initial = await captured.ReadAsync(deadline.Token);
                        }
                    }

                    if (initial is null || initial.Track.UniqueKey != expectedTrack.UniqueKey ||
                        initial.PlaybackStatus != MediaTransportPlaybackStatus.Paused)
                    {
                        result = new(false, false, "Fresh preflight could not verify the same paused current track after at most one session refresh; no Previous was sent.");
                        _logger.Warn(result.Detail);
                    }
                    else
                    {
                        result = await new GuardedPreviousRestart(captured, _logger)
                            .TryRestartWithPreviousAsync(expectedTrack, precedingTrack, deadline.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new(false, false, "Previous-restart reached its bounded deadline or the source stopped; automatic resume is unsafe.");
                    _logger.Warn(result.Detail);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new(false, false, "Previous-restart failed with an exception; automatic resume is unsafe.");
                    _logger.Error($"{result.Detail} HRESULT=0x{ex.HResult:X8}.", ex);
                }
            }
        }
        finally
        {
            try
            {
                // In-flight read operations may be canceled safely. Remote navigation commands
                // cannot be undone; the coordinator holds mute on an unsafe/canceled result.
                if (captured is not null && !_disposed)
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try { finalSnapshot = await captured.ReadFinalAsync(cleanupDeadline.Token); }
                    catch (Exception ex) { _logger.Warn($"Previous-restart final metadata unavailable: {ex.Message}"); }
                }

                lock (_previousGuardSync)
                {
                    cleanupPublicationRevision = Interlocked.Increment(ref _propertiesRefreshRevision);
                    _previousGuardActive = false;
                    _previousGuardExpectedKey = null;
                    _previousGuardUnexpectedKey = null;
                    if (_deferredTrack is not null)
                        _logger.Info("Previous-restart: discarded transient callbacks; only a freshly read final item may be published.");
                    _deferredTrack = null;
                }

                var finalMatches = finalSnapshot is not null && finalSnapshot.Track.UniqueKey == expectedTrack.UniqueKey &&
                    finalSnapshot.NavigationRevision == captured?.CapturedNavigationRevision;
                if (result.SafeToResume && !finalMatches)
                    result = new(false, false, "Fresh final metadata did not match the original track; automatic resume is unsafe.");
                if (result.CanRecoverOnNewTrack &&
                    (captured is null || captured.NavigationDispatched || finalSnapshot is null ||
                     finalSnapshot.PlaybackStatus != MediaTransportPlaybackStatus.Paused ||
                     finalSnapshot.Position is null || finalSnapshot.Position < GuardedPreviousRestart.MinimumPosition))
                    result = new(false, false, "Previous-unavailable recovery lost the verified paused item or dispatch certainty; automatic resume is unsafe.");
                if (result.Restarted && finalSnapshot is { } final &&
                    (final.PlaybackStatus != MediaTransportPlaybackStatus.Paused || final.Position is null ||
                     final.Position < TimeSpan.Zero || final.Position > GuardedPreviousRestart.PositionTolerance))
                    result = new(false, finalMatches, "Track was rechecked, but paused 0:00 no longer held before releasing the guard.");

                if (finalSnapshot is not null && captured?.LastPlaybackInfo is { } playbackInfo && !_disposed)
                {
                    var artwork = IsSameTrack(GetSessionSnapshot(), finalSnapshot.Track)
                        ? GetArtworkSnapshot() : MediaArtworkSnapshot.CreateUnavailable();
                    lock (_previousGuardSync)
                    {
                        if (cleanupPublicationRevision == Volatile.Read(ref _propertiesRefreshRevision))
                            UpdateSessionState(BuildSessionSnapshot(captured.Session, finalSnapshot.Track, playbackInfo, artwork), artwork);
                    }
                    PublishSnapshot(finalSnapshot.Track, "previous-restart-final", cleanupPublicationRevision);
                }
                else
                {
                    _logger.Warn("Previous-restart: no fresh final item was published; waiting for the next real metadata callback.");
                }
            }
            finally
            {
                _previousRestartGate.Release();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (result.SafeToResume && cleanupPublicationRevision != Volatile.Read(ref _propertiesRefreshRevision))
        {
            _logger.Warn("Previous-restart: a newer properties callback started after the final read; automatic resume is unsafe.");
            result = new(false, false, "Media properties changed after the final identity check; automatic resume is unsafe.");
        }
        _logger.Info($"Previous-restart transport result: Restarted={result.Restarted}, SafeToResume={result.SafeToResume}, " +
            $"FailureKind={result.FailureKind}, NavigationDispatched={result.NavigationDispatched}, CanRecoverOnNewTrack={result.CanRecoverOnNewTrack}; {result.Detail}");
        return result;
    }

    private async void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        await RefreshAttachedSessionFromEventAsync();
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        await RefreshAttachedSessionFromEventAsync();
    }

    private async Task RefreshAttachedSessionFromEventAsync()
    {
        try { await RefreshAttachedSessionAsync(); }
        catch (Exception ex) { _logger.Warn($"Apple Music session refresh event failed: {ex.Message}"); }
    }

    private async Task RefreshAttachedSessionAsync(bool forceReattach = false, CancellationToken cancellationToken = default)
    {
        await _sessionRefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_manager is null || _disposed) return;

            var sessions = _manager.GetSessions().ToList();
            _logger.Info(
                $"GSMTC sessions visible: {sessions.Count}. " +
                $"{string.Join(", ", sessions.Select(session => session.SourceAppUserModelId))}");

            var candidate = SelectPreferredSession(sessions, _manager.GetCurrentSession(), _attachedSession);

            if (candidate is not null) CancelSessionLossDebounce();

            if (!forceReattach && NativeSessionIdentity.AreSame(candidate, _attachedSession)) return;

            if (candidate is null)
            {
                BeginSessionLossDebounce();
                return;
            }

            DetachSessionCore();
            _attachedSession = candidate;
            var epoch = Interlocked.Increment(ref _sessionEpoch);
            _logger.Info($"Attaching Apple Music session {candidate.SourceAppUserModelId}. native=0x{NativeSessionIdentity.GetIdentity(candidate):X}, epoch={epoch}, forced={forceReattach}, lastTrack={_lastTrackKey ?? "none"}.");
            // Every subscription captures its epoch. A queued old callback cannot acquire
            // the new epoch merely because the application id or native object is reused.
            _mediaPropertiesHandler = (sender, args) => OnMediaPropertiesChanged(sender, args, epoch);
            _playbackInfoHandler = (sender, args) => OnPlaybackInfoChanged(sender, args, epoch);
            candidate.MediaPropertiesChanged += _mediaPropertiesHandler;
            candidate.PlaybackInfoChanged += _playbackInfoHandler;
            // A hung media-properties request must not hold the attachment gate forever.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try { await RefreshSessionFromPropertiesAsync(candidate, "session-attached", publishTrackSnapshot: true, epoch, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { _logger.Warn("Initial Apple Music metadata timed out; retaining the attachment for the next event."); }
        }
        finally { _sessionRefreshGate.Release(); }
    }

    private async Task DetachSessionAsync()
    {
        await _sessionRefreshGate.WaitAsync();
        try { DetachSessionCore(); }
        finally { _sessionRefreshGate.Release(); }
    }

    private void DetachSessionCore()
    {
        // Capture every old subscription before clearing global state. This method runs
        // only under the refresh gate; detach can never unsubscribe a successor's object.
        var previous = _attachedSession;
        var propertiesHandler = _mediaPropertiesHandler;
        var playbackHandler = _playbackInfoHandler;
        _attachedSession = null;
        _mediaPropertiesHandler = null;
        _playbackInfoHandler = null;
        Interlocked.Increment(ref _sessionEpoch);
        Interlocked.Increment(ref _propertiesRefreshRevision);
        if (previous is not null)
        {
            _logger.Info($"Detaching Apple Music session {previous.SourceAppUserModelId}. native=0x{NativeSessionIdentity.GetIdentity(previous):X}, lastTrack={_lastTrackKey ?? "none"}.");
            if (propertiesHandler is not null) previous.MediaPropertiesChanged -= propertiesHandler;
            if (playbackHandler is not null) previous.PlaybackInfoChanged -= playbackHandler;
        }
        UpdateSessionState(MediaSessionSnapshot.CreateUnavailable(), MediaArtworkSnapshot.CreateUnavailable());
    }

    private bool IsAttachedSession(GlobalSystemMediaTransportControlsSession session, long epoch) =>
        !_disposed && epoch == Volatile.Read(ref _sessionEpoch) && NativeSessionIdentity.AreSame(_attachedSession, session);

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args, long epoch)
    {
        if (!IsAttachedSession(sender, epoch)) { _logger.Verbose("Discarding obsolete Apple Music properties event."); return; }
        _logger.Info($"Apple Music media-properties callback received from {sender.SourceAppUserModelId}.");
        await RefreshSessionFromPropertiesAsync(sender, "media-properties", publishTrackSnapshot: true, epoch);
    }

    private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args, long epoch)
    {
        if (!IsAttachedSession(sender, epoch)) { _logger.Verbose("Discarding obsolete Apple Music playback event."); return; }
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        try
        {
            status = sender.GetPlaybackInfo().PlaybackStatus;
        }
        catch
        {
            if (IsAttachedSession(sender, epoch)) UpdateSessionFromPlaybackInfo(sender);
            return;
        }

        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            _logger.Info($"Apple Music playback-playing callback received from {sender.SourceAppUserModelId}.");
            await RefreshSessionFromPropertiesAsync(sender, "playback-playing", publishTrackSnapshot: true, epoch);
            PublishPlaybackStateIfCurrent(sender, epoch);
            return;
        }

        if (IsAttachedSession(sender, epoch)) UpdateSessionFromPlaybackInfo(sender);
        PublishPlaybackStateIfCurrent(sender, epoch);
    }

    private void PublishPlaybackStateIfCurrent(GlobalSystemMediaTransportControlsSession session, long epoch)
    {
        if (!IsAttachedSession(session, epoch)) return;
        try
        {
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            if (!IsAttachedSession(session, epoch)) return;
            // This notification is independent of PublishSnapshot's same-track suppression.
            // A user Play on the same paused item must wake a deferred format change.
            PlaybackStateChanged?.Invoke(this, new MediaTransportState(
                MapPlaybackStatus(info.PlaybackStatus),
                info.Controls?.IsPauseEnabled == true,
                info.Controls?.IsPlayEnabled == true,
                timeline.Position,
                DateTimeOffset.UtcNow)
            {
                CanNext = info.Controls?.IsNextEnabled == true,
                CanPrevious = info.Controls?.IsPreviousEnabled == true,
            });
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Current Apple Music playback notification was unavailable: {ex.Message}");
        }
    }

    private async Task RefreshSessionFromPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session,
        string reason,
        bool publishTrackSnapshot,
        long epoch,
        CancellationToken cancellationToken = default)
    {
        if (!IsAttachedSession(session, epoch)) return;
        var propertiesRevision = Interlocked.Increment(ref _propertiesRefreshRevision);
        var callbackStopwatch = Stopwatch.StartNew();
        GlobalSystemMediaTransportControlsSessionMediaProperties properties;
        try
        {
            properties = await ReadMediaPropertiesAsync(session, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to read Apple Music media properties ({reason}): {ex.Message}");
            return;
        }

        if (propertiesRevision != Volatile.Read(ref _propertiesRefreshRevision) || !IsAttachedSession(session, epoch))
        {
            _logger.Verbose($"Discarding superseded Apple Music properties callback ({reason}).");
            return;
        }

        var snapshot = BuildTrackSnapshot(session, properties, reason);
        snapshot = NormalizeSnapshot(snapshot);

        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo;
        try
        {
            playbackInfo = session.GetPlaybackInfo();
        }
        catch
        {
            if (IsAttachedSession(session, epoch)) UpdateSessionFromPlaybackInfo(session);
            return;
        }

        var sessionSnapshot = BuildSessionSnapshot(session, snapshot, playbackInfo, MediaArtworkSnapshot.CreateUnavailable());
        if (!IsAttachedSession(session, epoch)) return;
        _ = ProcessResolvedSnapshotAsync(
            snapshot,
            sessionSnapshot,
            _ => ReadArtworkSnapshotAsync(properties, CancellationToken.None),
            publishTrackSnapshot,
            reason,
            callbackStopwatch.ElapsedMilliseconds,
            CancellationToken.None,
            cancellation => ReadArtworkFreshAsync(session, cancellation),
            propertiesRevision);
    }

    // GSMTC's thumbnail often lags a track change: the media-properties callback for the NEW
    // track can still stream the PREVIOUS track's image. Detected by byte-identical artwork
    // (same SHA revision) arriving for a different track key; one delayed re-read (against a
    // freshly fetched properties object — the original's thumbnail reference stays stale) gets
    // the real image. Same-album tracks legitimately share bytes, so the retry result is applied
    // either way; they just gain a short delay.
    internal static TimeSpan StaleArtworkRetryDelay { get; set; } = TimeSpan.FromMilliseconds(1200);

    internal async Task ProcessResolvedSnapshotAsync(
        TrackSnapshot snapshot,
        MediaSessionSnapshot sessionSnapshot,
        Func<CancellationToken, Task<MediaArtworkSnapshot>> artworkLoader,
        bool publishTrackSnapshot,
        string reason,
        long callbackLatencyMs,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<MediaArtworkSnapshot>>? artworkRetryLoader = null,
        long? propertiesRevision = null)
    {
        if (propertiesRevision is { } revision && revision != Volatile.Read(ref _propertiesRefreshRevision))
            return;
        if (publishTrackSnapshot)
        {
            _logger.Info(
                $"Publishing Apple Music track snapshot after {callbackLatencyMs} ms ({reason}).",
                new DiagnosticsLogger.LogCorrelation(TrackKey: snapshot.UniqueKey));
            HandleSnapshot(snapshot, isNormalized: true, propertiesRevision);
        }

        lock (_previousGuardSync)
        {
            if (_previousGuardActive || (propertiesRevision is { } currentRevision && currentRevision != Volatile.Read(ref _propertiesRefreshRevision)))
                return;
            UpdateSessionState(sessionSnapshot, MediaArtworkSnapshot.CreateUnavailable());
        }

        try
        {
            var artworkSnapshot = await artworkLoader(cancellationToken);
            if (artworkRetryLoader is not null && IsSuspectedStaleArtwork(snapshot, artworkSnapshot))
            {
                _logger.Verbose(
                    $"Artwork for {snapshot.UniqueKey} is byte-identical to the previous track's; re-reading after a delay.");
                await Task.Delay(StaleArtworkRetryDelay, cancellationToken);
                artworkSnapshot = await artworkRetryLoader(cancellationToken);
            }

            if (_disposed || (propertiesRevision is { } artworkRevision && artworkRevision != Volatile.Read(ref _propertiesRefreshRevision)))
                return;
            ApplyArtworkSnapshotIfCurrent(snapshot, sessionSnapshot, artworkSnapshot, reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to refresh Apple Music artwork ({reason}): {ex.Message}");
        }
    }

    private bool IsSuspectedStaleArtwork(TrackSnapshot snapshot, MediaArtworkSnapshot artworkSnapshot)
    {
        lock (_sessionStateSync)
        {
            return artworkSnapshot.HasArtwork &&
                   _lastAppliedRealArtwork is { } last &&
                   string.Equals(artworkSnapshot.Revision, last.Revision, StringComparison.Ordinal) &&
                   !string.Equals(snapshot.UniqueKey, last.TrackUniqueKey, StringComparison.Ordinal);
        }
    }

    private async Task<MediaArtworkSnapshot> ReadArtworkFreshAsync(
        GlobalSystemMediaTransportControlsSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = await ReadMediaPropertiesAsync(session, cancellationToken);
            return properties is null
                ? MediaArtworkSnapshot.CreateUnavailable()
                : await ReadArtworkSnapshotAsync(properties, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to re-read Apple Music artwork: {ex.Message}");
            return MediaArtworkSnapshot.CreateUnavailable();
        }
    }

    internal void HandleSnapshot(TrackSnapshot snapshot)
    {
        snapshot = NormalizeSnapshot(snapshot);
        HandleSnapshot(snapshot, isNormalized: true);
    }

    internal static bool IsPlaceholderTrack(TrackSnapshot snapshot)
    {
        return IsPlaceholderValue(snapshot.Title) || IsPlaceholderValue(snapshot.Artist);
    }

    private void HandleSnapshot(TrackSnapshot snapshot, bool isNormalized, long? propertiesRevision = null)
    {
        if (!isNormalized)
        {
            snapshot = NormalizeSnapshot(snapshot);
        }

        var snapshotGeneration = Interlocked.Increment(ref _snapshotGeneration);
        var correlation = CreateSnapshotCorrelation(snapshotGeneration, snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.Title) && string.IsNullOrWhiteSpace(snapshot.Artist))
        {
            _logger.Info("Ignoring empty Apple Music snapshot.", correlation);
            return;
        }

        if (IsPlaceholderTrack(snapshot))
        {
            if (_lastRealTrack is not null)
            {
                _logger.Info($"Suppressing placeholder Apple Music snapshot while preserving last real track: {snapshot.Title} ({snapshot.DetectionReason}).", correlation);
                return;
            }

            SchedulePlaceholderPublish(snapshot, snapshotGeneration);
            return;
        }

        CancelPlaceholderPublish();
        PublishSnapshot(snapshot, snapshot.DetectionReason, propertiesRevision);
    }

    private void UpdateSessionFromPlaybackInfo(GlobalSystemMediaTransportControlsSession session)
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        if (!IsAttachedSession(session, epoch)) return;
        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var currentSnapshot = GetSessionSnapshot();
            if (string.IsNullOrWhiteSpace(currentSnapshot.SourceAppUserModelId))
            {
                return;
            }

            var updatedSnapshot = currentSnapshot with
            {
                PlaybackStatus = MapPlaybackStatus(playbackInfo.PlaybackStatus),
                CanPause = playbackInfo.Controls?.IsPauseEnabled ?? false,
                CanPlay = playbackInfo.Controls?.IsPlayEnabled ?? false,
                CanGoNext = playbackInfo.Controls?.IsNextEnabled ?? false,
                CanGoPrevious = playbackInfo.Controls?.IsPreviousEnabled ?? false,
                CanShuffle = playbackInfo.Controls?.IsShuffleEnabled ?? false,
                CanRepeat = playbackInfo.Controls?.IsRepeatEnabled ?? false,
                IsShuffleActive = playbackInfo.IsShuffleActive,
                RepeatMode = MapRepeatMode(playbackInfo.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None),
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };

            if (IsAttachedSession(session, epoch)) UpdateSessionState(updatedSnapshot, GetArtworkSnapshot());
        }
        catch
        {
        }
    }

    private static TrackSnapshot BuildTrackSnapshot(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties properties,
        string reason) =>
        new(
            session.SourceAppUserModelId,
            null,
            properties.Title,
            properties.Artist,
            properties.AlbumTitle,
            reason,
            DateTimeOffset.UtcNow);

    private TrackSnapshot NormalizeSnapshot(TrackSnapshot snapshot)
    {
        var rawSnapshot = snapshot;
        snapshot = AppleMusicTrackMetadataNormalizer.NormalizeSnapshot(snapshot);
        if (rawSnapshot.Title != snapshot.Title ||
            rawSnapshot.Artist != snapshot.Artist ||
            rawSnapshot.Album != snapshot.Album)
        {
            _logger.Info(
                $"Normalized Apple Music metadata: raw=({rawSnapshot.Title} | {rawSnapshot.Artist} | {rawSnapshot.Album}), " +
                $"normalized=({snapshot.Title} | {snapshot.Artist} | {snapshot.Album}).");
        }

        if (ShouldLogIncompleteMetadata(rawSnapshot, snapshot))
        {
            _logger.Info(
                $"Observed irregular Apple Music metadata: raw=({rawSnapshot.Title} | {rawSnapshot.Artist} | {rawSnapshot.Album}), " +
                $"normalized=({snapshot.Title} | {snapshot.Artist} | {snapshot.Album}).",
                new DiagnosticsLogger.LogCorrelation(TrackKey: snapshot.UniqueKey));
        }

        return snapshot;
    }

    private static bool IsPlaceholderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().TrimEnd('.', '…', '。').TrimEnd();
        return normalized.Equals("Connecting", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("正在連線", StringComparison.Ordinal) ||
               normalized.Equals("正在连线", StringComparison.Ordinal) ||
               normalized.Equals("正在连接", StringComparison.Ordinal) ||
               normalized.Equals("正在連接", StringComparison.Ordinal);
    }

    private void SchedulePlaceholderPublish(TrackSnapshot snapshot, long generation)
    {
        CancelPlaceholderPublish();

        var cts = new CancellationTokenSource();
        _placeholderPublishCts = cts;
        _logger.Info($"Deferring placeholder Apple Music snapshot: {snapshot.Title} ({snapshot.DetectionReason}).", CreateSnapshotCorrelation(generation, snapshot));
        _ = PublishPlaceholderAfterDelayAsync(snapshot, generation, cts);
    }

    private async Task PublishPlaceholderAfterDelayAsync(TrackSnapshot snapshot, long generation, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_placeholderDebounce, cts.Token);
            if (generation != Volatile.Read(ref _snapshotGeneration))
            {
                _logger.Info($"Discarding stale placeholder Apple Music snapshot: {snapshot.Title} ({snapshot.DetectionReason}).", CreateSnapshotCorrelation(generation, snapshot));
                return;
            }

            PublishSnapshot(snapshot, snapshot.DetectionReason);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_placeholderPublishCts, cts))
            {
                _placeholderPublishCts = null;
            }

            cts.Dispose();
        }
    }

    private void BeginSessionLossDebounce()
    {
        if (_sessionLossCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _sessionLossCts = cts;
        _logger.Info("Apple Music session disappeared; waiting briefly before detaching.");
        _ = HandleSessionLossAsync(cts);
    }

    private async Task HandleSessionLossAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_sessionLossDebounce, cts.Token);
            var refresh = false;
            await _sessionRefreshGate.WaitAsync(cts.Token);
            try
            {
                if (_manager is null || _disposed) return;
                var candidate = _manager.GetSessions()
                    .FirstOrDefault(session => IsAppleMusicSessionId(session.SourceAppUserModelId));
                if (candidate is not null) refresh = true;
                else
                {
                    DetachSessionCore();
                    _logger.Warn("Apple Music session was not found after debounce.");
                }
            }
            finally { _sessionRefreshGate.Release(); }
            if (refresh) await RefreshAttachedSessionAsync(cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_sessionLossCts, cts))
            {
                _sessionLossCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelSessionLossDebounce()
    {
        _sessionLossCts?.Cancel();
    }

    private void CancelPlaceholderPublish()
    {
        _placeholderPublishCts?.Cancel();
    }

    private void PublishSnapshot(TrackSnapshot snapshot, string reason, long? propertiesRevision = null)
    {
        lock (_previousGuardSync)
        {
            if (propertiesRevision is { } revision && revision != Volatile.Read(ref _propertiesRefreshRevision))
                return;
            if (_previousGuardActive)
            {
                _deferredTrack = snapshot;
                if (snapshot.UniqueKey != _previousGuardExpectedKey)
                {
                    if (_previousGuardUnexpectedKey is not null && _previousGuardUnexpectedKey != snapshot.UniqueKey)
                        Interlocked.Increment(ref _navigationRevision);
                    _previousGuardUnexpectedKey = snapshot.UniqueKey;
                }
                _logger.Info($"Previous-restart: deferred track notification while verifying the guarded command ({reason}).");
                return;
            }
            if (!IsPlaceholderTrack(snapshot))
                _lastRealTrack = snapshot;
            if (snapshot.UniqueKey == _lastTrackKey)
            {
                _logger.Info($"Suppressing duplicate Apple Music snapshot publish ({reason}).", CreateSnapshotCorrelation(Volatile.Read(ref _snapshotGeneration), snapshot));
                return;
            }
            _lastTrackKey = snapshot.UniqueKey;
        }

        _logger.Info($"Track changed: {snapshot.Title} - {snapshot.Artist} ({reason}).", CreateSnapshotCorrelation(Volatile.Read(ref _snapshotGeneration), snapshot));
        TrackChanged?.Invoke(this, snapshot);
    }

    private static MediaSessionSnapshot BuildSessionSnapshot(
        GlobalSystemMediaTransportControlsSession session,
        TrackSnapshot snapshot,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo,
        MediaArtworkSnapshot artworkSnapshot) =>
        new(
            session.SourceAppUserModelId,
            snapshot.Title,
            snapshot.Artist,
            snapshot.Album,
            MapPlaybackStatus(playbackInfo.PlaybackStatus),
            playbackInfo.Controls?.IsPauseEnabled ?? false,
            playbackInfo.Controls?.IsPlayEnabled ?? false,
            playbackInfo.Controls?.IsNextEnabled ?? false,
            playbackInfo.Controls?.IsPreviousEnabled ?? false,
            playbackInfo.Controls?.IsShuffleEnabled ?? false,
            playbackInfo.Controls?.IsRepeatEnabled ?? false,
            playbackInfo.IsShuffleActive,
            MapRepeatMode(playbackInfo.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None),
            artworkSnapshot.Revision,
            DateTimeOffset.UtcNow);

    private static MediaTransportPlaybackStatus MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) =>
        status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaTransportPlaybackStatus.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaTransportPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaTransportPlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaTransportPlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaTransportPlaybackStatus.Playing,
            _ => MediaTransportPlaybackStatus.Unknown,
        };

    private static MediaTransportRepeatMode MapRepeatMode(MediaPlaybackAutoRepeatMode repeatMode) =>
        repeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaTransportRepeatMode.Off,
            MediaPlaybackAutoRepeatMode.Track => MediaTransportRepeatMode.One,
            MediaPlaybackAutoRepeatMode.List => MediaTransportRepeatMode.All,
            _ => MediaTransportRepeatMode.Unknown,
        };

    private async Task<MediaArtworkSnapshot> ReadArtworkSnapshotAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties properties,
        CancellationToken cancellationToken)
    {
        if (properties.Thumbnail is null)
        {
            return MediaArtworkSnapshot.CreateUnavailable();
        }

        try
        {
            using var randomAccessStream = await properties.Thumbnail.OpenReadAsync();
            if (randomAccessStream.Size == 0 || randomAccessStream.Size > int.MaxValue)
            {
                return MediaArtworkSnapshot.CreateUnavailable();
            }

            using var reader = new DataReader(randomAccessStream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)randomAccessStream.Size).AsTask(cancellationToken);
            var bytes = new byte[randomAccessStream.Size];
            reader.ReadBytes(bytes);
            if (bytes.Length == 0)
            {
                return MediaArtworkSnapshot.CreateUnavailable();
            }

            var revision = Convert.ToHexString(SHA256.HashData(bytes));
            return new MediaArtworkSnapshot(bytes, randomAccessStream.ContentType, revision, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to read Apple Music artwork: {ex.Message}");
            return MediaArtworkSnapshot.CreateUnavailable();
        }
    }

    private void UpdateSessionState(MediaSessionSnapshot sessionSnapshot, MediaArtworkSnapshot artworkSnapshot)
    {
        lock (_sessionStateSync)
        {
            _sessionSnapshot = sessionSnapshot;
            _artworkSnapshot = artworkSnapshot;
        }
    }

    private void ApplyArtworkSnapshotIfCurrent(
        TrackSnapshot snapshot,
        MediaSessionSnapshot sessionSnapshot,
        MediaArtworkSnapshot artworkSnapshot,
        string reason)
    {
        // Tag the artwork with the track it was read for, so consumers can refuse to render it
        // against a different track.
        artworkSnapshot = artworkSnapshot with { TrackUniqueKey = snapshot.UniqueKey };

        lock (_sessionStateSync)
        {
            if (!IsSameTrack(_sessionSnapshot, snapshot))
            {
                _logger.Info(
                    $"Skipping stale Apple Music artwork refresh ({reason}) for {snapshot.UniqueKey}.",
                    new DiagnosticsLogger.LogCorrelation(TrackKey: snapshot.UniqueKey));
                return;
            }

            _sessionSnapshot = sessionSnapshot with
            {
                ArtworkRevision = artworkSnapshot.Revision,
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
            _artworkSnapshot = artworkSnapshot;
            if (artworkSnapshot.HasArtwork)
            {
                _lastAppliedRealArtwork = artworkSnapshot;
            }
        }
    }

    private static bool IsSameTrack(MediaSessionSnapshot sessionSnapshot, TrackSnapshot snapshot)
    {
        return string.Equals(sessionSnapshot.SourceAppUserModelId, snapshot.SourceAppUserModelId, StringComparison.Ordinal) &&
               string.Equals(sessionSnapshot.Title, snapshot.Title, StringComparison.Ordinal) &&
               string.Equals(sessionSnapshot.Artist, snapshot.Artist, StringComparison.Ordinal) &&
               string.Equals(sessionSnapshot.Album, snapshot.Album, StringComparison.Ordinal);
    }

    private static bool ShouldLogIncompleteMetadata(TrackSnapshot rawSnapshot, TrackSnapshot normalizedSnapshot)
    {
        return string.IsNullOrWhiteSpace(rawSnapshot.Artist) ||
               string.IsNullOrWhiteSpace(rawSnapshot.Album) ||
               string.IsNullOrWhiteSpace(normalizedSnapshot.Artist) ||
               string.IsNullOrWhiteSpace(normalizedSnapshot.Album);
    }

    private static DiagnosticsLogger.LogCorrelation CreateSnapshotCorrelation(long snapshotGeneration, TrackSnapshot snapshot) =>
        new(TrackKey: snapshot.UniqueKey, SnapshotGeneration: snapshotGeneration);

    private sealed class CapturedPreviousSession : IPreviousRestartSession
    {
        private readonly AppleMusicTrackSource _owner;
        private readonly long _epoch;
        private readonly string _identity;
        private readonly TrackSnapshot _expectedTrack;
        private readonly TrackSnapshot? _precedingTrack;
        private PreviousRestartSnapshot? _beforePrevious;
        private string? _lastReadRefusal;

        public CapturedPreviousSession(
            AppleMusicTrackSource owner,
            GlobalSystemMediaTransportControlsSession session,
            long epoch,
            TrackSnapshot expectedTrack,
            TrackSnapshot? precedingTrack)
        {
            _owner = owner;
            Session = session;
            _epoch = epoch;
            _identity = $"{session.SourceAppUserModelId}#{epoch}:0x{NativeSessionIdentity.GetIdentity(session):X}";
            _expectedTrack = expectedTrack;
            _precedingTrack = precedingTrack;
            CapturedNavigationRevision = Volatile.Read(ref owner._navigationRevision);
        }

        public GlobalSystemMediaTransportControlsSession Session { get; }
        public long CapturedNavigationRevision { get; }
        public bool NavigationDispatched { get; private set; }
        public GlobalSystemMediaTransportControlsSessionPlaybackInfo? LastPlaybackInfo { get; private set; }

        public Task<PreviousRestartSnapshot?> ReadAsync(CancellationToken cancellationToken) =>
            ReadCoreAsync(ignoreNavigationRevision: false, cancellationToken);

        public Task<PreviousRestartSnapshot?> ReadFinalAsync(CancellationToken cancellationToken) =>
            ReadCoreAsync(ignoreNavigationRevision: true, cancellationToken);

        private async Task<PreviousRestartSnapshot?> ReadCoreAsync(bool ignoreNavigationRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRead(ignoreNavigationRevision, "before metadata")) return null;
            var properties = await ReadMediaPropertiesAsync(Session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanRead(ignoreNavigationRevision, "after metadata")) return null;
            var track = _owner.NormalizeSnapshot(BuildTrackSnapshot(Session, properties, "previous-restart-read"));
            if (string.IsNullOrWhiteSpace(track.Title) || IsPlaceholderTrack(track))
            {
                RefuseRead($"empty/placeholder metadata: title={track.Title}, artist={track.Artist}");
                return null;
            }
            var info = Session.GetPlaybackInfo();
            var timeline = Session.GetTimelineProperties();
            var controls = info.Controls;
            LastPlaybackInfo = info;
            return new(
                _identity,
                track,
                MapPlaybackStatus(info.PlaybackStatus),
                timeline.Position,
                controls?.IsPreviousEnabled == true,
                controls?.IsNextEnabled == true,
                controls?.IsPauseEnabled == true,
                info.IsShuffleActive,
                MapRepeatMode(info.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None),
                Volatile.Read(ref _owner._navigationRevision));
        }

        public async Task<bool> PreviousAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fresh = await ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (fresh is null || fresh.Track.UniqueKey != _expectedTrack.UniqueKey ||
                fresh.PlaybackStatus != MediaTransportPlaybackStatus.Paused ||
                fresh.Position is null || fresh.Position < GuardedPreviousRestart.MinimumPosition || !fresh.CanPrevious)
            {
                _owner._logger.Warn("Previous-restart dispatch blocked: fresh captured-session metadata, pause, position or Previous capability no longer matched.");
                return false;
            }
            _beforePrevious = fresh;
            // This narrows the read/dispatch gap; GSMTC still cannot atomically bind a navigation
            // command to a media-item identity, so post-command verification remains essential.
            if (!IsCapturedSession() || NavigationChanged())
                return false;
            NavigationDispatched = true; // Set before awaiting: a timed-out remote command may still execute.
            var accepted = await BoundedMediaCommand.InvokeAsync(ct => Session.TrySkipPreviousAsync().AsTask(ct), "guarded Previous", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _owner._logger.Info($"Previous-restart actual Previous API returned {accepted} on {_identity}.");
            return accepted;
        }

        public async Task<bool> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fresh = await ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_precedingTrack is null || _beforePrevious is null || fresh is null ||
                _precedingTrack.UniqueKey == _expectedTrack.UniqueKey || fresh.Track.UniqueKey != _precedingTrack.UniqueKey ||
                fresh.PlaybackStatus != MediaTransportPlaybackStatus.Paused || !fresh.CanNext ||
                _beforePrevious.IsShuffleActive != false || fresh.IsShuffleActive != false ||
                _beforePrevious.RepeatMode is not (MediaTransportRepeatMode.Off or MediaTransportRepeatMode.All) ||
                fresh.RepeatMode is not (MediaTransportRepeatMode.Off or MediaTransportRepeatMode.All))
            {
                _owner._logger.Warn("Previous-restart correction blocked: fresh captured-session predecessor, pause or queue mode no longer matched.");
                return false;
            }
            if (!IsCapturedSession() || NavigationChanged())
                return false;
            NavigationDispatched = true;
            var accepted = await BoundedMediaCommand.InvokeAsync(ct => Session.TrySkipNextAsync().AsTask(ct), "guarded Next", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _owner._logger.Info($"Previous-restart actual correcting Next API returned {accepted} on {_identity}.");
            return accepted;
        }

        public async Task<bool> PauseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCapturedSession() || NavigationChanged() || Session.GetPlaybackInfo().Controls?.IsPauseEnabled != true)
                return false;
            var accepted = await BoundedMediaCommand.InvokeAsync(ct => Session.TryPauseAsync().AsTask(ct), "guarded Pause", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _owner._logger.Info($"Previous-restart captured-session Pause API returned {accepted} on {_identity}.");
            return accepted;
        }

        public async Task<bool> PlayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fresh = await ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (fresh is null || fresh.Track.UniqueKey != _expectedTrack.UniqueKey ||
                fresh.PlaybackStatus != MediaTransportPlaybackStatus.Paused || !IsCapturedSession() || NavigationChanged() ||
                Session.GetPlaybackInfo().Controls?.IsPlayEnabled != true)
            {
                _owner._logger.Warn("Previous-restart activation Play blocked: current captured-session item, pause or Play capability changed.");
                return false;
            }
            var accepted = await BoundedMediaCommand.InvokeAsync(ct => Session.TryPlayAsync().AsTask(ct), "guarded activation Play", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _owner._logger.Info($"Previous-restart captured-session activation Play API returned {accepted} on {_identity}; endpoint must remain muted.");
            return accepted;
        }

        private bool NavigationChanged() => Volatile.Read(ref _owner._navigationRevision) != CapturedNavigationRevision;

        private bool CanRead(bool ignoreNavigationRevision, string stage)
        {
            var reason = _owner._disposed ? "source disposed" :
                Volatile.Read(ref _owner._sessionEpoch) != _epoch ? "attached epoch changed" :
                !NativeSessionIdentity.AreSame(_owner._attachedSession, Session) ? "attached native identity changed" :
                !NativeSessionIdentity.AreSame(_owner.GetPreferredCommandSession(), Session) ? "preferred native identity changed or unavailable" :
                !ignoreNavigationRevision && NavigationChanged() ? "navigation revision changed" : null;
            if (reason is null) return true;
            RefuseRead($"{stage}: {reason}");
            return false;
        }

        private void RefuseRead(string reason)
        {
            if (_lastReadRefusal == reason) return;
            _lastReadRefusal = reason;
            _owner._logger.Warn($"Previous-restart fresh read refused ({reason}); captured={_identity}, " +
                $"epoch={_epoch}/{Volatile.Read(ref _owner._sessionEpoch)}, navigation={CapturedNavigationRevision}/{Volatile.Read(ref _owner._navigationRevision)}, " +
                $"attached=0x{(_owner._attachedSession is { } attached ? NativeSessionIdentity.GetIdentity(attached) : 0):X}, " +
                $"preferred=0x{(_owner.GetPreferredCommandSession() is { } preferred ? NativeSessionIdentity.GetIdentity(preferred) : 0):X}.");
        }

        private bool IsCapturedSession() => CanRead(ignoreNavigationRevision: true, "command identity");
    }

    internal GlobalSystemMediaTransportControlsSession? GetPreferredCommandSession()
    {
        var manager = _manager;
        if (manager is null)
        {
            return _attachedSession;
        }

        try
        {
            return SelectPreferredSession(manager.GetSessions(), manager.GetCurrentSession(), _attachedSession);
        }
        catch
        {
            return null; // Failed census cannot prove that the cached idle session is still present.
        }
    }

    internal static GlobalSystemMediaTransportControlsSession? SelectPreferredSession(
        IEnumerable<GlobalSystemMediaTransportControlsSession> sessions,
        GlobalSystemMediaTransportControlsSession? currentSession,
        GlobalSystemMediaTransportControlsSession? attachedSession)
    {
        var appleMusicSessions = sessions
            .Where(session => IsAppleMusicSessionId(session.SourceAppUserModelId))
            .ToList();
        if (appleMusicSessions.Count == 0)
        {
            return null;
        }

        foreach (var session in appleMusicSessions)
        {
            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return NativeSessionIdentity.AreSame(session, attachedSession) ? attachedSession : session;
                }
            }
            catch
            {
            }
        }

        if (currentSession is not null &&
            IsAppleMusicSessionId(currentSession.SourceAppUserModelId))
        {
            var current = appleMusicSessions.FirstOrDefault(session => NativeSessionIdentity.AreSame(session, currentSession));
            if (current is not null)
                return NativeSessionIdentity.AreSame(current, attachedSession) ? attachedSession : current;
        }

        if (attachedSession is not null)
        {
            var attached = appleMusicSessions.FirstOrDefault(session => NativeSessionIdentity.AreSame(session, attachedSession));
            if (attached is not null) return attachedSession;
        }

        return appleMusicSessions[0];
    }
}
