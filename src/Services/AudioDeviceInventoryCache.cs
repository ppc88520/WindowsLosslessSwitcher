using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Generation-stamped metadata cache. Native notifications only invalidate counters; a single
/// background refresh reads PnP data. Readers never wait for that refresh or invoke its loader.
/// </summary>
internal sealed class AudioDeviceInventoryCache(
    Func<IReadOnlyList<AudioDeviceInfo>> load,
    Func<long>? clock = null,
    long refreshAfterMilliseconds = 30_000,
    long maximumAgeMilliseconds = 120_000)
{
    private sealed record Snapshot(long TopologyVersion, long ObservedAt, IReadOnlyList<AudioDeviceInfo> Devices);
    private readonly object _refreshSync = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private Snapshot? _snapshot;
    private long _topologyVersion;
    private long _changeVersion;

    public long ChangeVersion => Volatile.Read(ref _changeVersion);
    public long TopologyVersion => Volatile.Read(ref _topologyVersion);
    public bool IsCurrent
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot is not null && snapshot.TopologyVersion == Volatile.Read(ref _topologyVersion) &&
                _clock() - snapshot.ObservedAt <= maximumAgeMilliseconds;
        }
    }

    public IReadOnlyList<AudioDeviceInfo> Devices
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot is not null && snapshot.TopologyVersion == Volatile.Read(ref _topologyVersion) &&
                _clock() - snapshot.ObservedAt <= maximumAgeMilliseconds ? snapshot.Devices : [];
        }
    }

    public void InvalidateTopology()
    {
        Interlocked.Increment(ref _topologyVersion);
        Interlocked.Increment(ref _changeVersion);
    }

    public void InvalidateRouting() => Interlocked.Increment(ref _changeVersion);

    public void Refresh(bool forceRefresh = false)
    {
        lock (_refreshSync)
        {
            var previous = Volatile.Read(ref _snapshot);
            var topology = Volatile.Read(ref _topologyVersion);
            if (!forceRefresh && previous is not null && previous.TopologyVersion == topology &&
                _clock() - previous.ObservedAt < refreshAfterMilliseconds) return;
            IReadOnlyList<AudioDeviceInfo> devices;
            try { devices = load().ToArray(); }
            catch { InvalidateTopology(); Volatile.Write(ref _snapshot, null); return; }
            // A delayed old enumeration must not restore readiness after a hotplug event.
            if (topology != Volatile.Read(ref _topologyVersion)) return;
            if (previous is not null && previous.TopologyVersion == topology && !SameHardware(previous.Devices, devices))
            {
                // Repair a missed notification. Changes to default/friendly-name display
                // fields do not constitute a new physical device or invalidate active work.
                if (Interlocked.CompareExchange(ref _topologyVersion, topology + 1, topology) != topology) return;
                topology++;
                Interlocked.Increment(ref _changeVersion);
            }
            Volatile.Write(ref _snapshot, new Snapshot(topology, _clock(), devices));
        }
    }

    private static bool SameHardware(IReadOnlyList<AudioDeviceInfo> previous, IReadOnlyList<AudioDeviceInfo> current)
    {
        if (previous.Count != current.Count) return false;
        var oldDevices = previous.OrderBy(device => device.Id, StringComparer.OrdinalIgnoreCase);
        var newDevices = current.OrderBy(device => device.Id, StringComparer.OrdinalIgnoreCase);
        return oldDevices.Zip(newDevices).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.PhysicalUsbDeviceId, pair.Second.PhysicalUsbDeviceId, StringComparison.OrdinalIgnoreCase) &&
            pair.First.EndpointFormFactor == pair.Second.EndpointFormFactor);
    }
}
