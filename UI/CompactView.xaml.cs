using System.Windows;
using NotchBar.Core;
using MediaBrush = System.Windows.Media.Brush;
using UserControl = System.Windows.Controls.UserControl;

namespace NotchBar.UI;

public partial class CompactView : UserControl
{
    private const double MinPreferredWidth = 286;
    private const double MaxPreferredWidth = 520;
    private const double WidthStep = 4;

    public event EventHandler? PinClicked;

    public CompactView()
    {
        InitializeComponent();
    }

    public void ShowItem(StatusItem item, bool pinned)
    {
        TitleText.Text = item.Title;
        SummaryText.Text = item.Text;

        var secondary = item.SecondaryText?.Trim();
        SecondaryText.Text = secondary ?? string.Empty;
        SecondaryText.Visibility = string.IsNullOrWhiteSpace(secondary)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var accentKey = item.IsNotification ? "NotificationAccent" : "Accent";
        var haloKey = item.IsNotification ? "NotificationAccentSoft" : "AccentSoft";
        StatusDot.Fill = (MediaBrush)FindResource(accentKey);
        StatusHalo.Background = (MediaBrush)FindResource(haloKey);

        PinGlyph.Fill = (MediaBrush)FindResource(pinned ? "Accent" : "SecondaryText");
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";
    }

    public double GetPreferredWidth()
    {
        LayoutRoot.Measure(new Size(double.PositiveInfinity, 44));
        return Quantize(Math.Clamp(LayoutRoot.DesiredSize.Width + 4, MinPreferredWidth, MaxPreferredWidth));
    }

    private static double Quantize(double width) => Math.Ceiling(width / WidthStep) * WidthStep;

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinClicked?.Invoke(this, EventArgs.Empty);
    }
}
