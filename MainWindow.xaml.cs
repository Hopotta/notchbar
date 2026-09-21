using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NotchBar.Core;
using NotchBar.Services;

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
    private readonly SpringMotion _contentMotion = new(response: 0.34);
    private readonly MonitorPlacementService _monitorPlacementService;
    private readonly WindowController _windowController;
    private readonly WindowBlurService _windowBlurService;
    private readonly AutoHideService _autoHideService;
    private readonly HotkeyService _hotkeyService = new();
    private readonly FullscreenSuppressionService? _fullscreenSuppressionService;
    private string? _displayedItemId;
    private NotchState _renderedVisualState = NotchState.Hidden;
    private long _layoutTransitionVersion;
    private bool _pendingItemTransition;
    private bool _contentMotionActive;
    private DateTime _contentMotionLastFrame;
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
        _stateMachine.StateChanged += StateMachine_OnStateChanged;
        _statusStore.Changed += StatusStore_OnChanged;
        CompactContent.PinClicked += Pin_OnClicked;
        ExpandedContent.PinClicked += Pin_OnClicked;
        ExpandedContent.CollapseRequested += ExpandedContent_OnCollapseRequested;
        CompositionTarget.Rendering += CompositionTarget_OnRendering;

        RefreshItem();
        ApplyVisualState(_stateMachine.Current);
    }

    public event EventHandler? PinStateChanged;

    public bool IsPinned => _stateMachine.IsPinned;

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
            StopContentMotion();
            SetContentStateImmediate(NotchState.Hidden);
            _renderedVisualState = NotchState.Hidden;
            return;
        }

        if (visualState == NotchState.Hidden)
        {
            // The window itself moves out of the screen. Keep the compact
            // surface mounted inside it so the edge transition does not turn
            // into a separate fade-out animation.
            StopContentMotion();
            SetContentStateImmediate(NotchState.Compact);
            _renderedVisualState = NotchState.Hidden;
            return;
        }

        if (_renderedVisualState == NotchState.Hidden)
        {
            // The island is physically sliding into view; showing the content
            // immediately keeps it attached to that movement instead of
            // making it pop in after the window has arrived.
            StopContentMotion();
            SetContentStateImmediate(visualState);
            _renderedVisualState = visualState;
            _pendingItemTransition = false;
            return;
        }

        if (_renderedVisualState == visualState && !_contentMotionActive)
        {
            EnsureContentHostVisible(visualState);
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            StopContentMotion();
            SetContentStateImmediate(visualState);
            _renderedVisualState = visualState;
            return;
        }

        StartContentMorph(visualState);
        _renderedVisualState = visualState;
        _pendingItemTransition = false;
    }

    private void StartContentMorph(NotchState targetState)
    {
        if (!_contentMotionActive)
        {
            var currentProgress = _renderedVisualState == NotchState.Expanded ? 1d : 0d;
            _contentMotion.SetImmediate(currentProgress);
            CompactHost.Visibility = Visibility.Visible;
            ExpandedHost.Visibility = Visibility.Visible;
            ExpandedContent.ResetLayoutTransition();
        }

        _contentMotion.SetTarget(targetState == NotchState.Expanded ? 1d : 0d);
        _contentMotionActive = true;
        _contentMotionLastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering -= CompositionTarget_OnRendering;
        CompositionTarget.Rendering += CompositionTarget_OnRendering;
        ApplyContentMorphFrame();
    }

    private void CompositionTarget_OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_contentMotionActive)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - _contentMotionLastFrame;
        _contentMotionLastFrame = now;
        _contentMotion.Step(elapsed);
        ApplyContentMorphFrame();

        if (!_contentMotion.IsSettled)
        {
            return;
        }

        _contentMotionActive = false;
        CompositionTarget.Rendering -= CompositionTarget_OnRendering;
        var expanded = _contentMotion.Target > 0.5;
        var settledHost = expanded ? ExpandedHost : CompactHost;
        var dismissedHost = expanded ? CompactHost : ExpandedHost;
        var settledTranslate = expanded ? ExpandedHostTranslate : CompactHostTranslate;
        var settledScale = expanded ? ExpandedHostScale : CompactHostScale;
        var dismissedTranslate = expanded ? CompactHostTranslate : ExpandedHostTranslate;
        var dismissedScale = expanded ? CompactHostScale : ExpandedHostScale;

        dismissedHost.Visibility = Visibility.Collapsed;
        ResetHost(dismissedHost, dismissedTranslate, dismissedScale);
        settledHost.Visibility = Visibility.Visible;
        ResetHost(settledHost, settledTranslate, settledScale);
        ExpandedContent.ResetLayoutTransition();
    }

    private void ApplyContentMorphFrame()
    {
        var progress = Math.Clamp(_contentMotion.Value, 0d, 1d);
        var smoothProgress = progress * progress * (3d - (2d * progress));

        CompactHost.Visibility = Visibility.Visible;
        ExpandedHost.Visibility = Visibility.Visible;
        CompactHost.Opacity = 1d - smoothProgress;
        ExpandedHost.Opacity = smoothProgress;
        CompactHostTranslate.Y = -1.5d * smoothProgress;
        ExpandedHostTranslate.Y = 4d * (1d - smoothProgress);
        CompactHostScale.ScaleX = 1d - (0.008d * smoothProgress);
        CompactHostScale.ScaleY = 1d - (0.018d * smoothProgress);
        ExpandedHostScale.ScaleX = 0.992d + (0.008d * smoothProgress);
        ExpandedHostScale.ScaleY = 0.972d + (0.028d * smoothProgress);
    }

    private void StopContentMotion()
    {
        _contentMotionActive = false;
        CompositionTarget.Rendering -= CompositionTarget_OnRendering;
    }

    private void SetContentStateImmediate(NotchState visualState)
    {
        _layoutTransitionVersion++;
        ResetHost(CompactHost, CompactHostTranslate, CompactHostScale);
        ResetHost(ExpandedHost, ExpandedHostTranslate, ExpandedHostScale);
        ExpandedContent.ResetLayoutTransition();

        CompactHost.Visibility = visualState == NotchState.Compact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedHost.Visibility = visualState == NotchState.Expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EnsureContentHostVisible(NotchState visualState)
    {
        if (visualState == NotchState.Compact && CompactHost.Visibility != Visibility.Visible)
        {
            SetContentStateImmediate(visualState);
        }
        else if (visualState == NotchState.Expanded && ExpandedHost.Visibility != Visibility.Visible)
        {
            SetContentStateImmediate(visualState);
        }
    }

    private static void StopHostAnimationsPreservingCurrent(
        FrameworkElement host,
        TranslateTransform translate,
        ScaleTransform scale)
    {
        var opacity = host.Opacity;
        var y = translate.Y;
        var scaleX = scale.ScaleX;
        var scaleY = scale.ScaleY;
        host.BeginAnimation(OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        host.Opacity = opacity;
        translate.Y = y;
        scale.ScaleX = scaleX;
        scale.ScaleY = scaleY;
    }

    private static void ResetHost(
        FrameworkElement host,
        TranslateTransform translate,
        ScaleTransform scale)
    {
        host.BeginAnimation(OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        host.Opacity = 1;
        translate.Y = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
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

        var compactWidth = CompactContent.GetPreferredWidth();
        var expandedWidth = Math.Max(ExpandedContent.GetPreferredWidth(), compactWidth + 36);
        var expandedHeight = ExpandedContent.GetPreferredHeight(expandedWidth);
        _windowController.SetPreferredSize(compactWidth, expandedWidth, expandedHeight, applyWindowSize);
    }

    private void TryRunPendingItemTransition()
    {
        if (!_pendingItemTransition || _isFullscreenSuppressed || _stateMachine.Current == NotchState.Hidden)
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
        _statusStore.Changed -= StatusStore_OnChanged;
        _stateMachine.StateChanged -= StateMachine_OnStateChanged;
        _monitorPlacementService.Changed -= MonitorPlacementService_OnChanged;
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
        CompositionTarget.Rendering -= CompositionTarget_OnRendering;
    }
}
