using System.Windows;
using System.Windows.Threading;
using NotchBar.Core;
using MediaBrush = System.Windows.Media.Brush;
using UserControl = System.Windows.Controls.UserControl;

namespace NotchBar.UI;

public partial class ExpandedView : UserControl
{
    private const double MinPreferredWidth = 360;
    private const double MaxPreferredWidth = 560;
    private const double MinPreferredHeight = 148;
    private const double MaxPreferredHeight = 300;
    private const double WidthStep = 4;

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
    public event EventHandler? CollapseRequested;

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
        StatusDot.Fill = (MediaBrush)FindResource(accentKey);
        StatusHalo.Background = (MediaBrush)FindResource(haloKey);

        PinGlyph.Fill = (MediaBrush)FindResource(pinned ? "Accent" : "SecondaryText");
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";

        RefreshUpdatedText();
        UpdateTimerState();
    }

    public double GetPreferredWidth()
    {
        LayoutRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Quantize(Math.Clamp(LayoutRoot.DesiredSize.Width + 4, MinPreferredWidth, MaxPreferredWidth));
    }

    public double GetPreferredHeight(double availableWidth)
    {
        LayoutRoot.Measure(new Size(Math.Max(1, availableWidth), double.PositiveInfinity));
        return Math.Ceiling(Math.Clamp(LayoutRoot.DesiredSize.Height + 4, MinPreferredHeight, MaxPreferredHeight));
    }

    private static double Quantize(double width) => Math.Ceiling(width / WidthStep) * WidthStep;

    private void RefreshUpdatedText()
    {
        if (_currentItem is null || _currentItem.IsBuiltIn)
        {
            UpdatedText.Text = string.Empty;
            UpdatedPanel.Visibility = Visibility.Collapsed;
            return;
        }

        UpdatedPanel.Visibility = Visibility.Visible;
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

    private void HeaderContent_OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        CollapseRequested?.Invoke(this, EventArgs.Empty);
    }
}
