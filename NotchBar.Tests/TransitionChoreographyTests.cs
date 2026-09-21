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
}
