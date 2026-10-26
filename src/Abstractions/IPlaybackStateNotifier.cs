using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>
/// Observes fresh transport state independently of track identity deduplication.
/// Consumers must verify the current track before issuing any playback command.
/// </summary>
public interface IPlaybackStateNotifier
{
    event EventHandler<MediaTransportState>? PlaybackStateChanged;
}
