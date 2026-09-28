using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class FullscreenWakeAutoHideCoordinatorTests
{
    [Fact]
    public void TryComplete_WaitsForSettledEndpointAndCompletesOnce()
    {
        var coordinator = new FullscreenWakeAutoHideCoordinator();

        coordinator.Begin();

        Assert.False(coordinator.TryComplete(isAtSettledEndpoint: false, canSchedule: true));
        Assert.True(coordinator.TryComplete(isAtSettledEndpoint: true, canSchedule: true));
        Assert.False(coordinator.TryComplete(isAtSettledEndpoint: true, canSchedule: true));
    }

    [Fact]
    public void TryComplete_CancelsWhenWakeIsNoLongerValid()
    {
        var coordinator = new FullscreenWakeAutoHideCoordinator();

        coordinator.Begin();

        Assert.False(coordinator.TryComplete(isAtSettledEndpoint: false, canSchedule: false));
        Assert.False(coordinator.TryComplete(isAtSettledEndpoint: true, canSchedule: true));
    }

    [Fact]
    public void Cancel_ClearsPendingRequest()
    {
        var coordinator = new FullscreenWakeAutoHideCoordinator();

        coordinator.Begin();
        coordinator.Cancel();

        Assert.False(coordinator.TryComplete(isAtSettledEndpoint: true, canSchedule: true));
    }
}
