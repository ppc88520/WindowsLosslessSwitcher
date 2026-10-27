using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using WindowsLosslessSwitcher.ViewModels;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class LocalizationTests
{
    private static LocalizationService Localizer() => new(new Dictionary<string, string>
    {
        ["Nothing playing"] = "目前沒有播放內容",
        ["Watching Apple Music media session"] = "正在監測 Apple Music 播放狀態",
        ["Playback restored"] = "播放已恢復",
        ["was {0}"] = "先前為 {0}",
        ["Switched to {0} for “{1}”"] = "已為「{1}」切換至 {0}",
        ["Detected from the Windows region: {0}"] = "依 Windows 地區偵測：{0}",
        ["Override: “{0}”"] = "手動指定：「{0}」",
    });

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("en", "en")]
    [InlineData("fr", "en")]
    [InlineData("zh-CN", "en")]
    [InlineData(" ZH-tw ", "zh-TW")]
    public void UiLanguage_NormalizesAndFallsBackToEnglish(string? input, string expected)
    {
        Assert.Equal(expected, LocalizationService.NormalizeLanguage(input));
        Assert.Equal(expected, new AppSettings { UiLanguage = input! }.UiLanguage);
    }

    [Fact]
    public void Translate_DefaultsToEnglishAndPreservesUnknownResources()
    {
        var localizer = Localizer();
        Assert.Equal("en", localizer.Language);
        Assert.Equal("Nothing playing", localizer["Nothing playing"]);
        localizer.SetLanguage("zh-TW");
        Assert.Equal("目前沒有播放內容", localizer["Nothing playing"]);
        Assert.Equal("A future English resource", localizer["A future English resource"]);
    }

    [Fact]
    public void FormattedPresentation_PreservesValuesAndReturnsToEnglish()
    {
        var localizer = Localizer();
        const string source = "Switched to 24-bit / 96 kHz for “歌曲 ‘Test’”";
        localizer.SetLanguage("zh-TW");
        Assert.Equal("已為「歌曲 ‘Test’」切換至 24-bit / 96 kHz", localizer[source]);
        Assert.Equal("手動指定：「jp」", localizer.Format("Override: “{0}”", "jp"));
        localizer.SetLanguage("en");
        Assert.Equal(source, localizer[source]);
    }

    [Fact]
    public void SwitchingUiLanguage_IsPresentationOnlyAndKeepsCatalogRegion()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var localizer = Localizer();
        var viewModel = new MainWindowViewModel(localizer)
        {
            AppleMusicStorefront = "jp",
            SelectedDeviceId = "test-dac",
            RestartTrackAfterSampleRateChange = true,
            SwitchBitDepth = false,
        };
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.UiLanguage = "zh-TW";

        Assert.Equal("jp", viewModel.AppleMusicStorefront);
        Assert.Equal("test-dac", viewModel.SelectedDeviceId);
        Assert.True(viewModel.RestartTrackAfterSampleRateChange);
        Assert.False(viewModel.SwitchBitDepth);
        Assert.Equal("手動指定：「jp」", viewModel.StorefrontNoteText);
        Assert.Same(culture, CultureInfo.CurrentCulture);
        Assert.Same(uiCulture, CultureInfo.CurrentUICulture);
        Assert.Contains(nameof(MainWindowViewModel.UiLanguage), changed);
        Assert.DoesNotContain(nameof(MainWindowViewModel.AppleMusicStorefront), changed);
        Assert.DoesNotContain(nameof(MainWindowViewModel.SelectedDeviceId), changed);
        Assert.DoesNotContain(nameof(MainWindowViewModel.RestartTrackAfterSampleRateChange), changed);
        viewModel.DetachLocalization();
    }

    [Fact]
    public void SwitchingUiLanguage_RepaintsStatusWithoutDuplicatingActivitiesOrAdvancingFormats()
    {
        var localizer = Localizer();
        var viewModel = new MainWindowViewModel(localizer);
        var oldFormat = new AudioFormatCandidate(44100, 24, 2);
        var newFormat = new AudioFormatCandidate(96000, 24, 2);
        // A real metadata value that happens to match an English UI key stays metadata.
        var track = new TrackSnapshot("AppleInc.AppleMusicWin", "test", "Nothing playing",
            "Waiting for Apple Music", "Test album", "test", DateTimeOffset.UtcNow);
        var requested = new ResolvedAudioFormat(96000, 24, ResolutionConfidence.Exact,
            AudioFormatSource.CatalogManifest, "test");
        var status = new SwitchingStatus("Switching: Playback restored", "Test DAC", track,
            requested, newFormat, null, true);
        viewModel.UpdateCurrentDeviceFormat(oldFormat);
        viewModel.UpdateStatus(status);
        var originalActivity = Assert.Single(viewModel.ActivityLog);

        viewModel.UiLanguage = "zh-TW";

        Assert.Equal(SwitchPhase.Restored, viewModel.Phase);
        Assert.False(viewModel.IsBusyPhase);
        Assert.Equal(oldFormat, viewModel.PreviousAppliedFormat);
        Assert.Equal("播放已恢復", viewModel.PhaseText);
        Assert.Equal("先前為 24-bit / 44.1 kHz", viewModel.WasLine);
        Assert.Equal("Nothing playing", viewModel.TrackTitleText);
        Assert.Equal("Waiting for Apple Music — Test album", viewModel.TrackMetaText);
        Assert.Equal(originalActivity.TimeText, Assert.Single(viewModel.ActivityLog).TimeText);
        Assert.Equal("已為「Nothing playing」切換至 24-bit / 96 kHz", viewModel.ActivityLog[0].Text);
        Assert.Equal("Switching: Playback restored", status.ResolverStatusText);

        viewModel.UiLanguage = "en";
        Assert.Equal("Playback restored", viewModel.PhaseText);
        Assert.Equal(originalActivity.Text, Assert.Single(viewModel.ActivityLog).Text);
        viewModel.DetachLocalization();
    }

    [Fact]
    public void UiLanguage_RoundTripsInIndependentSettingsAndOldFilesRemainEnglish()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.LocalizationTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var service = new SettingsService(directory);
            const string oldJson = "{\"switchBitDepth\":false,\"appleMusicStorefront\":\"jp\"}";
            File.WriteAllText(service.GetSettingsPath(), oldJson);
            var settings = service.Load();
            Assert.Equal("en", settings.UiLanguage);
            Assert.Equal(oldJson, File.ReadAllText(service.GetSettingsPath()));
            settings.UiLanguage = "zh-TW";
            service.Save(settings);
            var loaded = service.Load();
            Assert.Equal("zh-TW", loaded.UiLanguage);
            Assert.Equal("jp", loaded.AppleMusicStorefront);
            Assert.False(loaded.SwitchBitDepth);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ActualDeviceAndTrackNames_AreNeverTranslatedAsUiResources()
    {
        var localizer = new LocalizationService(new Dictionary<string, string>
        {
            ["Unknown"] = "未知",
            ["Play"] = "播放",
            ["No active target device"] = "沒有目標裝置",
        });
        var viewModel = new MainWindowViewModel(localizer);
        viewModel.UpdateActiveTargetCapabilities(new CurrentTargetDeviceCapabilitiesSnapshot("Unknown", []));
        viewModel.UpdateStatus(new SwitchingStatus("Resolver: Idle", "Unknown",
            new TrackSnapshot("AppleInc.AppleMusicWin", "test", "Play", "Unknown", "Play", "test", DateTimeOffset.UtcNow),
            null, null, null));
        viewModel.UiLanguage = "zh-TW";
        Assert.Equal("Play", viewModel.TrackTitleText);
        Assert.Equal("Play - Unknown", viewModel.CurrentTrackText);
        Assert.Equal("Unknown — Play", viewModel.TrackMetaText);
        Assert.Equal("Unknown", viewModel.ActiveTargetDeviceNameText);
        viewModel.UpdateActiveTargetCapabilities(new CurrentTargetDeviceCapabilitiesSnapshot("No active target device", []));
        Assert.Equal("No active target device", viewModel.ActiveTargetDeviceNameText);
        viewModel.DetachLocalization();
    }

    [Fact]
    public void ComposedDiagnostics_TranslateKnownPartsAndPreserveExternalDetails()
    {
        var localizer = new LocalizationService(new Dictionary<string, string>
        {
            ["Automatic switching stopped."] = "已停止自動切換。",
            ["Pause Apple Music."] = "請暫停 Apple Music。",
        });
        localizer.SetLanguage("zh-TW");
        Assert.Equal("已停止自動切換。 請暫停 Apple Music。 HRESULT 0x80004005",
            localizer.TranslateDiagnostic("Automatic switching stopped. Pause Apple Music. HRESULT 0x80004005"));
    }

    [Theory]
    [InlineData("Switching: Preparing")]
    [InlineData("Switching: Applying format")]
    [InlineData("Switching: Waiting for device")]
    [InlineData("Switching: Paused before format change")]
    [InlineData("Switching: Muted warmup before 0:00")]
    [InlineData("Switching: Waiting for user playback")]
    [InlineData("Switching: Manual recovery needed")]
    [InlineData("Switching: Restart unavailable")]
    [InlineData("Switching: Rebuilding audio pipeline")]
    [InlineData("Switching: Recovering playback")]
    [InlineData("Switching: Playback restored")]
    [InlineData("Switching: Playback paused")]
    [InlineData("Switching: Restarting Apple Music")]
    [InlineData("Switching: Apple Music restarted")]
    [InlineData("Resolver: Waiting for Apple Music")]
    [InlineData("Resolver: Resolving format")]
    [InlineData("Resolver: No target device")]
    [InlineData("No active target device.")]
    [InlineData("No format resolver returned a result.")]
    public void KnownDiagnosticStatuses_HaveTraditionalChineseResources(string english)
    {
        var localizer = new LocalizationService();
        localizer.SetLanguage("zh-TW");
        Assert.NotEqual(english, localizer[english]);
    }

    [Fact]
    public void EmbeddedTranslations_HaveValuesAndPreserveAllFormatParameters()
    {
        var assembly = typeof(LocalizationService).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Localization.", StringComparison.Ordinal) &&
                           name.EndsWith(".zh-TW.json", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(names);
        var parameter = new Regex(@"\{\d+\}");
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            Assert.NotEmpty(entries);
            foreach (var (english, translation) in entries)
            {
                Assert.False(string.IsNullOrWhiteSpace(translation), $"Blank resource: {english}");
                Assert.Equal(parameter.Matches(english).Select(match => match.Value).Order(),
                    parameter.Matches(translation).Select(match => match.Value).Order());
            }
        }
        var localizer = new LocalizationService();
        localizer.SetLanguage("zh-TW");
        Assert.Equal("目前沒有播放內容", localizer["Nothing playing"]);
    }
}
