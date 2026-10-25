using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>Freshly verifies the current Apple Music item without changing playback.</summary>
public interface ITrackIdentityVerifier
{
    Task<bool> IsCurrentTrackAsync(TrackSnapshot expectedTrack, CancellationToken cancellationToken);
}
