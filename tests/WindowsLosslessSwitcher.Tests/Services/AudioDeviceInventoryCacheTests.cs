using System.Diagnostics;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AudioDeviceInventoryCacheTests
{
    private static AudioDeviceInfo Dac => new("dac", "TOPPING USB DAC", true)
        { PhysicalUsbDeviceId = "USB\\VID_152A&PID_8750\\test-unit", EndpointFormFactor = 1 };

    [Fact]
    public void StableHardware_120FreshOperationChecksDoNotRepeatPhysicalEnumeration()
    {
        var endpoint = new InventoryEndpoint();
        using var guard = new DacOnlyAudioEndpointController(endpoint, endpoint);
        Assert.True(guard.EnsureReady().IsReady);
        var stateVersion = guard.StateVersion;
        for (var i = 0; i < 120; i++) Assert.True(guard.IsReadyForDevice("dac"));
        Assert.Equal(1, endpoint.InventoryReads);
        Assert.True(endpoint.DefaultReads >= 242); // defaults are still read, not blindly cached
        Assert.Equal(stateVersion, guard.StateVersion);
    }

    [Fact]
    public void DefaultsChangedWithoutNotification_AreStillCheckedBeforeAudio()
    {
        var endpoint = new InventoryEndpoint();
        using var guard = new DacOnlyAudioEndpointController(endpoint, endpoint);
        Assert.True(guard.EnsureReady().IsReady);
        endpoint.Multimedia = "monitor";
        Assert.False(guard.TryApplyFormat("dac", new(96000, 24, 2), out _, out _));
        Assert.Equal(0, endpoint.Applies);
        Assert.Equal(1, endpoint.InventoryReads);
    }

    [Fact]
    public async Task SlowCensusCannotBlockMemoryOnlyUiStateOrPublishAStaleReadyResult()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var endpoint = new InventoryEndpoint();
        using var guard = new DacOnlyAudioEndpointController(endpoint, endpoint);
        Assert.True(guard.EnsureReady().IsReady);
        endpoint.OnInventory = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        endpoint.Cache.InvalidateTopology();
        var pending = Task.Run(guard.EnsureReady);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
            {
                Assert.False(guard.CurrentState.IsReady);
                Assert.False(guard.IsSnapshotReady);
                _ = guard.StateVersion;
            }
            Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500), "UI state waited for the blocked census.");
            endpoint.Cache.InvalidateTopology(); // invalidate the blocked read a second time
        }
        finally { release.Set(); }
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(2))).IsReady);
        Assert.False(guard.IsSnapshotReady);
        endpoint.OnInventory = null;
        Assert.True(guard.EnsureReady().IsReady);
    }

    [Fact]
    public void DefaultNotificationInvalidatesUiImmediatelyWithoutRepeatingPhysicalEnumeration()
    {
        var endpoint = new InventoryEndpoint();
        using var guard = new DacOnlyAudioEndpointController(endpoint, endpoint);
        Assert.True(guard.EnsureReady().IsReady);
        var version = guard.DeviceChangeVersion;
        endpoint.Cache.InvalidateRouting();
        Assert.True(guard.DeviceChangeVersion > version);
        Assert.False(guard.IsSnapshotReady);
        Assert.True(guard.EnsureReady().IsReady);
        Assert.Equal(1, endpoint.InventoryReads);
    }

    [Fact]
    public void InactiveEndpointCannotBeAuthorizedByCachedIdentity()
    {
        var endpoint = new InventoryEndpoint();
        using var guard = new DacOnlyAudioEndpointController(endpoint, endpoint);
        Assert.True(guard.EnsureReady().IsReady);
        endpoint.Active = false;
        Assert.False(guard.IsReadyForDevice("dac"));
        Assert.False(guard.TrySetMasterMute("dac", true));
    }

    [Fact]
    public void RefreshBeforeExpiryKeepsStableGeneration_ExpiredUnrefreshedMetadataFailsClosed()
    {
        long now = 0;
        var reads = 0;
        var cache = new AudioDeviceInventoryCache(() => { reads++; return [Dac]; }, () => now, 20, 100);
        cache.Refresh();
        var generation = cache.ChangeVersion;
        now = 21;
        cache.Refresh();
        Assert.Equal(2, reads);
        Assert.Equal(generation, cache.ChangeVersion);
        Assert.Single(cache.Devices);
        now = 122;
        Assert.False(cache.IsCurrent);
        Assert.Empty(cache.Devices);
        Assert.Equal(2, reads); // expired readers never start a slow hardware query
    }

    [Fact]
    public void MissedNotification_PhysicalReplacementAdvancesGenerationButDisplayChangesDoNot()
    {
        long now = 0;
        IReadOnlyList<AudioDeviceInfo> devices = [Dac];
        var cache = new AudioDeviceInventoryCache(() => devices, () => now, 20, 100);
        cache.Refresh();
        var generation = cache.ChangeVersion;
        devices = [Dac with { FriendlyName = "Renamed by user", IsDefault = false }];
        now = 21;
        cache.Refresh();
        Assert.Equal(generation, cache.ChangeVersion);
        devices = [Dac with { PhysicalUsbDeviceId = "USB\\VID_152A&PID_8750\\replacement-unit" }];
        now = 42;
        cache.Refresh();
        Assert.True(cache.ChangeVersion > generation);
        Assert.EndsWith("replacement-unit", Assert.Single(cache.Devices).PhysicalUsbDeviceId);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareOnePhysicalEnumeration()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var cache = new AudioDeviceInventoryCache(() =>
        {
            Interlocked.Increment(ref reads);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return [Dac];
        });
        var first = Task.Run(() => cache.Refresh());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var followers = Enumerable.Range(0, 5).Select(_ => Task.Run(() => cache.Refresh())).ToArray();
        Assert.Empty(cache.Devices);
        release.Set();
        await Task.WhenAll(followers.Append(first)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, reads);
        Assert.Single(cache.Devices);
    }

    private sealed class InventoryEndpoint : IAudioEndpointController, IDefaultAudioDeviceRouter, IAudioDeviceInventory
    {
        public InventoryEndpoint() => Cache = new(() => { InventoryReads++; OnInventory?.Invoke(); return [Dac]; });
        public AudioDeviceInventoryCache Cache { get; }
        public int InventoryReads { get; private set; }
        public int DefaultReads { get; private set; }
        public int Applies { get; private set; }
        public string Multimedia { get; set; } = "dac";
        public bool Active { get; set; } = true;
        public Action? OnInventory { get; set; }
        public long DeviceChangeVersion => Cache.ChangeVersion;
        public bool IsDeviceInventoryCurrent => Cache.IsCurrent;
        public void RefreshDeviceInventory(bool forceRefresh = false) => Cache.Refresh(forceRefresh);
        public bool IsDeviceActive(string id) => Active && id == "dac";
        public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => Cache.Devices;
        public AudioDeviceInfo? GetDefaultRenderDevice() => Dac;
        public string? GetDefaultDeviceId(AudioOutputRole role) { DefaultReads++; return role == AudioOutputRole.Multimedia ? Multimedia : "dac"; }
        public bool TrySetDefaultDeviceId(string id, AudioOutputRole role, out string failureReason) { failureReason = "Unused by these tests"; return false; }
        public AudioFormatCandidate? GetCurrentDeviceFormat(string id) => new(44100, 24, 2);
        public AudioFormatCandidate? GetCurrentMixFormat(string id) => new(44100, 24, 2);
        public IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string id, bool forceRefresh = false) => [new(44100, 24, 2)];
        public string? DescribeSupportedFormat(string id, AudioFormatCandidate format) => null;
        public string? GetLastApplyDiagnostics(string id) => null;
        public string? GetLastProbeDiagnostics(string id) => null;
        public float? GetMasterPeakValue(string id) => 0;
        public bool? IsProcessSessionActive(string id, string processName) => false;
        public float? GetProcessSessionPeak(string id, string processName) => 0;
        public bool? GetMasterMute(string id) => false;
        public bool TrySetMasterMute(string id, bool muted) => true;
        public bool TryApplyFormat(string id, AudioFormatCandidate format, out AudioFormatCandidate? verifiedDeviceFormat, out string failureReason)
        { Applies++; verifiedDeviceFormat = format; failureReason = ""; return true; }
    }
}
