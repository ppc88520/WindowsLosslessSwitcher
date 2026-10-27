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

    public PlaybackControlsViewModel()
    {
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

    public void Configure(SwitchingCoordinator coordinator, IAppleMusicProcessController processController)
    {
        _coordinator = coordinator;
        _processController = processController;
        Refresh();
    }

    public void SetRestartBusy(bool busy)
    {
        _restartBusy = busy;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        try
        {
            if (_coordinator is not null) _availability = _coordinator.GetUserPlaybackAvailability();
        }
        catch (ObjectDisposedException) { _availability = new(MediaTransportPlaybackStatus.Unknown, false, false, false, false, false, false); }
        RaiseStateChanged();
    }

    private async Task ExecuteAsync(UserPlaybackAction action)
    {
        if (_disposed || IsBusy || _restartBusy || _coordinator is null) return;
        IsBusy = true;
        _lastResult = null;
        RaiseStateChanged();
        try { _lastResult = (await _coordinator.ExecuteUserPlaybackAsync(action, _lifetime.Token)).Message; }
        catch (OperationCanceledException) { }
        catch { _lastResult = "Playback control failed. Check the recovery guidance."; }
        finally { IsBusy = false; Refresh(); }
    }

    private async Task OpenAsync()
    {
        if (_disposed || IsBusy || _restartBusy || _processController is null) return;
        IsBusy = true;
        _lastResult = null;
        RaiseStateChanged();
        try
        {
            _lastResult = await _processController.TryOpenAsync(_lifetime.Token)
                ? "Apple Music opened." : "Could not open Apple Music.";
        }
        catch (OperationCanceledException) { }
        catch { _lastResult = "Could not open Apple Music."; }
        finally { IsBusy = false; Refresh(); }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RaiseStateChanged();

    private void RaiseStateChanged()
    {
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
        _lifetime.Cancel();
        LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
        _coordinator = null;
        _processController = null;
        _lifetime.Dispose();
    }
}
