using System.Drawing;
using NotchBar.Core;
using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class WindowTransitionMotionTests
{
    [Fact]
    public void SpringMotion_ZeroElapsed_DoesNotAdvance()
    {
        var motion = new SpringMotion();
        motion.SetTarget(1);

        Assert.False(motion.Step(TimeSpan.Zero));
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
    [InlineData(96u, 560, 335, -1240, -35, 120, 0, 320, 37, 35)]
    [InlineData(120u, 700, 419, -1310, -44, 150, 0, 400, 46, 44)]
    [InlineData(144u, 840, 503, -1380, -53, 180, 0, 480, 56, 53)]
    public void EnvelopeGeometry_UsesFixedEnvelopeAndKeepsIslandWithinIt(
        uint dpi,
        int envelopeWidth,
        int envelopeHeight,
        int envelopeX,
        int envelopeY,
        int hiddenIslandX,
        int hiddenIslandY,
        int hiddenIslandWidth,
        int hiddenIslandHeight,
        int visibleIslandY)
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080);
        var hidden = WindowEnvelopeGeometry.Calculate(
            monitor,
            dpi,
            CreateFrame(width: 320, height: 37, topOffset: -35),
            isSuppressed: false);
        var visibleCompact = WindowEnvelopeGeometry.Calculate(
            monitor,
            dpi,
            CreateFrame(width: 320, height: 37, topOffset: 0),
            isSuppressed: false);
        var expanded = WindowEnvelopeGeometry.Calculate(
            monitor,
            dpi,
            CreateFrame(width: 500, height: 220, topOffset: 0),
            isSuppressed: false);

        Assert.Equal(envelopeWidth, hidden.Envelope.Width);
        Assert.Equal(envelopeHeight, hidden.Envelope.Height);
        Assert.Equal(envelopeX, hidden.Envelope.X);
        Assert.Equal(envelopeY, hidden.Envelope.Y);
        Assert.Equal(hidden.Envelope, visibleCompact.Envelope);
        Assert.Equal(hidden.Envelope, expanded.Envelope);
        Assert.Equal(hiddenIslandX, hidden.Island.X);
        Assert.Equal(hiddenIslandY, hidden.Island.Y);
        Assert.Equal(hiddenIslandWidth, hidden.Island.Width);
        Assert.Equal(hiddenIslandHeight, hidden.Island.Height);
        Assert.Equal(visibleIslandY, visibleCompact.Island.Y);
        Assert.Equal(visibleIslandY, expanded.Island.Y);
        Assert.InRange(expanded.Island.X, 0, envelopeWidth - expanded.Island.Width);
        Assert.InRange(expanded.Island.Y, 0, envelopeHeight - expanded.Island.Height);
    }

    [Fact]
    public void EnvelopeGeometry_ClampsToNarrowMonitorAndMovesWholeEnvelopeDuringSuppression()
    {
        var narrowMonitor = new Rectangle(1920, -80, 420, 900);
        var expanded = WindowEnvelopeGeometry.Calculate(
            narrowMonitor,
            dpi: 120,
            CreateFrame(width: 500, height: 220, topOffset: 0),
            isSuppressed: false);
        var suppressed = WindowEnvelopeGeometry.Calculate(
            narrowMonitor,
            dpi: 120,
            CreateFrame(width: 500, height: 220, topOffset: 0),
            isSuppressed: true);

        Assert.Equal(narrowMonitor.Left, expanded.Envelope.X);
        Assert.Equal(narrowMonitor.Width, expanded.Envelope.Width);
        Assert.Equal(narrowMonitor.Width, expanded.Island.Width);
        Assert.Equal(0, expanded.Island.X);
        Assert.Equal(narrowMonitor.Top - suppressed.Envelope.Height, suppressed.Envelope.Y);
        Assert.Equal(narrowMonitor.Top, suppressed.Envelope.Y + suppressed.Envelope.Height);
        Assert.Equal(expanded.Island, suppressed.Island);
    }

    [Theory]
    [InlineData(96u)]
    [InlineData(120u)]
    [InlineData(144u)]
    public void EnvelopeGeometry_FractionalSpringFramesRetainTheirTargetScreenTop(uint dpi)
    {
        var monitor = new Rectangle(-1920, -40, 1920, 1080);
        var scale = dpi / 96d;

        foreach (var topOffset in new[] { -34.1, -23.7, -11.2, -0.3 })
        {
            var geometry = WindowEnvelopeGeometry.Calculate(
                monitor,
                dpi,
                CreateFrame(width: 420, height: 130, topOffset),
                isSuppressed: false);
            var expectedTop = monitor.Top + (int)Math.Round(
                topOffset * scale,
                MidpointRounding.AwayFromZero);

            Assert.Equal(expectedTop, geometry.Envelope.Y + geometry.Island.Y);
        }
    }

    [Fact]
    public void EnvelopeGeometry_ClipsTransientReversalOvershootToFixedIslandLimits()
    {
        var geometry = WindowEnvelopeGeometry.Calculate(
            new Rectangle(0, 0, 1920, 1080),
            dpi: 96,
            CreateFrame(width: 620, height: 340, topOffset: 340),
            isSuppressed: false);

        Assert.Equal(560, geometry.Island.Width);
        Assert.Equal(300, geometry.Island.Height);
        Assert.Equal(35, geometry.Island.Y);
        Assert.Equal(335, geometry.Island.Y + geometry.Island.Height);
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

    private static WindowMotionFrame CreateFrame(double width, double height, double topOffset) =>
        new(
            Width: width,
            Height: height,
            TopOffset: topOffset,
            ExpansionProgress: 0,
            TargetState: NotchState.Compact,
            IsSettled: false);

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
