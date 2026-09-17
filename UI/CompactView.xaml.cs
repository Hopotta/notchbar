using System.Windows;
using NotchBar.Core;
using MediaBrush = System.Windows.Media.Brush;
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
        StatusDot.Fill = (MediaBrush)FindResource(accentKey);
        StatusHalo.Background = (MediaBrush)FindResource(haloKey);

        PinGlyph.Fill = (MediaBrush)FindResource(pinned ? "Accent" : "SecondaryText");
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";
    }

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinClicked?.Invoke(this, EventArgs.Empty);
    }
}
