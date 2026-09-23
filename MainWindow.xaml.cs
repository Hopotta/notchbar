using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Point = System.Windows.Point;
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
    private Task<bool> _backdropInitializationTask = Task.FromResult(false);
    private DispatcherOperation? _clockOverlayLayoutOperation;
    private bool _clockOverlayRenderingSubscribed;
    private bool _clockOverlayHasEndpointSizeOverrides;
    private bool _clockOverlayHasTargetTextSettings;
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
        _windowBlurService = new WindowBlurService(this, _windowController);
        _windowBlurService.AvailabilityChanged += WindowBlurService_OnAvailabilityChanged;
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

    public Task<bool> InitializeBackdropAsync() => _backdropInitializationTask;

    public void ActivateBackdrop()
    {
        if (!_disposed)
        {
            ApplyBackdropVisual(_windowBlurService.Activate());
        }
    }

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
        ApplyBackdropVisual(backdropActive: false);
        _backdropInitializationTask = _windowBlurService.InitializeAsync();

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
            backdropActive ? "IslandGlassWash" : "IslandFallbackBackground");
    }


    private void WindowBlurService_OnAvailabilityChanged(
        object? sender,
        BackdropAvailabilityChangedEventArgs e)
    {
        if (!_disposed)
        {
            ApplyBackdropVisual(e.IsActive);
        }
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
        _windowBlurService.SetSuppressed(_isFullscreenSuppressed);
        ApplyBackdropVisual(_windowBlurService.IsActive);
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
        if (!_isFullscreenSuppressed && IsClockOverlayFinalizing)
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
        var isSettledWindowEndpoint = frame.IsSettled &&
            frame.TargetState is NotchState.Compact or NotchState.Expanded or NotchState.Pinned;
        var settledState = frame.TargetState is NotchState.Expanded or NotchState.Pinned
            ? NotchState.Expanded
            : NotchState.Compact;
        var endpoint = settledState == NotchState.Expanded
            ? ClockOverlayEndpoint.Expanded
            : ClockOverlayEndpoint.Compact;
        var shouldHandoffClockOverlay = isSettledWindowEndpoint &&
            _displayedItemIsClock &&
            ClockTransitionOverlay.Visibility == Visibility.Visible &&
            (_clockOverlayHandoff.State == ClockOverlayHandoffState.Moving ||
             _clockOverlayHandoff.IsFinalizingEndpoint(endpoint));

        if (!shouldHandoffClockOverlay && !isSettledWindowEndpoint)
        {
            ResumeClockOverlayMotion();
        }

        // The controller first reports the spring's target, then commits the
        // final HWND size. Keep the last overlay frame until the dispatcher has
        // arranged that committed size and captured the real target anchors.
        if (!shouldHandoffClockOverlay)
        {
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
        }

        if (shouldHandoffClockOverlay)
        {
            // Starting while the controller is finishing the settled render
            // callback is safe: the queued layout operation runs after its final
            // native geometry commit and before the next presented frame.
            BeginClockOverlayEndpointHandoff(settledState);
        }
        else if (!e.IsTransitionActive && frame.IsSettled)
        {
            SetContentStateImmediate(settledState);
            TryRunPendingItemTransition();
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
        ResetClockOverlayEndpointSizes();
        var settingsFromExpandedTarget = _stateMachine.VisualState is NotchState.Expanded or NotchState.Pinned;
        ApplyClockOverlayTextSettings(
            ClockTitleOverlay,
            settingsFromExpandedTarget ? expandedAnchors.Title : compactAnchors.Title);
        ApplyClockOverlayTextSettings(
            ClockTimeOverlay,
            settingsFromExpandedTarget ? expandedAnchors.Time : compactAnchors.Time);
        ApplyClockOverlayTextSettings(
            ClockDateOverlay,
            settingsFromExpandedTarget ? expandedAnchors.Date : compactAnchors.Date);
        ApplyClockOverlayMotionSize(ClockTitleOverlay, compactAnchors.Title, expandedAnchors.Title);
        ApplyClockOverlayMotionSize(ClockTimeOverlay, compactAnchors.Time, expandedAnchors.Time);
        ApplyClockOverlayMotionSize(ClockDateOverlay, compactAnchors.Date, expandedAnchors.Date);
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
        ResetClockOverlayEndpointSizes();
        ResetClockOverlaySizes();
        ClearClockOverlayTextSettings();
        ClockTitleOverlay.Opacity = 1;
        ClockTimeOverlay.Opacity = 1;
        ClockDateOverlay.Opacity = 1;
    }

    private bool IsClockOverlayFinalizing =>
        _clockOverlayHandoff.State is
            ClockOverlayHandoffState.AwaitingLayout or
            ClockOverlayHandoffState.EndpointArmed or
            ClockOverlayHandoffState.EndpointFrameObserved or
            ClockOverlayHandoffState.Release;

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

        var exactProgress = endpoint == ClockOverlayEndpoint.Expanded ? 1d : 0d;
        PrepareContentMorph();
        ContentRoot.UpdateLayout();
        ApplyContentMorphFrame(exactProgress);
        ContentRoot.UpdateLayout();

        ArrangeClockEndpointUnderOverlay(endpoint);

        // Capture after the final endpoint transforms and final host geometry
        // have both reached Measure/Arrange. The expanded date specifically comes
        // from SimpleSecondaryText after BodyMotionTranslate reaches zero.
        var anchors = endpoint == ClockOverlayEndpoint.Expanded
            ? ExpandedContent.CaptureFinalClockTextAnchors(ContentRoot)
            : CompactContent.CaptureFinalClockTextAnchors(ContentRoot);
        if (!AreFinalClockAnchorsUsable(anchors))
        {
            SetContentStateImmediate(_clockOverlaySettledState);
            TryRunPendingItemTransition();
            return;
        }

        ApplyClockEndpointOverlay(anchors);
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

        SetClockOverlayLayout(ClockTitleOverlay, anchors.Title);
        SetClockOverlayLayout(ClockTimeOverlay, anchors.Time);
        SetClockOverlayLayout(ClockDateOverlay, anchors.Date);
        _clockOverlayHasEndpointSizeOverrides = true;
        ClockTransitionOverlay.UpdateLayout();

        // Match actual arranged element origins, not just computed baseline
        // coordinates. This corrects any difference from Canvas arrange,
        // layout rounding, or device-pixel snapping at the current DPI.
        AlignClockOverlayToTarget(ClockTitleOverlay, anchors.Title);
        AlignClockOverlayToTarget(ClockTimeOverlay, anchors.Time);
        AlignClockOverlayToTarget(ClockDateOverlay, anchors.Date);
        ClockTransitionOverlay.UpdateLayout();
        AlignClockOverlayToTarget(ClockTitleOverlay, anchors.Title);
        AlignClockOverlayToTarget(ClockTimeOverlay, anchors.Time);
        AlignClockOverlayToTarget(ClockDateOverlay, anchors.Date);
        ClockTransitionOverlay.UpdateLayout();
    }

    private void ResetClockOverlayEndpointSizes()
    {
        if (!_clockOverlayHasEndpointSizeOverrides)
        {
            return;
        }

        _clockOverlayHasEndpointSizeOverrides = false;
        ClockTitleOverlay.Width = double.NaN;
        ClockTitleOverlay.Height = double.NaN;
        ClockTimeOverlay.Width = double.NaN;
        ClockTimeOverlay.Height = double.NaN;
        ClockDateOverlay.Width = double.NaN;
        ClockDateOverlay.Height = double.NaN;
    }

    private void ResetClockOverlaySizes()
    {
        ResetClockOverlaySize(ClockTitleOverlay);
        ResetClockOverlaySize(ClockTimeOverlay);
        ResetClockOverlaySize(ClockDateOverlay);
        _clockOverlayHasEndpointSizeOverrides = false;
    }

    private static void ResetClockOverlaySize(TextBlock overlay)
    {
        if (!double.IsNaN(overlay.Width))
        {
            overlay.Width = double.NaN;
        }
        if (!double.IsNaN(overlay.Height))
        {
            overlay.Height = double.NaN;
        }
    }

    private void ClearClockOverlayTextSettings()
    {
        if (!_clockOverlayHasTargetTextSettings)
        {
            return;
        }

        _clockOverlayHasTargetTextSettings = false;
        ClearClockOverlayTextSettings(ClockTitleOverlay);
        ClearClockOverlayTextSettings(ClockTimeOverlay);
        ClearClockOverlayTextSettings(ClockDateOverlay);
    }

    private static void ClearClockOverlayTextSettings(TextBlock overlay)
    {
        overlay.ClearValue(FrameworkElement.UseLayoutRoundingProperty);
        overlay.ClearValue(UIElement.SnapsToDevicePixelsProperty);
        overlay.ClearValue(TextOptions.TextFormattingModeProperty);
        overlay.ClearValue(TextOptions.TextRenderingModeProperty);
        overlay.ClearValue(TextOptions.TextHintingModeProperty);
        overlay.ClearValue(TextBlock.TextTrimmingProperty);
        overlay.ClearValue(TextBlock.TextWrappingProperty);
        overlay.ClearValue(TextBlock.LineHeightProperty);
        overlay.ClearValue(TextBlock.LineStackingStrategyProperty);
        overlay.ClearValue(TextBlock.PaddingProperty);
        overlay.ClearValue(TextBlock.TextAlignmentProperty);
        overlay.ClearValue(FrameworkElement.LanguageProperty);
    }

    private void ApplyClockOverlayTextSettings(TextBlock overlay, ClockTextAnchor target)
    {
        _clockOverlayHasTargetTextSettings = true;
        if (overlay.UseLayoutRounding != target.UseLayoutRounding)
        {
            overlay.UseLayoutRounding = target.UseLayoutRounding;
        }
        if (overlay.SnapsToDevicePixels != target.SnapsToDevicePixels)
        {
            overlay.SnapsToDevicePixels = target.SnapsToDevicePixels;
        }
        if (TextOptions.GetTextFormattingMode(overlay) != target.TextFormattingMode)
        {
            TextOptions.SetTextFormattingMode(overlay, target.TextFormattingMode);
        }
        if (TextOptions.GetTextRenderingMode(overlay) != target.TextRenderingMode)
        {
            TextOptions.SetTextRenderingMode(overlay, target.TextRenderingMode);
        }
        if (TextOptions.GetTextHintingMode(overlay) != target.TextHintingMode)
        {
            TextOptions.SetTextHintingMode(overlay, target.TextHintingMode);
        }
        if (overlay.TextTrimming != target.TextTrimming)
        {
            overlay.TextTrimming = target.TextTrimming;
        }
        if (overlay.TextWrapping != target.TextWrapping)
        {
            overlay.TextWrapping = target.TextWrapping;
        }
        if (!overlay.LineHeight.Equals(target.LineHeight))
        {
            overlay.LineHeight = target.LineHeight;
        }
        if (overlay.LineStackingStrategy != target.LineStackingStrategy)
        {
            overlay.LineStackingStrategy = target.LineStackingStrategy;
        }
        if (!overlay.Padding.Equals(target.Padding))
        {
            overlay.Padding = target.Padding;
        }
        if (overlay.TextAlignment != target.TextAlignment)
        {
            overlay.TextAlignment = target.TextAlignment;
        }
        if (target.Language is not null && overlay.Language != target.Language)
        {
            overlay.Language = target.Language;
        }
    }

    private static void ApplyClockOverlayMotionSize(
        TextBlock overlay,
        ClockTextAnchor compact,
        ClockTextAnchor expanded)
    {
        var size = TransitionChoreography.EvaluateTextMotionSize(compact, expanded);
        if (double.IsFinite(size.Width) && size.Width > 0 && !overlay.Width.Equals(size.Width))
        {
            overlay.Width = size.Width;
        }
        if (double.IsFinite(size.Height) && size.Height > 0 && !overlay.Height.Equals(size.Height))
        {
            overlay.Height = size.Height;
        }
    }

    private void SetClockOverlayLayout(TextBlock overlay, ClockTextAnchor target)
    {
        if (double.IsFinite(target.RenderedWidth) && target.RenderedWidth > 0)
        {
            overlay.Width = target.RenderedWidth;
        }

        if (double.IsFinite(target.RenderedHeight) && target.RenderedHeight > 0)
        {
            overlay.Height = target.RenderedHeight;
        }

        ApplyClockOverlayTextSettings(overlay, target);
    }

    private void AlignClockOverlayToTarget(TextBlock overlay, ClockTextAnchor target)
    {
        if (!double.IsFinite(target.ArrangedTopLeft.X) ||
            !double.IsFinite(target.ArrangedTopLeft.Y))
        {
            return;
        }

        var currentOrigin = overlay.TranslatePoint(new Point(0, 0), ContentRoot);
        var left = Canvas.GetLeft(overlay);
        var top = Canvas.GetTop(overlay);
        if (!double.IsFinite(left))
        {
            left = 0;
        }

        if (!double.IsFinite(top))
        {
            top = 0;
        }

        Canvas.SetLeft(overlay, left + target.ArrangedTopLeft.X - currentOrigin.X);
        Canvas.SetTop(overlay, top + target.ArrangedTopLeft.Y - currentOrigin.Y);
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

        ReleaseClockOverlayOwnership();
        TryRunPendingItemTransition();
    }

    private void ArrangeClockEndpointUnderOverlay(ClockOverlayEndpoint endpoint)
    {
        // Commit the exact final host/transform state while the overlay still
        // owns the Clock glyphs. Only the target Clock TextBlocks stay hidden;
        // they remain measured and arranged for exact endpoint capture.
        CompactContent.ResetTransitionVisuals();
        ExpandedContent.ResetLayoutTransition();
        CompactHost.Opacity = 1;
        ExpandedHost.Opacity = 1;

        var expanded = endpoint == ClockOverlayEndpoint.Expanded;
        CompactHost.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            ExpandedContent.SetClockTextOverlayOwned(owned: true);
        }
        else
        {
            CompactContent.SetClockTextOverlayOwned(owned: true);
        }

        _contentMorphPrepared = false;
        ContentRoot.UpdateLayout();
    }

    private void ReleaseClockOverlayOwnership()
    {
        // The target host is already at final geometry; release changes only
        // the overlay and the opacity of the real target Clock TextBlocks.
        ClockTransitionOverlay.Visibility = Visibility.Collapsed;
        if (_clockOverlaySettledState == NotchState.Expanded)
        {
            ExpandedContent.SetClockTextOverlayOwned(owned: false);
        }
        else
        {
            CompactContent.SetClockTextOverlayOwned(owned: false);
        }
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
        if (!IsClockOverlayFinalizing)
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

    private static bool AreFinalClockAnchorsUsable(ClockTextAnchors anchors) =>
        AreClockAnchorsUsable(anchors) &&
        IsClockAnchorArranged(anchors.Title) &&
        IsClockAnchorArranged(anchors.Time) &&
        IsClockAnchorArranged(anchors.Date);

    private static bool IsClockAnchorArranged(ClockTextAnchor anchor) =>
        double.IsFinite(anchor.ArrangedTopLeft.X) &&
        double.IsFinite(anchor.ArrangedTopLeft.Y) &&
        double.IsFinite(anchor.RenderedWidth) &&
        anchor.RenderedWidth > 0 &&
        double.IsFinite(anchor.RenderedHeight) &&
        anchor.RenderedHeight > 0;

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
        _windowBlurService.AvailabilityChanged -= WindowBlurService_OnAvailabilityChanged;
        CompactContent.PinClicked -= Pin_OnClicked;
        ExpandedContent.PinClicked -= Pin_OnClicked;
        ExpandedContent.CollapseRequested -= ExpandedContent_OnCollapseRequested;
        DpiChanged -= MainWindow_OnDpiChanged;
        _hotkeyService.Pressed -= HotkeyService_OnPressed;
        _hotkeyService.RegistrationFailed -= HotkeyService_OnRegistrationFailed;
        _hotkeyService.Dispose();
        _autoHideService.Dispose();
        _windowBlurService.Dispose();
        _windowController.Dispose();
        _monitorPlacementService.Dispose();
        if (_fullscreenSuppressionService is not null)
        {
            _fullscreenSuppressionService.Changed -= FullscreenSuppressionService_OnChanged;
            _fullscreenSuppressionService.Dispose();
        }
    }
}
