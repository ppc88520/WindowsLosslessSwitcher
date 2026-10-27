namespace WindowsLosslessSwitcher.Models;

public enum UserPlaybackAction { Play, Pause, Previous, Next }

/// <summary>Fresh player capabilities combined with the switcher's safety state.</summary>
public sealed record UserPlaybackAvailability(
    MediaTransportPlaybackStatus PlaybackStatus,
    bool CanPlay,
    bool CanPause,
    bool CanPrevious,
    bool CanNext,
    bool IsBusy,
    bool IsSafetyHeld);

public sealed record UserPlaybackResult(bool Succeeded, string Message);
