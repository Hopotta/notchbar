namespace NotchBar.Services;

/// <summary>
/// Holds a fullscreen wake's auto-hide request until the reveal reaches its
/// settled endpoint, while allowing state changes to cancel the handoff.
/// </summary>
internal sealed class FullscreenWakeAutoHideCoordinator
{
    private bool _pending;

    public bool IsPending => _pending;

    public void Begin() => _pending = true;

    public void Cancel() => _pending = false;

    public bool TryComplete(bool isAtSettledEndpoint, bool canSchedule)
    {
        if (!_pending)
        {
            return false;
        }

        if (!canSchedule)
        {
            _pending = false;
            return false;
        }

        if (!isAtSettledEndpoint)
        {
            return false;
        }

        _pending = false;
        return true;
    }
}
