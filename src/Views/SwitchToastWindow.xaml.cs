using System.Windows;
using System.Windows.Threading;
using WindowsLosslessSwitcher.Controls;
using WindowsLosslessSwitcher.Services;

namespace WindowsLosslessSwitcher.Views;

/// <summary>
/// The Nocturne switch toast — one window, two layouts (1c rich card / 1d minimal pill),
/// chosen by the content record. Bottom-right of the work area, 4-second auto-close, click on
/// the pill (or the rich card's X) dismisses.
/// </summary>
public partial class SwitchToastWindow : Window, ISwitchToastWindow
{
    // The 28px transparent margin exists only for the shadow; the visible card should still sit
    // 16px from the work-area edges.
    private const double OuterMargin = 28;
    private const double EdgeOffset = 16;

    private readonly DispatcherTimer _closeTimer;
    private readonly SwitchToastContent _content;

    internal SwitchToastWindow(SwitchToastContent content)
    {
        InitializeComponent();
        _content = content;

        if (content.Variant == ToastVariant.Pill)
        {
            RichRoot.Visibility = Visibility.Collapsed;
            PillRoot.Visibility = Visibility.Visible;
            PillRateText.Text = content.NewRateText ?? content.NewFormatText;
            PillBitsText.Text = content.NewBitsText is { Length: > 0 } bits ? LocalizationService.Current.Format("{0} · lossless", bits) : LocalizationService.Current["lossless"];
            if (content.Artwork is not null)
            {
                PillArtworkBrush.ImageSource = content.Artwork;
                PillArtworkHost.Visibility = Visibility.Visible;
            }

            // The pill has no close button; clicking anywhere dismisses it.
            MouseLeftButtonUp += (_, _) => DismissNow();
        }
        else
        {
            KickerText.Text = Tracking.Apply(LocalizationService.Current[content.Kicker]);
            if (content.OldFormatText is { Length: > 0 } old)
            {
                OldFormatText.Text = old;
            }
            else
            {
                OldFormatText.Visibility = Visibility.Collapsed;
                TransitionArrow.Visibility = Visibility.Collapsed;
            }

            NewFormatText.Text = content.NewFormatText ?? "";
            if (content.TrackLine is { Length: > 0 } trackLine)
            {
                TrackLineText.Text = content.TrackUniqueKey is null || content.Kicker == "RATE UNDETERMINED"
                    ? LocalizationService.Current[trackLine] : trackLine;
                TrackLineText.Visibility = Visibility.Visible;
            }

            if (content.DeviceName is { Length: > 0 } device)
            {
                DeviceLineText.Text = device;
                DeviceLineText.Visibility = Visibility.Visible;
            }

            if (content.Artwork is not null)
            {
                RichArtworkBrush.ImageSource = content.Artwork;
                RichArtworkHost.Visibility = Visibility.Visible;
            }
        }

        Loaded += (_, _) => PositionInBottomRightCorner();

        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        KickerText.Text = Tracking.Apply(LocalizationService.Current[_content.Kicker]);
        PillBitsText.Text = _content.NewBitsText is { Length: > 0 } bits
            ? LocalizationService.Current.Format("{0} · lossless", bits) : LocalizationService.Current["lossless"];
        if (_content.Kicker == "RATE UNDETERMINED" && _content.TrackLine is { } explanation)
            TrackLineText.Text = LocalizationService.Current[explanation];
    }

    public void StartAutoClose() => _closeTimer.Start();

    /// <summary>
    /// Swaps in artwork that finished loading after the toast was shown (GSMTC art routinely
    /// lags the track change by a few seconds).
    /// </summary>
    public void UpdateArtwork(System.Windows.Media.ImageSource artwork)
    {
        RichArtworkBrush.ImageSource = artwork;
        RichArtworkHost.Visibility = Visibility.Visible;
        PillArtworkBrush.ImageSource = artwork;
        PillArtworkHost.Visibility = Visibility.Visible;
    }

    private void PositionInBottomRightCorner()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth + OuterMargin - EdgeOffset;
        Top = workArea.Bottom - ActualHeight + OuterMargin - EdgeOffset;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => DismissNow();

    private void DismissNow()
    {
        _closeTimer.Stop();
        Close();
    }
}
