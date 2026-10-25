namespace WindowsLosslessSwitcher.Services;

/// <summary>A timeout never means that a remotely queued playback command was rejected.</summary>
internal static class BoundedMediaCommand
{
    internal static async Task<bool> InvokeAsync(
        Func<CancellationToken, Task<bool>> dispatch,
        string command,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            var result = await dispatch(deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Apple Music {command} command timed out; it may still be queued. Automatic replay or navigation retry is unsafe.");
        }
    }
}
