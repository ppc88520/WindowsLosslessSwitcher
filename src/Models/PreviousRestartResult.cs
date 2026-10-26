namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// After accepted navigation, SafeToResume requires a verified original-track paused 0:00.
/// A safe preflight rejection can still return Restarted=false; it does not authorize a
/// coordinator that promises playback from zero to unmute at the old position.
/// </summary>
public enum PreviousRestartFailureKind
{
    Unspecified,
    PreviousUnavailable,
}

public sealed record PreviousRestartResult(
    bool Restarted,
    bool SafeToResume,
    string Detail,
    PreviousRestartFailureKind FailureKind = PreviousRestartFailureKind.Unspecified,
    bool NavigationDispatched = true)
{
    // Existing/unclassified results default to an uncertain dispatch state. Only an
    // explicitly verified no-navigation capability failure can be cleared by a new track.
    public bool CanRecoverOnNewTrack => !Restarted && SafeToResume &&
        FailureKind == PreviousRestartFailureKind.PreviousUnavailable && !NavigationDispatched;
}
