using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class FullscreenWakeInputTests
{
    [Fact]
    public void HoverEdgeLatch_EmitsOneEnterAndLeaveForDuplicateInputPaths()
    {
        var latch = new HoverEdgeLatch();

        // WM_MOUSEMOVE and WM_INPUT can report the same boundary crossing.
        Assert.True(latch.SetHovered(true));
        Assert.False(latch.SetHovered(true));

        Assert.True(latch.SetHovered(false));
        Assert.False(latch.SetHovered(false));
    }

    [Fact]
    public void HoverEdgeLatch_ResetAllowsNextShowToWakeAgain()
    {
        var latch = new HoverEdgeLatch();
        Assert.True(latch.SetHovered(true));

        latch.Reset();

        Assert.True(latch.SetHovered(true));
    }
}
