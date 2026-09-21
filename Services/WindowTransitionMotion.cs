using NotchBar.Core;

namespace NotchBar.Services;

/// <summary>
/// Pure, retargetable motion state for the island. Window geometry and content
/// progress advance from the same elapsed time so they cannot drift apart.
/// </summary>
public sealed class WindowTransitionMotion
{
    private const double HiddenTopOffset = -(WindowController.CompactHeight - WindowController.HiddenTriggerHeight);

    private readonly SpringMotion _width = new(WindowController.DefaultWindowWidth, response: 0.34);
    private readonly SpringMotion _height = new(WindowController.CompactHeight, response: 0.34);
    private readonly SpringMotion _top = new(HiddenTopOffset, response: 0.30);
    private readonly SpringMotion _expansion = new(response: 0.34);

    private double _compactWidth = WindowController.DefaultWindowWidth;
    private double _expandedWidth = WindowController.DefaultWindowWidth;
    private double _expandedHeight = WindowController.DefaultExpandedHeight;

    public WindowTransitionMotion()
    {
        TargetState = NotchState.Hidden;
    }

    public NotchState TargetState { get; private set; }

    public bool IsSettled =>
        _width.IsSettled &&
        _height.IsSettled &&
        _top.IsSettled &&
        _expansion.IsSettled;

    public WindowMotionFrame Current => new(
        _width.Value,
        _height.Value,
        _top.Value,
        Math.Clamp(_expansion.Value, 0d, 1d),
        TargetState,
        IsSettled);

    public void SetPreferredSize(
        double compactWidth,
        double expandedWidth,
        double expandedHeight,
        bool animate)
    {
        _compactWidth = compactWidth;
        _expandedWidth = expandedWidth;
        _expandedHeight = expandedHeight;

        var expanded = TargetState is NotchState.Expanded or NotchState.Pinned;
        SetTarget(_width, expanded ? _expandedWidth : _compactWidth, animate);
        SetTarget(_height, expanded ? _expandedHeight : WindowController.CompactHeight, animate);
    }

    public void Retarget(NotchState state, bool animate)
    {
        TargetState = state;
        var expanded = state is NotchState.Expanded or NotchState.Pinned;
        var hidden = state == NotchState.Hidden;

        var geometryResponse = hidden ? 0.30 : 0.34;
        _width.Response = geometryResponse;
        _height.Response = geometryResponse;
        _expansion.Response = geometryResponse;
        _top.Response = hidden ? 0.30 : 0.28;

        SetTarget(_width, expanded ? _expandedWidth : _compactWidth, animate);
        SetTarget(_height, expanded ? _expandedHeight : WindowController.CompactHeight, animate);
        SetTarget(_top, hidden ? HiddenTopOffset : 0d, animate);
        SetTarget(_expansion, expanded ? 1d : 0d, animate);
    }

    public bool Step(TimeSpan elapsed)
    {
        var changed = _width.Step(elapsed);
        changed |= _height.Step(elapsed);
        changed |= _top.Step(elapsed);
        changed |= _expansion.Step(elapsed);
        return changed;
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
    bool IsSettled);
