using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NotchBar.Core;
using NotchBar.Services;
using MediaBrush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
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
    private readonly ScaleTransform _statusScale = new();
    private readonly TranslateTransform _statusTranslate = new();
    private readonly TranslateTransform _titleTranslate = new();
    private readonly TranslateTransform _summaryTranslate = new();
    private readonly TranslateTransform _pinTranslate = new();
    private TextBaselineMetrics? _titleBaselineMetrics;
    private TextBaselineMetrics? _summaryBaselineMetrics;
    private TextBaselineMetrics? _secondaryBaselineMetrics;
    private StatusItem? _currentItem;
    private bool _usesSimpleBody;

    public ExpandedView()
    {
        InitializeComponent();

        StatusHalo.RenderTransformOrigin = new Point(0.5, 0.5);
        StatusHalo.RenderTransform = CreateStatusTransform(_statusScale, _statusTranslate);
        TitleText.RenderTransform = _titleTranslate;
        SummaryText.RenderTransform = _summaryTranslate;
        PinButton.RenderTransform = _pinTranslate;

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
        var isClock = string.Equals(item.Id, StatusStore.ClockId, StringComparison.OrdinalIgnoreCase);
        StatusHalo.Visibility = isClock ? Visibility.Collapsed : Visibility.Visible;
        HeaderTextStack.Margin = isClock
            ? new Thickness(0, -1, 12, 0)
            : new Thickness(10, -1, 12, 0);
        SimpleBody.Margin = isClock
            ? new Thickness(0, 12, 0, 0)
            : new Thickness(34, 12, 0, 0);
        SummaryText.Text = item.Text;

        var detail = item.Detail?.Trim();
        var secondary = item.SecondaryText?.Trim();
        var hasDetail = !string.IsNullOrWhiteSpace(detail);
        var hasSecondary = !string.IsNullOrWhiteSpace(secondary);

        _usesSimpleBody = item.IsBuiltIn && item.Progress is null;
        ApplyBodyContent(detail, secondary, hasDetail, hasSecondary);

        ProgressRow.Visibility = item.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressText.Text = item.Progress is { } progress ? progress.ToString("P0") : string.Empty;
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

    public SharedElementAnchors CaptureTransitionAnchors(UIElement ancestor) => new(
        StatusHalo.Visibility == Visibility.Visible
            ? GetUntranslatedCenter(StatusHalo, _statusTranslate, ancestor)
            : null,
        GetUntranslatedCenter(TitleText, _titleTranslate, ancestor),
        GetUntranslatedCenter(SummaryText, _summaryTranslate, ancestor),
        GetUntranslatedCenter(PinButton, _pinTranslate, ancestor));

    public ClockTextAnchors CaptureClockTextAnchors(UIElement ancestor) => new(
        CaptureTextAnchor(
            TitleText,
            _titleTranslate,
            ancestor,
            0,
            ref _titleBaselineMetrics),
        CaptureTextAnchor(
            SummaryText,
            _summaryTranslate,
            ancestor,
            0,
            ref _summaryBaselineMetrics),
        CaptureTextAnchor(
            SimpleSecondaryText,
            null,
            ancestor,
            BodyMotionTranslate.Y,
            ref _secondaryBaselineMetrics));

    public void ApplyTransition(
        TransitionChoreographyFrame frame,
        SharedElementOffsets offsets,
        bool clockOverlayOwnsText = false)
    {
        var reverseProgress = frame.PositionProgress - 1d;
        SetTranslation(_statusTranslate, offsets.Status, reverseProgress);
        SetTranslation(_titleTranslate, offsets.Title, reverseProgress);
        SetTranslation(_summaryTranslate, offsets.Summary, reverseProgress);
        SetTranslation(_pinTranslate, offsets.Pin, reverseProgress);

        var sharedOpacity = frame.ExpandedSharedOpacity;
        StatusHalo.Opacity = sharedOpacity;
        TitleText.Opacity = clockOverlayOwnsText ? 0 : sharedOpacity;
        SummaryText.Opacity = clockOverlayOwnsText ? 0 : sharedOpacity;

        var haloScale = (18d / 24d) + ((1d - (18d / 24d)) * frame.PositionProgress);
        _statusScale.ScaleX = haloScale;
        _statusScale.ScaleY = haloScale;

        BodyMotionHost.Opacity = frame.ExpandedBodyOpacity;
        BodyMotionTranslate.Y = frame.ExpandedBodyOffsetY;
        BodyMotionHost.IsHitTestVisible = frame.ExpandedBodyOpacity > 0.95;
        SimpleSecondaryText.Opacity = clockOverlayOwnsText ? 0 : 1;
    }

    public void ResetLayoutTransition()
    {
        Reset(_statusTranslate);
        Reset(_titleTranslate);
        Reset(_summaryTranslate);
        Reset(_pinTranslate);
        _statusScale.ScaleX = 1;
        _statusScale.ScaleY = 1;
        StatusHalo.Opacity = 1;
        TitleText.Opacity = 1;
        SummaryText.Opacity = 1;
        SimpleSecondaryText.Opacity = 1;
        BodyMotionHost.Opacity = 1;
        BodyMotionTranslate.Y = 0;
        BodyMotionHost.IsHitTestVisible = true;
    }

    private static ClockTextAnchor CaptureTextAnchor(
        TextBlock element,
        TranslateTransform? translation,
        UIElement ancestor,
        double inheritedTranslationY,
        ref TextBaselineMetrics? baselineMetrics)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var key = new TextBaselineKey(
            element.FontFamily,
            element.FontStyle,
            element.FontWeight,
            element.FontStretch,
            element.FontSize,
            dpi.PixelsPerDip,
            element.FlowDirection,
            CultureInfo.CurrentUICulture);
        // Font metrics are invariant across clock ticks and motion frames. Only
        // DPI or typography changes require another FormattedText measurement.
        if (baselineMetrics is not { } metrics || metrics.Key != key)
        {
            var formatted = new FormattedText(
                "Ag",
                key.Culture,
                key.FlowDirection,
                new Typeface(
                    key.FontFamily,
                    key.FontStyle,
                    key.FontWeight,
                    key.FontStretch),
                key.FontSize,
                System.Windows.Media.Brushes.Transparent,
                key.PixelsPerDip);
            metrics = new TextBaselineMetrics(key, formatted.Baseline);
            baselineMetrics = metrics;
        }

        var baselineFromTop = metrics.BaselineFromTop;
        var leadingX = element.FlowDirection == System.Windows.FlowDirection.RightToLeft
            ? element.ActualWidth
            : 0d;
        var transformed = element.TranslatePoint(
            new Point(leadingX, baselineFromTop),
            ancestor);

        return new ClockTextAnchor(
            new Point(
                transformed.X - (translation?.X ?? 0),
                transformed.Y - (translation?.Y ?? 0) - inheritedTranslationY),
            element.FontSize,
            baselineFromTop);
    }

    private readonly record struct TextBaselineKey(
        System.Windows.Media.FontFamily FontFamily,
        System.Windows.FontStyle FontStyle,
        System.Windows.FontWeight FontWeight,
        System.Windows.FontStretch FontStretch,
        double FontSize,
        double PixelsPerDip,
        System.Windows.FlowDirection FlowDirection,
        CultureInfo Culture);

    private readonly record struct TextBaselineMetrics(
        TextBaselineKey Key,
        double BaselineFromTop);

    private static TransformGroup CreateStatusTransform(
        ScaleTransform scale,
        TranslateTransform translate)
    {
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(translate);
        return transform;
    }

    private static Point GetUntranslatedCenter(
        FrameworkElement element,
        TranslateTransform translation,
        UIElement ancestor)
    {
        var transformed = element.TranslatePoint(
            new Point(element.ActualWidth / 2d, element.ActualHeight / 2d),
            ancestor);
        return new Point(transformed.X - translation.X, transformed.Y - translation.Y);
    }

    private static void SetTranslation(
        TranslateTransform transform,
        Vector offset,
        double multiplier)
    {
        transform.X = offset.X * multiplier;
        transform.Y = offset.Y * multiplier;
    }

    private static void Reset(TranslateTransform transform)
    {
        transform.X = 0;
        transform.Y = 0;
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
