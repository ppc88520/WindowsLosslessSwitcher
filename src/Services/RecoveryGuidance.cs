using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed record RecoveryAdvice(RecoveryCategory Category, string Title, string Message, bool IsFailure)
{
    public static RecoveryAdvice From(SwitchingStatus status)
    {
        var category = status.Recovery;
        if (category == RecoveryCategory.None)
        {
            category = status.ResolverStatusText switch
            {
                "Switching: Waiting for user playback" => RecoveryCategory.WaitingForPlayback,
                "Switching: Restart unavailable" => RecoveryCategory.PreviousUnavailable,
                "Switching: Manual recovery needed" => RecoveryCategory.AudioRecoveryFailed,
                "Resolver: No target device" => RecoveryCategory.DeviceUnavailable,
                "Resolver: Failed" or "Resolver: Error" => RecoveryCategory.UnsafeState,
                _ => RecoveryCategory.None,
            };
            if (category == RecoveryCategory.None && status.ResolverStatusText.StartsWith("Resolver: ") &&
                status.AppliedFormat is null && !string.IsNullOrWhiteSpace(status.FailureReason))
                category = RecoveryCategory.UnsafeState;
        }
        return category switch
        {
            RecoveryCategory.WaitingForPlayback => new(category, "Waiting for you to play", "Playback is paused and the device format is unchanged. Press Play in Apple Music to continue.", false),
            RecoveryCategory.PreviousUnavailable => new(category, "Cannot safely restart this track", "No navigation was sent. Select a different track to continue; a restart is usually unnecessary.", true),
            RecoveryCategory.AudioRecoveryFailed => new(category, "Audio recovery stopped", "Automatic recovery has stopped for this track. Select another track; if several tracks remain silent, manually restart Apple Music and the tool.", true),
            RecoveryCategory.DeviceUnavailable => new(category, "Output device unavailable", "Check the DAC connection and selected output device. Restarting cannot reconnect a missing device.", true),
            RecoveryCategory.UnsupportedFormat => new(category, "Target format unavailable", "Check the supported and allowed formats. Restarting cannot add hardware format support.", true),
            RecoveryCategory.FormatApplyFailed or RecoveryCategory.FormatNotStable => new(category, "Sample-rate change not confirmed", "Check the output device and format first. If handling is held, pause Apple Music before restarting the tool. The player will not restart automatically.", true),
            RecoveryCategory.MetadataUnavailable => new(category, "Track format unavailable", "Check track metadata and the network connection, or select another track. A player restart is not known to fix this failure.", true),
            RecoveryCategory.UnsafeState => new(category, "Automatic handling stopped", "The track, command result, or session could not be verified safely. Pause Apple Music before restarting the tool. Restart both only if silence persists.", true),
            _ => new(RecoveryCategory.None, "", "", false),
        };
    }
}

/// <summary>One non-blocking notification per incident; normal waiting is never an error.</summary>
public sealed class RecoveryNotificationGate
{
    private readonly HashSet<string> _seen = [];

    public bool ShouldNotify(SwitchingStatus status)
    {
        var advice = RecoveryAdvice.From(status);
        if (!advice.IsFailure)
        {
            if (status.ResolverStatusText is "Switching: Playback restored" ||
                status.ResolverStatusText.StartsWith("Resolver: Detecting", StringComparison.Ordinal))
                _seen.Clear();
            return false;
        }
        return _seen.Add($"{status.Track?.UniqueKey}|{advice.Category}");
    }
}
