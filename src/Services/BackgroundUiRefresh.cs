namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Runs a single background query at a time and posts only the newest result to the UI.
/// Requests arriving during a query collapse into one follow-up; a forced refresh is never lost.
/// No native query or wait for its completion runs on the requesting thread.
/// </summary>
public sealed class BackgroundUiRefresh<T> : IDisposable
{
    private readonly Func<bool, T> _query;
    private readonly Action<T> _apply;
    private readonly Action<Exception>? _onError;
    private SynchronizationContext? _context;
    private readonly object _sync = new();
    private bool _running;
    private bool _pending;
    private bool _force;
    private bool _disposed;
    private long _revision;
    private Task _worker = Task.CompletedTask;
    private TaskCompletionSource _idle = NewCompletion();

    public BackgroundUiRefresh(Func<bool, T> query, Action<T> apply,
        SynchronizationContext? context = null, Action<Exception>? onError = null)
    {
        _query = query;
        _apply = apply;
        _context = context ?? SynchronizationContext.Current;
        _onError = onError;
        _idle.TrySetResult();
    }

    public Task RequestAsync(bool forceRefresh = false)
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            _pending = true;
            _force |= forceRefresh;
            // Timer/status requests are observations of the same device generation. They
            // must not starve a slow valid read by perpetually making it obsolete.
            if (forceRefresh) _revision++;
            if (!_running)
            {
                _running = true;
                _idle = NewCompletion();
                _worker = Task.Run(RunAsync);
            }
            return _idle.Task;
        }
    }

    public void BindContext(SynchronizationContext context)
    {
        lock (_sync) _context = context;
    }

    /// <summary>Called for an actual target/generation change, rather than every UI poll.</summary>
    public void Invalidate()
    {
        lock (_sync) _revision++;
    }

    /// <summary>
    /// Waits for the actual worker, including any in-flight native query and posted callback.
    /// Call Dispose first to stop new work; keep the UI dispatcher alive while awaiting this.
    /// </summary>
    public Task DrainAsync()
    {
        lock (_sync) return _worker;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            bool force;
            long revision;
            lock (_sync)
            {
                if (_disposed) { Complete(); return; }
                force = _force;
                revision = _revision;
                _force = false;
                _pending = false;
            }
            try
            {
                var result = _query(force);
                await PostAsync(() =>
                {
                    lock (_sync)
                    {
                        if (!_disposed && revision == _revision) _apply(result);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await PostAsync(() =>
                {
                    lock (_sync)
                    {
                        if (!_disposed && revision == _revision) _onError?.Invoke(ex);
                    }
                }).ConfigureAwait(false);
            }
            lock (_sync)
            {
                if (_disposed || !_pending) { Complete(); return; }
            }
        }
    }

    private Task PostAsync(Action action)
    {
        var context = Volatile.Read(ref _context);
        if (context is null) { action(); return Task.CompletedTask; }
        var completion = NewCompletion();
        context.Post(_ =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, null);
        return completion.Task;
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Complete() { _running = false; _idle.TrySetResult(); }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _pending = false;
            _revision++;
            _idle.TrySetResult();
        }
    }
}
