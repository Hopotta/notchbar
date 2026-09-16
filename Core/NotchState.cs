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
    private NotchState _pinnedVisualState = NotchState.Compact;

    public NotchState Current { get; private set; } = NotchState.Hidden;

    public NotchState VisualState => Current == NotchState.Pinned ? _pinnedVisualState : Current;

    public bool IsPinned => Current == NotchState.Pinned;

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
        else if (Current == NotchState.Pinned && _pinnedVisualState == NotchState.Compact)
        {
            _pinnedVisualState = NotchState.Expanded;
            NotifyVisualStateChanged();
        }
    }

    public void Collapse()
    {
        if (Current == NotchState.Expanded)
        {
            Set(NotchState.Compact);
        }
        else if (Current == NotchState.Pinned && _pinnedVisualState == NotchState.Expanded)
        {
            _pinnedVisualState = NotchState.Compact;
            NotifyVisualStateChanged();
        }
    }

    public void TogglePinned()
    {
        if (Current == NotchState.Pinned)
        {
            Set(_pinnedVisualState);
            return;
        }

        _pinnedVisualState = Current == NotchState.Expanded ? NotchState.Expanded : NotchState.Compact;
        Set(NotchState.Pinned);
    }

    public void ToggleVisibility()
    {
        Set(Current == NotchState.Hidden ? NotchState.Compact : NotchState.Hidden);
    }

    private void NotifyVisualStateChanged()
    {
        StateChanged?.Invoke(this, new NotchStateChangedEventArgs(Current, Current));
    }
}

public sealed class NotchStateChangedEventArgs(NotchState previous, NotchState current) : EventArgs
{
    public NotchState Previous { get; } = previous;
    public NotchState Current { get; } = current;
}
