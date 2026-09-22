namespace NotchBar.UI;

/// <summary>
/// Deterministic ownership state for the Clock shared-element overlay. It has
/// no timing of its own: WPF layout and rendering boundaries advance it using
/// the generation returned when endpoint finalization begins.
/// </summary>
public sealed class ClockOverlayHandoff
{
    private long _generation;

    public ClockOverlayHandoffState State { get; private set; } = ClockOverlayHandoffState.Inactive;

    public ClockOverlayEndpoint? Endpoint { get; private set; }

    public long Generation => _generation;

    public bool OwnsOverlay => State is not ClockOverlayHandoffState.Inactive;

    public long BeginMotion()
    {
        if (State != ClockOverlayHandoffState.Moving)
        {
            _generation++;
            State = ClockOverlayHandoffState.Moving;
            Endpoint = null;
        }

        return _generation;
    }

    public long BeginAwaitingLayout(ClockOverlayEndpoint endpoint)
    {
        if (IsFinalizingEndpoint(endpoint))
        {
            return _generation;
        }

        _generation++;
        State = ClockOverlayHandoffState.AwaitingLayout;
        Endpoint = endpoint;
        return _generation;
    }

    public bool TryArmEndpoint(long generation)
    {
        if (!IsCurrent(generation, ClockOverlayHandoffState.AwaitingLayout))
        {
            return false;
        }

        State = ClockOverlayHandoffState.EndpointArmed;
        return true;
    }

    public ClockOverlayRenderAction ObserveRendering(long generation)
    {
        if (generation != _generation)
        {
            return ClockOverlayRenderAction.None;
        }

        if (State == ClockOverlayHandoffState.EndpointArmed)
        {
            State = ClockOverlayHandoffState.EndpointFrameObserved;
            return ClockOverlayRenderAction.RetainOverlay;
        }

        if (State == ClockOverlayHandoffState.EndpointFrameObserved)
        {
            State = ClockOverlayHandoffState.Release;
            return ClockOverlayRenderAction.ReleaseOverlay;
        }

        return ClockOverlayRenderAction.None;
    }

    public bool CompleteRelease(long generation)
    {
        if (!IsCurrent(generation, ClockOverlayHandoffState.Release))
        {
            return false;
        }

        State = ClockOverlayHandoffState.Inactive;
        Endpoint = null;
        return true;
    }

    public long Cancel(bool continueMoving)
    {
        _generation++;
        State = continueMoving
            ? ClockOverlayHandoffState.Moving
            : ClockOverlayHandoffState.Inactive;
        Endpoint = null;
        return _generation;
    }

    public bool IsFinalizingEndpoint(ClockOverlayEndpoint endpoint) =>
        Endpoint == endpoint && State is
            ClockOverlayHandoffState.AwaitingLayout or
            ClockOverlayHandoffState.EndpointArmed or
            ClockOverlayHandoffState.EndpointFrameObserved or
            ClockOverlayHandoffState.Release;

    private bool IsCurrent(long generation, ClockOverlayHandoffState state) =>
        generation == _generation && State == state;
}

public enum ClockOverlayHandoffState
{
    Inactive,
    Moving,
    AwaitingLayout,
    EndpointArmed,
    EndpointFrameObserved,
    Release
}

public enum ClockOverlayEndpoint
{
    Compact,
    Expanded
}

public enum ClockOverlayRenderAction
{
    None,
    RetainOverlay,
    ReleaseOverlay
}
