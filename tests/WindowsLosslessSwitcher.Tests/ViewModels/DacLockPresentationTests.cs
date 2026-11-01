using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.ViewModels;

public sealed class DacLockPresentationTests
{
    [Fact]
    public void ToggleRequestsTheDesiredStateWithoutClaimingBackendSuccess()
    {
        using var scope = new ViewModelScope();
        var vm = scope.ViewModel;
        Assert.False(vm.DacLockToggleCommand.CanExecute(null));
        var requested = new List<bool>();
        vm.DacLockToggleRequested += requested.Add;
        vm.UpdateDacLockState(false, false);
        Assert.True(vm.DacLockToggleCommand.CanExecute(null));
        Assert.Equal("Pause DAC Lock", vm.DacLockToggleText);
        vm.DacLockToggleCommand.Execute(null);
        Assert.False(vm.IsDacLockPaused);
        vm.UpdateDacLockState(true, false);
        Assert.Equal("Resume DAC Lock", vm.DacLockToggleText);
        vm.DacLockToggleCommand.Execute(null);
        Assert.Equal(new[] { true, false }, requested);
    }

    [Fact]
    public void PausedLockCannotBeOverwrittenByAReadyDeviceProjection()
    {
        using var scope = new ViewModelScope();
        var vm = scope.ViewModel;
        vm.UpdateDacLockState(true, false);
        vm.UpdateAudioOutputSafety(true, "TOPPING USB DAC", "DAC output is ready.");
        Assert.False(vm.AudioOutputSafetyReady);
        Assert.True(vm.ShowAudioOutputNotice);
        Assert.Equal("DAC lock paused", vm.AudioOutputSafetyTitleText);
        Assert.Equal("Lock paused", vm.DeviceModeChipText);
        Assert.Equal("Automatic switching paused", vm.PhaseText);
        Assert.Equal("Automatic selection paused", vm.OutputDeviceDisplayText);
        Assert.Equal("Last DAC format", vm.WasLine);
        Assert.False(vm.RestartPlayerAndToolCommand.CanExecute(null));
        Assert.True(vm.RestartToolCommand.CanExecute(null));
    }

    [Fact]
    public void BusyTransitionBlocksConflictingRestartAndRepeatedToggle()
    {
        using var scope = new ViewModelScope();
        var vm = scope.ViewModel;
        vm.DacLockToggleRequested += _ => { };
        vm.UpdateDacLockState(false, true);
        Assert.True(vm.ShowAudioOutputNotice);
        Assert.False(vm.DacLockToggleCommand.CanExecute(null));
        Assert.False(vm.RestartToolCommand.CanExecute(null));
        Assert.False(vm.RestartPlayerAndToolCommand.CanExecute(null));
        Assert.Equal("Updating DAC lock", vm.DacLockToggleText);
        vm.UpdateDacLockState(false, false);
        vm.IsRestartBusy = true;
        Assert.False(vm.DacLockToggleCommand.CanExecute(null));
        vm.IsRestartBusy = false;
        Assert.True(vm.DacLockToggleCommand.CanExecute(null));
    }

    [Fact]
    public void UncertainPauseShowsBackendGuidanceInsteadOfClaimingItIsSafeToSwitchSpeakers()
    {
        using var scope = new ViewModelScope();
        const string guidance = "An earlier playback command may still execute. Protective mute is retained.";
        scope.ViewModel.UpdateDacLockState(true, false, guidance);
        Assert.Equal(guidance, scope.ViewModel.AudioOutputSafetyDetailText);
        Assert.Equal(guidance, scope.ViewModel.DacLockHelpText);
        Assert.DoesNotContain("You can now choose", scope.ViewModel.DacLockHelpText);
    }

    [Fact]
    public void LanguageSelectionOnlyChangesPresentationAndPreservesPausedState()
    {
        using var scope = new ViewModelScope();
        var vm = scope.ViewModel;
        var commands = 0;
        vm.DacLockToggleRequested += _ => commands++;
        vm.UpdateDacLockState(true, false);
        vm.SelectTraditionalChineseLanguageCommand.Execute(null);
        Assert.Equal("zh-TW", vm.UiLanguage);
        Assert.Equal("恢復 DAC 鎖定", vm.DacLockToggleText);
        Assert.Equal("DAC 鎖定已暫停", vm.AudioOutputSafetyTitleText);
        Assert.True(vm.IsDacLockPaused);
        vm.SelectEnglishLanguageCommand.Execute(null);
        Assert.Equal("en", vm.UiLanguage);
        Assert.Equal("Resume DAC Lock", vm.DacLockToggleText);
        Assert.Equal(0, commands);
    }

    [Fact]
    public void ReadyOutputIsQuietAndNamedDeviceUpdatesStillReachTheMainCard()
    {
        using var scope = new ViewModelScope();
        var vm = scope.ViewModel;
        vm.UpdateAudioOutputSafety(true, "TOPPING USB DAC", "DAC output is ready.");
        Assert.False(vm.ShowAudioOutputNotice);
        var projectedNameChanged = false;
        vm.PropertyChanged += (_, args) => projectedNameChanged |= args.PropertyName == nameof(vm.OutputDeviceDisplayText);
        vm.UpdateActiveTargetCapabilities(new("喇叭 (TOPPING USB DAC)", []));
        Assert.Equal("喇叭 (TOPPING USB DAC)", vm.OutputDeviceDisplayText);
        Assert.True(projectedNameChanged);
    }

    private sealed class ViewModelScope : IDisposable
    {
        public MainWindowViewModel ViewModel { get; } = new(new LocalizationService());
        public void Dispose() { ViewModel.DetachLocalization(); ViewModel.Playback.Dispose(); }
    }
}
