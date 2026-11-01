using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>Checks the current output immediately before every command that can start or navigate audio.</summary>
public sealed class DacGuardedMediaTransportController(
    IMediaTransportController inner,
    Func<bool> canDispatch) : IMediaTransportController, IPlaybackStateNotifier, ITrackIdentityVerifier, IPreviousTrackRestarter
{
    public event EventHandler<MediaTransportState>? PlaybackStateChanged
    {
        add { if (inner is IPlaybackStateNotifier notifier) notifier.PlaybackStateChanged += value; }
        remove { if (inner is IPlaybackStateNotifier notifier) notifier.PlaybackStateChanged -= value; }
    }

    public MediaTransportState GetPlaybackState()
    {
        var state = inner.GetPlaybackState();
        return canDispatch() ? state : state with { CanPlay = false, CanNext = false, CanPrevious = false };
    }

    // Pause remains available to settle an already-running player safely, including after device loss.
    public Task<bool> TryPauseAsync(CancellationToken token) => inner.TryPauseAsync(token);
    public Task<bool> TryPlayAsync(CancellationToken token) => Dispatch(inner.TryPlayAsync, token);
    public Task<bool> TrySkipNextAsync(CancellationToken token) => Dispatch(inner.TrySkipNextAsync, token);
    public Task<bool> TrySkipPreviousAsync(CancellationToken token) => Dispatch(inner.TrySkipPreviousAsync, token);
    public Task<bool> TryChangePlaybackPositionAsync(TimeSpan position, CancellationToken token) =>
        Dispatch(ct => inner.TryChangePlaybackPositionAsync(position, ct), token);

    public Task<bool> TryTogglePlayPauseAsync(CancellationToken token) =>
        inner.GetPlaybackState().IsPlaying ? inner.TryPauseAsync(token) : TryPlayAsync(token);

    public async Task<bool> IsCurrentTrackAsync(TrackSnapshot track, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return canDispatch() && inner is ITrackIdentityVerifier verifier &&
            await verifier.IsCurrentTrackAsync(track, token) && canDispatch();
    }

    public async Task<PreviousRestartResult> TryRestartWithPreviousAsync(
        TrackSnapshot expectedTrack, TrackSnapshot? precedingTrack, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!canDispatch() || inner is not IPreviousTrackRestarter restarter)
            return new(false, false, "DAC output is not ready; no guarded restart was dispatched.", NavigationDispatched: false);
        var result = await restarter.TryRestartWithPreviousAsync(expectedTrack, precedingTrack, token);
        token.ThrowIfCancellationRequested();
        return canDispatch() ? result : new(false, false,
            "DAC output changed during the guarded restart. Playback remains suspended.",
            NavigationDispatched: result.NavigationDispatched);
    }

    private Task<bool> Dispatch(Func<CancellationToken, Task<bool>> command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return canDispatch() ? command(token) : Task.FromResult(false);
    }
}
