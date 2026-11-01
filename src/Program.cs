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
        // This manually launched acceptance build has no install/update lifecycle and never
        // migrates or reads the installed application's data.
        LoadUiLanguage();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private static void LoadUiLanguage() => LocalizationService.Current.SetLanguage(
        LocalizationService.ReadSavedLanguage(System.IO.Path.Combine(AppDataPaths.RootDirectory, "settings.json")));
}
