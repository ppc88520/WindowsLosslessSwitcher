using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _isDacLockPaused;
    private bool _isDacLockBusy;
    private string? _dacLockDetail;

    /// <summary>Display and command state only; audio work is owned by the application/coordinator.</summary>
    public bool IsDacLockPaused => _isDacLockPaused;
    public bool IsDacLockBusy => _isDacLockBusy;
    public bool ShowAudioOutputNotice => IsDacLockPaused || IsDacLockBusy || (_useDacOnlyOutput && !AudioOutputSafetyReady);
    public string DacLockToggleText => _localization[IsDacLockBusy ? "Updating DAC lock" : IsDacLockPaused ? "Resume DAC Lock" : "Pause DAC Lock"];
    public string DacLockHelpText => !string.IsNullOrWhiteSpace(_dacLockDetail) ? _localization.TranslateDiagnostic(_dacLockDetail) : _localization[IsDacLockPaused
        ? "Resume the lock to select the DAC again. Playback will not start automatically."
        : "Pause the lock to use another speaker without closing the tool."];
    public string OutputDeviceDisplayText => IsDacLockPaused
        ? _localization["Automatic selection paused"] : ActiveTargetDeviceNameText;

    public RelayCommand DacLockToggleCommand { get; private set; } = null!;
    public RelayCommand SelectEnglishLanguageCommand { get; }
    public RelayCommand SelectTraditionalChineseLanguageCommand { get; }
    public event Action<bool>? DacLockToggleRequested;

    private void InitializeDacLockControls() => DacLockToggleCommand = new RelayCommand(
        () => DacLockToggleRequested?.Invoke(!IsDacLockPaused),
        () => DacLockToggleRequested is not null && !IsRestartBusy && !IsDacLockBusy &&
              (!IsDacLockPaused || !Playback.IsBusy));

    public void UpdateDacLockState(bool paused, bool busy, string? detail = null)
    {
        _isDacLockPaused = paused;
        _isDacLockBusy = busy;
        _dacLockDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        RaiseDacLockPresentationChanged();
        RestartToolCommand.RaiseCanExecuteChanged();
        RestartPlayerAndToolCommand.RaiseCanExecuteChanged();
        DacLockToggleCommand.RaiseCanExecuteChanged();
    }

    private void RaiseDacLockPresentationChanged()
    {
        foreach (var name in new[] { nameof(IsDacLockPaused), nameof(IsDacLockBusy), nameof(ShowAudioOutputNotice),
                     nameof(DacLockToggleText), nameof(DacLockHelpText), nameof(OutputDeviceDisplayText),
                     nameof(AudioOutputSafetyReady), nameof(AudioOutputSafetyTitleText), nameof(AudioOutputSafetyDetailText),
                     nameof(DeviceModeChipText), nameof(PhaseText), nameof(WasLine) })
            OnPropertyChanged(name);
    }
}
