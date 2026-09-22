using System.Windows;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WindowsFlowDirection = System.Windows.FlowDirection;
using WindowsFontStretch = System.Windows.FontStretch;
using WindowsFontStyle = System.Windows.FontStyle;
using WindowsFontWeight = System.Windows.FontWeight;
using Point = System.Windows.Point;
using Vector = System.Windows.Vector;

namespace NotchBar.UI;

/// <summary>
/// Pure choreography derived from the window's expansion spring. Keeping these
/// values as functions of one progress value makes interruption and reversal
/// deterministic without timers or a second animation clock.
/// </summary>
public static class TransitionChoreography
{
    private const double SharedHandoffStart = 0.38;
    private const double SharedHandoffEnd = 0.62;
    private const double CompactSecondaryExitStart = 0.10;
    private const double CompactSecondaryExitEnd = 0.34;
    private const double ExpandedBodyEnterStart = 0.58;
    private const double ExpandedBodyEnterEnd = 0.90;
    private const double DateHorizontalTravelStart = 0.18;
    private const double DateVerticalTravelEnd = 0.88;
    private const double DateDetachmentArc = 6;

    public static TransitionChoreographyFrame Evaluate(double expansionProgress)
    {
        var progress = Math.Clamp(expansionProgress, 0d, 1d);
        var positionProgress = SmoothStep(progress);
        var expandedSharedOpacity = SmoothStep(
            Normalize(progress, SharedHandoffStart, SharedHandoffEnd));
        var compactSecondaryExit = SmoothStep(
            Normalize(progress, CompactSecondaryExitStart, CompactSecondaryExitEnd));
        var expandedBodyOpacity = SmoothStep(
            Normalize(progress, ExpandedBodyEnterStart, ExpandedBodyEnterEnd));

        return new TransitionChoreographyFrame(
            positionProgress,
            1d - expandedSharedOpacity,
            expandedSharedOpacity,
            1d - compactSecondaryExit,
            -2d * compactSecondaryExit,
            expandedBodyOpacity,
            8d * (1d - expandedBodyOpacity));
    }

    public static ClockTextPlacement EvaluateSharedText(
        double expansionProgress,
        ClockTextAnchor compact,
        ClockTextAnchor expanded)
    {
        var progress = SmoothStep(Math.Clamp(expansionProgress, 0d, 1d));
        return PlaceText(
            compact,
            expanded,
            progress,
            progress,
            progress,
            verticalDetachment: 0);
    }

    public static ClockTextPlacement EvaluateClockDate(
        double expansionProgress,
        ClockTextAnchor compact,
        ClockTextAnchor expanded)
    {
        var progress = Math.Clamp(expansionProgress, 0d, 1d);
        var horizontalProgress = SmoothStep(
            Normalize(progress, DateHorizontalTravelStart, 1d));
        var verticalProgress = SmoothStep(
            Normalize(progress, 0d, DateVerticalTravelEnd));
        var detachment = DateDetachmentArc * Math.Sin(Math.PI * progress);

        return PlaceText(
            compact,
            expanded,
            horizontalProgress,
            verticalProgress,
            SmoothStep(progress),
            detachment);
    }

    private static ClockTextPlacement PlaceText(
        ClockTextAnchor compact,
        ClockTextAnchor expanded,
        double horizontalProgress,
        double verticalProgress,
        double typographyProgress,
        double verticalDetachment)
    {
        typographyProgress = Math.Clamp(typographyProgress, 0d, 1d);
        var fontSize = Lerp(compact.FontSize, expanded.FontSize, typographyProgress);
        var baselineFromTop = Lerp(
            compact.BaselineFromTop,
            expanded.BaselineFromTop,
            typographyProgress);
        var leadingBaseline = new Point(
            Lerp(compact.LeadingBaseline.X, expanded.LeadingBaseline.X, horizontalProgress),
            Lerp(compact.LeadingBaseline.Y, expanded.LeadingBaseline.Y, verticalProgress) +
            verticalDetachment);
        var topLeft = new Point(
            leadingBaseline.X,
            leadingBaseline.Y - baselineFromTop);

        // Typeface properties are discrete in WPF. Clock overlay ownership is
        // only enabled for matching endpoint typefaces, so carrying the
        // expanded value at the exact endpoint is lossless without a mid-path
        // weight/style switch. Font size and color remain continuous.
        var typography = typographyProgress >= 1d ? expanded : compact;

        return new ClockTextPlacement(
            topLeft,
            leadingBaseline,
            fontSize,
            baselineFromTop,
            typography.FontFamily,
            typography.FontStyle,
            typography.FontWeight,
            typography.FontStretch,
            typography.FlowDirection,
            Lerp(compact.ForegroundColor, expanded.ForegroundColor, typographyProgress),
            Opacity: 1d);
    }

    public static bool HasCompatibleTypeface(ClockTextAnchor compact, ClockTextAnchor expanded) =>
        string.Equals(
            compact.FontFamily?.Source,
            expanded.FontFamily?.Source,
            StringComparison.OrdinalIgnoreCase) &&
        compact.FontStyle == expanded.FontStyle &&
        compact.FontWeight == expanded.FontWeight &&
        compact.FontStretch == expanded.FontStretch &&
        compact.FlowDirection == expanded.FlowDirection;

    private static double Normalize(double value, double start, double end) =>
        Math.Clamp((value - start) / (end - start), 0d, 1d);

    private static double SmoothStep(double value) => value * value * (3d - (2d * value));

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * progress);

    private static MediaColor Lerp(MediaColor start, MediaColor end, double progress) => MediaColor.FromArgb(
        Lerp(start.A, end.A, progress),
        Lerp(start.R, end.R, progress),
        Lerp(start.G, end.G, progress),
        Lerp(start.B, end.B, progress));

    private static byte Lerp(byte start, byte end, double progress) =>
        (byte)Math.Clamp(
            Math.Round(Lerp((double)start, end, progress), MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);
}

public readonly record struct TransitionChoreographyFrame(
    double PositionProgress,
    double CompactSharedOpacity,
    double ExpandedSharedOpacity,
    double CompactSecondaryOpacity,
    double CompactSecondaryOffsetY,
    double ExpandedBodyOpacity,
    double ExpandedBodyOffsetY);

public readonly record struct SharedElementAnchors(
    Point? Status,
    Point Title,
    Point Summary,
    Point Pin);

public readonly record struct SharedElementOffsets(
    Vector Status,
    Vector Title,
    Vector Summary,
    Vector Pin)
{
    public static SharedElementOffsets Between(
        SharedElementAnchors compact,
        SharedElementAnchors expanded) => new(
            compact.Status is { } compactStatus && expanded.Status is { } expandedStatus
                ? expandedStatus - compactStatus
                : default,
            expanded.Title - compact.Title,
            expanded.Summary - compact.Summary,
            expanded.Pin - compact.Pin);
}

public readonly record struct ClockTextAnchor(
    Point LeadingBaseline,
    double FontSize,
    double BaselineFromTop,
    MediaFontFamily? FontFamily = null,
    WindowsFontStyle FontStyle = default,
    WindowsFontWeight FontWeight = default,
    WindowsFontStretch FontStretch = default,
    WindowsFlowDirection FlowDirection = WindowsFlowDirection.LeftToRight,
    MediaColor ForegroundColor = default);

public readonly record struct ClockTextAnchors(
    ClockTextAnchor Title,
    ClockTextAnchor Time,
    ClockTextAnchor Date);

public readonly record struct ClockTextPlacement(
    Point TopLeft,
    Point LeadingBaseline,
    double FontSize,
    double BaselineFromTop,
    MediaFontFamily? FontFamily,
    WindowsFontStyle FontStyle,
    WindowsFontWeight FontWeight,
    WindowsFontStretch FontStretch,
    WindowsFlowDirection FlowDirection,
    MediaColor ForegroundColor,
    double Opacity);
