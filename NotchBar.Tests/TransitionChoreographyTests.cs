using System.Windows;
using NotchBar.UI;
using Xunit;
using Point = System.Windows.Point;

namespace NotchBar.Tests;

public sealed class TransitionChoreographyTests
{
    [Fact]
    public void Endpoints_PreserveCompactAndExpandedVisualStates()
    {
        var compact = TransitionChoreography.Evaluate(0);
        var expanded = TransitionChoreography.Evaluate(1);

        Assert.Equal(1, compact.CompactSharedOpacity);
        Assert.Equal(0, compact.ExpandedSharedOpacity);
        Assert.Equal(1, compact.CompactSecondaryOpacity);
        Assert.Equal(0, compact.ExpandedBodyOpacity);
        Assert.Equal(8, compact.ExpandedBodyOffsetY);

        Assert.Equal(0, expanded.CompactSharedOpacity);
        Assert.Equal(1, expanded.ExpandedSharedOpacity);
        Assert.Equal(0, expanded.CompactSecondaryOpacity);
        Assert.Equal(1, expanded.ExpandedBodyOpacity);
        Assert.Equal(0, expanded.ExpandedBodyOffsetY);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.35)]
    [InlineData(0.5)]
    [InlineData(0.78)]
    [InlineData(0.95)]
    public void SharedCopies_FollowTheSamePositionThroughoutHandoff(double progress)
    {
        var frame = TransitionChoreography.Evaluate(progress);
        var compact = new Point(18, 12);
        var expanded = new Point(48, 25);
        var offset = expanded - compact;

        var compactRendered = compact + (offset * frame.PositionProgress);
        var expandedRendered = expanded + (offset * (frame.PositionProgress - 1));
        Assert.Equal(compactRendered.X, expandedRendered.X, 10);
        Assert.Equal(compactRendered.Y, expandedRendered.Y, 10);
        Assert.Equal(
            1,
            frame.CompactSharedOpacity + frame.ExpandedSharedOpacity,
            10);
    }

    [Fact]
    public void ExpandedBody_EntersAfterSharedHeaderHandoffBegins()
    {
        var midpoint = TransitionChoreography.Evaluate(0.5);
        var late = TransitionChoreography.Evaluate(0.75);

        Assert.InRange(midpoint.ExpandedSharedOpacity, 0.49, 0.51);
        Assert.Equal(0, midpoint.ExpandedBodyOpacity);
        Assert.True(late.ExpandedBodyOpacity > 0);
        Assert.True(late.ExpandedBodyOffsetY < midpoint.ExpandedBodyOffsetY);
    }

    [Fact]
    public void Evaluation_IsStatelessForRapidReversal()
    {
        var firstPass = TransitionChoreography.Evaluate(0.43);
        _ = TransitionChoreography.Evaluate(0.82);
        var reversed = TransitionChoreography.Evaluate(0.43);

        Assert.Equal(firstPass, reversed);
    }

    [Fact]
    public void ClockText_UsesLeadingBaselineEndpointsAndTypographyScale()
    {
        var compact = new ClockTextAnchor(new Point(250, 24), 10, 8);
        var expanded = new ClockTextAnchor(new Point(32, 34), 15, 12);

        var atCompact = TransitionChoreography.EvaluateSharedText(0, compact, expanded);
        var atExpanded = TransitionChoreography.EvaluateSharedText(1, compact, expanded);

        Assert.Equal(new Point(250, 16), atCompact.TopLeft);
        Assert.Equal(1, atCompact.Scale);
        Assert.Equal(new Point(32, 22), atExpanded.TopLeft);
        Assert.Equal(1.5, atExpanded.Scale);
        Assert.Equal(1, atCompact.Opacity);
        Assert.Equal(1, atExpanded.Opacity);
    }

    [Fact]
    public void ClockText_ClampsProgressOutsideTransitionRange()
    {
        var compact = new ClockTextAnchor(new Point(210, 21), 11, 8);
        var expanded = new ClockTextAnchor(new Point(28, 38), 13.5, 9.818181818181818);

        Assert.Equal(
            TransitionChoreography.EvaluateSharedText(0, compact, expanded),
            TransitionChoreography.EvaluateSharedText(-0.5, compact, expanded));
        Assert.Equal(
            TransitionChoreography.EvaluateSharedText(1, compact, expanded),
            TransitionChoreography.EvaluateSharedText(1.5, compact, expanded));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(0.99)]
    public void ClockText_RemainsFullyVisibleForSingleOwnerMotion(double progress)
    {
        var compact = new ClockTextAnchor(new Point(210, 21), 11, 8);
        var expanded = new ClockTextAnchor(new Point(28, 38), 13.5, 9.818181818181818);

        var placement = TransitionChoreography.EvaluateSharedText(progress, compact, expanded);

        Assert.Equal(1, placement.Opacity);
    }

    [Fact]
    public void ClockText_SameProgressUsesLatestGeometryAnchors()
    {
        var compact = new ClockTextAnchor(new Point(210, 21), 11, 8);
        var firstExpanded = new ClockTextAnchor(new Point(28, 38), 13.5, 9.818181818181818);
        var rearrangedExpanded = firstExpanded with
        {
            LeadingBaseline = new Point(44, 42)
        };

        var first = TransitionChoreography.EvaluateSharedText(0.5, compact, firstExpanded);
        var rearranged = TransitionChoreography.EvaluateSharedText(0.5, compact, rearrangedExpanded);

        Assert.NotEqual(first.LeadingBaseline, rearranged.LeadingBaseline);
        Assert.Equal(8, rearranged.LeadingBaseline.X - first.LeadingBaseline.X, 10);
        Assert.Equal(2, rearranged.LeadingBaseline.Y - first.LeadingBaseline.Y, 10);
    }

    [Fact]
    public void ClockDate_DetachesVerticallyBeforeTravellingHorizontally()
    {
        var compact = new ClockTextAnchor(new Point(286, 23), 11, 8);
        var expanded = new ClockTextAnchor(new Point(28, 78), 13.5, 9.818181818181818);

        var early = TransitionChoreography.EvaluateClockDate(0.14, compact, expanded);
        var compactPlacement = TransitionChoreography.EvaluateClockDate(0, compact, expanded);
        var horizontalTravel = Math.Abs(early.LeadingBaseline.X - compactPlacement.LeadingBaseline.X);
        var verticalTravel = Math.Abs(early.LeadingBaseline.Y - compactPlacement.LeadingBaseline.Y);

        Assert.True(verticalTravel > horizontalTravel * 2);
        Assert.Equal(1, early.Opacity);
    }

    [Fact]
    public void ClockDate_FollowsEndpointExactBoundedArc()
    {
        var compact = new ClockTextAnchor(new Point(286, 23), 11, 8);
        var expanded = new ClockTextAnchor(new Point(28, 78), 13.5, 9.818181818181818);

        var atCompact = TransitionChoreography.EvaluateClockDate(0, compact, expanded);
        var midpoint = TransitionChoreography.EvaluateClockDate(0.5, compact, expanded);
        var atExpanded = TransitionChoreography.EvaluateClockDate(1, compact, expanded);
        var linearMidpointY = (compact.LeadingBaseline.Y + expanded.LeadingBaseline.Y) / 2;

        Assert.Equal(compact.LeadingBaseline, atCompact.LeadingBaseline);
        Assert.Equal(expanded.LeadingBaseline, atExpanded.LeadingBaseline);
        Assert.True(midpoint.LeadingBaseline.Y > linearMidpointY);
        Assert.InRange(
            midpoint.LeadingBaseline.Y,
            Math.Min(compact.LeadingBaseline.Y, expanded.LeadingBaseline.Y),
            Math.Max(compact.LeadingBaseline.Y, expanded.LeadingBaseline.Y) + 8);
    }

    [Fact]
    public void ClockDate_EvaluationIsDeterministicAcrossReversalSamples()
    {
        var compact = new ClockTextAnchor(new Point(286, 23), 11, 8);
        var expanded = new ClockTextAnchor(new Point(28, 78), 13.5, 9.818181818181818);

        var first = TransitionChoreography.EvaluateClockDate(0.37, compact, expanded);
        _ = TransitionChoreography.EvaluateClockDate(0.83, compact, expanded);
        var reversed = TransitionChoreography.EvaluateClockDate(0.37, compact, expanded);

        Assert.Equal(first, reversed);
    }
}
