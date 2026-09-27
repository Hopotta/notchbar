using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class FullscreenZOrderReassertionGateTests
{
    [Fact]
    public void Observe_ReassertsForEntryAndHwndSwitchButNotIconArrival()
    {
        var gate = new FullscreenZOrderReassertionGate();
        var firstApp = new IntPtr(0x1001);
        var secondApp = new IntPtr(0x2002);

        Assert.True(gate.Observe(isFullscreen: true, badgeVisible: true, firstApp, processId: 42));
        Assert.False(gate.Observe(isFullscreen: true, badgeVisible: true, firstApp, processId: 42));

        // A foreground app can replace its top-level HWND without changing PID.
        Assert.True(gate.Observe(isFullscreen: true, badgeVisible: true, secondApp, processId: 42));
    }

    [Fact]
    public void Observe_ReassertsWhenBadgeReturnsAndAfterFullscreenReentry()
    {
        var gate = new FullscreenZOrderReassertionGate();
        var app = new IntPtr(0x3003);

        Assert.True(gate.Observe(isFullscreen: true, badgeVisible: true, app, processId: 7));
        Assert.False(gate.Observe(isFullscreen: true, badgeVisible: false, app, processId: 7));
        Assert.True(gate.Observe(isFullscreen: true, badgeVisible: true, app, processId: 7));

        Assert.False(gate.Observe(isFullscreen: false, badgeVisible: false, IntPtr.Zero, processId: 0));
        Assert.True(gate.Observe(isFullscreen: true, badgeVisible: true, app, processId: 7));
    }

    [Fact]
    public void Observe_DoesNotRaiseOverlayWithoutBadgeOrWindowIdentity()
    {
        var gate = new FullscreenZOrderReassertionGate();

        Assert.False(gate.Observe(isFullscreen: true, badgeVisible: false, new IntPtr(0x4004), processId: 8));
        Assert.False(gate.Observe(isFullscreen: true, badgeVisible: true, IntPtr.Zero, processId: 0));
    }
}
