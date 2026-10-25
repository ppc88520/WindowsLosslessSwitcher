using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>Restarts only a verified current item using one guarded Previous request.</summary>
public interface IPreviousTrackRestarter
{
    Task<PreviousRestartResult> TryRestartWithPreviousAsync(
        TrackSnapshot expectedTrack,
        TrackSnapshot? precedingTrack,
        CancellationToken cancellationToken);
}
