using System.Windows;
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

    private static double Normalize(double value, double start, double end) =>
        Math.Clamp((value - start) / (end - start), 0d, 1d);

    private static double SmoothStep(double value) => value * value * (3d - (2d * value));
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
