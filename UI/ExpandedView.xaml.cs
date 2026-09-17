using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NotchBar.Core;
using UserControl = System.Windows.Controls.UserControl;

namespace NotchBar.UI;

public partial class ExpandedView : UserControl
{
    private readonly DispatcherTimer _updatedTimer;
    private StatusItem? _currentItem;

    public ExpandedView()
    {
        InitializeComponent();

        _updatedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _updatedTimer.Tick += UpdatedTimer_OnTick;
        IsVisibleChanged += ExpandedView_OnIsVisibleChanged;
        Unloaded += ExpandedView_OnUnloaded;
    }

    public event EventHandler? PinClicked;

    public void ShowItem(StatusItem item, bool pinned)
    {
        _currentItem = item;
        TitleText.Text = item.Title;
        SummaryText.Text = item.Text;
        DetailText.Text = string.IsNullOrWhiteSpace(item.Detail) ? "No additional context" : item.Detail;

        var secondary = item.SecondaryText?.Trim();
        SecondaryText.Text = secondary ?? string.Empty;
        SecondaryText.Visibility = string.IsNullOrWhiteSpace(secondary)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ProgressBar.Visibility = item.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Value = item.Progress ?? 0;

        var accentKey = item.IsNotification ? "NotificationAccent" : "Accent";
        var haloKey = item.IsNotification ? "NotificationAccentSoft" : "AccentSoft";
        StatusDot.Fill = (Brush)FindResource(accentKey);
        StatusHalo.Background = (Brush)FindResource(haloKey);

        PinGlyph.Fill = (Brush)FindResource(pinned ? "Accent" : "SecondaryText");
        PinButton.Background = pinned
            ? (Brush)FindResource("AccentSoft")
            : Brushes.Transparent;
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";

        RefreshUpdatedText();
        UpdateTimerState();
    }

    private void RefreshUpdatedText()
    {
        if (_currentItem is null || _currentItem.IsBuiltIn)
        {
            UpdatedText.Text = string.Empty;
            UpdatedText.Visibility = Visibility.Collapsed;
            return;
        }

        UpdatedText.Visibility = Visibility.Visible;
        UpdatedText.Text = RelativeTimeFormatter.FormatUpdated(_currentItem.UpdatedAt, DateTimeOffset.UtcNow);
    }

    private void UpdateTimerState()
    {
        if (IsVisible && _currentItem is { IsBuiltIn: false })
        {
            if (!_updatedTimer.IsEnabled)
            {
                _updatedTimer.Start();
            }
        }
        else
        {
            _updatedTimer.Stop();
        }
    }

    private void UpdatedTimer_OnTick(object? sender, EventArgs e) => RefreshUpdatedText();

    private void ExpandedView_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshUpdatedText();
        UpdateTimerState();
    }

    private void ExpandedView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _updatedTimer.Stop();
    }

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinClicked?.Invoke(this, EventArgs.Empty);
    }
}
