using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.ViewModels;

public sealed class ManualRecoveryPresentationTests
{
    [Theory]
    [InlineData("Switching: Restart unavailable", RecoveryCategory.PreviousUnavailable)]
    [InlineData("Switching: Manual recovery needed", RecoveryCategory.AudioRecoveryFailed)]
    public void LegacyHoldStatusesAreFailedAndNotBusy(string text, RecoveryCategory expected)
    {
        var status = Status(text);
        var viewModel = new MainWindowViewModel();
        viewModel.UpdateStatus(status);
        Assert.Equal(expected, RecoveryAdvice.From(status).Category);
        Assert.Equal(SwitchPhase.Failed, MainWindowViewModel.DerivePhase(status));
        Assert.False(viewModel.IsBusyPhase);
        Assert.True(viewModel.HasRecoveryAdvice);
        Assert.DoesNotContain("Switching device", viewModel.PhaseText);
    }

    [Fact]
    public void WaitingForPlayIsNeitherBusyNorAnErrorNotification()
    {
        var status = Status("Switching: Waiting for user playback");
        var viewModel = new MainWindowViewModel();
        viewModel.UpdateStatus(status);
        Assert.False(viewModel.IsBusyPhase);
        Assert.NotEqual(SwitchPhase.Failed, viewModel.Phase);
        Assert.True(viewModel.HasRecoveryAdvice);
        Assert.False(new RecoveryNotificationGate().ShouldNotify(status));
    }

    [Theory]
    [InlineData(RecoveryCategory.DeviceUnavailable, "device")]
    [InlineData(RecoveryCategory.UnsupportedFormat, "format")]
    [InlineData(RecoveryCategory.FormatApplyFailed, "change")]
    [InlineData(RecoveryCategory.FormatNotStable, "change")]
    [InlineData(RecoveryCategory.MetadataUnavailable, "Track")]
    [InlineData(RecoveryCategory.UnsafeState, "stopped")]
    public void StructuredFailureOverridesAnIntermediateText(RecoveryCategory category, string titlePart)
    {
        var status = Status("Switching: Applying format") with { Recovery = category };
        Assert.Equal(SwitchPhase.Failed, MainWindowViewModel.DerivePhase(status));
        Assert.Contains(titlePart, RecoveryAdvice.From(status).Title);
    }

    [Fact]
    public void RepeatedFailureNotifiesOnceAndACompletedRecoveryStartsANewIncident()
    {
        var gate = new RecoveryNotificationGate();
        var status = Status("Switching: Manual recovery needed");
        Assert.True(gate.ShouldNotify(status));
        Assert.False(gate.ShouldNotify(status));
        Assert.False(gate.ShouldNotify(status with { FailureReason = "More diagnostics for the same incident" }));
        Assert.False(gate.ShouldNotify(Status("Switching: Playback restored")));
        Assert.True(gate.ShouldNotify(status));
    }

    [Fact]
    public void RestartCommandsAreDistinctAndBothDisabledDuringHandoff()
    {
        var vm = new MainWindowViewModel();
        var tool = 0; var both = 0;
        vm.RestartToolRequested += () => tool++;
        vm.RestartPlayerAndToolRequested += () => both++;
        vm.RestartToolCommand.Execute(null);
        vm.RestartPlayerAndToolCommand.Execute(null);
        Assert.Equal(1, tool);
        Assert.Equal(1, both);
        vm.IsRestartBusy = true;
        Assert.False(vm.RestartToolCommand.CanExecute(null));
        Assert.False(vm.RestartPlayerAndToolCommand.CanExecute(null));
        vm.IsRestartBusy = false;
        Assert.True(vm.RestartToolCommand.CanExecute(null));
    }

    [Fact]
    public void RestartRequiresTheCurrentApplicationHost()
    {
        Assert.True(ManualRestartHandoff.IsSupportedExecutable("WindowsLosslessSwitcher.exe"));
        Assert.False(ManualRestartHandoff.IsSupportedExecutable("dotnet.exe"));
        Assert.False(ManualRestartHandoff.IsSupportedExecutable("AnotherApplication.exe"));
    }

    private static SwitchingStatus Status(string text) => new(text, null, null, null, null, null);
}
