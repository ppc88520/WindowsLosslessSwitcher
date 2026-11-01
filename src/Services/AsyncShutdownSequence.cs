namespace WindowsLosslessSwitcher.Services;

/// <summary>Completes native cleanup on a worker before requesting application shutdown.</summary>
public static class AsyncShutdownSequence
{
    public static async Task RunAsync(Func<Task> cleanup, Action shutdown)
    {
        await Task.Run(cleanup);
        shutdown();
    }
}
