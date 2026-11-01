using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class BackgroundUiRefreshTests
{
    [Fact]
    public Task DispatcherButtonRemainsClickableWhileNativeQueryIsBlocked() => OnDispatcher(async dispatcher =>
    {
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        var clicked = Completion();
        var appliedOnDispatcher = false;
        var queryThread = 0;
        using var pump = new BackgroundUiRefresh<int>(_ =>
        {
            queryThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Test driver was not released.");
            return 42;
        }, _ => appliedOnDispatcher = dispatcher.CheckAccess());
        var refresh = pump.RequestAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var button = new Button();
            button.Click += (_, _) => clicked.TrySetResult();
            _ = dispatcher.BeginInvoke(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await clicked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(refresh.IsCompleted);
            Assert.NotEqual(Environment.CurrentManagedThreadId, queryThread);
        }
        finally { release.Set(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(appliedOnDispatcher);
    });

    [Fact]
    public Task ConcurrentRequestsCollapseAndPreserveOneExplicitForcedRefresh() => OnDispatcher(async dispatcher =>
    {
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        var calls = 0;
        var forced = new List<bool>();
        var applied = new List<int>();
        using var pump = new BackgroundUiRefresh<int>(force =>
        {
            var call = Interlocked.Increment(ref calls);
            forced.Add(force);
            if (call == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
            }
            return call;
        }, applied.Add);
        var first = pump.RequestAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 20; i++) _ = pump.RequestAsync();
        var newest = pump.RequestAsync(forceRefresh: true);
        release.Set();
        await newest.WaitAsync(TimeSpan.FromSeconds(3));
        await first;
        Assert.Equal(2, calls);
        Assert.Equal(new[] { false, true }, forced);
        Assert.Equal(new[] { 2 }, applied);
    });

    [Fact]
    public Task RepeatedNormalPollsDoNotStarveASlowValidResult() => OnDispatcher(async dispatcher =>
    {
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        var calls = 0;
        var applied = new List<int>();
        using var pump = new BackgroundUiRefresh<int>(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
            }
            return call;
        }, applied.Add);
        var initial = pump.RequestAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 20; i++) _ = pump.RequestAsync();
        release.Set();
        await initial.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 1, 2 }, applied);
        Assert.Equal(2, calls);
    });

    [Fact]
    public Task DisposalDropsAQueryResultWithoutWaitingForTheDriver() => OnDispatcher(async dispatcher =>
    {
        using var release = new ManualResetEventSlim();
        var entered = Completion();
        var exited = Completion();
        var applied = false;
        var pump = new BackgroundUiRefresh<int>(_ =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(8));
            exited.TrySetResult();
            return 1;
        }, _ => applied = true);
        var pending = pump.RequestAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        pump.Dispose();
        Assert.True(pending.IsCompleted);
        var drain = pump.DrainAsync();
        Assert.False(drain.IsCompleted);
        release.Set();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await drain.WaitAsync(TimeSpan.FromSeconds(3));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(applied);
    });

    [Fact]
    public Task AFailedNativeReadIsReportedOnTheDispatcherAndAFollowupCanRecover() => OnDispatcher(async dispatcher =>
    {
        var calls = 0;
        var errorOnUi = false;
        var value = 0;
        using var pump = new BackgroundUiRefresh<int>(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Driver unavailable");
            return 2;
        }, result => value = result, onError: _ => errorOnUi = dispatcher.CheckAccess());
        await pump.RequestAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(errorOnUi);
        await pump.RequestAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, value);
    });

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task OnDispatcher(Func<Dispatcher, Task> action)
    {
        var completion = Completion();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(dispatcher); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Offline responsive UI test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
