namespace WindowsLosslessSwitcher.Models;

/// <summary>Fresh metadata, timeline, controls, and local navigation revision from one session.</summary>
public sealed record PreviousRestartSnapshot(
    string SessionId,
    TrackSnapshot Track,
    MediaTransportPlaybackStatus PlaybackStatus,
    TimeSpan? Position,
    bool CanPrevious,
    bool CanNext,
    bool CanPause,
    bool? IsShuffleActive,
    MediaTransportRepeatMode RepeatMode,
    long NavigationRevision);
