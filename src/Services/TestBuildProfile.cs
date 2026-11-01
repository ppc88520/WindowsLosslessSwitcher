using System.IO;
using System.Text.Json;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>Standalone acceptance build. Never reads or migrates the installed application's data.</summary>
public static class TestBuildProfile
{
    public const string DataFolderName = "WindowsLosslessSwitcher.DacGuardTest";
    public const string DisplayName = "Lossless Switcher — DAC Guard TEST 0.3";
    public const string Version = "1.0.0-beta.7-dac0.3";
    public const string BaseCommit = "c05ce188796bf792c87c238d7fa73f5fb53b3b5e";

    public static void ApplyFirstRunDefaults(AppSettings settings, bool hasPersistedSettings)
    {
        if (hasPersistedSettings) return;
        settings.UiLanguage = "en";
        settings.SwitchBitDepth = false;
        settings.DefaultBitDepth = 24;
        settings.RestartTrackAfterSampleRateChange = true;
    }

    public static void EnforceIsolation(AppSettings settings)
    {
        settings.DeviceSelectionMode = DeviceSelectionMode.FollowDefault;
        settings.PinnedDeviceId = null;
        settings.LaunchAtLogin = false;
        settings.RestartAppleMusicOnPlaybackFailure = false;
    }

    public static string ReadSavedLanguage()
    {
        try
        {
            var path = Path.Combine(AppDataPaths.RootDirectory, "settings.json");
            if (!File.Exists(path)) return "en";
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("uiLanguage", out var value) && value.ValueKind == JsonValueKind.String
                ? LocalizationService.NormalizeLanguage(value.GetString()) : "en";
        }
        catch { return "en"; }
    }

    /// <summary>Read-only helper preflight; a paused test profile must never restart the player.</summary>
    public static bool ReadSavedDacLockPaused() =>
        ReadSavedDacLockPaused(Path.Combine(AppDataPaths.RootDirectory, "settings.json"));

    internal static bool ReadSavedDacLockPaused(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("dacLockPaused", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
}
