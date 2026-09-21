using System.Drawing;
using NotchBar.Core;
using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class WindowTransitionMotionTests
{
    [Fact]
    public void SpringMotion_ZeroOrNegativeElapsed_DoesNotAdvance()
    {
        var motion = new SpringMotion();
        motion.SetTarget(1);

        Assert.False(motion.Step(TimeSpan.Zero));
        Assert.False(motion.Step(TimeSpan.FromMilliseconds(-10)));
        Assert.Equal(0, motion.Value);
        Assert.Equal(0, motion.Velocity);
    }

    [Fact]
    public void SpringMotion_SlowFrame_IsClampedToFiftyMilliseconds()
    {
        var slowFrame = new SpringMotion();
        var clampedFrame = new SpringMotion();
        slowFrame.SetTarget(1);
        clampedFrame.SetTarget(1);

        slowFrame.Step(TimeSpan.FromSeconds(1));
        clampedFrame.Step(TimeSpan.FromMilliseconds(50));

        Assert.Equal(clampedFrame.Value, slowFrame.Value, 12);
        Assert.Equal(clampedFrame.Velocity, slowFrame.Velocity, 12);
    }

    [Fact]
    public void TransitionMotion_SettlesWithinAVisuallyPracticalInterval()
    {
        var motion = CreateVisibleCompactMotion();
        motion.Retarget(NotchState.Expanded, animate: true);

        var frames = 0;
        while (frames < 60 && !motion.IsSettled)
        {
            motion.Step(TimeSpan.FromSeconds(1d / 60d));
            frames++;
        }

        Assert.True(motion.IsSettled);
        Assert.InRange(frames, 1, 55);
        Assert.Equal(500, motion.Current.Width, 6);
        Assert.Equal(220, motion.Current.Height, 6);
        Assert.Equal(1, motion.Current.ExpansionProgress, 6);
    }

    [Theory]
    [InlineData(96u, 320, 37, -1120, -35)]
    [InlineData(120u, 400, 46, -1160, -44)]
    [InlineData(144u, 480, 56, -1200, -53)]
    public void PixelGeometry_IsDeterministicAcrossCommonDpiScales(
        uint dpi,
        int expectedWidth,
        int expectedHeight,
        int expectedX,
        int expectedY)
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        var frame = new WindowMotionFrame(
            Width: 320,
            Height: 37,
            TopOffset: -35,
            ExpansionProgress: 0,
            TargetState: NotchState.Hidden,
            IsSettled: false);

        var geometry = WindowPixelGeometry.Calculate(monitor, dpi, frame, isSuppressed: false);

        Assert.Equal(expectedWidth, geometry.Width);
        Assert.Equal(expectedHeight, geometry.Height);
        Assert.Equal(expectedX, geometry.X);
        Assert.Equal(expectedY, geometry.Y);
    }

    [Fact]
    public void ExpandedToHidden_RetargetsWithoutGeometryJumpAndSettlesHidden()
    {
        var motion = CreateVisibleCompactMotion();
        motion.Retarget(NotchState.Expanded, animate: true);
        Advance(motion, 12, TimeSpan.FromSeconds(1d / 60d));
        var beforeHide = motion.Current;

        motion.Retarget(NotchState.Hidden, animate: true);
        var retargeted = motion.Current;

        Assert.Equal(beforeHide.Width, retargeted.Width);
        Assert.Equal(beforeHide.Height, retargeted.Height);
        Assert.Equal(beforeHide.TopOffset, retargeted.TopOffset);
        Assert.Equal(beforeHide.ExpansionProgress, retargeted.ExpansionProgress);

        Settle(motion);
        var settled = motion.Current;
        Assert.Equal(NotchState.Hidden, settled.TargetState);
        Assert.Equal(320, settled.Width, 6);
        Assert.Equal(WindowController.CompactHeight, settled.Height, 6);
        Assert.Equal(
            -(WindowController.CompactHeight - WindowController.HiddenTriggerHeight),
            settled.TopOffset,
            6);
        Assert.Equal(0, settled.ExpansionProgress, 6);
    }

    [Fact]
    public void CompactExpandedCompact_ReversesFromLiveMotionState()
    {
        var motion = CreateVisibleCompactMotion();
        motion.Retarget(NotchState.Expanded, animate: true);
        Advance(motion, 8, TimeSpan.FromSeconds(1d / 120d));
        var beforeReverse = motion.Current;

        motion.Retarget(NotchState.Compact, animate: true);
        var retargeted = motion.Current;

        Assert.Equal(beforeReverse.Width, retargeted.Width);
        Assert.Equal(beforeReverse.Height, retargeted.Height);
        Assert.Equal(beforeReverse.ExpansionProgress, retargeted.ExpansionProgress);

        Settle(motion);
        AssertCompact(motion.Current);
    }

    [Fact]
    public void ExpandedCompactExpanded_ReversesWithoutResettingToAnEndpoint()
    {
        var motion = CreateVisibleCompactMotion();
        motion.Retarget(NotchState.Expanded, animate: false);
        motion.Retarget(NotchState.Compact, animate: true);
        Advance(motion, 10, TimeSpan.FromSeconds(1d / 90d));
        var beforeReverse = motion.Current;

        motion.Retarget(NotchState.Expanded, animate: true);

        Assert.Equal(beforeReverse.Height, motion.Current.Height);
        Assert.InRange(motion.Current.ExpansionProgress, 0.01, 0.99);
        Settle(motion);
        Assert.Equal(220, motion.Current.Height, 6);
        Assert.Equal(1, motion.Current.ExpansionProgress, 6);
    }

    [Fact]
    public void HiddenCompact_RetainsCompactGeometryAndOnlyMovesTopEdge()
    {
        var motion = new WindowTransitionMotion();
        motion.SetPreferredSize(320, 500, 220, animate: false);
        var hidden = motion.Current;

        motion.Retarget(NotchState.Compact, animate: true);
        Advance(motion, 4, TimeSpan.FromSeconds(1d / 60d));

        Assert.Equal(hidden.Width, motion.Current.Width);
        Assert.Equal(hidden.Height, motion.Current.Height);
        Assert.True(motion.Current.TopOffset > hidden.TopOffset);
        Assert.Equal(0, motion.Current.ExpansionProgress);
    }

    [Fact]
    public void PreferredSizeChangeDuringExpansion_RetargetsWithoutJump()
    {
        var motion = CreateVisibleCompactMotion();
        motion.Retarget(NotchState.Expanded, animate: true);
        Advance(motion, 10, TimeSpan.FromSeconds(1d / 60d));
        var beforeResize = motion.Current;

        motion.SetPreferredSize(332, 540, 260, animate: true);

        Assert.Equal(beforeResize.Width, motion.Current.Width);
        Assert.Equal(beforeResize.Height, motion.Current.Height);
        Settle(motion);
        Assert.Equal(540, motion.Current.Width, 6);
        Assert.Equal(260, motion.Current.Height, 6);
    }

    [Fact]
    public void EqualElapsedTime_IsRefreshRateIndependent()
    {
        var sixtyHertz = CreateVisibleCompactMotion();
        var oneFortyFourHertz = CreateVisibleCompactMotion();
        sixtyHertz.Retarget(NotchState.Expanded, animate: true);
        oneFortyFourHertz.Retarget(NotchState.Expanded, animate: true);

        Advance(sixtyHertz, 30, TimeSpan.FromSeconds(1d / 60d));
        Advance(oneFortyFourHertz, 72, TimeSpan.FromSeconds(1d / 144d));

        Assert.Equal(sixtyHertz.Current.Width, oneFortyFourHertz.Current.Width, 3);
        Assert.Equal(sixtyHertz.Current.Height, oneFortyFourHertz.Current.Height, 3);
        Assert.Equal(
            sixtyHertz.Current.ExpansionProgress,
            oneFortyFourHertz.Current.ExpansionProgress,
            3);
    }

    private static WindowTransitionMotion CreateVisibleCompactMotion()
    {
        var motion = new WindowTransitionMotion();
        motion.SetPreferredSize(320, 500, 220, animate: false);
        motion.Retarget(NotchState.Compact, animate: false);
        return motion;
    }

    private static void AssertCompact(WindowMotionFrame frame)
    {
        Assert.Equal(NotchState.Compact, frame.TargetState);
        Assert.Equal(320, frame.Width, 6);
        Assert.Equal(WindowController.CompactHeight, frame.Height, 6);
        Assert.Equal(0, frame.TopOffset, 6);
        Assert.Equal(0, frame.ExpansionProgress, 6);
    }

    private static void Advance(
        WindowTransitionMotion motion,
        int frameCount,
        TimeSpan elapsedPerFrame)
    {
        for (var frame = 0; frame < frameCount; frame++)
        {
            motion.Step(elapsedPerFrame);
        }
    }

    private static void Settle(WindowTransitionMotion motion)
    {
        for (var frame = 0; frame < 600 && !motion.IsSettled; frame++)
        {
            motion.Step(TimeSpan.FromSeconds(1d / 60d));
        }

        Assert.True(motion.IsSettled);
    }
}
