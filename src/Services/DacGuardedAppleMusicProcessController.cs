using WindowsLosslessSwitcher.Abstractions;

namespace WindowsLosslessSwitcher.Services;

/// <summary>Automatic player restarts cannot bypass a disconnected or unverified output.</summary>
public sealed class DacGuardedAppleMusicProcessController(
    IAppleMusicProcessController inner,
    Func<bool> canDispatch) : IAppleMusicProcessController
{
    public Task<bool> TryOpenAsync(CancellationToken token) => inner.TryOpenAsync(token);
    public Task<bool> TryRestartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return canDispatch() ? inner.TryRestartAsync(token) : Task.FromResult(false);
    }
}
