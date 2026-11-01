using System.Windows.Threading;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AsyncShutdownSequenceTests
{
    [Fact]
    public Task ShutdownWaitsForOwnedMuteCleanupWithoutBlockingTheDispatcher() => BackgroundUiRefreshTests.OnDispatcher(async dispatcher =>
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unmuted = false;
        var shutDown = false;
        var shutdownOnDispatcher = false;
        var sequence = AsyncShutdownSequence.RunAsync(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
            unmuted = true;
            return Task.CompletedTask;
        }, () =>
        {
            Assert.True(unmuted);
            shutDown = true;
            shutdownOnDispatcher = dispatcher.CheckAccess();
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(shutDown);
        }
        finally { release.Set(); }
        await sequence.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(shutDown);
        Assert.True(shutdownOnDispatcher);
    });

    [Fact]
    public async Task FailedCleanupDoesNotAuthorizeShutdown()
    {
        var shutDown = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AsyncShutdownSequence.RunAsync(
            () => throw new InvalidOperationException("Mute release failed"), () => shutDown = true));
        Assert.False(shutDown);
    }
}
