using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed partial class SwitchingCoordinatorTests
{
    [Fact]
    public async Task DacLockUiPauseRemainsAvailableDuringAUserCommandButResumeWaitsForIt()
    {
        var process = new DacLockUiBlockingProcess();
        await using var coordinator = CreateCoordinator(new TestTrackSource(), new TestMediaTransportController(), PauseEndpoint(), PauseResolver(CurrentFormat));
        var vm = new MainWindowViewModel();
        try
        {
            vm.DacLockToggleRequested += _ => { };
            vm.UpdateDacLockState(false, false);
            vm.Playback.Configure(coordinator, process);
            await vm.Playback.RefreshAsync().WaitAsync(DefaultTimeout);
            vm.Playback.OpenAppleMusicCommand.Execute(null);
            await process.Entered.Task.WaitAsync(DefaultTimeout);
            Assert.True(vm.Playback.IsBusy);
            Assert.True(vm.DacLockToggleCommand.CanExecute(null));
            vm.UpdateDacLockState(true, false);
            Assert.False(vm.DacLockToggleCommand.CanExecute(null));
            process.Release.TrySetResult();
            await vm.Playback.DrainAsync().WaitAsync(DefaultTimeout);
            var deadline = DateTimeOffset.UtcNow + DefaultTimeout;
            while (vm.Playback.IsBusy && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
            Assert.True(vm.DacLockToggleCommand.CanExecute(null));
        }
        finally
        {
            process.Release.TrySetResult();
            vm.DetachLocalization();
            vm.Playback.Dispose();
        }
    }

    private sealed class DacLockUiBlockingProcess : IAppleMusicProcessController
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> TryOpenAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task;
            return true;
        }
        public Task<bool> TryRestartAsync(CancellationToken token) => Task.FromResult(false);
    }
}
