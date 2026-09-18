using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotchBar.Core;
using MediaBrush = System.Windows.Media.Brush;
using UserControl = System.Windows.Controls.UserControl;

namespace NotchBar.UI;

public partial class ExpandedView : UserControl
{
    private const double SimpleMinPreferredWidth = 324;
    private const double StandardMinPreferredWidth = 360;
    private const double MaxPreferredWidth = 560;
    private const double SimpleMinPreferredHeight = 118;
    private const double StandardMinPreferredHeight = 148;
    private const double MaxPreferredHeight = 300;
    private const double WidthStep = 4;

    private readonly DispatcherTimer _updatedTimer;
    private StatusItem? _currentItem;
    private bool _usesSimpleBody;

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

        var detail = item.Detail?.Trim();
        var secondary = item.SecondaryText?.Trim();
        var hasDetail = !string.IsNullOrWhiteSpace(detail);
        var hasSecondary = !string.IsNullOrWhiteSpace(secondary);

        _usesSimpleBody = item.IsBuiltIn && item.Progress is null;
        ApplyBodyContent(detail, secondary, hasDetail, hasSecondary);

        ProgressBar.Visibility = item.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressBar.Value = item.Progress ?? 0;

        var accentKey = item.IsNotification
            ? "NotificationAccent"
            : item.IsBuiltIn ? "BuiltInAccent" : "Accent";
        var haloKey = item.IsNotification
            ? "NotificationAccentSoft"
            : item.IsBuiltIn ? "BuiltInAccentSoft" : "AccentSoft";
        StatusDot.Fill = (MediaBrush)FindResource(accentKey);
        StatusHalo.Background = (MediaBrush)FindResource(haloKey);

        PinGlyph.Fill = (MediaBrush)FindResource(pinned ? "Accent" : "SecondaryText");
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";

        RefreshUpdatedText();
        UpdateTimerState();
    }

    public double GetPreferredWidth()
    {
        LayoutRoot.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var minWidth = _usesSimpleBody ? SimpleMinPreferredWidth : StandardMinPreferredWidth;
        return Quantize(Math.Clamp(LayoutRoot.DesiredSize.Width + 4, minWidth, MaxPreferredWidth));
    }

    public double GetPreferredHeight(double availableWidth)
    {
        LayoutRoot.Measure(new System.Windows.Size(Math.Max(1, availableWidth), double.PositiveInfinity));
        var minHeight = _usesSimpleBody ? SimpleMinPreferredHeight : StandardMinPreferredHeight;
        return Math.Ceiling(Math.Clamp(LayoutRoot.DesiredSize.Height + 4, minHeight, MaxPreferredHeight));
    }

    public void RunLayoutTransition(bool expanding, bool preserveCurrent)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            ResetLayoutTransition();
            return;
        }

        StopBodyMotionPreservingCurrent();

        if (expanding && !preserveCurrent)
        {
            BodyMotionHost.Opacity = 0;
            BodyMotionTranslate.Y = -6;
        }

        var currentOpacity = BodyMotionHost.Opacity;
        var currentY = BodyMotionTranslate.Y;
        var duration = new Duration(TimeSpan.FromMilliseconds(expanding ? 210 : 95));
        var delay = expanding ? TimeSpan.FromMilliseconds(62) : TimeSpan.Zero;
        var easing = new CubicEase
        {
            EasingMode = expanding ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        BodyMotionHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(currentOpacity, expanding ? 1 : 0, duration)
            {
                BeginTime = delay,
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);

        BodyMotionTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(currentY, expanding ? 0 : -5, duration)
            {
                BeginTime = delay,
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    public void ResetLayoutTransition()
    {
        BodyMotionHost.BeginAnimation(OpacityProperty, null);
        BodyMotionTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        BodyMotionHost.Opacity = 1;
        BodyMotionTranslate.Y = 0;
    }

    private void StopBodyMotionPreservingCurrent()
    {
        var opacity = BodyMotionHost.Opacity;
        var y = BodyMotionTranslate.Y;
        BodyMotionHost.BeginAnimation(OpacityProperty, null);
        BodyMotionTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        BodyMotionHost.Opacity = opacity;
        BodyMotionTranslate.Y = y;
    }

    private void ApplyBodyContent(
        string? detail,
        string? secondary,
        bool hasDetail,
        bool hasSecondary)
    {
        var hasBodyContent = hasDetail || hasSecondary;
        BodyHost.Visibility = hasBodyContent ? Visibility.Visible : Visibility.Collapsed;

        if (_usesSimpleBody)
        {
            SimpleBody.Visibility = hasBodyContent ? Visibility.Visible : Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Collapsed;

            SimpleSecondaryText.Text = secondary ?? string.Empty;
            SimpleSecondaryText.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
            SimpleDetailText.Text = detail ?? string.Empty;
            SimpleDetailText.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
            SimpleDetailText.Margin = hasSecondary && hasDetail
                ? new Thickness(0, 3, 0, 0)
                : new Thickness(0);
            return;
        }

        SimpleBody.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = hasBodyContent ? Visibility.Visible : Visibility.Collapsed;

        DetailText.Text = detail ?? string.Empty;
        DetailText.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        SecondaryText.Text = secondary ?? string.Empty;
        SecondaryText.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryText.Margin = hasDetail && hasSecondary
            ? new Thickness(0, 9, 0, 0)
            : new Thickness(0);
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
