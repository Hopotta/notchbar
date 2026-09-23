namespace NotchBar.Services;

public static class BackdropWindowPolicy
{
    public const uint WindowStyle = 0x80000000; // WS_POPUP
    public const uint ExtendedWindowStyle =
        0x00200000 | // WS_EX_NOREDIRECTIONBITMAP
        0x00000080 | // WS_EX_TOOLWINDOW
        0x08000000 | // WS_EX_NOACTIVATE
        0x00000020;  // WS_EX_TRANSPARENT
    public const uint ActiveExtendedWindowStyle = ExtendedWindowStyle | 0x00080000; // WS_EX_LAYERED

    public static BackdropActivationMode SelectActivationMode(
        bool isWindows,
        bool highContrast,
        bool compositionEnabled,
        int windowsBuild)
    {
        if (!isWindows || highContrast || !compositionEnabled)
        {
            return BackdropActivationMode.Fallback;
        }

        if (windowsBuild >= 22000)
        {
            return BackdropActivationMode.Windows11HostBackdrop;
        }

        return windowsBuild is >= 19041 and <= 19045
            ? BackdropActivationMode.Windows10HostBackdrop
            : BackdropActivationMode.Fallback;
    }

    public static bool HasTransparentInputContract(uint extendedStyle) =>
        (extendedStyle & ActiveExtendedWindowStyle) == ActiveExtendedWindowStyle;

    public static CompanionWindowGeometry DecideGeometry(
        WindowPixelGeometry mainGeometry,
        bool active,
        bool suppressed,
        bool destroyed) => new(
            mainGeometry,
            ShouldShow: active && !suppressed && !destroyed,
            MainPrecedesCompanion: true);

    public static BackdropMaskInsets CalculateMaskInsets(uint dpi)
    {
        var effectiveDpi = dpi == 0 ? 96u : dpi;
        var scale = effectiveDpi / 96f;
        // The mask asset has square top corners and 20-pixel bottom/side
        // corners. The visual tree tracks its HWND target, so only the source
        // pixel-to-target scale depends on monitor DPI.
        return new BackdropMaskInsets(
            TopPixels: 0f,
            BottomPixels: 20f,
            LeftPixels: 20f,
            RightPixels: 20f,
            InsetScale: scale);
    }
}

public enum BackdropActivationMode
{
    Fallback,
    Windows11HostBackdrop,
    Windows10HostBackdrop
}

public readonly record struct CompanionWindowGeometry(
    WindowPixelGeometry Bounds,
    bool ShouldShow,
    bool MainPrecedesCompanion);

public readonly record struct BackdropMaskInsets(
    float TopPixels,
    float BottomPixels,
    float LeftPixels,
    float RightPixels,
    float InsetScale);

public enum BackdropLifecycleState
{
    CreatedHidden,
    Initializing,
    Prepared,
    Active,
    Suppressed,
    Fallback,
    Destroyed
}

/// <summary>
/// Prevents the companion from being shown until the composition target has
/// committed its rounded tree and the native click-through styles are verified.
/// Generation checks make stale async preparation results inert.
/// </summary>
public sealed class BackdropActivationGate
{
    private long _generation;

    public long Generation => _generation;
    public BackdropLifecycleState State { get; private set; } = BackdropLifecycleState.CreatedHidden;
    public bool ClipCommitted { get; private set; }
    public bool TransparentInputVerified { get; private set; }
    public bool CanShow => State == BackdropLifecycleState.Active &&
        ClipCommitted && TransparentInputVerified;

    public long BeginInitialization()
    {
        _generation++;
        ClipCommitted = false;
        TransparentInputVerified = false;
        State = BackdropLifecycleState.Initializing;
        return _generation;
    }

    public bool MarkClipCommitted(long generation)
    {
        if (!IsCurrent(generation, BackdropLifecycleState.Initializing))
        {
            return false;
        }

        ClipCommitted = true;
        State = BackdropLifecycleState.Prepared;
        return true;
    }

    public bool MarkInputContractVerified(long generation, bool verified)
    {
        if (!IsCurrent(generation, BackdropLifecycleState.Prepared) || !verified)
        {
            return false;
        }

        TransparentInputVerified = true;
        return true;
    }

    public bool Activate(long generation)
    {
        if (!IsCurrent(generation, BackdropLifecycleState.Prepared) ||
            !ClipCommitted || !TransparentInputVerified)
        {
            return false;
        }

        State = BackdropLifecycleState.Active;
        return true;
    }

    public void SetSuppressed(bool suppressed)
    {
        if (State == BackdropLifecycleState.Active && suppressed)
        {
            State = BackdropLifecycleState.Suppressed;
        }
        else if (State == BackdropLifecycleState.Suppressed && !suppressed)
        {
            State = BackdropLifecycleState.Active;
        }
    }

    public void Fail()
    {
        _generation++;
        State = BackdropLifecycleState.Fallback;
        TransparentInputVerified = false;
    }

    public void Destroy()
    {
        _generation++;
        State = BackdropLifecycleState.Destroyed;
        TransparentInputVerified = false;
    }

    private bool IsCurrent(long generation, BackdropLifecycleState state) =>
        generation == _generation && State == state;
}
