using System.ComponentModel;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.ViewModels;

/// <summary>One command surface shared by the main window and tray; inert until configured.</summary>
public sealed class PlaybackControlsViewModel : INotifyPropertyChanged, IDisposable
{
    private SwitchingCoordinator? _coordinator;
    private IAppleMusicProcessController? _processController;
    private UserPlaybackAvailability _availability = new(MediaTransportPlaybackStatus.Unknown, false, false, false, false, false, false);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _restartBusy;
    private bool _disposed;
    private string? _lastResult;
    private readonly BackgroundUiRefresh<UserPlaybackAvailability> _refresh;
    private Task _activeCommandWorker = Task.CompletedTask;

    public PlaybackControlsViewModel()
    {
        _refresh = new BackgroundUiRefresh<UserPlaybackAvailability>(
            _ => _coordinator?.GetUserPlaybackAvailability() ?? Unavailable(),
            availability => { if (!_disposed) { _availability = availability; RaiseStateChanged(); } },
            onError: _ => { if (!_disposed) { _availability = Unavailable(); RaiseStateChanged(); } });
        TogglePlaybackCommand = new RelayCommand(() => _ = ExecuteAsync(_availability.PlaybackStatus == MediaTransportPlaybackStatus.Playing
            ? UserPlaybackAction.Pause : UserPlaybackAction.Play), () => !_disposed && !_restartBusy && !IsBusy && (_availability.CanPause || _availability.CanPlay));
        PreviousTrackCommand = new RelayCommand(() => _ = ExecuteAsync(UserPlaybackAction.Previous), () => !_disposed && !_restartBusy && !IsBusy && _availability.CanPrevious);
        NextTrackCommand = new RelayCommand(() => _ = ExecuteAsync(UserPlaybackAction.Next), () => !_disposed && !_restartBusy && !IsBusy && _availability.CanNext);
        OpenAppleMusicCommand = new RelayCommand(() => _ = OpenAsync(), () => !_disposed && !_restartBusy && !IsBusy && !_availability.IsBusy && _processController is not null);
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public RelayCommand TogglePlaybackCommand { get; }
    public RelayCommand PreviousTrackCommand { get; }
    public RelayCommand NextTrackCommand { get; }
    public RelayCommand OpenAppleMusicCommand { get; }
    public bool IsBusy { get; private set; }
    public string PlayPauseText => LocalizationService.Current[_availability.PlaybackStatus == MediaTransportPlaybackStatus.Playing ? "Pause" : "Play"];
    public string StatusText => LocalizationService.Current[_restartBusy ? "Playback controls are unavailable during restart."
        : _availability.IsSafetyHeld ? "Playback is held for safety. Follow the recovery guidance."
        : _lastResult ?? (_availability.PlaybackStatus == MediaTransportPlaybackStatus.Unknown ? "Apple Music is not connected."
            : _availability.IsBusy ? "Playback controls are temporarily unavailable." : "")];

    public void Configure(SwitchingCoordinator coordinator, IAppleMusicProcessController processController,
        SynchronizationContext? context = null)
    {
        if (_disposed) return;
        if ((context ?? SynchronizationContext.Current) is { } uiContext) _refresh.BindContext(uiContext);
        _coordinator = coordinator;
        _processController = processController;
        Refresh();
    }

    public void SetRestartBusy(bool busy)
    {
        if (_disposed) return;
        _restartBusy = busy;
        RaiseStateChanged();
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        if (_coordinator is null) { RaiseStateChanged(); return; }
        _ = RefreshAsync();
    }

    public Task RefreshAsync() => _disposed || _coordinator is null ? Task.CompletedTask : _refresh.RequestAsync();

    public Task DrainAsync() => Task.WhenAll(_refresh.DrainAsync(), Volatile.Read(ref _activeCommandWorker));

    private static UserPlaybackAvailability Unavailable() =>
        new(MediaTransportPlaybackStatus.Unknown, false, false, false, false, false, false);

    private async Task ExecuteAsync(UserPlaybackAction action)
    {
        if (_disposed || IsBusy || _restartBusy || _coordinator is null) return;
        IsBusy = true;
        _lastResult = null;
        RaiseStateChanged();
        var coordinator = _coordinator;
        var token = _lifetime.Token;
        try
        {
            var worker = Task.Run(() => coordinator.ExecuteUserPlaybackAsync(action, token), token);
            Volatile.Write(ref _activeCommandWorker, worker);
            var result = await worker;
            if (!_disposed) _lastResult = result.Message;
        }
        catch (OperationCanceledException) { }
        catch { if (!_disposed) _lastResult = "Playback control failed. Check the recovery guidance."; }
        finally { if (!_disposed) { IsBusy = false; RaiseStateChanged(); Refresh(); } }
    }

    private async Task OpenAsync()
    {
        if (_disposed || IsBusy || _restartBusy || _processController is null) return;
        IsBusy = true;
        _lastResult = null;
        RaiseStateChanged();
        try
        {
            var processController = _processController;
            var token = _lifetime.Token;
            var worker = Task.Run(() => processController.TryOpenAsync(token), token);
            Volatile.Write(ref _activeCommandWorker, worker);
            var opened = await worker;
            if (!_disposed) _lastResult = opened ? "Apple Music opened." : "Could not open Apple Music.";
        }
        catch (OperationCanceledException) { }
        catch { if (!_disposed) _lastResult = "Could not open Apple Music."; }
        finally { if (!_disposed) { IsBusy = false; RaiseStateChanged(); Refresh(); } }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        if (_disposed) return;
        PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        PropertyChanged?.Invoke(this, new(nameof(PlayPauseText)));
        PropertyChanged?.Invoke(this, new(nameof(StatusText)));
        TogglePlaybackCommand.RaiseCanExecuteChanged();
        PreviousTrackCommand.RaiseCanExecuteChanged();
        NextTrackCommand.RaiseCanExecuteChanged();
        OpenAppleMusicCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refresh.Dispose();
        _lifetime.Cancel();
        LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
        _coordinator = null;
        _processController = null;
        _lifetime.Dispose();
    }
}
