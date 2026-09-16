namespace NotchBar.Core;

public enum NotchState
{
    Hidden,
    Compact,
    Expanded,
    Pinned
}

public sealed class NotchStateMachine
{
    public NotchState Current { get; private set; } = NotchState.Hidden;

    public event EventHandler<NotchStateChangedEventArgs>? StateChanged;

    public bool Set(NotchState next)
    {
        if (Current == next)
        {
            return false;
        }

        var previous = Current;
        Current = next;
        StateChanged?.Invoke(this, new NotchStateChangedEventArgs(previous, next));
        return true;
    }

    public void Wake()
    {
        if (Current == NotchState.Hidden)
        {
            Set(NotchState.Compact);
        }
    }

    public void Expand()
    {
        if (Current == NotchState.Compact)
        {
            Set(NotchState.Expanded);
        }
    }

    public void Collapse()
    {
        if (Current == NotchState.Expanded)
        {
            Set(NotchState.Compact);
        }
    }

    public void TogglePinned()
    {
        Set(Current == NotchState.Pinned ? NotchState.Compact : NotchState.Pinned);
    }

    public void ToggleVisibility()
    {
        Set(Current == NotchState.Hidden ? NotchState.Compact : NotchState.Hidden);
    }
}

public sealed class NotchStateChangedEventArgs(NotchState previous, NotchState current) : EventArgs
{
    public NotchState Previous { get; } = previous;
    public NotchState Current { get; } = current;
}
