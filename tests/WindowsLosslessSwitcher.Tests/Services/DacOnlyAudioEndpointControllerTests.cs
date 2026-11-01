using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class DacOnlyAudioEndpointControllerTests
{
    private const string DacId = "dac-output";
    private const string MonitorId = "display-output";
    private static readonly AudioFormatCandidate Format = new(96000, 24, 2);
    private static AudioDeviceInfo Dac(string id = DacId) => new(id, "Speakers", false)
        { PhysicalUsbDeviceId = "USB\\VID_152A&PID_8750\\physical-unit", EndpointFormFactor = 1 };
    private static AudioDeviceInfo Monitor => new(MonitorId, "TOPPING USB DAC", true)
        { PhysicalUsbDeviceId = null, EndpointFormFactor = 9 };

    [Fact]
    public void DacLockPaused_RepeatedCensusesNeverReadHardwareRouteOrTouchAudio()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()] };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        guard.SetRoutingPaused(true);
        raw.ThrowInventory = true;
        for (var i = 0; i < 500; i++)
        {
            Assert.False(guard.EnsureReady().IsReady);
            Assert.False(guard.IsReady);
            Assert.False(guard.IsSnapshotReady);
        }
        AssertAllAudioDenied(guard, DacId);
        Assert.Empty(raw.Routes);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Fact]
    public void DacLockPaused_StopsAnInFlightDefaultSelectionBeforeTheSecondRole()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()] };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        raw.OnRoute = _ => guard.SetRoutingPaused(true);
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Single(raw.Routes);
        Assert.Equal(DacId, raw.ConsoleDefault);
        Assert.Equal(MonitorId, raw.MultimediaDefault);
        for (var i = 0; i < 20; i++) guard.EnsureReady();
        Assert.Single(raw.Routes);
        raw.OnRoute = null;
        guard.SetRoutingPaused(false);
        Assert.False(guard.CurrentState.IsReady);
        Assert.True(guard.EnsureReady().IsReady);
        Assert.Equal(2, raw.Routes.Count);
        Assert.True(guard.IsSnapshotReady);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Fact]
    public void DacLockPaused_CleanupReleasesOnlyItsOwnedMuteOnTheSameDac()
    {
        var raw = ReadyEndpoint();
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.True(guard.EnsureReady().IsReady);
        Assert.True(guard.TrySetMasterMute(DacId, true));
        guard.SetRoutingPaused(true);
        raw.ConsoleDefault = MonitorId;
        raw.MultimediaDefault = MonitorId;
        Assert.False(guard.TrySetMasterMute(MonitorId, false));
        Assert.True(guard.TrySetMasterMute(DacId, false));
        Assert.False(guard.TrySetMasterMute(DacId, false));
        Assert.Equal(2, raw.AudioCalls);
        Assert.Equal(0, raw.MonitorAudioCalls);
        Assert.Empty(raw.Routes);
    }

    [Fact]
    public void MissingDac_NeverRoutesOrTouchesMonitorAudio()
    {
        var raw = new FakeEndpoint { Devices = [Monitor] };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        AssertAllAudioDenied(guard, MonitorId);
        Assert.Equal(0, raw.AudioCalls);
        Assert.Empty(raw.Routes);
    }

    [Fact]
    public void UnknownUsbAudio_IsBlockedEvenWithDacFriendlyName()
    {
        var raw = new FakeEndpoint { Devices = [Dac() with { PhysicalUsbDeviceId = "USB\\VID_1234&PID_5678\\unit", FriendlyName = "External DAC" }] };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        AssertAllAudioDenied(guard, DacId);
        Assert.Empty(raw.Routes);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Fact]
    public void ConnectedDac_IsSelectedBeforeAudioWork_OnlyPlaybackRolesChange()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()] };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.IsReady);
        Assert.Equal(0, raw.AudioCalls);
        Assert.Empty(guard.GetSupportedFormats(DacId, true));
        var state = guard.EnsureReady();
        Assert.True(state.IsReady);
        Assert.Equal(DacId, state.Device?.Id);
        Assert.Equal(new[] { (DacId, AudioOutputRole.Console), (DacId, AudioOutputRole.Multimedia) }, raw.Routes);
        Assert.Equal(Format, Assert.Single(guard.GetSupportedFormats(DacId, true)));
        Assert.True(guard.TryApplyFormat(DacId, Format, out var applied, out var failure));
        Assert.Equal(Format, applied);
        Assert.Empty(failure);
        Assert.True(guard.TrySetMasterMute(DacId, true));
        Assert.True(guard.TrySetMasterMute(DacId, false));
        Assert.True(raw.AudioCalls >= 4);
        Assert.Equal(0, raw.MonitorAudioCalls);
    }

    [Fact]
    public void AlreadyReady_DoesNotRouteAgain()
    {
        var raw = ReadyEndpoint();
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.True(guard.EnsureReady().IsReady);
        Assert.True(guard.EnsureReady().IsReady);
        Assert.True(guard.IsReadyForDevice(DacId));
        Assert.False(guard.IsReadyForDevice(MonitorId));
        Assert.Empty(raw.Routes);
    }

    [Fact]
    public void FailedRouting_StaysBlockedAndDoesNotRetryUntilExplicitRefresh()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()], RejectRole = AudioOutputRole.Console };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Single(raw.Routes);
        for (var i = 0; i < 5; i++) Assert.False(guard.EnsureReady().IsReady);
        Assert.Single(raw.Routes);
        AssertAllAudioDenied(guard, DacId, allowConnectedDacCleanup: true);
        guard.ResetRoutingAttempt();
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Equal(2, raw.Routes.Count);
    }

    [Fact]
    public void PartialRouting_DoesNotProbeApplyOrAutomaticallyRetry()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()], RejectRole = AudioOutputRole.Multimedia };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Equal(DacId, raw.ConsoleDefault);
        Assert.Equal(MonitorId, raw.MultimediaDefault);
        Assert.Equal(2, raw.Routes.Count);
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Equal(2, raw.Routes.Count);
        AssertAllAudioDenied(guard, DacId, allowConnectedDacCleanup: true);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Fact]
    public void ReportedRouteSuccess_WithoutActualDefaultChange_DoesNotAuthorizeAudio()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()], IgnoreSuccessfulRoute = true };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        AssertAllAudioDenied(guard, DacId, allowConnectedDacCleanup: true);
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Equal(2, raw.Routes.Count);
        // Delayed Windows propagation can be observed without issuing the command twice.
        raw.ConsoleDefault = DacId;
        raw.MultimediaDefault = DacId;
        Assert.True(guard.IsReady);
        Assert.True(guard.EnsureReady().IsReady);
        Assert.Equal(2, raw.Routes.Count);
    }

    [Fact]
    public void UnplugBetweenRoles_StopsBeforeSecondRoleAndAllAudioCalls()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()] };
        raw.OnRoute = role => { if (role == AudioOutputRole.Console) raw.Devices = [Monitor]; };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Single(raw.Routes);
        AssertAllAudioDenied(guard, DacId);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Fact]
    public void DefaultMovesToMonitorAfterReadiness_NoStaleAuthorization()
    {
        var raw = ReadyEndpoint();
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.True(guard.IsReady);
        Assert.True(guard.TrySetMasterMute(DacId, true));
        raw.ConsoleDefault = MonitorId;
        raw.MultimediaDefault = MonitorId;
        AssertAllAudioDenied(guard, DacId, allowConnectedDacCleanup: true);
        AssertAllAudioDenied(guard, MonitorId);
        Assert.Equal(1, raw.AudioCalls);
        Assert.True(guard.TrySetMasterMute(DacId, false));
        Assert.Equal(2, raw.AudioCalls); // Cleanup only, addressed to the original DAC.
        Assert.Equal(0, raw.MonitorAudioCalls);
        Assert.Empty(raw.Routes); // Read-only calls never route.
    }

    [Fact]
    public void CleanupCannotUnmuteUserMutedOrDifferentPhysicalDac()
    {
        var raw = ReadyEndpoint();
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.TrySetMasterMute(DacId, false)); // No mute owned by this instance.
        Assert.True(guard.TrySetMasterMute(DacId, true));
        raw.Devices = [Monitor, Dac() with { PhysicalUsbDeviceId = "USB\\VID_152A&PID_8750\\different-unit" }];
        Assert.False(guard.TrySetMasterMute(DacId, false));
        Assert.Equal(1, raw.AudioCalls);
    }

    [Fact]
    public void UnplugAfterReadiness_DeniesCachedTargetAndCleanup()
    {
        var raw = ReadyEndpoint();
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.True(guard.IsReadyForDevice(DacId));
        raw.Devices = [Monitor];
        AssertAllAudioDenied(guard, DacId);
        Assert.False(guard.IsReady);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Fact]
    public void MultipleDacsWithoutCurrentDacDefault_FailClosed()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac(), Dac("second-dac")] };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        Assert.Contains("multiple", guard.CurrentState.Detail);
        Assert.Empty(raw.Routes);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Fact]
    public void MultipleDacs_KeepExistingMultimediaDacRatherThanChoosingFirst()
    {
        var raw = new FakeEndpoint { Devices = [Dac(), Dac("second-dac")], MultimediaDefault = "second-dac" };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.True(guard.EnsureReady().IsReady);
        Assert.Equal("second-dac", guard.CurrentState.Device?.Id);
        Assert.Equal(("second-dac", AudioOutputRole.Console), Assert.Single(raw.Routes));
        Assert.False(guard.IsReadyForDevice(DacId));
        Assert.True(guard.IsReadyForDevice("second-dac"));
    }

    [Fact]
    public void FailedSnapshotMayRetryAfterConnectionChange()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()], RejectRole = AudioOutputRole.Console };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        raw.Devices = [Monitor];
        Assert.False(guard.EnsureReady().IsReady);
        raw.Devices = [Monitor, Dac() with { PhysicalUsbDeviceId = "USB\\VID_152A&PID_8750\\reconnected-unit" }];
        raw.RejectRole = null;
        Assert.True(guard.EnsureReady().IsReady);
        Assert.Equal(3, raw.Routes.Count);
    }

    [Fact]
    public void ObservedDisconnectAndSameDeviceReconnect_EnablesOneNewRoutingAttempt()
    {
        var raw = new FakeEndpoint { Devices = [Monitor, Dac()], RejectRole = AudioOutputRole.Console };
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.EnsureReady().IsReady);
        raw.Devices = [Monitor];
        Assert.False(guard.EnsureReady().IsReady);
        raw.Devices = [Monitor, Dac()];
        raw.RejectRole = null;
        Assert.True(guard.EnsureReady().IsReady);
        Assert.Equal(3, raw.Routes.Count);
    }

    [Fact]
    public void InventoryFailure_FailsClosedWithoutNativeAudioCalls()
    {
        var raw = ReadyEndpoint();
        raw.ThrowInventory = true;
        var guard = new DacOnlyAudioEndpointController(raw, raw);
        Assert.False(guard.IsReady);
        Assert.False(guard.EnsureReady().IsReady);
        AssertAllAudioDenied(guard, DacId);
        Assert.Equal(0, raw.AudioCalls);
    }

    [Theory]
    [InlineData(null, 1, false)]
    [InlineData("USB\\VID_152A&PID_8750\\unit", null, false)]
    [InlineData("USB\\VID_152A&PID_8750\\unit", 9, false)]
    [InlineData("USB\\VID_152A&PID_8750\\unit", 10, false)]
    [InlineData("USB\\VID_152A&PID_8750\\unit", 4, false)]
    [InlineData("ROOT\\VID_152A&PID_8750\\unit", 1, false)]
    [InlineData("TUSBAUDIO_ENUM\\VID_152A&PID_8750&KS\\unit", 1, false)]
    [InlineData("USB\\VID_152A&PID_8750&MI_00\\unit", 1, false)]
    [InlineData("USB\\VID_152A&PID_87500\\unit", 1, false)]
    [InlineData("USB\\VID_152A&PID_8750\\", 1, false)]
    [InlineData("USB\\VID_152A&PID_8750\\unit\\extra", 1, false)]
    [InlineData("USB\\VID_152A&PID_8750\\unit", 1, true)]
    [InlineData("USB\\VID_152A&PID_8750\\unit", 2, true)]
    [InlineData("usb\\vid_152a&pid_8750\\unit", 3, true)]
    [InlineData("USB\\VID_152A&PID_8750\\unit", 5, true)]
    public void HardwareRecognition_RequiresExactPhysicalDeviceAndKnownRenderFormFactor(string? hardwareId, int? formFactor, bool expected)
    {
        var device = new AudioDeviceInfo("endpoint", "TOPPING USB DAC", true)
            { PhysicalUsbDeviceId = hardwareId, EndpointFormFactor = formFactor };
        Assert.Equal(expected, DacDeviceIdentity.IsSupportedDac(device));
    }

    private static FakeEndpoint ReadyEndpoint() => new()
        { Devices = [Monitor, Dac()], ConsoleDefault = DacId, MultimediaDefault = DacId };

    private static void AssertAllAudioDenied(DacOnlyAudioEndpointController guard, string id, bool allowConnectedDacCleanup = false)
    {
        Assert.False(guard.IsReadyForDevice(id));
        Assert.Null(guard.GetCurrentDeviceFormat(id));
        Assert.Null(guard.GetCurrentMixFormat(id));
        Assert.Empty(guard.GetSupportedFormats(id, true));
        Assert.Null(guard.DescribeSupportedFormat(id, Format));
        Assert.Null(guard.GetLastApplyDiagnostics(id));
        Assert.Null(guard.GetLastProbeDiagnostics(id));
        Assert.Null(guard.GetMasterPeakValue(id));
        Assert.Null(guard.IsProcessSessionActive(id, "AppleMusic"));
        Assert.Null(guard.GetProcessSessionPeak(id, "AppleMusic"));
        Assert.Null(guard.GetMasterMute(id));
        Assert.False(guard.TrySetMasterMute(id, true));
        if (!allowConnectedDacCleanup) Assert.False(guard.TrySetMasterMute(id, false));
        Assert.False(guard.TryApplyFormat(id, Format, out var applied, out var failure));
        Assert.Null(applied);
        Assert.NotEmpty(failure);
    }

    private sealed class FakeEndpoint : IAudioEndpointController, IDefaultAudioDeviceRouter
    {
        public List<AudioDeviceInfo> Devices { get; set; } = [];
        public string ConsoleDefault { get; set; } = MonitorId;
        public string MultimediaDefault { get; set; } = MonitorId;
        public AudioOutputRole? RejectRole { get; set; }
        public bool IgnoreSuccessfulRoute { get; set; }
        public bool ThrowInventory { get; set; }
        public Action<AudioOutputRole>? OnRoute { get; set; }
        public List<(string, AudioOutputRole)> Routes { get; } = [];
        public int AudioCalls { get; private set; }
        public int MonitorAudioCalls { get; private set; }
        private void Audio(string id) { AudioCalls++; if (id == MonitorId) MonitorAudioCalls++; }
        public IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => ThrowInventory ? throw new InvalidOperationException() : Devices;
        public AudioDeviceInfo? GetDefaultRenderDevice() => Devices.FirstOrDefault(device => device.Id == MultimediaDefault);
        public string? GetDefaultDeviceId(AudioOutputRole role) => role == AudioOutputRole.Console ? ConsoleDefault : MultimediaDefault;
        public bool TrySetDefaultDeviceId(string id, AudioOutputRole role, out string failure)
        {
            Routes.Add((id, role));
            failure = role == RejectRole ? "Routing rejected." : string.Empty;
            if (role == RejectRole) return false;
            if (!IgnoreSuccessfulRoute) { if (role == AudioOutputRole.Console) ConsoleDefault = id; else MultimediaDefault = id; }
            OnRoute?.Invoke(role);
            return true;
        }
        public AudioFormatCandidate? GetCurrentDeviceFormat(string id) { Audio(id); return Format; }
        public AudioFormatCandidate? GetCurrentMixFormat(string id) { Audio(id); return Format; }
        public IReadOnlyList<AudioFormatCandidate> GetSupportedFormats(string id, bool forceRefresh = false) { Audio(id); return [Format]; }
        public string? DescribeSupportedFormat(string id, AudioFormatCandidate format) { Audio(id); return "Supported"; }
        public string? GetLastApplyDiagnostics(string id) { Audio(id); return "Applied"; }
        public string? GetLastProbeDiagnostics(string id) { Audio(id); return "Probed"; }
        public float? GetMasterPeakValue(string id) { Audio(id); return 0; }
        public bool? IsProcessSessionActive(string id, string processName) { Audio(id); return true; }
        public float? GetProcessSessionPeak(string id, string processName) { Audio(id); return 0; }
        public bool? GetMasterMute(string id) { Audio(id); return false; }
        public bool TrySetMasterMute(string id, bool muted) { Audio(id); return true; }
        public bool TryApplyFormat(string id, AudioFormatCandidate format, out AudioFormatCandidate? applied, out string failure)
        { Audio(id); applied = format; failure = string.Empty; return true; }
    }
}
