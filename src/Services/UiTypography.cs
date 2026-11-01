using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace WindowsLosslessSwitcher.Services;

/// <summary>Loads the bundled Latin/Traditional Chinese composite without relying on installed fonts.</summary>
public static class UiTypography
{
    public static FontFamily FontFamily { get; } = new(
        new Uri($"pack://application:,,,/{typeof(UiTypography).Assembly.GetName().Name};component/Assets/Fonts/", UriKind.Absolute),
        "./#Nocturne UI");
}
