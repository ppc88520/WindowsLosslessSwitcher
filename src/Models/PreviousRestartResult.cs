namespace WindowsLosslessSwitcher.Models;

/// <summary>
/// After accepted navigation, SafeToResume requires a verified original-track paused 0:00.
/// A safe preflight rejection can still return Restarted=false; it does not authorize a
/// coordinator that promises playback from zero to unmute at the old position.
/// </summary>
public sealed record PreviousRestartResult(bool Restarted, bool SafeToResume, string Detail);
