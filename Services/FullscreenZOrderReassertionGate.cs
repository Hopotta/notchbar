namespace NotchBar.Services;

/// <summary>
/// Decides when a passive full-screen badge needs a one-shot Z-order restore.
/// Icon-only updates intentionally leave the foreground-window identity intact.
/// </summary>
internal sealed class FullscreenZOrderReassertionGate
{
    private IntPtr _windowHandle;
    private uint _processId;
    private bool _hasIdentity;
    private bool _wasFullscreen;
    private bool _wasBadgeVisible;

    public bool Observe(bool isFullscreen, bool badgeVisible, IntPtr windowHandle, uint processId)
    {
        if (!isFullscreen)
        {
            Reset();
            return false;
        }

        var hasIdentity = windowHandle != IntPtr.Zero;
        var identityChanged = hasIdentity &&
            (!_hasIdentity || windowHandle != _windowHandle || processId != _processId);
        var shouldReassert = badgeVisible && hasIdentity &&
            (!_wasFullscreen || !_wasBadgeVisible || identityChanged);

        _wasFullscreen = true;
        _wasBadgeVisible = badgeVisible;
        _hasIdentity = hasIdentity;
        _windowHandle = hasIdentity ? windowHandle : IntPtr.Zero;
        _processId = hasIdentity ? processId : 0;
        return shouldReassert;
    }

    public void Reset()
    {
        _windowHandle = IntPtr.Zero;
        _processId = 0;
        _hasIdentity = false;
        _wasFullscreen = false;
        _wasBadgeVisible = false;
    }
}
