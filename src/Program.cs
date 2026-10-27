using System;
using System.Windows;
using Velopack;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher;

/// <summary>
/// Boots the WPF application after Velopack has processed any install/update lifecycle events.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == ManualRestartHandoff.HelperArgument)
        {
            LoadUiLanguage();
            ManualRestartHandoff.RunHelperAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        VelopackApp.Build().Run();

        // Must run before App is constructed: its field initializers create the logger, which
        // creates the new data folder and would shadow files still at the legacy location.
        AppDataPaths.MigrateFromLegacyLocation();
        LoadUiLanguage();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private static void LoadUiLanguage() => LocalizationService.Current.SetLanguage(
        LocalizationService.ReadSavedLanguage(System.IO.Path.Combine(AppDataPaths.RootDirectory, "settings.json")));
}
