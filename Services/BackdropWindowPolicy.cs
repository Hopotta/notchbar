namespace NotchBar.Services;

public static class BackdropWindowPolicy
{
    public const uint WindowStyle = 0x80000000;
    public const uint ExtendedWindowStyle = 0x00200000 | 0x00000080 | 0x08000000 | 0x00000020;
    public const uint ActiveExtendedWindowStyle = ExtendedWindowStyle | 0x00080000;

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

    public static CompanionWindowGeometry DecideGeometry(
        WindowPixelGeometry mainGeometry,
        bool active,
        bool suppressed,
        bool destroyed) => new(
            mainGeometry,
            ShouldShow: active && !suppressed && !destroyed,
            MainPrecedesCompanion: true);
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

public enum PointerPassThroughGateState
{
    Unknown,
    Probing,
    Passed,
    Failed
}

public enum BackdropLifecycleState
{
    CreatedHidden,
    Initializing,
    ProbingInput,
    Active,
    Suppressed,
    Fallback,
    Destroyed
}

public sealed class BackdropActivationGate
{
    private long _generation;

    public long Generation => _generation;
    public PointerPassThroughGateState PointerState { get; private set; }
    public BackdropLifecycleState State { get; private set; } = BackdropLifecycleState.CreatedHidden;
    public bool ClipCommitted { get; private set; }
    public bool FrostObserved { get; private set; }
    public bool CanShow => State == BackdropLifecycleState.Active &&
        PointerState == PointerPassThroughGateState.Passed &&
        ClipCommitted && FrostObserved;

    public long BeginInitialization()
    {
        _generation++;
        PointerState = PointerPassThroughGateState.Unknown;
        ClipCommitted = false;
        FrostObserved = false;
        State = BackdropLifecycleState.Initializing;
        return _generation;
    }

    public bool MarkClipCommitted(long generation)
    {
        if (!IsCurrent(generation, BackdropLifecycleState.Initializing)) return false;
        ClipCommitted = true;
        return true;
    }

    public bool BeginProbe(long generation, bool frostObserved)
    {
        if (!IsCurrent(generation, BackdropLifecycleState.Initializing) || !ClipCommitted) return false;
        FrostObserved = frostObserved;
        PointerState = PointerPassThroughGateState.Probing;
        State = BackdropLifecycleState.ProbingInput;
        return true;
    }

    public bool CompleteProbe(long generation, PointerProbeEvidence evidence)
    {
        if (!IsCurrent(generation, BackdropLifecycleState.ProbingInput)) return false;
        var passed = FrostObserved && evidence.IsVerifiedPass;
        PointerState = passed ? PointerPassThroughGateState.Passed : PointerPassThroughGateState.Failed;
        State = passed ? BackdropLifecycleState.Active : BackdropLifecycleState.Fallback;
        if (!passed) _generation++;
        return passed;
    }

    public void SetSuppressed(bool suppressed)
    {
        if (State == BackdropLifecycleState.Active && suppressed) State = BackdropLifecycleState.Suppressed;
        else if (State == BackdropLifecycleState.Suppressed && !suppressed) State = BackdropLifecycleState.Active;
    }

    public void Fail()
    {
        _generation++;
        PointerState = PointerPassThroughGateState.Failed;
        State = BackdropLifecycleState.Fallback;
    }

    public void Destroy()
    {
        _generation++;
        PointerState = PointerPassThroughGateState.Failed;
        State = BackdropLifecycleState.Destroyed;
    }

    private bool IsCurrent(long generation, BackdropLifecycleState state) =>
        generation == _generation && State == state;
}

public readonly record struct PointerProbeEvidence(
    bool DownReceived,
    bool UpReceived,
    int ExpectedProcessId,
    int ActualProcessId,
    nint ExpectedWindow,
    nint ActualWindow,
    nint ForegroundBefore,
    nint ForegroundAfter,
    nint FocusBefore,
    nint FocusAfter,
    bool TimedOut = false)
{
    public bool IsVerifiedPass =>
        !TimedOut &&
        DownReceived &&
        UpReceived &&
        ExpectedProcessId > 0 &&
        ActualProcessId == ExpectedProcessId &&
        ExpectedWindow != 0 &&
        ActualWindow == ExpectedWindow &&
        ForegroundBefore == ForegroundAfter &&
        FocusBefore == FocusAfter;
}
