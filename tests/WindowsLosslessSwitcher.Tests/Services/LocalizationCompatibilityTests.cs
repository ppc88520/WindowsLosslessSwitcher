using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class LocalizationCompatibilityTests
{
    [Fact]
    public void ReadSavedLanguage_MissingPreferenceDoesNotCreateDirectoriesOrSettings()
    {
        var directory = NewTemporaryDirectory();
        var path = Path.Combine(directory, "settings.json");
        Assert.False(Directory.Exists(directory));
        Assert.Equal("en", LocalizationService.ReadSavedLanguage(path));
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("{\"uiLanguage\":\"zh-TW\",\"switchBitDepth\":false}", "zh-TW")]
    [InlineData("{\"uiLanguage\":\" ZH-tw \"}", "zh-TW")]
    [InlineData("{\"uiLanguage\":\"en\"}", "en")]
    [InlineData("{\"uiLanguage\":\"de\"}", "en")]
    [InlineData("{\"UiLanguage\":\"zh-TW\"}", "en")]
    [InlineData("{\"uiLanguage\":null}", "en")]
    [InlineData("{\"uiLanguage\":12}", "en")]
    [InlineData("{\"launchAtLogin\":true}", "en")]
    [InlineData("[]", "en")]
    [InlineData("null", "en")]
    [InlineData("invalid JSON", "en")]
    public void ReadSavedLanguage_IsReadOnlyAndToleratesExistingSettings(string json, string expected)
    {
        var directory = NewTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, json);
            var modifiedAt = File.GetLastWriteTimeUtc(path);
            Assert.Equal(expected, LocalizationService.ReadSavedLanguage(path));
            Assert.Equal(json, File.ReadAllText(path));
            Assert.Equal(modifiedAt, File.GetLastWriteTimeUtc(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LanguagePreference_DoesNotImportIsolatedAudioDefaults()
    {
        var settings = new AppSettings { UiLanguage = "zh-TW" };
        Assert.True(settings.SwitchBitDepth);
        Assert.True(settings.RestartAppleMusicOnPlaybackFailure);
        Assert.False(settings.RestartTrackAfterSampleRateChange);
        Assert.False(settings.LaunchAtLogin);
        Assert.Equal(DeviceSelectionMode.FollowDefault, settings.DeviceSelectionMode);
    }

    [Theory]
    [InlineData(UpdateActionKind.DownloadAndPrepare, "Download update", "下載更新")]
    [InlineData(UpdateActionKind.RestartToApply, "Restart to apply", "重新啟動並套用")]
    [InlineData(UpdateActionKind.OpenPortableDownload, "Open portable download", "開啟可攜版下載")]
    public void NormalUpdateActionsStayAvailableAcrossLanguageChanges(UpdateActionKind action, string english, string chinese)
    {
        var localizer = new LocalizationService();
        var viewModel = new MainWindowViewModel(localizer);
        try
        {
            viewModel.UpdateAppVersion(new UpdateStatusSnapshot("1.0.0", "Version 1.1.0 is available for download.",
                action, english, true, true, true, false, action == UpdateActionKind.OpenPortableDownload, "1.1.0"));
            Assert.Equal(english, viewModel.UpdatePrimaryActionText);
            Assert.True(viewModel.CanCheckForUpdates);
            Assert.True(viewModel.RunUpdatePrimaryActionCommand.CanExecute(null));

            viewModel.UiLanguage = "zh-TW";
            Assert.Equal(chinese, viewModel.UpdatePrimaryActionText);
            Assert.Equal("版本 1.1.0 已可下載。", viewModel.UpdateStatusText);
            Assert.Equal("有可用更新", viewModel.UpdateBadgeText);
            Assert.True(viewModel.CanCheckForUpdates);
            Assert.True(viewModel.RunUpdatePrimaryActionCommand.CanExecute(null));

            viewModel.UiLanguage = "en";
            Assert.Equal(english, viewModel.UpdatePrimaryActionText);
            Assert.Equal("Version 1.1.0 is available for download.", viewModel.UpdateStatusText);
        }
        finally
        {
            viewModel.DetachLocalization();
            viewModel.Playback.Dispose();
        }
    }

    [Fact]
    public void PortableUpdateKeepsReleaseNavigationAndTranslatesConfigurationInstructions()
    {
        var localizer = new LocalizationService();
        var viewModel = new MainWindowViewModel(localizer);
        try
        {
            viewModel.UpdateAppVersion(new UpdateStatusSnapshot("1.0.0",
                "Portable package detected. Check GitHub Releases to download newer portable packages.",
                UpdateActionKind.OpenReleasesPage, "Open releases", true, false, true, false, true));
            viewModel.UiLanguage = "zh-TW";
            Assert.True(viewModel.CanOpenReleasesPage);
            Assert.False(viewModel.HasUpdatePrimaryAction);
            Assert.Equal("偵測到可攜版。請到 GitHub Releases 下載較新的可攜套件。", viewModel.UpdateStatusText);

            viewModel.UpdateStatusText = "Updates are not configured yet. " + ReleaseRepositoryOptions.ConfigurationInstructions;
            Assert.DoesNotContain("Set WlsGitHubOwner", viewModel.UpdateStatusText, StringComparison.Ordinal);
            Assert.Contains("WLS_GITHUB_OWNER", viewModel.UpdateStatusText, StringComparison.Ordinal);
        }
        finally
        {
            viewModel.DetachLocalization();
            viewModel.Playback.Dispose();
        }
    }

    private static string NewTemporaryDirectory() => Path.Combine(Path.GetTempPath(),
        "WindowsLosslessSwitcher.LocalizationCompatibilityTests", Guid.NewGuid().ToString("N"));
}
