using System.IO;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class DacLockSettingsTests
{
    [Fact]
    public void PausePreferenceSurvivesSavingAndIsolationWithoutChangingOtherPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowsLosslessSwitcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"uiLanguage\":\"zh-TW\",\"switchBitDepth\":false,\"defaultBitDepth\":24}");
            var service = new SettingsService(directory);
            var settings = service.Load();
            Assert.False(settings.DacLockPaused);
            Assert.False(TestBuildProfile.ReadSavedDacLockPaused(path));
            settings.DacLockPaused = true;
            TestBuildProfile.EnforceIsolation(settings);
            service.Save(settings);
            var restored = service.Load();
            Assert.True(restored.DacLockPaused);
            Assert.True(TestBuildProfile.ReadSavedDacLockPaused(path));
            Assert.Equal("zh-TW", restored.UiLanguage);
            Assert.False(restored.SwitchBitDepth);
            Assert.Equal(24, restored.DefaultBitDepth);
            restored.DacLockPaused = false;
            service.Save(restored);
            Assert.False(TestBuildProfile.ReadSavedDacLockPaused(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"dacLockPaused\":false}")]
    [InlineData("{\"dacLockPaused\":\"true\"}")]
    [InlineData("invalid JSON")]
    public void HelperDoesNotInferAPauseFromMissingOrInvalidValues(string json)
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, json); Assert.False(TestBuildProfile.ReadSavedDacLockPaused(path)); }
        finally { File.Delete(path); }
    }
}
