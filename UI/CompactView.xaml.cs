using System.Windows;
using System.Windows.Controls;
using NotchBar.Core;

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
        var secondary = string.IsNullOrWhiteSpace(item.SecondaryText) ? string.Empty : $" · {item.SecondaryText}";
        CompactText.Text = $"{item.Title}  ●  {item.Text}{secondary}";
        PinGlyph.Text = pinned ? "◆" : "◇";
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";
    }

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinClicked?.Invoke(this, EventArgs.Empty);
    }
}
