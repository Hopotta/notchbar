using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchBar.Core;
using MediaBrush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace NotchBar.UI;

public partial class CompactView : UserControl
{
    private const double MinPreferredWidth = 286;
    private const double MaxPreferredWidth = 520;
    private const double WidthStep = 4;

    private readonly ScaleTransform _statusScale = new();
    private readonly TranslateTransform _statusTranslate = new();
    private readonly TranslateTransform _titleTranslate = new();
    private readonly TranslateTransform _summaryTranslate = new();
    private readonly TranslateTransform _secondaryTranslate = new();
    private readonly TranslateTransform _pinTranslate = new();
    private TextBaselineMetrics? _titleBaselineMetrics;
    private TextBaselineMetrics? _summaryBaselineMetrics;
    private TextBaselineMetrics? _secondaryBaselineMetrics;

    public event EventHandler? PinClicked;

    public CompactView()
    {
        InitializeComponent();

        StatusHalo.RenderTransformOrigin = new Point(0.5, 0.5);
        StatusHalo.RenderTransform = CreateStatusTransform(_statusScale, _statusTranslate);
        TitleChip.RenderTransform = _titleTranslate;
        SummaryText.RenderTransform = _summaryTranslate;
        SecondaryText.RenderTransform = _secondaryTranslate;
        PinButton.RenderTransform = _pinTranslate;
    }

    public void ShowItem(StatusItem item, bool pinned)
    {
        TitleText.Text = item.Title;
        var isClock = string.Equals(item.Id, StatusStore.ClockId, StringComparison.OrdinalIgnoreCase);
        SecondaryText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isClock ? "ClockDateText" : "MutedText");
        if (ApplyClockTextFormatting(isClock))
        {
            _titleBaselineMetrics = null;
            _summaryBaselineMetrics = null;
            _secondaryBaselineMetrics = null;
        }

        StatusHalo.Visibility = isClock ? Visibility.Collapsed : Visibility.Visible;
        TitleChip.Margin = isClock
            ? new Thickness(0)
            : new Thickness(6, 0, 0, 0);
        TitleChip.Background = item.IsBuiltIn
            ? System.Windows.Media.Brushes.Transparent
            : (MediaBrush)FindResource("GlassChipBackground");
        TitleChip.BorderBrush = item.IsBuiltIn
            ? System.Windows.Media.Brushes.Transparent
            : (MediaBrush)FindResource("PanelBorder");
        TitleChip.BorderThickness = item.IsBuiltIn ? new Thickness(0) : new Thickness(0);
        TitleChip.Padding = item.IsBuiltIn ? new Thickness(0) : new Thickness(7, 3, 7, 3);
        SummaryText.Text = item.Text;

        var secondary = item.SecondaryText?.Trim();
        SecondaryText.Text = secondary ?? string.Empty;
        SecondaryText.Visibility = string.IsNullOrWhiteSpace(secondary)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SecondaryText.FontWeight = isClock ? FontWeights.SemiBold : FontWeights.Normal;

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
    }

    private bool ApplyClockTextFormatting(bool isClock)
    {
        var formattingProperty = TextOptions.TextFormattingModeProperty;
        var hintingProperty = TextOptions.TextHintingModeProperty;
        var textElements = new[] { TitleText, SecondaryText };
        if (isClock)
        {
            var formattingChanged = textElements.Any(
                element => TextOptions.GetTextFormattingMode(element) != TextFormattingMode.Ideal);
            foreach (var element in textElements)
            {
                if (TextOptions.GetTextFormattingMode(element) != TextFormattingMode.Ideal)
                {
                    TextOptions.SetTextFormattingMode(element, TextFormattingMode.Ideal);
                }

                if (TextOptions.GetTextHintingMode(element) != TextHintingMode.Animated)
                {
                    TextOptions.SetTextHintingMode(element, TextHintingMode.Animated);
                }
            }

            return formattingChanged;
        }

        var formattingChangedOnReset = false;
        foreach (var element in textElements)
        {
            if (element.ReadLocalValue(formattingProperty) != DependencyProperty.UnsetValue)
            {
                element.ClearValue(formattingProperty);
                formattingChangedOnReset = true;
            }

            if (element.ReadLocalValue(hintingProperty) != DependencyProperty.UnsetValue)
            {
                element.ClearValue(hintingProperty);
            }
        }

        return formattingChangedOnReset;
    }

    public double GetPreferredWidth()
    {
        LayoutRoot.Measure(new System.Windows.Size(double.PositiveInfinity, 44));
        return Quantize(Math.Clamp(LayoutRoot.DesiredSize.Width + 4, MinPreferredWidth, MaxPreferredWidth));
    }

    public SharedElementAnchors CaptureTransitionAnchors(UIElement ancestor) => new(
        StatusHalo.Visibility == Visibility.Visible
            ? GetUntranslatedCenter(StatusHalo, _statusTranslate, ancestor)
            : null,
        GetUntranslatedCenter(TitleChip, _titleTranslate, ancestor),
        GetUntranslatedCenter(SummaryText, _summaryTranslate, ancestor),
        GetUntranslatedCenter(PinButton, _pinTranslate, ancestor));

    public ClockTextAnchors CaptureClockTextAnchors(UIElement ancestor) => new(
        CaptureTextAnchor(TitleText, _titleTranslate, ancestor, false, ref _titleBaselineMetrics),
        CaptureTextAnchor(SummaryText, _summaryTranslate, ancestor, false, ref _summaryBaselineMetrics),
        CaptureTextAnchor(SecondaryText, _secondaryTranslate, ancestor, false, ref _secondaryBaselineMetrics));

    public ClockTextAnchors CaptureFinalClockTextAnchors(UIElement ancestor) => new(
        CaptureTextAnchor(TitleText, _titleTranslate, ancestor, true, ref _titleBaselineMetrics),
        CaptureTextAnchor(SummaryText, _summaryTranslate, ancestor, true, ref _summaryBaselineMetrics),
        CaptureTextAnchor(SecondaryText, _secondaryTranslate, ancestor, true, ref _secondaryBaselineMetrics));

    public void ApplyTransition(
        TransitionChoreographyFrame frame,
        SharedElementOffsets offsets,
        bool clockOverlayOwnsText = false)
    {
        var progress = frame.PositionProgress;
        SetTranslation(_statusTranslate, offsets.Status, progress);
        SetTranslation(_titleTranslate, offsets.Title, progress);
        SetTranslation(_summaryTranslate, offsets.Summary, progress);
        SetTranslation(_pinTranslate, offsets.Pin, progress);
        _secondaryTranslate.Y = frame.CompactSecondaryOffsetY;

        var sharedOpacity = frame.CompactSharedOpacity;
        StatusHalo.Opacity = sharedOpacity;
        TitleChip.Opacity = clockOverlayOwnsText ? 0 : sharedOpacity;
        SummaryText.Opacity = clockOverlayOwnsText ? 0 : sharedOpacity;
        SecondaryText.Opacity = clockOverlayOwnsText ? 0 : frame.CompactSecondaryOpacity;

        // 18px compact halo and 24px expanded halo meet at the same apparent
        // size during the handoff; text is never scaled.
        var haloScale = 1d + ((24d / 18d - 1d) * progress);
        _statusScale.ScaleX = haloScale;
        _statusScale.ScaleY = haloScale;
    }

    public void SetClockTextOverlayOwned(bool owned)
    {
        var opacity = owned ? 0d : 1d;
        TitleText.Opacity = opacity;
        SummaryText.Opacity = opacity;
        SecondaryText.Opacity = opacity;
    }

    public void ResetTransitionVisuals()
    {
        Reset(_statusTranslate);
        Reset(_titleTranslate);
        Reset(_summaryTranslate);
        Reset(_secondaryTranslate);
        Reset(_pinTranslate);
        _statusScale.ScaleX = 1;
        _statusScale.ScaleY = 1;
        StatusHalo.Opacity = 1;
        TitleChip.Opacity = 1;
        SummaryText.Opacity = 1;
        SecondaryText.Opacity = 1;
    }

    private static ClockTextAnchor CaptureTextAnchor(
        TextBlock element,
        TranslateTransform translation,
        UIElement ancestor,
        bool useArrangedBaseline,
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

        // Motion frames use cached typeface metrics to avoid text formatting work.
        // Endpoint handoff uses the arranged TextBlock baseline that WPF itself
        // will use to draw the target glyphs.
        var baselineFromTop = useArrangedBaseline
            ? element.BaselineOffset
            : metrics.BaselineFromTop;
        var leadingX = element.FlowDirection == System.Windows.FlowDirection.RightToLeft
            ? element.ActualWidth
            : 0d;
        var transformed = element.TranslatePoint(
            new Point(leadingX, baselineFromTop),
            ancestor);
        var arrangedTopLeft = element.TranslatePoint(new Point(0, 0), ancestor);

        return new ClockTextAnchor(
            new Point(
                transformed.X - translation.X,
                transformed.Y - translation.Y),
            element.FontSize,
            baselineFromTop,
            element.FontFamily,
            element.FontStyle,
            element.FontWeight,
            element.FontStretch,
            element.FlowDirection,
            (element.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent,
            element.ActualWidth,
            element.ActualHeight,
            new Point(
                arrangedTopLeft.X - translation.X,
                arrangedTopLeft.Y - translation.Y),
            element.UseLayoutRounding,
            element.SnapsToDevicePixels,
            TextOptions.GetTextFormattingMode(element),
            TextOptions.GetTextRenderingMode(element),
            TextOptions.GetTextHintingMode(element),
            element.TextTrimming,
            element.TextWrapping,
            element.LineHeight,
            element.LineStackingStrategy,
            element.Padding,
            element.TextAlignment,
            element.Language);
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

    private static double Quantize(double width) => Math.Ceiling(width / WidthStep) * WidthStep;

    private void PinButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinClicked?.Invoke(this, EventArgs.Empty);
    }
}
