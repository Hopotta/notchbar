using System.Drawing;
using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class FullscreenAppContextServiceTests
{
    [Fact]
    public void CoversBounds_AcceptsSmallRoundingDifferencesAroundMonitorEdges()
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        var window = new Rectangle(-1918, 2, 1916, 1076);

        Assert.True(FullscreenWindowClassifier.CoversBounds(window, monitor, tolerance: 2));
    }

    [Theory]
    [InlineData(-1917, 0, 1920, 1080)]
    [InlineData(-1920, 0, 1917, 1080)]
    [InlineData(-1920, 3, 1920, 1080)]
    [InlineData(-1920, 0, 1920, 1077)]
    public void CoversBounds_RejectsWindowsThatLeaveMoreThanToleranceUncovered(
        int x,
        int y,
        int width,
        int height)
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);

        Assert.False(FullscreenWindowClassifier.CoversBounds(
            new Rectangle(x, y, width, height), monitor, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_AcceptsBorderlessMonitorBoundsWithoutMaximizedState()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);

        Assert.True(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            monitor, monitor, workArea, isMaximized: false, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_AcceptsOsMaximizedWindowCoveringWorkArea()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);
        var codexMaximizedBounds = new Rectangle(-8, -8, 1936, 1056);

        Assert.True(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            codexMaximizedBounds, monitor, workArea, isMaximized: true, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_RejectsLargeFloatingWindowThatIsNotMaximized()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);
        var largeFloatingBounds = new Rectangle(-8, -8, 1936, 1056);

        Assert.False(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            largeFloatingBounds, monitor, workArea, isMaximized: false, tolerance: 2));
    }

    [Fact]
    public void FullscreenClassifier_RejectsMaximizedWindowThatDoesNotCoverWorkArea()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var workArea = new Rectangle(0, 0, 1920, 1040);
        var restoredBounds = new Rectangle(40, 40, 1840, 960);

        Assert.False(FullscreenWindowClassifier.IsFullscreenOrMaximized(
            restoredBounds, monitor, workArea, isMaximized: true, tolerance: 2));
    }

    [Theory]
    [InlineData(true, false, false, 10, 9, true, MonitorPlacementMode.Primary, true)]
    [InlineData(true, false, false, 11, 10, true, MonitorPlacementMode.Primary, true)]
    [InlineData(true, false, false, 11, 10, false, MonitorPlacementMode.Primary, false)]
    [InlineData(true, false, false, 11, 10, false, MonitorPlacementMode.ActiveWindow, true)]
    [InlineData(false, false, false, 11, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, true, false, 11, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, false, true, 11, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, false, false, 0, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    [InlineData(true, false, false, 10, 10, true, MonitorPlacementMode.ActiveWindow, false)]
    public void IsEligibleTarget_AppliesForegroundWindowAndMonitorRules(
        bool isVisible,
        bool isMinimized,
        bool isDesktopShell,
        uint processId,
        uint ownProcessId,
        bool monitorIsPrimary,
        MonitorPlacementMode mode,
        bool expected)
    {
        Assert.Equal(expected, FullscreenWindowClassifier.IsEligibleTarget(
            isVisible,
            isMinimized,
            isDesktopShell,
            processId,
            ownProcessId,
            monitorIsPrimary,
            mode));
    }

    [Fact]
    public void SelectFirstAvailableIcon_PrefersWindowIconBeforeClassAndExecutableFallbacks()
    {
        var windowSmall2 = IntPtr.Zero;
        var windowSmall = new IntPtr(101);
        var windowBig = new IntPtr(102);
        var classSmall = new IntPtr(201);
        var executable = new IntPtr(301);
        var attempts = 0;

        Assert.Equal(windowSmall, FullscreenWindowClassifier.SelectFirstAvailableIcon(
            () => { attempts++; return windowSmall2; },
            () => { attempts++; return windowSmall; },
            () => { attempts++; return windowBig; },
            () => { attempts++; return classSmall; },
            () => { attempts++; return executable; }));
        Assert.Equal(2, attempts);

        Assert.Equal(classSmall, FullscreenWindowClassifier.SelectFirstAvailableIcon(
            () => IntPtr.Zero,
            () => IntPtr.Zero,
            () => classSmall,
            () => executable));
        Assert.Equal(IntPtr.Zero, FullscreenWindowClassifier.SelectFirstAvailableIcon(
            () => IntPtr.Zero,
            () => IntPtr.Zero));
    }

    [Fact]
    public void IconRetryPolicy_UsesIncreasingDelaysThenStops()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), FullscreenIconRetryPolicy.GetDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(750), FullscreenIconRetryPolicy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), FullscreenIconRetryPolicy.GetDelay(2));
        Assert.Null(FullscreenIconRetryPolicy.GetDelay(3));
        Assert.Null(FullscreenIconRetryPolicy.GetDelay(-1));
    }
}
