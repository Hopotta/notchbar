using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private bool _clockAnchorsAvailable;
    private SharedElementOffsets _sharedElementOffsets;
    private ClockTextAnchors _compactClockAnchors;
    private ClockTextAnchors _expandedClockAnchors;
    private bool _isFullscreenSuppressed;
    private bool _disposed;

    public MainWindow(StatusStore statusStore, SettingsService settings)
    {
        _statusStore = statusStore;
        _settings = settings;
        InitializeComponent();

        CompactContent.RenderTransform = _compactContentTranslate;
        ExpandedContent.RenderTransform = _expandedContentTranslate;

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

        RefreshItem();
        ApplyVisualState(_stateMachine.Current);
    }

    public event EventHandler? PinStateChanged;

    public bool IsPinned => _stateMachine.IsPinned;

    public void RefreshTheme()
    {
        if (!_disposed)
        {
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
        _windowBlurService.TryApply();

        _monitorPlacementService.Start();
        _hotkeyService.Pressed += HotkeyService_OnPressed;
        _hotkeyService.RegistrationFailed += HotkeyService_OnRegistrationFailed;
        _hotkeyService.Attach(this, _settings.HotkeyModifiers, _settings.HotkeyKey);
        _fullscreenSuppressionService?.Start();
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
        // following frames only update render transforms and opacity.
        ContentRoot.UpdateLayout();
        var compactAnchors = CompactContent.CaptureTransitionAnchors(ContentRoot);
        var expandedAnchors = ExpandedContent.CaptureTransitionAnchors(ContentRoot);
        _sharedElementOffsets = SharedElementOffsets.Between(compactAnchors, expandedAnchors);
        _clockAnchorsAvailable = false;
        if (_displayedItemIsClock)
        {
            var compactClockAnchors = CompactContent.CaptureClockTextAnchors(ContentRoot);
            var expandedClockAnchors = ExpandedContent.CaptureClockTextAnchors(ContentRoot);
            if (AreClockAnchorsUsable(compactClockAnchors) &&
                AreClockAnchorsUsable(expandedClockAnchors))
            {
                _compactClockAnchors = compactClockAnchors;
                _expandedClockAnchors = expandedClockAnchors;
                _clockAnchorsAvailable = true;
            }
        }

        _contentMorphPrepared = true;
    }

    private void ApplyContentMorphFrame(double expansionProgress)
    {
        var choreography = TransitionChoreography.Evaluate(expansionProgress);
        var clockOverlayOwnsText = _displayedItemIsClock && _clockAnchorsAvailable;

        CompactContent.ApplyTransition(
            choreography,
            _sharedElementOffsets,
            clockOverlayOwnsText);
        ExpandedContent.ApplyTransition(
            choreography,
            _sharedElementOffsets,
            clockOverlayOwnsText);

        if (clockOverlayOwnsText)
        {
            ApplyClockOverlayFrame(expansionProgress);
        }
        else
        {
            ResetClockOverlayOwnership();
        }
    }

    private void SetContentStateImmediate(NotchState visualState)
    {
        _contentMorphPrepared = false;
        _clockAnchorsAvailable = false;
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
            _clockAnchorsAvailable = false;
        }

        var compactWidth = CompactContent.GetPreferredWidth();
        var expandedWidth = Math.Max(ExpandedContent.GetPreferredWidth(), compactWidth + 36);
        var expandedHeight = ExpandedContent.GetPreferredHeight(expandedWidth);
        _windowController.SetPreferredSize(compactWidth, expandedWidth, expandedHeight, applyWindowSize);
    }

    private void ApplyClockOverlayFrame(double expansionProgress)
    {
        var title = TransitionChoreography.EvaluateSharedText(
            expansionProgress,
            _compactClockAnchors.Title,
            _expandedClockAnchors.Title);
        var time = TransitionChoreography.EvaluateSharedText(
            expansionProgress,
            _compactClockAnchors.Time,
            _expandedClockAnchors.Time);
        var date = TransitionChoreography.EvaluateClockDate(
            expansionProgress,
            _compactClockAnchors.Date,
            _expandedClockAnchors.Date);

        ClockTransitionOverlay.Visibility = Visibility.Visible;
        ApplyClockTextPlacement(ClockTitleOverlay, ClockTitleOverlayScale, title);
        ApplyClockTextPlacement(ClockTimeOverlay, ClockTimeOverlayScale, time);
        ApplyClockTextPlacement(ClockDateOverlay, ClockDateOverlayScale, date);
    }

    private static void ApplyClockTextPlacement(
        TextBlock element,
        ScaleTransform scale,
        ClockTextPlacement placement)
    {
        Canvas.SetLeft(element, placement.TopLeft.X);
        Canvas.SetTop(element, placement.TopLeft.Y);
        scale.ScaleX = placement.Scale;
        scale.ScaleY = placement.Scale;
        element.Opacity = placement.Opacity;
    }

    private void ResetClockOverlayOwnership()
    {
        ClockTransitionOverlay.Visibility = Visibility.Collapsed;
        ClockTitleOverlay.Opacity = 1;
        ClockTimeOverlay.Opacity = 1;
        ClockDateOverlay.Opacity = 1;
    }

    private static bool AreClockAnchorsUsable(ClockTextAnchors anchors) =>
        IsClockAnchorUsable(anchors.Title) &&
        IsClockAnchorUsable(anchors.Time) &&
        IsClockAnchorUsable(anchors.Date);

    private static bool IsClockAnchorUsable(ClockTextAnchor anchor) =>
        double.IsFinite(anchor.LeadingBaseline.X) &&
        double.IsFinite(anchor.LeadingBaseline.Y) &&
        double.IsFinite(anchor.FontSize) &&
        anchor.FontSize > 0 &&
        double.IsFinite(anchor.BaselineFromTop) &&
        anchor.BaselineFromTop > 0;

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
        _clockAnchorsAvailable = false;
        ResetClockOverlayOwnership();
        _statusStore.Changed -= StatusStore_OnChanged;
        _stateMachine.StateChanged -= StateMachine_OnStateChanged;
        _monitorPlacementService.Changed -= MonitorPlacementService_OnChanged;
        _windowController.MotionFrameChanged -= WindowController_OnMotionFrameChanged;
        CompactContent.PinClicked -= Pin_OnClicked;
        ExpandedContent.PinClicked -= Pin_OnClicked;
        ExpandedContent.CollapseRequested -= ExpandedContent_OnCollapseRequested;
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
