using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class DacTestIsolationTests
{
    [Fact]
    public void AcceptanceBuildHasIndependentDataAndHelperIdentity()
    {
        Assert.Equal("WindowsLosslessSwitcher.DacGuardTest", TestBuildProfile.DataFolderName);
        Assert.Equal(TestBuildProfile.DataFolderName, Path.GetFileName(AppDataPaths.RootDirectory));
        Assert.Equal(TestBuildProfile.DataFolderName, Path.GetFileName(AppDataPaths.LegacyRootDirectory));
        Assert.Equal(TestBuildProfile.DataFolderName, ManualRestartHandoff.ProfileIdentity);
        Assert.Equal(TestBuildProfile.DisplayName, ManualRestartHandoff.ApplicationName);
        Assert.Contains("DAC Guard TEST", TestBuildProfile.DisplayName, StringComparison.Ordinal);
        Assert.Equal("WindowsLosslessSwitcher.DacGuardTest", typeof(TestBuildProfile).Assembly.GetName().Name);
    }

    [Fact]
    public void FirstRunUsesKnownAcceptanceSettingsAndEnforcesNoAutomaticPlayerRestart()
    {
        var settings = new AppSettings { UiLanguage = "zh-TW", LaunchAtLogin = true };
        TestBuildProfile.ApplyFirstRunDefaults(settings, hasPersistedSettings: false);
        TestBuildProfile.EnforceIsolation(settings);
        Assert.Equal("en", settings.UiLanguage);
        Assert.False(settings.SwitchBitDepth);
        Assert.Equal(24, settings.DefaultBitDepth);
        Assert.True(settings.RestartTrackAfterSampleRateChange);
        Assert.False(settings.LaunchAtLogin);
        Assert.False(settings.RestartAppleMusicOnPlaybackFailure);
    }

    [Fact]
    public void PersistedLanguageAndAudioPreferencesRemainIndependentOfIsolation()
    {
        var settings = new AppSettings
        {
            UiLanguage = "zh-TW", SwitchBitDepth = true, DefaultBitDepth = 16,
            RestartTrackAfterSampleRateChange = false, LaunchAtLogin = true,
            RestartAppleMusicOnPlaybackFailure = true,
            DeviceSelectionMode = DeviceSelectionMode.PinnedDevice, PinnedDeviceId = "monitor",
        };
        TestBuildProfile.ApplyFirstRunDefaults(settings, hasPersistedSettings: true);
        TestBuildProfile.EnforceIsolation(settings);
        Assert.Equal("zh-TW", settings.UiLanguage);
        Assert.True(settings.SwitchBitDepth);
        Assert.Equal(16, settings.DefaultBitDepth);
        Assert.False(settings.RestartTrackAfterSampleRateChange);
        Assert.False(settings.LaunchAtLogin);
        Assert.False(settings.RestartAppleMusicOnPlaybackFailure);
        Assert.Equal(DeviceSelectionMode.FollowDefault, settings.DeviceSelectionMode);
        Assert.Null(settings.PinnedDeviceId);
    }

    [Fact]
    public async Task IsolatedUpdaterHasNoAvailableActions()
    {
        var updater = new IsolatedAppUpdater();
        await updater.InitializeAsync(CancellationToken.None);
        await updater.CheckForUpdatesAsync(true, CancellationToken.None);
        await updater.RunPrimaryActionAsync(CancellationToken.None);
        updater.OpenReleasesPage();
        Assert.False(updater.CurrentStatus.CanCheckForUpdates);
        Assert.False(updater.CurrentStatus.CanRunPrimaryAction);
        Assert.False(updater.CurrentStatus.CanOpenReleasesPage);
    }

    [Fact]
    public void DacSafetyBannerRetranslatesWithoutChangingAudioSettings()
    {
        var localizer = new LocalizationService();
        var model = new MainWindowViewModel(localizer);
        try
        {
            model.UpdateAudioOutputSafety(false, null, "Connect a supported DAC to enable audio switching.");
            var changed = new List<string?>();
            model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            model.UiLanguage = "zh-TW";
            Assert.Equal("等待支援的 DAC", model.AudioOutputSafetyTitleText);
            Assert.Equal("請連接支援的 DAC，才能啟用音訊格式切換。", model.AudioOutputSafetyDetailText);
            Assert.DoesNotContain(nameof(MainWindowViewModel.SelectedMode), changed);
            Assert.DoesNotContain(nameof(MainWindowViewModel.SelectedDeviceId), changed);
            Assert.DoesNotContain(nameof(MainWindowViewModel.SwitchBitDepth), changed);
            Assert.False(model.IsSelectedDeviceSelectionEnabled);
            Assert.False(model.IsSelectedDeviceSelectionVisible);

            model.UpdateAudioOutputSafety(true, "喇叭 (TOPPING USB DAC)", "Ready");
            Assert.Equal("DAC 輸出已就緒", model.AudioOutputSafetyTitleText);
            Assert.Equal("音訊格式切換僅限於 喇叭 (TOPPING USB DAC)。", model.AudioOutputSafetyDetailText);
            model.UiLanguage = "en";
            Assert.Equal("Audio switching is limited to 喇叭 (TOPPING USB DAC).", model.AudioOutputSafetyDetailText);
        }
        finally
        {
            model.DetachLocalization();
            model.Playback.Dispose();
        }
    }
}
