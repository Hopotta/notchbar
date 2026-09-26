using NotchBar.UI;
using Xunit;

namespace NotchBar.Tests;

public sealed class ClockOverlayHandoffTests
{
    [Theory]
    [InlineData(ClockOverlayEndpoint.Compact)]
    [InlineData(ClockOverlayEndpoint.Expanded)]
    public void Endpoint_WaitsForLayoutThenOnePresentedFrameBeforeRelease(ClockOverlayEndpoint endpoint)
    {
        var handoff = new ClockOverlayHandoff();
        handoff.BeginMotion();
        var generation = handoff.BeginAwaitingLayout(endpoint);

        Assert.Equal(ClockOverlayRenderAction.None, handoff.ObserveRendering(generation));
        Assert.True(handoff.TryArmEndpoint(generation));
        Assert.Equal(
            ClockOverlayRenderAction.RetainOverlay,
            handoff.ObserveRendering(generation));
        Assert.True(handoff.OwnsOverlay);
        Assert.Equal(
            ClockOverlayRenderAction.ReleaseOverlay,
            handoff.ObserveRendering(generation));
        Assert.True(handoff.CompleteRelease(generation));
        Assert.False(handoff.OwnsOverlay);
    }

    [Theory]
    [InlineData(ClockOverlayHandoffState.AwaitingLayout)]
    [InlineData(ClockOverlayHandoffState.EndpointArmed)]
    [InlineData(ClockOverlayHandoffState.EndpointFrameObserved)]
    [InlineData(ClockOverlayHandoffState.Release)]
    public void Reversal_InvalidatesEveryPendingCompletionPhase(ClockOverlayHandoffState phase)
    {
        var handoff = CreateAtPhase(phase, out var staleGeneration);

        var motionGeneration = handoff.Cancel(continueMoving: true);

        Assert.True(motionGeneration > staleGeneration);
        Assert.Equal(ClockOverlayHandoffState.Moving, handoff.State);
        Assert.Null(handoff.Endpoint);
        Assert.Equal(ClockOverlayRenderAction.None, handoff.ObserveRendering(staleGeneration));
        Assert.False(handoff.CompleteRelease(staleGeneration));
    }

    [Fact]
    public void NewEndpoint_InvalidatesStaleLayoutGeneration()
    {
        var handoff = new ClockOverlayHandoff();
        var expandedGeneration = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Expanded);
        var compactGeneration = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Compact);

        Assert.True(compactGeneration > expandedGeneration);
        Assert.Equal(ClockOverlayEndpoint.Compact, handoff.Endpoint);
        Assert.False(handoff.TryArmEndpoint(expandedGeneration));
        Assert.True(handoff.TryArmEndpoint(compactGeneration));
    }

    private static ClockOverlayHandoff CreateAtPhase(
        ClockOverlayHandoffState phase,
        out long generation)
    {
        var handoff = new ClockOverlayHandoff();
        generation = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Expanded);
        if (phase == ClockOverlayHandoffState.AwaitingLayout)
        {
            return handoff;
        }

        handoff.TryArmEndpoint(generation);
        if (phase == ClockOverlayHandoffState.EndpointArmed)
        {
            return handoff;
        }

        handoff.ObserveRendering(generation);
        if (phase == ClockOverlayHandoffState.EndpointFrameObserved)
        {
            return handoff;
        }

        handoff.ObserveRendering(generation);
        return handoff;
    }
}
