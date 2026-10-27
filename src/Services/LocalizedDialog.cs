using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace WindowsLosslessSwitcher.Services;

/// <summary>Uses the selected UI language for app-owned dialog text and buttons, including the helper.</summary>
public static class LocalizedDialog
{
    public static MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information,
        MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            var result = MessageBoxResult.None;
            var thread = new Thread(() => result = Show(message, title, buttons, image, defaultResult));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return result;
        }

        var strings = LocalizationService.Current;
        var window = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 700,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(27, 30, 38)),
            Foreground = Brushes.WhiteSmoke,
            FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI"),
            FontSize = 14,
            ShowInTaskbar = true,
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = strings[message], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 22) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        panel.Children.Add(actions);
        window.Content = panel;
        var selected = defaultResult;

        void Add(string label, MessageBoxResult value, bool isCancel = false)
        {
            var button = new Button
            {
                Content = strings[label], MinWidth = 88, Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(8, 0, 0, 0), IsDefault = value == defaultResult, IsCancel = isCancel,
            };
            button.Click += (_, _) => { selected = value; window.DialogResult = value is MessageBoxResult.OK or MessageBoxResult.Yes; };
            actions.Children.Add(button);
        }

        if (buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
        {
            Add("Yes", MessageBoxResult.Yes);
            Add("No", MessageBoxResult.No, buttons == MessageBoxButton.YesNo);
            if (buttons == MessageBoxButton.YesNoCancel) Add("Cancel", MessageBoxResult.Cancel, true);
        }
        else
        {
            Add("OK", MessageBoxResult.OK);
            if (buttons == MessageBoxButton.OKCancel) Add("Cancel", MessageBoxResult.Cancel, true);
        }
        window.ShowDialog();
        return selected;
    }
}
