using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Abstractions;

/// <summary>Separates explicit output routing from read-only checks before audio operations.</summary>
public interface IAudioOutputGuard
{
    AudioOutputGuardState EnsureReady();
    bool IsReady { get; }
    bool IsReadyForDevice(string deviceId);
    AudioOutputGuardState CurrentState { get; }
    void ResetRoutingAttempt();
    /// <summary>Memory-only readiness projection for UI; never initiates native work.</summary>
    bool IsSnapshotReady => CurrentState.IsReady;
    /// <summary>Memory-only version used to reject stale background UI projections.</summary>
    long StateVersion => 0;
    /// <summary>Native topology/default discontinuity token; stable metadata refresh does not change it.</summary>
    long DeviceChangeVersion => 0;
    /// <summary>Explicit user suspension; this setter is memory-only and never routes an endpoint.</summary>
    void SetRoutingPaused(bool paused) { }
    bool IsRoutingPaused => false;
    /// <summary>Worker-only identity refresh for owned-mute cleanup; never changes routing.</summary>
    void RefreshHardwareIdentityForCleanup() { }
}

public sealed record AudioOutputGuardState(bool IsReady, AudioDeviceInfo? Device, string Detail);

/// <summary>Only playback roles used by this application; communications is never changed.</summary>
public enum AudioOutputRole { Console = 0, Multimedia = 1 }

public interface IDefaultAudioDeviceRouter
{
    string? GetDefaultDeviceId(AudioOutputRole role);
    bool TrySetDefaultDeviceId(string deviceId, AudioOutputRole role, out string failureReason);
}

/// <summary>Hardware identity observations refreshed only by the background output observer.</summary>
public interface IAudioDeviceInventory
{
    long DeviceChangeVersion { get; }
    bool IsDeviceInventoryCurrent { get; }
    void RefreshDeviceInventory(bool forceRefresh = false);
    bool IsDeviceActive(string deviceId);
}
