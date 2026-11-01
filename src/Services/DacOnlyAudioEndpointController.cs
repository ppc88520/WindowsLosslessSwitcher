using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// DAC-only audio boundary. Physical metadata is refreshed on the observer worker, while
/// operation checks re-read targeted native defaults and activity without walking the PnP tree.
/// UI projections never acquire an I/O lock or invoke native work.
/// </summary>
public sealed class DacOnlyAudioEndpointController : IAudioEndpointController, IAudioOutputGuard, IDisposable
{
    private sealed record Observation(AudioOutputGuardState State, long NativeVersion, long CheckedAt);
    private readonly IAudioEndpointController _inner;
    private readonly IDefaultAudioDeviceRouter _router;
    private readonly IAudioDeviceInventory? _inventory;
    private readonly object _routingSync = new();
    private readonly object _publicationSync = new();
    private readonly object _muteSync = new();
    private Observation _observation = new(new(false, null, "DAC protection: checking the connected output."), 0, Environment.TickCount64);
    private long _publicationVersion;
    private string? _failedRoutingSnapshot;
    private int _forceInventoryRefresh;
    private int _disposed;
    private int _routingPaused;
    private long _permissionVersion;
    private readonly Dictionary<string, string> _ownedMutes = new(StringComparer.OrdinalIgnoreCase);

    public DacOnlyAudioEndpointController(IAudioEndpointController inner, IDefaultAudioDeviceRouter router)
    {
        _inner = inner;
        _router = router;
        _inventory = inner as IAudioDeviceInventory;
        if (inner is CoreAudioEndpointController native)
        {
            native.EnableCachedInventory();
            native.DacOperationAllowed = IsReadyForDevice;
            native.DacCleanupAllowed = CanReleaseOwnedMute;
        }
    }

    public long DeviceChangeVersion => (_inventory?.DeviceChangeVersion ?? 0) + Volatile.Read(ref _permissionVersion);
    public long StateVersion => DeviceChangeVersion + Volatile.Read(ref _publicationVersion);
    public bool IsSnapshotReady => CurrentState.IsReady;
    public AudioOutputGuardState CurrentState
    {
        get
        {
            if (IsRoutingPaused) return RoutingPausedState;
            var observed = Volatile.Read(ref _observation);
            if (observed.State.IsReady &&
                (Volatile.Read(ref _disposed) != 0 ||
                 observed.NativeVersion != DeviceChangeVersion ||
                 (_inventory is not null && !_inventory.IsDeviceInventoryCurrent) ||
                 Environment.TickCount64 - observed.CheckedAt > 2_000))
                return observed.State with { IsReady = false, Detail = "DAC protection: checking the connected output." };
            return observed.State;
        }
    }
    public bool IsReady => Observe().IsReady;
    public bool IsRoutingPaused => Volatile.Read(ref _routingPaused) != 0;
    private static AudioOutputGuardState RoutingPausedState =>
        new(false, null, "DAC lock is paused. Choose another output in Windows when you are ready.");

    public void SetRoutingPaused(bool paused)
    {
        if (Interlocked.Exchange(ref _routingPaused, paused ? 1 : 0) == (paused ? 1 : 0)) return;
        Interlocked.Increment(ref _permissionVersion);
        Interlocked.Increment(ref _publicationVersion);
    }

    public void RefreshHardwareIdentityForCleanup() => _inventory?.RefreshDeviceInventory();
    public void ResetRoutingAttempt()
    {
        Volatile.Write(ref _failedRoutingSnapshot, null);
        Interlocked.Exchange(ref _forceInventoryRefresh, 1);
    }

    public AudioOutputGuardState EnsureReady()
    {
        if (IsRoutingPaused) return RoutingPausedState;
        // Multiple queued UI/status requests never queue another slow census behind this one.
        // The caller is the observer worker; UI reads CurrentState/IsSnapshotReady instead.
        if (!Monitor.TryEnter(_routingSync)) return CurrentState;
        try
        {
            if (IsRoutingPaused) return RoutingPausedState;
            if (Volatile.Read(ref _disposed) != 0) return PublishBlocked();
            _inventory?.RefreshDeviceInventory(Interlocked.Exchange(ref _forceInventoryRefresh, 0) != 0);
            if (IsRoutingPaused) return RoutingPausedState;
            var observation = Observe();
            if (observation.IsReady) return observation;
            if (observation.Device is null)
            {
                if (Volatile.Read(ref _failedRoutingSnapshot) is { } failed && RoutingSnapshot() != failed)
                    Volatile.Write(ref _failedRoutingSnapshot, null);
                return observation;
            }
            var before = RoutingSnapshot();
            if (before == Volatile.Read(ref _failedRoutingSnapshot))
                return Publish(observation with { Detail = "DAC protection: output routing failed. Refresh devices to retry." });

            var id = observation.Device.Id;
            foreach (var role in new[] { AudioOutputRole.Console, AudioOutputRole.Multimedia })
            {
                if (IsRoutingPaused) return RoutingPausedState;
                if (!IsConnectedDac(id)) return RoutingFailed("The DAC was disconnected while selecting the output.");
                if (string.Equals(_router.GetDefaultDeviceId(role), id, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsRoutingPaused) return RoutingPausedState;
                if (!_router.TrySetDefaultDeviceId(id, role, out var failure))
                    return RoutingFailed("DAC protection: output routing failed. " + failure);
            }
            var after = Observe();
            if (!after.IsReady) return RoutingFailed("DAC protection: waiting for Windows to confirm the DAC as the default output.");
            Volatile.Write(ref _failedRoutingSnapshot, null);
            return after;
        }
        catch { return RoutingFailed("DAC protection: the output could not be verified. Switching is disabled."); }
        finally { Monitor.Exit(_routingSync); }
    }

    public bool IsReadyForDevice(string deviceId)
    {
        var state = Observe();
        return state.IsReady && CurrentState.IsReady && string.Equals(state.Device?.Id, deviceId, StringComparison.OrdinalIgnoreCase);
    }

    private AudioOutputGuardState Observe()
    {
        if (IsRoutingPaused) return RoutingPausedState;
        try
        {
            var version = DeviceChangeVersion;
            if (Volatile.Read(ref _disposed) != 0 || (_inventory is not null && !_inventory.IsDeviceInventoryCurrent))
                return PublishBlocked();
            var candidates = _inner.GetRenderDevices().Where(DacDeviceIdentity.IsSupportedDac).ToList();
            var multimedia = _router.GetDefaultDeviceId(AudioOutputRole.Multimedia);
            var console = _router.GetDefaultDeviceId(AudioOutputRole.Console);
            var target = candidates.FirstOrDefault(device => string.Equals(device.Id, multimedia, StringComparison.OrdinalIgnoreCase));
            if (target is null && candidates.Count == 1) target = candidates[0];
            if (version != DeviceChangeVersion || (_inventory is not null && !_inventory.IsDeviceInventoryCurrent)) return PublishBlocked();
            if (target is null)
                return Publish(new(false, null, candidates.Count == 0
                    ? "DAC protection: no supported DAC is connected. Switching is disabled."
                    : "DAC protection: multiple DACs are connected. Select the intended DAC as the Windows default output."), version);
            var ready = string.Equals(target.Id, multimedia, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target.Id, console, StringComparison.OrdinalIgnoreCase) &&
                (_inventory?.IsDeviceActive(target.Id) ?? true);
            if (version != DeviceChangeVersion || (_inventory is not null && !_inventory.IsDeviceInventoryCurrent)) return PublishBlocked();
            return Publish(new(ready, target, ready
                ? "DAC protection: ready. The verified DAC is the default output."
                : "DAC protection: the verified DAC must be the default output before switching."), version);
        }
        catch { return Publish(new(false, null, "DAC protection: the output could not be verified. Switching is disabled.")); }
    }

    private AudioOutputGuardState Publish(AudioOutputGuardState state, long? nativeVersion = null)
    {
        var version = nativeVersion ?? DeviceChangeVersion;
        lock (_publicationSync)
        {
            if (IsRoutingPaused) state = RoutingPausedState;
            if (state.IsReady && version != DeviceChangeVersion)
                state = state with { IsReady = false, Detail = "DAC protection: checking the connected output." };
            var previous = Volatile.Read(ref _observation);
            if (previous.State != state || previous.NativeVersion != version) Interlocked.Increment(ref _publicationVersion);
            Volatile.Write(ref _observation, new(state, version, Environment.TickCount64));
        }
        return state;
    }
    private AudioOutputGuardState PublishBlocked() => Publish(new(false, null, "DAC protection: checking the connected output."));

    private bool IsConnectedDac(string deviceId)
    {
        try
        {
            var version = DeviceChangeVersion;
            if (_inventory is not null && !_inventory.IsDeviceInventoryCurrent) return false;
            var connected = _inner.GetRenderDevices().Any(device =>
                string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase) && DacDeviceIdentity.IsSupportedDac(device));
            return connected && (_inventory?.IsDeviceActive(deviceId) ?? true) && version == DeviceChangeVersion;
        }
        catch { return false; }
    }

    private bool CanReleaseOwnedMute(string deviceId)
    {
        string? originalUsbId;
        lock (_muteSync) _ownedMutes.TryGetValue(deviceId, out originalUsbId);
        if (originalUsbId is null) return false;
        try
        {
            var version = DeviceChangeVersion;
            if (_inventory is not null && !_inventory.IsDeviceInventoryCurrent) return false;
            var connected = _inner.GetRenderDevices().Any(device =>
                string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(device.PhysicalUsbDeviceId, originalUsbId, StringComparison.OrdinalIgnoreCase) &&
                DacDeviceIdentity.IsSupportedDac(device));
            return connected && (_inventory?.IsDeviceActive(deviceId) ?? true) && version == DeviceChangeVersion;
        }
        catch { return false; }
    }

    private string RoutingSnapshot()
    {
        try
        {
            return string.Join("|", _inner.GetRenderDevices().OrderBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .Select(device => $"{device.Id}:{device.PhysicalUsbDeviceId}:{device.EndpointFormFactor}")) +
                "|console=" + _router.GetDefaultDeviceId(AudioOutputRole.Console) +
                "|multimedia=" + _router.GetDefaultDeviceId(AudioOutputRole.Multimedia) + "|change=" + DeviceChangeVersion;
        }
        catch { return "unreadable"; }
    }
    private AudioOutputGuardState RoutingFailed(string detail)
    {
        Volatile.Write(ref _failedRoutingSnapshot, RoutingSnapshot());
        return Publish(Observe() with { IsReady = false, Detail = detail });
    }

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => _inner.GetRenderDevices();
    public AudioDeviceInfo? GetDefaultRenderDevice() => _inner.GetDefaultRenderDevice();
    public AudioFormatCandidate? GetCurrentDeviceFormat(string id) => IsReadyForDevice(id) ? _inner.GetCurrentDeviceFormat(id) : null;
    public AudioFormatCandidate? GetCurrentMixFormat(string id) => IsReadyForDevice(id) ? _inner.GetCurrentMixFormat(id) : null;
    public IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string id, bool forceRefresh = false) =>
        IsReadyForDevice(id) ? _inner.GetSupportedFormats(id, forceRefresh) : [];
    public string? DescribeSupportedFormat(string id, AudioFormatCandidate format) => IsReadyForDevice(id) ? _inner.DescribeSupportedFormat(id, format) : null;
    public string? GetLastApplyDiagnostics(string id) => IsReadyForDevice(id) ? _inner.GetLastApplyDiagnostics(id) : null;
    public string? GetLastProbeDiagnostics(string id) => IsReadyForDevice(id) ? _inner.GetLastProbeDiagnostics(id) : null;
    public float? GetMasterPeakValue(string id) => IsReadyForDevice(id) ? _inner.GetMasterPeakValue(id) : null;
    public bool? IsProcessSessionActive(string id, string processName) => IsReadyForDevice(id) ? _inner.IsProcessSessionActive(id, processName) : null;
    public float? GetProcessSessionPeak(string id, string processName) => IsReadyForDevice(id) ? _inner.GetProcessSessionPeak(id, processName) : null;
    public bool? GetMasterMute(string id) => IsReadyForDevice(id) ? _inner.GetMasterMute(id) : null;
    public bool TrySetMasterMute(string id, bool muted)
    {
        if (muted)
        {
            var state = Observe();
            if (!state.IsReady || state.Device is not { PhysicalUsbDeviceId: { } identity } device ||
                !string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)) return false;
            if (!_inner.TrySetMasterMute(id, true)) return false;
            lock (_muteSync) _ownedMutes[id] = identity;
            return true;
        }
        if (!CanReleaseOwnedMute(id) || !_inner.TrySetMasterMute(id, false)) return false;
        lock (_muteSync) _ownedMutes.Remove(id);
        return true;
    }
    public bool TryApplyFormat(string id, AudioFormatCandidate format, out AudioFormatCandidate? verifiedDeviceFormat, out string failureReason)
    {
        verifiedDeviceFormat = null;
        if (!IsReadyForDevice(id)) { failureReason = CurrentState.Detail; return false; }
        return _inner.TryApplyFormat(id, format, out verifiedDeviceFormat, out failureReason);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        PublishBlocked();
        if (_inner is IDisposable disposable) disposable.Dispose();
    }
}
