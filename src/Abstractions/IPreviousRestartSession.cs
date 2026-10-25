using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>A captured media session; implementations must never retarget its commands.</summary>
public interface IPreviousRestartSession
{
    Task<PreviousRestartSnapshot?> ReadAsync(CancellationToken cancellationToken);
    Task<bool> PreviousAsync(CancellationToken cancellationToken);
    Task<bool> NextAsync(CancellationToken cancellationToken);
    Task<bool> PauseAsync(CancellationToken cancellationToken);
    /// <summary>One captured-session activation while the coordinator keeps the endpoint muted.</summary>
    Task<bool> PlayAsync(CancellationToken cancellationToken);
}
