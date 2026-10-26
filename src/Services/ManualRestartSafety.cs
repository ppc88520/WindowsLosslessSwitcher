using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public static class ManualRestartSafety
{
    public static async Task<bool> ConfirmPausedAsync(IMediaTransportController transport, CancellationToken token, bool noPlayerProcess = false)
    {
        token.ThrowIfCancellationRequested();
        var state = transport.GetPlaybackState();
        if (IsSafe(state) || (noPlayerProcess && state.PlaybackStatus == MediaTransportPlaybackStatus.Unknown)) return true;
        if (!state.IsPlaying || !await transport.TryPauseAsync(token)) return false;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        do
        {
            token.ThrowIfCancellationRequested();
            if (IsSafe(transport.GetPlaybackState())) return true;
            await Task.Delay(50, token);
        } while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    private static bool IsSafe(MediaTransportState state) => state.PlaybackStatus is
        MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed;
}
