using System.Windows;
using System.Windows.Controls;
using NotchBar.Core;

namespace NotchBar.UI;

public partial class ExpandedView : UserControl
{
    public event EventHandler? PinClicked;

    public ExpandedView()
    {
        InitializeComponent();
    }

    public void ShowItem(StatusItem item, bool pinned)
    {
        TitleText.Text = item.Title;
        SummaryText.Text = item.Text;
        DetailText.Text = string.IsNullOrWhiteSpace(item.Detail) ? "No additional detail" : item.Detail;
        SecondaryText.Text = item.SecondaryText ?? string.Empty;
        UpdatedText.Text = $"Updated {item.UpdatedAt.ToLocalTime():HH:mm:ss} · TTL {item.TtlSeconds}s";
        ProgressBar.Visibility = item.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Value = item.Progress ?? 0;
        PinGlyph.Text = pinned ? "◆" : "◇";
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";
    }

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinClicked?.Invoke(this, EventArgs.Empty);
    }
}
