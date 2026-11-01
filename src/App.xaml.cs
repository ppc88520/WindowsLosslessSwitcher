using System.ComponentModel;
using System.Windows;
using Application = System.Windows.Application;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using WindowsLosslessSwitcher.Views;

namespace WindowsLosslessSwitcher;

public partial class App : Application
{
    private readonly SettingsService _settingsService = new();
    private readonly StartupRegistrationService _startupRegistrationService = new();
    private readonly StartupInstanceGuard _instanceGuard = new();
    private readonly DiagnosticsLogger _logger = new();
    private readonly IAppUpdater _appUpdater;
    private readonly AppleMusicPaths _paths = new();
    private readonly BinaryPlistReader _plistReader = new();
    private readonly MainWindowViewModel _viewModel = new();

    private TrayIconHost? _trayIconHost;
    private SwitchToastService? _switchToastService;
    private HeroWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private TrayFlyoutWindow? _trayFlyout;
    private SwitchingCoordinator? _coordinator;
    private FormatCacheStore? _formatCacheStore;
    private bool _clearFormatCacheInFlight;
    private bool _manualRestartInProgress;
    private readonly RecoveryNotificationGate _recoveryNotifications = new();
    private AppSettings? _settings;
    private SwitchingStatus? _latestStatus;
    private AppleMusicTrackSource? _trackSource;
    private AppleMusicAudioMonitor? _audioMonitor;
    private string? _lastArtworkRevision;
    private System.Windows.Threading.DispatcherTimer? _artworkRecheckTimer;
    private System.Windows.Threading.DispatcherTimer? _playbackRefreshTimer;
    private System.Windows.Threading.DispatcherTimer? _audioOutputRefreshTimer;
    private IAudioOutputGuard? _audioOutputGuard;
    private int _audioOutputRefreshInFlight;
    private bool _exiting;
    private BackgroundUiRefresh<DeviceUiSnapshot>? _deviceUiRefresh;
    private readonly CancellationTokenSource _dacLockLifetime = new();
    private Task _dacLockWorker = Task.CompletedTask;
    private bool _canRestoreOriginalFormat;
    private AudioOutputGuardState? _lastUiSafetyState;
    private bool _exitInProgress;
    private Task _audioOutputSafetyWorker = Task.CompletedTask;
    private Task _startupBackendWorker = Task.CompletedTask;
    private Task _restoreWorker = Task.CompletedTask;
    private int _artworkRechecksRemaining;
    private string? _storefront;

    public App()
    {
        _appUpdater = new IsolatedAppUpdater();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var launchMinimized = e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        if (!_instanceGuard.TryAcquire())
        {
            _logger.Warn("Startup ownership: duplicate instance rejected.");
            if (!launchMinimized)
            {
                LocalizedDialog.Show(
                    "Windows Lossless Switcher is already running.",
                    "Windows Lossless Switcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _logger.Info("Startup ownership: primary instance.");
        _logger.Info($"OS version: {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}).");

        _settings = _settingsService.Load();
        TestBuildProfile.ApplyFirstRunDefaults(_settings, System.IO.File.Exists(_settingsService.GetSettingsPath()));
        TestBuildProfile.EnforceIsolation(_settings);
        _viewModel.UiLanguage = _settings.UiLanguage;
        _logger.VerboseEnabled = _settings.EnableVerboseDiagnostics;
        _viewModel.SelectedMode = _settings.DeviceSelectionMode;
        _viewModel.SelectedDeviceId = _settings.PinnedDeviceId;
        _viewModel.LaunchAtLogin = _settings.LaunchAtLogin;
        _viewModel.SwitchBitDepth = _settings.SwitchBitDepth;
        _viewModel.RestartTrackAfterSampleRateChange = _settings.RestartTrackAfterSampleRateChange;
        _viewModel.PreferClosestSampleRateMultiple = _settings.PreferClosestSampleRateMultiple;
        _viewModel.DefaultBitDepth = _settings.DefaultBitDepth;
        _viewModel.EnableSwitchToasts = _settings.EnableSwitchToasts;
        _viewModel.IncludeTrackMetadataInSwitchToasts = _settings.IncludeTrackMetadataInSwitchToasts;
        _viewModel.FormatCacheRefreshDays = _settings.FormatCacheRefreshDays;
        _viewModel.AppleMusicStorefront = _settings.AppleMusicStorefront;
        _viewModel.EnableVerboseDiagnostics = _settings.EnableVerboseDiagnostics;
        _viewModel.RestartAppleMusicOnPlaybackFailure = _settings.RestartAppleMusicOnPlaybackFailure;
        _viewModel.SeedAllowedFormats(_settings.AllowedSampleRates, _settings.AllowedBitDepths);
        UpdateOriginalFormatRestoreState();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RefreshRequested += OnRefreshDevicesRequested;
        _viewModel.ExportDiagnosticsRequested += OnExportDiagnosticsRequested;
        _viewModel.CheckForUpdatesRequested += OnCheckForUpdatesRequested;
        _viewModel.RunUpdatePrimaryActionRequested += OnRunUpdatePrimaryActionRequested;
        _viewModel.OpenReleasesPageRequested += OnOpenReleasesPageRequested;
        _viewModel.RestoreOriginalFormatRequested += OnRestoreOriginalFormatRequested;
        _viewModel.ClearFormatCacheRequested += OnClearFormatCacheRequested;
        _viewModel.OpenSettingsWindowRequested += ShowSettingsWindow;
        _viewModel.RestartToolRequested += OnRestartToolRequested;
        _viewModel.RestartPlayerAndToolRequested += OnRestartPlayerAndToolRequested;
        _viewModel.DacLockToggleRequested += OnDacLockToggleRequested;
        _viewModel.UpdateDacLockState(_settings.DacLockPaused, false);

        _trayIconHost = new TrayIconHost();
        _trayIconHost.OpenRequested += ShowMainWindow;
        _trayIconHost.FlyoutRequested += ShowTrayFlyout;
        _trayIconHost.UpdateStatus("Resolver: Starting");
        _switchToastService = new SwitchToastService();
        _appUpdater.StatusChanged += OnAppUpdaterStatusChanged;
        _viewModel.UpdateAppVersion(_appUpdater.CurrentStatus);

        // Order matters in the resolver chain:
        // 1. FormatCacheResolver skips calling AppleMusicCatalogResolver on a JSON cache hit.
        // 2. AppleMusicCatalogResolver is the authoritative test when cache misses — a confident
        //    catalog match yields the exact Apple Music format.
        // 3. When the catalog does not match (local files, or tracks it can't identify),
        //    LocalDeviceMaxResolver applies the actual format read from the track's PlayCache file,
        //    or the device's highest supported format when the file can't be read.
        // 4. TierFallbackResolver is the terminal safety net when there is no usable device.
        var nativeAudioController = await Task.Run(() => new CoreAudioEndpointController());
        if (_exiting) return;
        var audioEndpointController = await Task.Run(() =>
            new DacOnlyAudioEndpointController(nativeAudioController, nativeAudioController));
        if (_exiting) return;
        _audioOutputGuard = audioEndpointController;
        audioEndpointController.SetRoutingPaused(_settings.DacLockPaused);
        var formatCacheStore = new FormatCacheStore(_logger);
        _formatCacheStore = formatCacheStore;
        var catalogResolver = new AppleMusicCatalogResolver(_logger, formatCacheStore, _settings.AppleMusicStorefront);
        // Deliberately store-less: the verification path applies results via a compare-and-swap in
        // FormatCacheStore, which a store-backed resolver would defeat by rewriting the entry during
        // the lookup. FormatCacheVerificationService rejects a store-backed resolver at construction.
        // Same storefront as the live path, so verification searches the catalog the entry came from.
        var catalogResolverForCacheVerification = new AppleMusicCatalogResolver(
            _logger,
            formatCacheStore: null,
            _settings.AppleMusicStorefront);
        var resolverChain = new ResolverChain(
            [
                // The cache reader must use the writer's storefront: keys are storefront-scoped,
                // and a mismatch silently misses on every lookup.
                new FormatCacheResolver(formatCacheStore, _logger, catalogResolver.Storefront),
                catalogResolver,
                new LocalDeviceMaxResolver(
                    audioEndpointController,
                    new PlayCacheTrackFormatReader(_paths, _logger),
                    _logger),
                new TierFallbackResolver(_paths, _plistReader, _logger),
            ]);

        var appleMusicTrackSource = new AppleMusicTrackSource(_logger)
        {
            AudioOutputReady = () => audioEndpointController.IsReady,
        };
        _trackSource = appleMusicTrackSource;
        _audioMonitor = new AppleMusicAudioMonitor(_logger, appleMusicTrackSource, () => audioEndpointController.IsReady);
        _storefront = catalogResolver.Storefront;
        _viewModel.SeedStorefrontInfo(catalogResolver.Storefront);
        var processController = new AppleMusicProcessController(_logger, () => audioEndpointController.IsReady);
        _coordinator = new SwitchingCoordinator(
            appleMusicTrackSource,
            appleMusicTrackSource,
            resolverChain,
            audioEndpointController,
            _logger,
            processController,
            formatCacheStore,
            catalogResolverForCacheVerification);
        _coordinator.StatusChanged += OnCoordinatorStatusChanged;
        _coordinator.FormatCacheUpdated += OnFormatCacheUpdated;
        _coordinator.InitializeOutputLockPaused(_settings.DacLockPaused);

        _mainWindow = new HeroWindow(_viewModel, _audioMonitor);
        _mainWindow.Title = TestBuildProfile.DisplayName;
        _mainWindow.WindowHidden += () => _trayIconHost?.UpdateStatus(_viewModel.ResolverStatusText);
        MainWindow = _mainWindow;
        _deviceUiRefresh = new BackgroundUiRefresh<DeviceUiSnapshot>(QueryDeviceUiSnapshot,
            ApplyDeviceUiSnapshot, new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher),
            ex => _logger.Warn("Device display refresh failed: " + ex.Message));
        // The visible shell stays responsive while native device discovery/probing runs.
        if (!launchMinimized) ShowMainWindow();

        // Seed settings before the track source starts so target-device UI and original-format capture
        // use the user's configured selection immediately.
        _coordinator.UpdateSettings(_settings);
        var initialSafety = Task.Run(_coordinator.RefreshAudioOutputSafety);
        _audioOutputSafetyWorker = initialSafety;
        var initialState = await initialSafety;
        if (_exiting) return;
        ApplyAudioOutputSafety(initialState, refreshProjection: false);
        await _deviceUiRefresh.RequestAsync();
        if (_exiting) return;
        UpdateFormatCacheStatus();
        await _appUpdater.InitializeAsync(CancellationToken.None);
        if (_exiting) return;
        if (!_settings.DacLockPaused && e.Args.Contains(ManualRestartHandoff.StartupArgument, StringComparer.Ordinal))
        {
            // This temporary source has no coordinator subscribers: it cannot switch or Play.
            var paused = false;
            try
            {
                var pauseCheck = Task.Run(ConfirmRestartStartupPauseAsync);
                _startupBackendWorker = pauseCheck;
                paused = await pauseCheck.WaitAsync(TimeSpan.FromSeconds(14));
            }
            catch (Exception ex) { _logger.Warn("Restart startup pause was not confirmed: " + ex.Message); }
            if (_exiting) return;
            if (!paused)
            {
                // Attach only for a later explicit retry; automatic switching stays stopped.
                _startupBackendWorker = Task.Run(() => _trackSource!.StartAsync(CancellationToken.None));
                await _startupBackendWorker;
                if (_exiting) return;
                _viewModel.SetLifecycleNotice("Automatic switching is paused after restart", "Apple Music could not be confirmed paused. Pause it manually, then choose Restart Tool. No format, navigation, or Play command will be issued.");
                ShowMainWindow();
                return;
            }
            _coordinator.WaitForUserPlaybackAfterManualRestart();
        }
        _startupBackendWorker = Task.Run(() => _coordinator.StartAsync(_settings, CancellationToken.None));
        await _startupBackendWorker;
        if (_exiting) return;
        _viewModel.Playback.Configure(_coordinator, processController,
            new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        _playbackRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750),
        };
        _playbackRefreshTimer.Tick += (_, _) => _viewModel.Playback.Refresh();
        _playbackRefreshTimer.Start();
        _audioOutputRefreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _audioOutputRefreshTimer.Tick += async (_, _) => await RefreshAudioOutputSafetyAsync();
        _audioOutputRefreshTimer.Start();
        if (e.Args.Contains("--manual-player-restart-failed", StringComparer.Ordinal))
            _viewModel.SetLifecycleNotice("Tool restarted; player restart not confirmed", "Check Apple Music, select a track, and press Play manually. The restart will not be retried automatically.");
        ApplyStartupRegistration();
        _ = CheckForUpdatesAsync(userInitiated: false);

        if (!launchMinimized)
        {
            ShowMainWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _deviceUiRefresh?.Dispose();
        _audioOutputRefreshTimer?.Stop();
        _playbackRefreshTimer?.Stop();
        _viewModel.Playback.Dispose();
        _viewModel.DetachLocalization();
        _viewModel.DacLockToggleRequested -= OnDacLockToggleRequested;
        _dacLockLifetime.Dispose();
        if (_coordinator is not null)
        {
            _coordinator.StatusChanged -= OnCoordinatorStatusChanged;
            _coordinator.FormatCacheUpdated -= OnFormatCacheUpdated;
            // Normal exits complete the native cleanup barrier before calling Shutdown.
            // Startup rejection has no coordinator/owned mute to clean up.
        }

        _trayIconHost?.Dispose();
        _switchToastService?.Dispose();
        _artworkRecheckTimer?.Stop();
        _appUpdater.StatusChanged -= OnAppUpdaterStatusChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ExportDiagnosticsRequested -= OnExportDiagnosticsRequested;
        _viewModel.CheckForUpdatesRequested -= OnCheckForUpdatesRequested;
        _viewModel.RunUpdatePrimaryActionRequested -= OnRunUpdatePrimaryActionRequested;
        _viewModel.OpenReleasesPageRequested -= OnOpenReleasesPageRequested;
        _viewModel.RestoreOriginalFormatRequested -= OnRestoreOriginalFormatRequested;
        _viewModel.ClearFormatCacheRequested -= OnClearFormatCacheRequested;
        _viewModel.OpenSettingsWindowRequested -= ShowSettingsWindow;
        _viewModel.RestartToolRequested -= OnRestartToolRequested;
        _viewModel.RestartPlayerAndToolRequested -= OnRestartPlayerAndToolRequested;
        _instanceGuard.Dispose();
        base.OnExit(e);
    }

    private void OnCoordinatorStatusChanged(object? sender, SwitchingStatus status)
    {
        if (_exiting || _manualRestartInProgress) return;
        _latestStatus = status;
        // BeginInvoke (not Invoke): a synchronous dispatch blocks the coordinator's switching
        // pipeline for hundreds of milliseconds per status update, delaying the track-change
        // mute/pause and letting the new track play audibly at the old format.
        Dispatcher.BeginInvoke(() =>
        {
            if (_exiting || _manualRestartInProgress) return;
            _viewModel.UpdateStatus(status);
            _viewModel.Playback.Refresh();
            if (_recoveryNotifications.ShouldNotify(status))
            {
                var advice = RecoveryAdvice.From(status);
                _trayIconHost?.ShowRecoveryNotification(LocalizationService.Current[advice.Title], LocalizationService.Current[advice.Message]);
            }
            _trayIconHost?.UpdateStatus(_viewModel.PhaseText);
            RefreshDevices();
            UpdateArtworkIfChanged();
            ScheduleArtworkRechecks();

            if (status.WasFormatChanged && status.AppliedFormat is not null)
            {
                // A Tier-confidence result means the real track rate could not be determined (no catalog
                // match, no local cache) and we fell back to a conservative 24/44.1. Tell the user, so
                // the rate isn't mistaken for the track's true resolution. Gating on WasFormatChanged
                // naturally dedupes: once the device is at the fallback rate, later undetermined tracks
                // don't change it and won't re-notify.
                var isUndetermined = status.RequestedFormat?.Confidence == ResolutionConfidence.Tier;
                if (isUndetermined)
                {
                    _trayIconHost?.UpdateStatus(
                        LocalizationService.Current.Format("Rate undetermined — using {0}", AudioFormatTextFormatter.Format(status.AppliedFormat)));
                }

                if (_settings?.EnableSwitchToasts == true)
                {
                    if (isUndetermined)
                    {
                        _switchToastService?.ShowRateUndetermined(
                            status.ActiveDeviceName,
                            status.AppliedFormat,
                            ArtworkForTrack(status.Track?.UniqueKey),
                            status.Track);
                    }
                    else
                    {
                        // UpdateStatus ran above, so PreviousAppliedFormat reflects this switch.
                        _switchToastService?.ShowSwitchedFormat(
                            status.ActiveDeviceName,
                            _viewModel.PreviousAppliedFormat,
                            status.AppliedFormat,
                            status.Track,
                            ArtworkForTrack(status.Track?.UniqueKey),
                            _settings.IncludeTrackMetadataInSwitchToasts);
                    }
                }
            }
        });
    }

    private void OnFormatCacheUpdated(object? sender, FormatCacheUpdateEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateFormatCacheStatus();
            if (_settings?.EnableSwitchToasts != true ||
                _formatCacheStore?.ClearGeneration != e.CacheGeneration)
            {
                return;
            }

            var previousFormat = new AudioFormatCandidate(
                e.PreviousEntry.SampleRateHz,
                e.PreviousEntry.BitDepth,
                2);
            var updatedFormat = new AudioFormatCandidate(
                e.UpdatedFormat.SampleRateHz,
                e.UpdatedFormat.BitDepth,
                2);
            // Only pass artwork when it belongs to the track this cache update is about.
            var artwork = ArtworkForTrack(e.Track?.UniqueKey);
            _switchToastService?.ShowFormatCacheUpdated(
                _latestStatus?.ActiveDeviceName,
                previousFormat,
                updatedFormat,
                e.Track,
                artwork,
                _settings.IncludeTrackMetadataInSwitchToasts);
        });
    }

    private void OnAppUpdaterStatusChanged(object? sender, UpdateStatusSnapshot status)
    {
        Dispatcher.Invoke(() => _viewModel.UpdateAppVersion(status));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        // A display-language change does not reconfigure audio or send media commands.
        if (e.PropertyName == nameof(MainWindowViewModel.UiLanguage))
        {
            _settings.UiLanguage = _viewModel.UiLanguage;
            _settingsService.Save(_settings);
            _trayIconHost?.UpdateStatus(_viewModel.PhaseText);
            return;
        }

        if (e.PropertyName is not nameof(MainWindowViewModel.SelectedMode) &&
            e.PropertyName is not nameof(MainWindowViewModel.SelectedDeviceId) &&
            e.PropertyName is not nameof(MainWindowViewModel.LaunchAtLogin) &&
            e.PropertyName is not nameof(MainWindowViewModel.SwitchBitDepth) &&
            e.PropertyName is not nameof(MainWindowViewModel.RestartTrackAfterSampleRateChange) &&
            e.PropertyName is not nameof(MainWindowViewModel.PreferClosestSampleRateMultiple) &&
            e.PropertyName is not nameof(MainWindowViewModel.DefaultBitDepth) &&
            e.PropertyName is not nameof(MainWindowViewModel.EnableSwitchToasts) &&
            e.PropertyName is not nameof(MainWindowViewModel.IncludeTrackMetadataInSwitchToasts) &&
            e.PropertyName is not nameof(MainWindowViewModel.FormatCacheRefreshDays) &&
            e.PropertyName is not nameof(MainWindowViewModel.AppleMusicStorefront) &&
            e.PropertyName is not nameof(MainWindowViewModel.EnableVerboseDiagnostics) &&
            e.PropertyName is not nameof(MainWindowViewModel.RestartAppleMusicOnPlaybackFailure) &&
            e.PropertyName is not nameof(MainWindowViewModel.AllowedSampleRates) &&
            e.PropertyName is not nameof(MainWindowViewModel.AllowedBitDepths))
        {
            return;
        }

        _settings.DeviceSelectionMode = _viewModel.SelectedMode;
        _settings.PinnedDeviceId = _viewModel.SelectedDeviceId;
        _settings.LaunchAtLogin = _viewModel.LaunchAtLogin;
        _settings.SwitchBitDepth = _viewModel.SwitchBitDepth;
        _settings.RestartTrackAfterSampleRateChange = _viewModel.RestartTrackAfterSampleRateChange;
        _settings.PreferClosestSampleRateMultiple = _viewModel.PreferClosestSampleRateMultiple;
        _settings.DefaultBitDepth = _viewModel.DefaultBitDepth;
        _settings.EnableSwitchToasts = _viewModel.EnableSwitchToasts;
        _settings.IncludeTrackMetadataInSwitchToasts = _viewModel.IncludeTrackMetadataInSwitchToasts;
        _settings.FormatCacheRefreshDays = _viewModel.FormatCacheRefreshDays;
        _settings.AppleMusicStorefront = _viewModel.AppleMusicStorefront;
        _settings.EnableVerboseDiagnostics = _viewModel.EnableVerboseDiagnostics;
        _settings.RestartAppleMusicOnPlaybackFailure = _viewModel.RestartAppleMusicOnPlaybackFailure;
        _settings.AllowedSampleRates = _viewModel.AllowedSampleRates.ToList();
        _settings.AllowedBitDepths = _viewModel.AllowedBitDepths.ToList();
        TestBuildProfile.EnforceIsolation(_settings);
        _settingsService.Save(_settings);
        _logger.VerboseEnabled = _settings.EnableVerboseDiagnostics;
        _coordinator?.UpdateSettings(_settings);
        if (e.PropertyName == nameof(MainWindowViewModel.EnableSwitchToasts) && !_settings.EnableSwitchToasts)
        {
            _switchToastService?.DiscardPendingFormatCacheUpdates();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.SelectedMode) or nameof(MainWindowViewModel.SelectedDeviceId))
        {
            UpdateActiveTargetCapabilities();
        }

        ApplyStartupRegistration();
        RefreshCurrentFormatDisplays();
    }

    private void OnCheckForUpdatesRequested() => _ = CheckForUpdatesAsync(userInitiated: true);

    private void OnRunUpdatePrimaryActionRequested() => _ = RunUpdatePrimaryActionAsync();

    private void OnOpenReleasesPageRequested() => _appUpdater.OpenReleasesPage();

    private void OnRestoreOriginalFormatRequested() => _ = RestoreOriginalFormatAsync();

    private async void OnClearFormatCacheRequested()
    {
        // Clear() serializes and fsyncs the cache file under the store lock and can block behind an
        // in-flight background Store, so it must not run on the UI thread.
        if (_clearFormatCacheInFlight)
        {
            return;
        }

        _clearFormatCacheInFlight = true;
        try
        {
            var store = _formatCacheStore;
            var cleared = store is not null && await Task.Run(store.Clear);
            if (cleared)
            {
                _switchToastService?.DiscardPendingFormatCacheUpdates();
                _viewModel.FormatCacheStatusText = $"Cache cleared just now · 0 lookups · storefront: {_storefront ?? "auto"}";
                _viewModel.AddActivity("Catalog cache cleared");
                return;
            }

            _viewModel.FormatCacheStatusText = "The cache could not be cleared. See diagnostics for details.";
        }
        finally
        {
            _clearFormatCacheInFlight = false;
        }
    }

    private void RefreshDevices()
    {
        if (!_exiting && _deviceUiRefresh is not null) _ = _deviceUiRefresh.RequestAsync();
    }

    private async void OnRefreshDevicesRequested()
    {
        if (_audioOutputGuard is { } guard) await Task.Run(guard.ResetRoutingAttempt);
        await RefreshAudioOutputSafetyAsync();
        if (!_exiting && _deviceUiRefresh is not null) await _deviceUiRefresh.RequestAsync(forceRefresh: true);
    }

    private async void OnDacLockToggleRequested(bool paused)
    {
        if (_exiting || _viewModel.IsRestartBusy || _coordinator is not { } coordinator ||
            coordinator.IsDacLockChangeInProgress) return;
        _deviceUiRefresh?.Invalidate();
        try
        {
            // The method's synchronous prefix closes the routing gate immediately.
            var worker = coordinator.SetOutputLockPausedAsync(paused, _dacLockLifetime.Token);
            _dacLockWorker = worker;
            PersistDacLockPreference(coordinator.IsDacLockPaused);
            UpdateDacLockProjection();
            _audioMonitor?.RefreshOutputSafety();
            var result = await worker;
            if (_exiting) return;
            PersistDacLockPreference(result.IsPaused);
            UpdateDacLockProjection();
            ApplyAudioOutputSafety(_audioOutputGuard?.CurrentState);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("DAC lock change did not complete.", ex);
            if (!_exiting)
                _viewModel.UpdateDacLockState(coordinator.IsDacLockPaused, false,
                    "DAC lock change did not complete. Check the safety guidance.");
        }
        finally
        {
            if (!_exiting)
            {
                PersistDacLockPreference(coordinator.IsDacLockPaused);
                UpdateDacLockProjection();
                _viewModel.Playback.Refresh();
                RefreshDevices();
            }
        }
    }

    private void UpdateDacLockProjection()
    {
        if (_coordinator is not { } coordinator) return;
        _viewModel.UpdateDacLockState(coordinator.IsDacLockPaused,
            coordinator.IsDacLockChangeInProgress, coordinator.DacLockStatusMessage);
    }

    private void PersistDacLockPreference(bool paused)
    {
        if (_settings is null || _settings.DacLockPaused == paused) return;
        var previous = _settings.DacLockPaused;
        _settings.DacLockPaused = paused;
        try { _settingsService.Save(_settings); }
        catch (Exception ex)
        {
            _settings.DacLockPaused = previous;
            _logger.Error("Could not save the DAC lock preference.", ex);
            _viewModel.SetLifecycleNotice("DAC lock preference was not saved",
                "The current lock state is active for this session, but it could not be saved for the next launch.");
        }
    }

    private async Task RefreshAudioOutputSafetyAsync()
    {
        if (_exiting || _manualRestartInProgress || _coordinator is not { } coordinator ||
            Interlocked.Exchange(ref _audioOutputRefreshInFlight, 1) != 0) return;
        try
        {
            var worker = Task.Run(coordinator.RefreshAudioOutputSafety);
            _audioOutputSafetyWorker = worker;
            var state = await worker;
            if (!_exiting && ReferenceEquals(_coordinator, coordinator)) ApplyAudioOutputSafety(state);
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.Warn("DAC safety refresh failed: " + ex.Message);
            if (!_exiting) _viewModel.UpdateAudioOutputSafety(false, null, "DAC identity or default output could not be verified.");
        }
        finally { Interlocked.Exchange(ref _audioOutputRefreshInFlight, 0); }
    }

    private void ApplyAudioOutputSafety(AudioOutputGuardState? state, bool refreshProjection = true)
    {
        if (state is null) return;
        UpdateDacLockProjection();
        var changed = state != _lastUiSafetyState;
        _lastUiSafetyState = state;
        _viewModel.UpdateAudioOutputSafety(state.IsReady, state.Device?.FriendlyName, state.Detail);
        _audioMonitor?.RefreshOutputSafety();
        UpdateOriginalFormatRestoreState();
        if (changed && refreshProjection)
        {
            _deviceUiRefresh?.Invalidate();
            RefreshDevices();
        }
        _viewModel.Playback.Refresh();
    }

    private DeviceUiSnapshot QueryDeviceUiSnapshot(bool forceRefresh)
    {
        var coordinator = _coordinator;
        if (_exiting || coordinator is null)
            return new(null, 0, [], new(null, []), new(null, null, null), false, false);
        var guard = _audioOutputGuard;
        var version = guard?.StateVersion ?? 0;
        var devices = coordinator.GetRenderDevices();
        // Capture the pre-switch format before the first capability probe. Startup waits
        // asynchronously for this one projection before attaching the track source.
        var format = coordinator.GetCurrentTargetDeviceFormat();
        var capabilities = coordinator.GetCurrentTargetDeviceCapabilities(forceRefresh);
        var restoreAllowed = _settings?.OriginalTarget is { } original &&
            guard?.IsReadyForDevice(original.DeviceId) == true;
        return new(coordinator, version, devices, capabilities, format, restoreAllowed,
            version == (guard?.StateVersion ?? 0));
    }

    private void ApplyDeviceUiSnapshot(DeviceUiSnapshot snapshot)
    {
        if (_exiting || !ReferenceEquals(snapshot.Coordinator, _coordinator)) return;
        if (!snapshot.IsCurrent || snapshot.SafetyVersion != (_audioOutputGuard?.StateVersion ?? 0) ||
            (snapshot.Capabilities.DeviceName is not null && _audioOutputGuard?.IsSnapshotReady == false))
        {
            // Device notification invalidated an in-flight query. Never display its old target.
            _viewModel.UpdateActiveTargetCapabilities(new(null, []));
            _viewModel.UpdateCurrentDeviceFormat(null);
            _canRestoreOriginalFormat = false;
            UpdateOriginalFormatRestoreState();
            RefreshDevices();
            return;
        }
        _viewModel.ReplaceDevices(snapshot.Devices);
        _viewModel.UpdateActiveTargetCapabilities(snapshot.Capabilities);
        _viewModel.UpdateCurrentDeviceFormat(snapshot.Format.Format);
        _canRestoreOriginalFormat = snapshot.RestoreAllowed;
        CaptureOriginalTargetFormatIfMissing(snapshot.Format);
        UpdateOriginalFormatRestoreState();
    }

    private sealed record DeviceUiSnapshot(SwitchingCoordinator? Coordinator, long SafetyVersion,
        IReadOnlyList<AudioDeviceInfo> Devices, CurrentTargetDeviceCapabilitiesSnapshot Capabilities,
        CurrentTargetDeviceFormatSnapshot Format, bool RestoreAllowed, bool IsCurrent);

    private void CaptureOriginalTargetFormatIfMissing(CurrentTargetDeviceFormatSnapshot snapshot)
    {
        if (_settings is null || _coordinator is null)
        {
            return;
        }

        if (TryGetOriginalTargetFormat(_settings, out _))
        {
            UpdateOriginalFormatRestoreState();
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.DeviceId) || snapshot.Format is null)
        {
            _logger.Warn("Original target format capture skipped because no target device format is available yet.");
            UpdateOriginalFormatRestoreState();
            return;
        }

        _settings.OriginalTarget = new OriginalTargetSnapshot(
            snapshot.DeviceId,
            snapshot.DeviceName,
            snapshot.Format.SampleRateHz,
            snapshot.Format.BitDepth,
            snapshot.Format.Channels,
            DateTimeOffset.UtcNow);
        _canRestoreOriginalFormat = true;
        _settingsService.Save(_settings);
        _logger.Info($"Captured original target format {snapshot.Format.DisplayName} for {snapshot.DeviceName ?? snapshot.DeviceId}.");
        _viewModel.AddActivity($"Original format captured: {AudioFormatTextFormatter.Format(snapshot.Format)}");
        UpdateOriginalFormatRestoreState();
    }

    private async Task RestoreOriginalFormatAsync()
    {
        if (_exiting || _settings is null || _coordinator is null)
        {
            return;
        }

        if (!TryGetOriginalTargetFormat(_settings, out var originalFormat) || originalFormat is null)
        {
            UpdateOriginalFormatRestoreState("Original format not captured yet.");
            return;
        }

        var originalTarget = _settings.OriginalTarget;
        if (originalTarget is null || string.IsNullOrWhiteSpace(originalTarget.DeviceId))
        {
            UpdateOriginalFormatRestoreState("Original target device was not captured.");
            return;
        }

        _viewModel.OriginalFormatText = $"Restoring original format: {BuildOriginalFormatText(_settings)}";
        _viewModel.CanRestoreOriginalFormat = false;

        using var restoreTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var coordinator = _coordinator;
            var worker = Task.Run(() => coordinator.RestoreOriginalFormatAsync(
                originalTarget.DeviceId,
                originalTarget.DeviceName,
                originalFormat,
                restoreTimeoutCts.Token));
            _restoreWorker = worker;
            var result = await worker;
            if (_exiting) return;

            _viewModel.OriginalFormatText = result.Succeeded
                ? $"Restored original format. Saved original: {BuildOriginalFormatText(_settings)}"
                : $"Restore failed: {result.Message}";
            if (result.Succeeded)
            {
                _viewModel.AddActivity($"Restored original format ({AudioFormatTextFormatter.Format(originalFormat)})");
            }
        }
        catch (OperationCanceledException) when (restoreTimeoutCts.IsCancellationRequested)
        {
            _logger.Warn("Restore original format timed out.");
            if (!_exiting) _viewModel.OriginalFormatText = "Restore timed out.";
        }
        catch (Exception ex)
        {
            _logger.Error("Restore original format failed unexpectedly.", ex);
            if (!_exiting) _viewModel.OriginalFormatText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            if (!_exiting)
            {
                UpdateOriginalFormatRestoreState();
                UpdateActiveTargetCapabilities(forceRefresh: true);
                RefreshCurrentFormatDisplays();
            }
        }
    }

    private void UpdateOriginalFormatRestoreState(string? statusOverride = null)
    {
        if (_settings is null)
        {
            _viewModel.OriginalFormatText = "Original format not captured yet.";
            _viewModel.CanRestoreOriginalFormat = false;
            return;
        }

        _viewModel.OriginalFormatText = statusOverride ?? BuildOriginalFormatText(_settings);
        _viewModel.CanRestoreOriginalFormat = TryGetOriginalTargetFormat(_settings, out _) &&
            _canRestoreOriginalFormat && _audioOutputGuard?.IsSnapshotReady == true;
    }

    private static string BuildOriginalFormatText(AppSettings settings)
    {
        if (!TryGetOriginalTargetFormat(settings, out var format) || format is null)
        {
            return "Original format not captured yet.";
        }

        var originalTarget = settings.OriginalTarget;
        if (originalTarget is null)
        {
            return "Original format not captured yet.";
        }

        var deviceName = string.IsNullOrWhiteSpace(originalTarget.DeviceName)
            ? "Captured target device"
            : originalTarget.DeviceName;
        var capturedText = $" (captured {originalTarget.CapturedAtUtc.ToLocalTime():g})";
        return $"{deviceName}: {AudioFormatTextFormatter.Format(format)}{capturedText}";
    }

    private static bool TryGetOriginalTargetFormat(AppSettings settings, out AudioFormatCandidate? format)
    {
        format = null;
        var originalTarget = settings.OriginalTarget;
        if (originalTarget is null ||
            string.IsNullOrWhiteSpace(originalTarget.DeviceId) ||
            originalTarget.SampleRateHz <= 0 ||
            originalTarget.BitDepth <= 0 ||
            originalTarget.Channels <= 0)
        {
            return false;
        }

        format = new AudioFormatCandidate(
            originalTarget.SampleRateHz,
            originalTarget.BitDepth,
            originalTarget.Channels);
        return true;
    }

    private void ApplyStartupRegistration()
    {
        // Portable acceptance build never writes the installed application's Run entry.
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ShowTrayFlyout()
    {
        // Exceptions here would be swallowed by the WinForms NotifyIcon WndProc — log them.
        try
        {
            if (_trayFlyout is null)
            {
                _trayFlyout = new TrayFlyoutWindow(_viewModel);
                _trayFlyout.OpenSettingsRequested += ShowMainWindow;
                _trayFlyout.ExitRequested += ExitApplication;
            }

            if (_trayFlyout.IsVisible)
            {
                _trayFlyout.Hide();
                return;
            }

            _trayFlyout.ShowNearTray();
        }
        catch (Exception ex)
        {
            _logger.Error("Tray flyout failed to open.", ex);
        }
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            if (_audioMonitor is null)
            {
                return;
            }

            _settingsWindow = new SettingsWindow(_viewModel, _audioMonitor);
            _settingsWindow.Title = TestBuildProfile.DisplayName;
        }

        _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void OnRestartToolRequested() => _ = RestartManuallyAsync(ManualRestartMode.ToolOnly);
    private void OnRestartPlayerAndToolRequested() => _ = RestartManuallyAsync(ManualRestartMode.AppleMusicAndTool);

    private async Task RestartManuallyAsync(ManualRestartMode mode)
    {
        if (_coordinator?.IsDacLockChangeInProgress == true) return;
        if (_manualRestartInProgress || _viewModel.IsRestartBusy || _viewModel.Playback.IsBusy || _coordinator is null) return;
        _viewModel.IsRestartBusy = true;
        _viewModel.Playback.SetRestartBusy(true);
        string? requestPath = null;
        try
        {
            if (mode == ManualRestartMode.AppleMusicAndTool && _audioOutputGuard?.IsSnapshotReady != true)
            {
                _viewModel.SetLifecycleNotice("Waiting for a supported DAC", "Apple Music restart is unavailable until a supported DAC is connected and confirmed as the default output.");
                return;
            }
            // IsRestartBusy claims both commands during the modal loop. Ordinary status
            // updates remain visible until the user confirms, including after Cancel.
            if (mode == ManualRestartMode.AppleMusicAndTool && LocalizedDialog.Show(
                "This will close and reopen Apple Music and Windows Lossless Switcher. The play queue may not be preserved. Select a track and press Play yourself afterward. Continue?",
                ManualRestartHandoff.ApplicationName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _manualRestartInProgress = true;
            _logger.Info($"Manual restart requested: mode={mode}; previousStatus={_latestStatus?.ResolverStatusText}.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var coordinator = _coordinator;
            var prepared = await Task.Run(() => coordinator.PrepareForManualRestartAsync(
                deadline.Token, noPlayerProcess: !ManualRestartHandoff.IsAppleMusicRunning())).WaitAsync(TimeSpan.FromSeconds(11));
            if (!prepared)
            {
                _viewModel.SetLifecycleNotice("Restart stopped: pause not confirmed", "Automatic processing has been suspended. Pause Apple Music manually, then retry. The application was not forcibly closed and its protective mute was not released.");
                return;
            }
            if (mode == ManualRestartMode.AppleMusicAndTool &&
                !await Task.Run(() => _audioOutputGuard?.IsReady == true))
                throw new InvalidOperationException("DAC output was lost before Apple Music restart.");
            requestPath = await ManualRestartHandoff.StartHelperAsync(mode, CancellationToken.None);
            // The user could have pressed Play while the helper started. Recheck before
            // Dispose releases owned mute, using the same bounded preparation barrier.
            if (!await Task.Run(() => coordinator.PrepareForManualRestartAsync(deadline.Token,
                    noPlayerProcess: !ManualRestartHandoff.IsAppleMusicRunning())).WaitAsync(TimeSpan.FromSeconds(4)))
                throw new InvalidOperationException("Apple Music was no longer confirmed paused before exit.");
            // Explicit cleanup is the barrier; WPF's async-void OnExit cannot provide it.
            await Task.Run(async () => await coordinator.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(15));
            _coordinator = null;
            ExitApplication();
        }
        catch (Exception ex)
        {
            if (requestPath is not null) ManualRestartHandoff.Cancel(requestPath);
            _logger.Error("Manual restart did not complete.", ex);
            _viewModel.SetLifecycleNotice("Restart did not complete", "Pause Apple Music before retrying. If the tool cannot exit, quit it from the tray and launch it manually. Reason: " + ex.Message);
        }
        finally
        {
            _viewModel.IsRestartBusy = false;
            _viewModel.Playback.SetRestartBusy(false);
            _manualRestartInProgress = false;
        }
    }

    private async Task<bool> ConfirmRestartStartupPauseAsync()
    {
        if (!ManualRestartHandoff.IsAppleMusicRunning()) return true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var probe = new AppleMusicTrackSource(_logger);
        await probe.StartAsync(deadline.Token).WaitAsync(deadline.Token);
        while (!deadline.IsCancellationRequested)
        {
            if (probe.GetPlaybackState().PlaybackStatus != MediaTransportPlaybackStatus.Unknown)
                return await ManualRestartSafety.ConfirmPausedAsync(probe, deadline.Token);
            await Task.Delay(100, deadline.Token);
        }
        return false;
    }

    private async void ExitApplication()
    {
        if (_exitInProgress || (_manualRestartInProgress && _coordinator is not null)) return;
        _exitInProgress = true;
        _exiting = true;
        _dacLockLifetime.Cancel();
        _coordinator?.RequestShutdown();
        _audioOutputRefreshTimer?.Stop();
        _playbackRefreshTimer?.Stop();
        _artworkRecheckTimer?.Stop();
        _deviceUiRefresh?.Dispose();
        _viewModel.Playback.Dispose();
        try
        {
            var coordinator = _coordinator;
            var monitor = _audioMonitor;
            var guard = _audioOutputGuard as IDisposable;
            var deviceDisplaysDrained = _deviceUiRefresh?.DrainAsync() ?? Task.CompletedTask;
            var playbackDisplaysDrained = _viewModel.Playback.DrainAsync();
            var safetyWorker = _audioOutputSafetyWorker;
            var startupWorker = _startupBackendWorker;
            var restoreWorker = _restoreWorker;
            var dacLockWorker = _dacLockWorker;
            await AsyncShutdownSequence.RunAsync(async () =>
            {
                // A disposed pump suppresses future work/UI application, but its current
                // native call may still be finishing. Drain before disposing shared audio state.
                await Task.WhenAll(new[] { deviceDisplaysDrained, playbackDisplaysDrained,
                    safetyWorker, startupWorker, restoreWorker, dacLockWorker }.Select(AwaitWorkerCompletionAsync)).ConfigureAwait(false);
                if (coordinator is not null)
                {
                    coordinator.StatusChanged -= OnCoordinatorStatusChanged;
                    coordinator.FormatCacheUpdated -= OnFormatCacheUpdated;
                    await coordinator.DisposeAsync();
                }
                monitor?.Dispose();
                guard?.Dispose();
            }, () =>
            {
                if (coordinator is not null) PersistDacLockPreference(coordinator.IsDacLockPaused);
                _coordinator = null;
                _audioMonitor = null;
                _audioOutputGuard = null;
                _mainWindow?.AllowCloseAndClose();
                _settingsWindow?.AllowCloseAndClose();
                Shutdown();
            });
        }
        catch (Exception ex)
        {
            // A failed cleanup cannot authorize destroying the process before mute release.
            _logger.Error("Application cleanup did not complete; shutdown was stopped.", ex);
            _exitInProgress = false;
            _viewModel.SetLifecycleNotice("Exit did not complete", "Pause Apple Music and retry Exit. Native cleanup did not complete: " + ex.Message);
        }
    }

    private static async Task AwaitWorkerCompletionAsync(Task worker)
    {
        // A read that already failed has finished touching native state. Its failure is
        // reported at the query boundary and must not prevent the actual cleanup below.
        try { await worker.ConfigureAwait(false); }
        catch { }
    }

    /// <summary>Pushes the current target-device format to the view model (flyout chip, "was" seed).</summary>
    private void RefreshCurrentFormatDisplays()
    {
        RefreshDevices();
    }

    private void UpdateFormatCacheStatus()
    {
        var count = _formatCacheStore?.Count ?? 0;
        var storefront = _storefront ?? "auto";
        _viewModel.FormatCacheStatusText = count == 1
            ? $"1 cached lookup · storefront: {storefront}"
            : $"{count} cached lookups · storefront: {storefront}";
    }

    private void UpdateArtworkIfChanged()
    {
        var snapshot = _trackSource?.GetArtworkSnapshot();
        if (snapshot is null)
        {
            return;
        }

        // Same bytes for a different track still needs a refresh: the artwork's track tag drives
        // which toasts may show it, so the (revision, track) pair is the change stamp.
        var stamp = $"{snapshot.Revision}|{snapshot.TrackUniqueKey}";
        if (string.Equals(stamp, _lastArtworkRevision, StringComparison.Ordinal))
        {
            return;
        }

        _lastArtworkRevision = stamp;
        if (!snapshot.HasArtwork)
        {
            _viewModel.UpdateArtwork(null, null, null);
            return;
        }

        var bytes = snapshot.Bytes!;
        var trackKey = snapshot.TrackUniqueKey;
        // Decode + color analysis off the UI thread; both results are frozen/immutable.
        Task.Run(() =>
        {
            try
            {
                var image = DecodeArtwork(bytes);
                var glow = ArtworkColorAnalyzer.TryGetDominantColor(bytes);
                Dispatcher.BeginInvoke(() =>
                {
                    _viewModel.UpdateArtwork(image, glow, trackKey);
                    // Late artwork lands in an already-open toast for the same track.
                    _switchToastService?.UpdateToastArtwork(trackKey, image);
                });
            }
            catch (Exception ex)
            {
                _logger.Warn($"Artwork decode failed: {ex.Message}");
            }
        });
    }

    /// <summary>Artwork for a toast: only when the loaded artwork belongs to that exact track.</summary>
    private System.Windows.Media.ImageSource? ArtworkForTrack(string? trackKey) =>
        trackKey is not null &&
        string.Equals(_viewModel.ArtworkTrackKey, trackKey, StringComparison.Ordinal)
            ? _viewModel.ArtworkImage
            : null;

    // GSMTC delivers artwork asynchronously after the track properties, so a status update often
    // lands before the thumbnail. A few short rechecks pick it up; the revision guard makes them
    // no-ops when nothing changed.
    private void ScheduleArtworkRechecks()
    {
        _artworkRechecksRemaining = 3;
        if (_artworkRecheckTimer is null)
        {
            _artworkRecheckTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5),
            };
            _artworkRecheckTimer.Tick += (_, _) =>
            {
                UpdateArtworkIfChanged();
                if (--_artworkRechecksRemaining <= 0)
                {
                    _artworkRecheckTimer!.Stop();
                }
            };
        }

        _artworkRecheckTimer.Stop();
        _artworkRecheckTimer.Start();
    }

    private static System.Windows.Media.Imaging.BitmapImage DecodeArtwork(byte[] bytes)
    {
        using var stream = new System.IO.MemoryStream(bytes, writable: false);
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 352;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnExportDiagnosticsRequested()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".log",
            Filter = LocalizationService.Current["Log files (*.log)|*.log|All files (*.*)|*.*"],
            FileName = $"windows-lossless-switcher-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };

        var owner = Windows.OfType<Window>().FirstOrDefault(window => window.IsActive && window.IsVisible);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed == true)
        {
            _logger.Export(dialog.FileName);
        }
    }

    private void UpdateActiveTargetCapabilities(bool forceRefresh = false)
    {
        if (!_exiting && _deviceUiRefresh is not null) _ = _deviceUiRefresh.RequestAsync(forceRefresh);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        try
        {
            await _appUpdater.CheckForUpdatesAsync(userInitiated, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("Update check failed unexpectedly.", ex);
        }
    }

    private async Task RunUpdatePrimaryActionAsync()
    {
        try
        {
            await _appUpdater.RunPrimaryActionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("Update action failed unexpectedly.", ex);
        }
    }
}
