using NotchBar.Core;

namespace NotchBar.Services;

/// <summary>
/// Pure, retargetable motion state for the island. Window geometry and content
/// progress advance from the same elapsed time so they cannot drift apart.
/// </summary>
public sealed class WindowTransitionMotion
{
    private const double HiddenTopOffset = -(WindowController.CompactHeight - WindowController.HiddenTriggerHeight);

    private readonly SpringMotion _width = new(
        WindowController.DefaultWindowWidth,
        response: 0.34,
        settledDistance: 0.08,
        settledVelocity: 0.5);
    private readonly SpringMotion _height = new(
        WindowController.CompactHeight,
        response: 0.34,
        settledDistance: 0.08,
        settledVelocity: 0.5);
    private readonly SpringMotion _top = new(
        HiddenTopOffset,
        response: 0.30,
        settledDistance: 0.04,
        settledVelocity: 0.35);
    private readonly SpringMotion _expansion = new(
        response: 0.34,
        settledDistance: 0.001,
        settledVelocity: 0.02);
    private readonly SpringMotion _fullscreenBadge = new(
        response: 0.30,
        settledDistance: 0.001,
        settledVelocity: 0.02);
    private readonly SpringMotion _badgeContent = new(
        initialValue: 1d,
        response: 0.24,
        settledDistance: 0.001,
        settledVelocity: 0.02);
    private readonly SpringMotion _badgeAnchor = new(
        response: 0.30,
        settledDistance: 0.001,
        settledVelocity: 0.02);

    private double _compactWidth = WindowController.DefaultWindowWidth;
    private double _expandedWidth = WindowController.DefaultWindowWidth;
    private double _expandedHeight = WindowController.DefaultExpandedHeight;
    private bool _fullscreenBadgeTarget;
    private double _fullscreenBadgeAnchorWidth;

    public WindowTransitionMotion()
    {
        TargetState = NotchState.Hidden;
    }

    public NotchState TargetState { get; private set; }

    public bool IsSettled =>
        _width.IsSettled &&
        _height.IsSettled &&
        _top.IsSettled &&
        _expansion.IsSettled &&
        _fullscreenBadge.IsSettled &&
        _badgeContent.IsSettled &&
        _badgeAnchor.IsSettled;

    public WindowMotionFrame Current
    {
        get
        {
            var expansionProgress = Math.Clamp(_expansion.Value, 0d, 1d);
            // Let an expanded island collapse before the side badge takes its
            // full width. This keeps the fixed envelope unclipped through the
            // transition while still sharing the same spring/rendering clock.
            var badgeProgress = Math.Clamp(_fullscreenBadge.Value, 0d, 1d) * (1d - expansionProgress);
            return new WindowMotionFrame(
                _width.Value + (WindowController.FullscreenBadgeWidth * badgeProgress),
                _height.Value,
                _top.Value,
                expansionProgress,
                TargetState,
                IsSettled,
                badgeProgress,
                Math.Clamp(_badgeContent.Value, 0d, 1d),
                Math.Clamp(_badgeAnchor.Value, 0d, 1d),
                IsFullscreenBadgeGeometryActive ? _fullscreenBadgeAnchorWidth : 0d);
        }
    }

    public void SetPreferredSize(
        double compactWidth,
        double expandedWidth,
        double expandedHeight,
        bool animate)
    {
        _compactWidth = compactWidth;
        _expandedWidth = expandedWidth;
        _expandedHeight = expandedHeight;

        var expanded = !_fullscreenBadgeTarget && TargetState is NotchState.Expanded or NotchState.Pinned;
        SetTarget(_width, expanded ? _expandedWidth : GetCompactWidthTarget(), animate);
        SetTarget(_height, expanded ? _expandedHeight : WindowController.CompactHeight, animate);
    }

    public void SetFullscreenBadge(bool enabled, bool animate)
    {
        if (enabled && !_fullscreenBadgeTarget && _fullscreenBadge.IsSettled && _fullscreenBadge.Value <= 0.001)
        {
            _fullscreenBadgeAnchorWidth = _compactWidth;
        }

        _fullscreenBadgeTarget = enabled;
        Retarget(TargetState, animate);
    }

    public void SetBadgeContentVisible(bool visible, bool animate)
    {
        SetTarget(_badgeContent, visible ? 1d : 0d, animate);
    }

    public void Retarget(NotchState state, bool animate)
    {
        TargetState = _fullscreenBadgeTarget ? NotchState.Compact : state;
        state = TargetState;
        var expanded = !_fullscreenBadgeTarget && state is NotchState.Expanded or NotchState.Pinned;
        var hidden = !_fullscreenBadgeTarget && state == NotchState.Hidden;

        var geometryResponse = hidden ? 0.30 : 0.34;
        _width.Response = geometryResponse;
        _height.Response = geometryResponse;
        _expansion.Response = geometryResponse;
        _top.Response = hidden ? 0.30 : 0.28;

        SetTarget(_width, expanded ? _expandedWidth : GetCompactWidthTarget(), animate);
        SetTarget(_height, expanded ? _expandedHeight : WindowController.CompactHeight, animate);
        SetTarget(_top, hidden ? HiddenTopOffset : 0d, animate);
        SetTarget(_expansion, expanded ? 1d : 0d, animate);
        SetTarget(_fullscreenBadge, _fullscreenBadgeTarget ? 1d : 0d, animate);
        SetTarget(_badgeAnchor, _fullscreenBadgeTarget ? 1d : 0d, animate);
    }

    public bool Step(TimeSpan elapsed)
    {
        var changed = _width.Step(elapsed);
        changed |= _height.Step(elapsed);
        changed |= _top.Step(elapsed);
        changed |= _expansion.Step(elapsed);
        changed |= _fullscreenBadge.Step(elapsed);
        changed |= _badgeContent.Step(elapsed);
        changed |= _badgeAnchor.Step(elapsed);
        return changed;
    }

    private bool IsFullscreenBadgeGeometryActive =>
        _fullscreenBadgeTarget ||
        !_fullscreenBadge.IsSettled ||
        _fullscreenBadge.Value > 0.001 ||
        !_badgeAnchor.IsSettled ||
        _badgeAnchor.Value > 0.001;

    private double GetCompactWidthTarget()
    {
        // Leave room for the right badge while retaining the fixed 560 DIP
        // envelope and the centered left edge of the compact content.
        var maxBadgeCompactWidth = WindowController.EnvelopeWidth - (WindowController.FullscreenBadgeWidth * 2d);
        return _fullscreenBadgeTarget ? Math.Min(_compactWidth, maxBadgeCompactWidth) : _compactWidth;
    }

    private static void SetTarget(SpringMotion motion, double target, bool animate)
    {
        if (animate)
        {
            motion.SetTarget(target);
        }
        else
        {
            motion.SetImmediate(target);
        }
    }
}

public readonly record struct WindowMotionFrame(
    double Width,
    double Height,
    double TopOffset,
    double ExpansionProgress,
    NotchState TargetState,
    bool IsSettled,
    double BadgeProgress = 0d,
    double BadgeContentProgress = 1d,
    double BadgeAnchorProgress = 0d,
    double BadgeAnchorWidth = 0d);
