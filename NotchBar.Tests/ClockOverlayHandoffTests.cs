using NotchBar.UI;
using Xunit;

namespace NotchBar.Tests;

public sealed class ClockOverlayHandoffTests
{
    [Fact]
    public void Endpoint_AdvancesThroughLayoutAndTwoRenderingBoundaries()
    {
        var handoff = new ClockOverlayHandoff();
        handoff.BeginMotion();
        var generation = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Expanded);

        Assert.Equal(ClockOverlayHandoffState.AwaitingLayout, handoff.State);
        Assert.True(handoff.TryArmEndpoint(generation));
        Assert.Equal(ClockOverlayHandoffState.EndpointArmed, handoff.State);

        Assert.Equal(
            ClockOverlayRenderAction.RetainOverlay,
            handoff.ObserveRendering(generation));
        Assert.True(handoff.OwnsOverlay);
        Assert.Equal(ClockOverlayHandoffState.EndpointFrameObserved, handoff.State);

        Assert.Equal(
            ClockOverlayRenderAction.ReleaseOverlay,
            handoff.ObserveRendering(generation));
        Assert.Equal(ClockOverlayHandoffState.Release, handoff.State);
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

        var currentGeneration = handoff.Cancel(continueMoving: true);

        Assert.True(currentGeneration > staleGeneration);
        Assert.Equal(ClockOverlayHandoffState.Moving, handoff.State);
        Assert.Equal(ClockOverlayRenderAction.None, handoff.ObserveRendering(staleGeneration));
        Assert.False(handoff.CompleteRelease(staleGeneration));
    }

    [Fact]
    public void HideOrInvalidation_CancelsOwnershipWithoutStartingMotion()
    {
        var handoff = CreateAtPhase(ClockOverlayHandoffState.EndpointArmed, out var staleGeneration);

        handoff.Cancel(continueMoving: false);

        Assert.Equal(ClockOverlayHandoffState.Inactive, handoff.State);
        Assert.False(handoff.OwnsOverlay);
        Assert.Equal(ClockOverlayRenderAction.None, handoff.ObserveRendering(staleGeneration));
    }

    [Fact]
    public void DuplicateLayoutCallback_IsIdempotent()
    {
        var handoff = new ClockOverlayHandoff();
        var generation = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Compact);

        Assert.True(handoff.TryArmEndpoint(generation));
        Assert.False(handoff.TryArmEndpoint(generation));
        Assert.Equal(ClockOverlayHandoffState.EndpointArmed, handoff.State);
    }

    [Fact]
    public void RepeatedSettledNotification_ReusesCurrentEndpointGeneration()
    {
        var handoff = new ClockOverlayHandoff();
        var first = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Expanded);
        var duplicate = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Expanded);

        Assert.Equal(first, duplicate);
        Assert.Equal(ClockOverlayHandoffState.AwaitingLayout, handoff.State);
    }

    [Fact]
    public void OppositeEndpoint_InvalidatesPendingGeneration()
    {
        var handoff = new ClockOverlayHandoff();
        var expanded = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Expanded);
        var compact = handoff.BeginAwaitingLayout(ClockOverlayEndpoint.Compact);

        Assert.True(compact > expanded);
        Assert.Equal(ClockOverlayEndpoint.Compact, handoff.Endpoint);
        Assert.False(handoff.TryArmEndpoint(expanded));
        Assert.True(handoff.TryArmEndpoint(compact));
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
