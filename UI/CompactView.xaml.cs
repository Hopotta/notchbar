using System.Windows;
using System.Windows.Media;
using NotchBar.Core;
using UserControl = System.Windows.Controls.UserControl;

namespace NotchBar.UI;

public partial class CompactView : UserControl
{
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
        StatusDot.Fill = (Brush)FindResource(accentKey);
        StatusHalo.Background = (Brush)FindResource(haloKey);

        PinGlyph.Fill = (Brush)FindResource(pinned ? "Accent" : "SecondaryText");
        PinButton.Background = pinned
            ? (Brush)FindResource("AccentSoft")
            : Brushes.Transparent;
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";
    }

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinClicked?.Invoke(this, EventArgs.Empty);
    }
}
