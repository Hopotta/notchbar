using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotchBar.Core;
using NotchBar.Services;
using NotchBar.UI;

namespace NotchBar;

public partial class MainWindow : Window, IDisposable
{
    private static readonly Duration ItemTransitionDuration = new(TimeSpan.FromMilliseconds(160));
    private const int ApiErrorNotificationPriority = 900;
    private const int ApiErrorNotificationTtlSeconds = 10;

    private readonly StatusStore _statusStore;
    private readonly SettingsService _settings;
    private readonly NotchStateMachine _stateMachine = new();
    private readonly TranslateTransform _compactContentTranslate = new();
    private readonly TranslateTransform _expandedContentTranslate = new();
    private readonly SolidColorBrush _clockTitleOverlayBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _clockTimeOverlayBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _clockDateOverlayBrush = new(Colors.Transparent);
    private readonly ClockOverlayHandoff _clockOverlayHandoff = new();
    private readonly MonitorPlacementService _monitorPlacementService;
    private readonly WindowController _windowController;
    private readonly WindowBlurService _windowBlurService;
    private readonly AutoHideService _autoHideService;
    private readonly HotkeyService _hotkeyService = new();
    private readonly FullscreenSuppressionService? _fullscreenSuppressionService;
    private string? _displayedItemId;
    private bool _pendingItemTransition;
    private bool _contentMorphPrepared;
    private bool _displayedItemIsClock;
    private bool _isFullscreenSuppressed;
    private DispatcherOperation? _clockOverlayLayoutOperation;
    private bool _clockOverlayRenderingSubscribed;
    private long _clockOverlayRenderingGeneration;
    private NotchState _clockOverlaySettledState = NotchState.Compact;
    private bool _disposed;

    public MainWindow(StatusStore statusStore, SettingsService settings)
    {
        _statusStore = statusStore;
        _settings = settings;
        InitializeComponent();

        CompactContent.RenderTransform = _compactContentTranslate;
        ExpandedContent.RenderTransform = _expandedContentTranslate;
        ClockTitleOverlay.Foreground = _clockTitleOverlayBrush;
        ClockTimeOverlay.Foreground = _clockTimeOverlayBrush;
        ClockDateOverlay.Foreground = _clockDateOverlayBrush;

        _monitorPlacementService = new MonitorPlacementService(_settings.MonitorMode);
        _windowController = new WindowController(this, _monitorPlacementService.Current);
        _windowBlurService = new WindowBlurService(this);
        _autoHideService = new AutoHideService(_stateMachine, _settings.AutoHideDelay);
        if (_settings.HideInFullscreen)
        {
            _fullscreenSuppressionService = new FullscreenSuppressionService(_settings.MonitorMode);
            _fullscreenSuppressionService.Changed += FullscreenSuppressionService_OnChanged;
        }

        _monitorPlacementService.Changed += MonitorPlacementService_OnChanged;
        _windowController.MotionFrameChanged += WindowController_OnMotionFrameChanged;
        _stateMachine.StateChanged += StateMachine_OnStateChanged;
        _statusStore.Changed += StatusStore_OnChanged;
        CompactContent.PinClicked += Pin_OnClicked;
        ExpandedContent.PinClicked += Pin_OnClicked;
        ExpandedContent.CollapseRequested += ExpandedContent_OnCollapseRequested;
        DpiChanged += MainWindow_OnDpiChanged;

        RefreshItem();
        ApplyVisualState(_stateMachine.Current);
    }

    public event EventHandler? PinStateChanged;

    public bool IsPinned => _stateMachine.IsPinned;

    public void RefreshTheme()
    {
        if (!_disposed)
        {
            InvalidateClockOverlayForContentChange();
            ApplyBackdropVisual(_windowBlurService.RefreshTheme());
            RefreshItem(applyWindowSize: false);
        }
    }

    public void ShowApiError(string message)
    {
        Debug.WriteLine($"NotchBar API failed to start: {message}");

        if (_disposed)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (detail?.Length > StatusItemValidation.MaxDetailLength)
        {
            detail = detail[..StatusItemValidation.MaxDetailLength];
        }

        try
        {
            _statusStore.AddNotification(new NotificationRequest
            {
                Title = "Local API unavailable",
                Text = "NotchBar is running without API updates",
                Detail = detail,
                Priority = ApiErrorNotificationPriority,
                TtlSeconds = ApiErrorNotificationTtlSeconds
            });
        }
        catch (StatusStoreCapacityException exception)
        {
            Debug.WriteLine($"NotchBar could not show the API error notification: {exception.Message}");
        }
    }

    public void ShowFromExternal()
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(ShowFromExternal);
            return;
        }

        _autoHideService.Cancel();
        if (_stateMachine.Current == NotchState.Hidden)
        {
            _stateMachine.Set(NotchState.Compact);
        }

        if (!_stateMachine.IsPinned && !_isFullscreenSuppressed)
        {
            ScheduleHideForActiveContent();
        }
    }

    public void TogglePinnedFromExternal()
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(TogglePinnedFromExternal);
            return;
        }

        TogglePinnedCore();
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _windowController.Attach();
        ApplyBackdropVisual(_windowBlurService.TryApply());

        _monitorPlacementService.Start();
        _hotkeyService.Pressed += HotkeyService_OnPressed;
        _hotkeyService.RegistrationFailed += HotkeyService_OnRegistrationFailed;
        _hotkeyService.Attach(this, _settings.HotkeyModifiers, _settings.HotkeyKey);
        _fullscreenSuppressionService?.Start();
    }

    private void ApplyBackdropVisual(bool backdropActive)
    {
        IslandBorder.SetResourceReference(
            Border.BackgroundProperty,
            backdropActive ? "IslandBackground" : "IslandFallbackBackground");
    }


    private void MonitorPlacementService_OnChanged(object? sender, MonitorTargetChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _windowController.SetMonitor(e.Target);
        ApplyVisualState(_stateMachine.Current);
    }

    private void HotkeyService_OnPressed(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var wasPinned = _stateMachine.IsPinned;
        _autoHideService.Cancel();
        _stateMachine.ToggleVisibility();
        if (wasPinned != _stateMachine.IsPinned)
        {
            PinStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HotkeyService_OnRegistrationFailed(object? sender, EventArgs e)
    {
        Debug.WriteLine("NotchBar global hotkey could not be registered.");
    }

    private void FullscreenSuppressionService_OnChanged(object? sender, FullscreenSuppressionChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _isFullscreenSuppressed = e.IsSuppressed;
        _windowController.SetSuppressed(_isFullscreenSuppressed);
        if (_isFullscreenSuppressed)
        {
            _autoHideService.ResetPointerState();
        }

        ApplyVisualState(_stateMachine.Current);

        if (!_isFullscreenSuppressed && !_stateMachine.IsPinned && _stateMachine.Current != NotchState.Hidden)
        {
            ScheduleHideForActiveContent();
        }
    }

    private void Window_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_disposed || _isFullscreenSuppressed)
        {
            return;
        }

        _autoHideService.OnMouseEnter();
        _stateMachine.Wake();
    }

    private void Window_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_disposed && !_isFullscreenSuppressed)
        {
            _autoHideService.OnMouseLeave(GetAutoHideDelayForActiveContent());
        }
    }

    private void ContentRoot_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_disposed && !_isFullscreenSuppressed && _stateMachine.VisualState == NotchState.Compact)
        {
            _autoHideService.Cancel();
            _stateMachine.Expand();
            e.Handled = true;
        }
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_disposed && e.Key == Key.Escape && _stateMachine.VisualState == NotchState.Expanded)
        {
            _stateMachine.Collapse();
            e.Handled = true;
        }
    }

    private void ExpandedContent_OnCollapseRequested(object? sender, EventArgs e)
    {
        if (!_disposed && !_isFullscreenSuppressed && _stateMachine.VisualState == NotchState.Expanded)
        {
            _autoHideService.Cancel();
            _autoHideService.OnMouseEnter();
            _stateMachine.Collapse();
        }
    }

    private void Pin_OnClicked(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            TogglePinnedCore();
        }
    }

    private void TogglePinnedCore()
    {
        _autoHideService.Cancel();
        _stateMachine.TogglePinned();
        PinStateChanged?.Invoke(this, EventArgs.Empty);

        if (!_stateMachine.IsPinned && !_isFullscreenSuppressed)
        {
            ScheduleHideForActiveContent();
        }
    }

    private void StateMachine_OnStateChanged(object? sender, NotchStateChangedEventArgs e)
    {
        if (!_disposed)
        {
            ApplyVisualState(e.Current);
        }
    }

    private void ApplyVisualState(NotchState state)
    {
        if (_disposed)
        {
            return;
        }

        var visualState = _stateMachine.VisualState;
        if (!_isFullscreenSuppressed &&
            _clockOverlayHandoff.State is
                ClockOverlayHandoffState.AwaitingLayout or
                ClockOverlayHandoffState.EndpointArmed or
                ClockOverlayHandoffState.EndpointFrameObserved or
                ClockOverlayHandoffState.Release)
        {
            ResumeClockOverlayMotion();
        }

        HiddenTrigger.Visibility = !_isFullscreenSuppressed && state == NotchState.Hidden
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (state is NotchState.Hidden or NotchState.Pinned || _isFullscreenSuppressed)
        {
            _autoHideService.Cancel();
        }

        RefreshItem(applyWindowSize: false);
        ApplyContentVisualState(visualState);
        _windowController.Apply(visualState);
        TryRunPendingItemTransition();
    }

    private void ApplyContentVisualState(NotchState visualState)
    {
        if (_isFullscreenSuppressed)
        {
            SetContentStateImmediate(NotchState.Hidden);
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            SetContentStateImmediate(visualState == NotchState.Hidden
                ? NotchState.Compact
                : visualState);
            return;
        }

        var frame = _windowController.CurrentFrame;
        if (frame.ExpansionProgress > 0.001 ||
            visualState is NotchState.Expanded or NotchState.Pinned)
        {
            PrepareContentMorph();
            ApplyContentMorphFrame(frame.ExpansionProgress);
            return;
        }

        SetContentStateImmediate(NotchState.Compact);
    }

    private void WindowController_OnMotionFrameChanged(
        object? sender,
        WindowMotionFrameChangedEventArgs e)
    {
        if (_disposed || _isFullscreenSuppressed)
        {
            return;
        }

        var frame = e.Frame;
        var isSettledEndpoint = !e.IsTransitionActive &&
            frame.IsSettled &&
            frame.TargetState is NotchState.Compact or NotchState.Expanded or NotchState.Pinned;
        if (!isSettledEndpoint)
        {
            ResumeClockOverlayMotion();
        }

        if (frame.ExpansionProgress > 0.001 ||
            frame.TargetState is NotchState.Expanded or NotchState.Pinned)
        {
            PrepareContentMorph();
            ApplyContentMorphFrame(frame.ExpansionProgress);
        }
        else
        {
            SetContentStateImmediate(NotchState.Compact);
        }

        if (!e.IsTransitionActive && frame.IsSettled)
        {
            var settledState = frame.TargetState is NotchState.Expanded or NotchState.Pinned
                ? NotchState.Expanded
                : NotchState.Compact;
            if (frame.TargetState == NotchState.Hidden ||
                !_displayedItemIsClock ||
                _clockOverlayHandoff.State != ClockOverlayHandoffState.Moving ||
                ClockTransitionOverlay.Visibility != Visibility.Visible)
            {
                SetContentStateImmediate(settledState);
                TryRunPendingItemTransition();
            }
            else
            {
                BeginClockOverlayEndpointHandoff(settledState);
            }
        }
    }

    private void PrepareContentMorph()
    {
        if (_contentMorphPrepared &&
            CompactHost.Visibility == Visibility.Visible &&
            ExpandedHost.Visibility == Visibility.Visible)
        {
            return;
        }

        CompactHost.Visibility = Visibility.Visible;
        ExpandedHost.Visibility = Visibility.Visible;
        CompactHost.Opacity = 1;
        ExpandedHost.Opacity = 1;

        // Arrange the target view once before the first transition frame. The
        // following frames read live arranged positions but never force layout.
        ContentRoot.UpdateLayout();
        _contentMorphPrepared = true;
    }

    private void ApplyContentMorphFrame(double expansionProgress)
    {
        var choreography = TransitionChoreography.Evaluate(expansionProgress);
        // Native window resizing can re-arrange both hosts between frames. Read
        // their lightweight point anchors after that arrange so the target does
        // not remain tied to whichever width started the transition.
        var compactAnchors = CompactContent.CaptureTransitionAnchors(ContentRoot);
        var expandedAnchors = ExpandedContent.CaptureTransitionAnchors(ContentRoot);
        var sharedElementOffsets = SharedElementOffsets.Between(compactAnchors, expandedAnchors);

        ClockTextAnchors compactClockAnchors = default;
        ClockTextAnchors expandedClockAnchors = default;
        var clockOverlayOwnsText = false;
        if (_displayedItemIsClock)
        {
            // These captures only translate cached baselines into live points;
            // FormattedText measurement is cached by each view's DPI/typeface.
            compactClockAnchors = CompactContent.CaptureClockTextAnchors(ContentRoot);
            expandedClockAnchors = ExpandedContent.CaptureClockTextAnchors(ContentRoot);
            clockOverlayOwnsText = AreClockAnchorsUsable(compactClockAnchors) &&
                AreClockAnchorsUsable(expandedClockAnchors) &&
                AreClockTypefacesCompatible(compactClockAnchors, expandedClockAnchors);
        }

        CompactContent.ApplyTransition(
            choreography,
            sharedElementOffsets,
            clockOverlayOwnsText);
        ExpandedContent.ApplyTransition(
            choreography,
            sharedElementOffsets,
            clockOverlayOwnsText);

        if (clockOverlayOwnsText)
        {
            ApplyClockOverlayFrame(
                expansionProgress,
                compactClockAnchors,
                expandedClockAnchors);
        }
        else
        {
            ResetClockOverlayOwnership();
        }
    }

    private void SetContentStateImmediate(NotchState visualState)
    {
        CancelClockOverlayHandoff(continueMoving: false);
        _contentMorphPrepared = false;
        ResetClockOverlayOwnership();
        CompactHost.Opacity = 1;
        ExpandedHost.Opacity = 1;
        CompactContent.ResetTransitionVisuals();
        ExpandedContent.ResetLayoutTransition();

        var hideAll = visualState == NotchState.Hidden && _isFullscreenSuppressed;
        CompactHost.Visibility = !hideAll && visualState != NotchState.Expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExpandedHost.Visibility = !hideAll && visualState == NotchState.Expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void StatusStore_OnChanged(object? sender, StatusStoreChangedEventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => StatusStore_OnChanged(sender, e));
            return;
        }

        InvalidateClockOverlayForContentChange();
        RefreshItem();
        if (e.WakeOnUpdate && !_stateMachine.IsPinned && !_isFullscreenSuppressed)
        {
            _autoHideService.Cancel();
            _stateMachine.Set(NotchState.Compact);
            ScheduleHideForActiveContent();
        }

        TryRunPendingItemTransition();
    }

    private void ScheduleHideForActiveContent()
    {
        _autoHideService.ScheduleHide(GetAutoHideDelayForActiveContent());
    }

    private TimeSpan GetAutoHideDelayForActiveContent()
    {
        var notificationLifetime = _statusStore.GetRemainingNotificationLifetime();
        return notificationLifetime is { } remaining && remaining > TimeSpan.Zero
            ? remaining
            : _settings.AutoHideDelay;
    }

    private void RefreshItem(bool applyWindowSize = true)
    {
        var item = _statusStore.GetDisplayItem();
        if (item is null)
        {
            return;
        }

        if (_displayedItemId is null)
        {
            _displayedItemId = item.Id;
        }
        else if (!string.Equals(_displayedItemId, item.Id, StringComparison.OrdinalIgnoreCase))
        {
            _displayedItemId = item.Id;
            _pendingItemTransition = true;
        }

        CompactContent.ShowItem(item, _stateMachine.IsPinned);
        ExpandedContent.ShowItem(item, _stateMachine.IsPinned);
        _displayedItemIsClock = string.Equals(
            item.Id,
            StatusStore.ClockId,
            StringComparison.Ordinal);
        ClockTitleOverlay.Text = item.Title;
        ClockTimeOverlay.Text = item.Text;
        ClockDateOverlay.Text = item.SecondaryText?.Trim() ?? string.Empty;
        if (_windowController.IsTransitionActive)
        {
            // The new text can change both header anchors. Re-arrange both
            // views before the next shared-element frame instead of rendering
            // one frame against stale positions.
            _contentMorphPrepared = false;
        }

        var compactWidth = CompactContent.GetPreferredWidth();
        var expandedWidth = Math.Max(ExpandedContent.GetPreferredWidth(), compactWidth + 36);
        var expandedHeight = ExpandedContent.GetPreferredHeight(expandedWidth);
        _windowController.SetPreferredSize(compactWidth, expandedWidth, expandedHeight, applyWindowSize);
    }

    private void ApplyClockOverlayFrame(
        double expansionProgress,
        ClockTextAnchors compactAnchors,
        ClockTextAnchors expandedAnchors)
    {
        var title = TransitionChoreography.EvaluateSharedText(
            expansionProgress,
            compactAnchors.Title,
            expandedAnchors.Title);
        var time = TransitionChoreography.EvaluateSharedText(
            expansionProgress,
            compactAnchors.Time,
            expandedAnchors.Time);
        var date = TransitionChoreography.EvaluateClockDate(
            expansionProgress,
            compactAnchors.Date,
            expandedAnchors.Date);

        ClockTransitionOverlay.Visibility = Visibility.Visible;
        ApplyClockTextPlacement(ClockTitleOverlay, _clockTitleOverlayBrush, title);
        ApplyClockTextPlacement(ClockTimeOverlay, _clockTimeOverlayBrush, time);
        ApplyClockTextPlacement(ClockDateOverlay, _clockDateOverlayBrush, date);
    }

    private static void ApplyClockTextPlacement(
        TextBlock element,
        SolidColorBrush foreground,
        ClockTextPlacement placement)
    {
        if (placement.FontFamily is not null &&
            !string.Equals(
                element.FontFamily.Source,
                placement.FontFamily.Source,
                StringComparison.OrdinalIgnoreCase))
        {
            element.FontFamily = placement.FontFamily;
        }

        if (element.FontStyle != placement.FontStyle)
        {
            element.FontStyle = placement.FontStyle;
        }

        if (element.FontWeight != placement.FontWeight)
        {
            element.FontWeight = placement.FontWeight;
        }

        if (element.FontStretch != placement.FontStretch)
        {
            element.FontStretch = placement.FontStretch;
        }

        if (element.FlowDirection != placement.FlowDirection)
        {
            element.FlowDirection = placement.FlowDirection;
        }

        element.FontSize = placement.FontSize;
        if (foreground.Color != placement.ForegroundColor)
        {
            foreground.Color = placement.ForegroundColor;
        }

        Canvas.SetLeft(element, placement.TopLeft.X);
        Canvas.SetTop(element, placement.TopLeft.Y);
        element.Opacity = placement.Opacity;
    }

    private void ResetClockOverlayOwnership()
    {
        ClockTransitionOverlay.Visibility = Visibility.Collapsed;
        ClockTitleOverlay.Opacity = 1;
        ClockTimeOverlay.Opacity = 1;
        ClockDateOverlay.Opacity = 1;
    }

    private void BeginClockOverlayEndpointHandoff(NotchState settledState)
    {
        var endpoint = settledState == NotchState.Expanded
            ? ClockOverlayEndpoint.Expanded
            : ClockOverlayEndpoint.Compact;
        if (_clockOverlayHandoff.IsFinalizingEndpoint(endpoint))
        {
            return;
        }

        CancelClockOverlayCallbacks();
        _clockOverlaySettledState = settledState;
        var generation = _clockOverlayHandoff.BeginAwaitingLayout(endpoint);
        _clockOverlayLayoutOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() => ArmClockOverlayEndpoint(generation, endpoint)));
    }

    private void ArmClockOverlayEndpoint(long generation, ClockOverlayEndpoint endpoint)
    {
        _clockOverlayLayoutOperation = null;
        if (_disposed ||
            _clockOverlayHandoff.Generation != generation ||
            _clockOverlayHandoff.State != ClockOverlayHandoffState.AwaitingLayout)
        {
            return;
        }

        PrepareContentMorph();
        ApplyContentMorphFrame(endpoint == ClockOverlayEndpoint.Expanded ? 1d : 0d);
        ContentRoot.UpdateLayout();

        var anchors = endpoint == ClockOverlayEndpoint.Expanded
            ? ExpandedContent.CaptureFinalClockTextAnchors(ContentRoot)
            : CompactContent.CaptureFinalClockTextAnchors(ContentRoot);
        if (!AreClockAnchorsUsable(anchors))
        {
            SetContentStateImmediate(_clockOverlaySettledState);
            TryRunPendingItemTransition();
            return;
        }

        ApplyClockEndpointOverlay(anchors);
        ClockTransitionOverlay.UpdateLayout();
        if (!_clockOverlayHandoff.TryArmEndpoint(generation))
        {
            return;
        }

        _clockOverlayRenderingGeneration = generation;
        CompositionTarget.Rendering += ClockOverlay_OnRendering;
        _clockOverlayRenderingSubscribed = true;
    }

    private void ApplyClockEndpointOverlay(ClockTextAnchors anchors)
    {
        ClockTransitionOverlay.Visibility = Visibility.Visible;
        ApplyClockTextPlacement(
            ClockTitleOverlay,
            _clockTitleOverlayBrush,
            TransitionChoreography.PlaceAtEndpoint(anchors.Title));
        ApplyClockTextPlacement(
            ClockTimeOverlay,
            _clockTimeOverlayBrush,
            TransitionChoreography.PlaceAtEndpoint(anchors.Time));
        ApplyClockTextPlacement(
            ClockDateOverlay,
            _clockDateOverlayBrush,
            TransitionChoreography.PlaceAtEndpoint(anchors.Date));
    }

    private void ClockOverlay_OnRendering(object? sender, EventArgs e)
    {
        var generation = _clockOverlayRenderingGeneration;
        var action = _clockOverlayHandoff.ObserveRendering(generation);
        if (action != ClockOverlayRenderAction.ReleaseOverlay)
        {
            return;
        }

        CancelClockOverlayCallbacks();
        if (!_clockOverlayHandoff.CompleteRelease(generation))
        {
            return;
        }

        SetContentStateImmediate(_clockOverlaySettledState);
        TryRunPendingItemTransition();
    }

    private void ResumeClockOverlayMotion()
    {
        CancelClockOverlayCallbacks();
        _clockOverlayHandoff.BeginMotion();
    }

    private void CancelClockOverlayHandoff(bool continueMoving)
    {
        CancelClockOverlayCallbacks();
        _clockOverlayHandoff.Cancel(continueMoving);
    }

    private void InvalidateClockOverlayForContentChange()
    {
        if (_clockOverlayHandoff.State is not (
                ClockOverlayHandoffState.AwaitingLayout or
                ClockOverlayHandoffState.EndpointArmed or
                ClockOverlayHandoffState.EndpointFrameObserved or
                ClockOverlayHandoffState.Release))
        {
            return;
        }

        var continueMoving = _windowController.IsTransitionActive;
        CancelClockOverlayHandoff(continueMoving);
        _contentMorphPrepared = false;
        if (!continueMoving)
        {
            SetContentStateImmediate(_stateMachine.VisualState == NotchState.Expanded
                ? NotchState.Expanded
                : NotchState.Compact);
        }
    }

    private void CancelClockOverlayCallbacks()
    {
        _clockOverlayLayoutOperation?.Abort();
        _clockOverlayLayoutOperation = null;
        if (_clockOverlayRenderingSubscribed)
        {
            CompositionTarget.Rendering -= ClockOverlay_OnRendering;
            _clockOverlayRenderingSubscribed = false;
        }
    }

    private void MainWindow_OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var continueMoving = _windowController.IsTransitionActive;
        CancelClockOverlayHandoff(continueMoving);
        _contentMorphPrepared = false;
        if (continueMoving)
        {
            PrepareContentMorph();
            ApplyContentMorphFrame(_windowController.CurrentFrame.ExpansionProgress);
        }
        else
        {
            SetContentStateImmediate(_stateMachine.VisualState == NotchState.Expanded
                ? NotchState.Expanded
                : NotchState.Compact);
        }
    }

    private static bool AreClockAnchorsUsable(ClockTextAnchors anchors) =>
        IsClockAnchorUsable(anchors.Title) &&
        IsClockAnchorUsable(anchors.Time) &&
        IsClockAnchorUsable(anchors.Date);

    private static bool AreClockTypefacesCompatible(
        ClockTextAnchors compact,
        ClockTextAnchors expanded) =>
        TransitionChoreography.HasCompatibleTypeface(compact.Title, expanded.Title) &&
        TransitionChoreography.HasCompatibleTypeface(compact.Time, expanded.Time) &&
        TransitionChoreography.HasCompatibleTypeface(compact.Date, expanded.Date);

    private static bool IsClockAnchorUsable(ClockTextAnchor anchor) =>
        double.IsFinite(anchor.LeadingBaseline.X) &&
        double.IsFinite(anchor.LeadingBaseline.Y) &&
        double.IsFinite(anchor.FontSize) &&
        anchor.FontSize > 0 &&
        double.IsFinite(anchor.BaselineFromTop) &&
        anchor.BaselineFromTop > 0 &&
        anchor.FontFamily is not null;

    private void TryRunPendingItemTransition()
    {
        if (!_pendingItemTransition ||
            _isFullscreenSuppressed ||
            _stateMachine.Current == NotchState.Hidden ||
            _windowController.IsTransitionActive)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            _pendingItemTransition = false;
            return;
        }

        FrameworkElement? target = _stateMachine.VisualState switch
        {
            NotchState.Compact when CompactHost.Visibility == Visibility.Visible => CompactContent,
            NotchState.Expanded when ExpandedHost.Visibility == Visibility.Visible => ExpandedContent,
            _ => null
        };

        if (target is null)
        {
            return;
        }

        var translate = ReferenceEquals(target, CompactContent)
            ? _compactContentTranslate
            : _expandedContentTranslate;

        _pendingItemTransition = false;
        var easing = new CriticallyDampedEase
        {
            Response = 0.3,
            EasingMode = EasingMode.EaseOut
        };

        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, ItemTransitionDuration)
        {
            EasingFunction = easing
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, ItemTransitionDuration)
        {
            EasingFunction = easing
        });
    }

    private void Window_OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _contentMorphPrepared = false;
        CancelClockOverlayHandoff(continueMoving: false);
        ResetClockOverlayOwnership();
        _statusStore.Changed -= StatusStore_OnChanged;
        _stateMachine.StateChanged -= StateMachine_OnStateChanged;
        _monitorPlacementService.Changed -= MonitorPlacementService_OnChanged;
        _windowController.MotionFrameChanged -= WindowController_OnMotionFrameChanged;
        CompactContent.PinClicked -= Pin_OnClicked;
        ExpandedContent.PinClicked -= Pin_OnClicked;
        ExpandedContent.CollapseRequested -= ExpandedContent_OnCollapseRequested;
        DpiChanged -= MainWindow_OnDpiChanged;
        _hotkeyService.Pressed -= HotkeyService_OnPressed;
        _hotkeyService.RegistrationFailed -= HotkeyService_OnRegistrationFailed;
        _hotkeyService.Dispose();
        _autoHideService.Dispose();
        _windowController.Dispose();
        _windowBlurService.Dispose();
        _monitorPlacementService.Dispose();
        if (_fullscreenSuppressionService is not null)
        {
            _fullscreenSuppressionService.Changed -= FullscreenSuppressionService_OnChanged;
            _fullscreenSuppressionService.Dispose();
        }
    }
}
