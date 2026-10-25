using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class BoundedMediaCommandTests
{
    [Fact]
    public async Task NonCooperativeQueuedCommandTimesOutWithoutRedispatch()
    {
        var remote = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => BoundedMediaCommand.InvokeAsync(
            _ => { dispatches++; return remote.Task; }, "Previous", CancellationToken.None, TimeSpan.FromMilliseconds(30)));
        // A late remote completion must not turn the timeout into a successful outcome
        // or start another command. The caller must hold mute after this exception.
        remote.SetResult(true);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public async Task CallerCancellationStaysCancellationAndNeverDispatchesAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var remote = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = BoundedMediaCommand.InvokeAsync(_ => remote.Task, "Play", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        remote.SetResult(true);
    }

    [Fact]
    public async Task ExplicitRemoteRejectionRemainsFalse()
    {
        Assert.False(await BoundedMediaCommand.InvokeAsync(_ => Task.FromResult(false), "Pause", CancellationToken.None));
    }

    [Fact]
    public async Task AlreadyCanceledCallerNeverSendsCommand()
    {
        var dispatches = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedMediaCommand.InvokeAsync(
            _ => { dispatches++; return Task.FromResult(true); }, "Previous", cancellation.Token));
        Assert.Equal(0, dispatches);
    }
}
