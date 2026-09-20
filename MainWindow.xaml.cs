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
    private static readonly Duration ExpandOutgoingDuration = new(TimeSpan.FromMilliseconds(165));
    private static readonly Duration ExpandIncomingDuration = new(TimeSpan.FromMilliseconds(205));
    private static readonly Duration CollapseOutgoingDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration CollapseIncomingDuration = new(TimeSpan.FromMilliseconds(190));
    private static readonly TimeSpan ExpandIncomingDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan CollapseIncomingDelay = TimeSpan.FromMilliseconds(12);

    private readonly StatusStore _statusStore;
    private readonly SettingsService _settings;
    private readonly NotchStateMachine _stateMachine = new();
    private readonly TranslateTransform _compactContentTranslate = new();
    private readonly TranslateTransform _expandedContentTranslate = new();
    private readonly MonitorPlacementService _monitorPlacementService;
    private readonly WindowController _windowController;
    private readonly AutoHideService _autoHideService;
    private readonly HotkeyService _hotkeyService = new();
    private readonly FullscreenSuppressionService? _fullscreenSuppressionService;
    private string? _displayedItemId;
    private NotchState _renderedVisualState = NotchState.Hidden;
    private long _layoutTransitionVersion;
    private bool _pendingItemTransition;
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

        RefreshItem();
        ApplyVisualState(_stateMachine.Current);
    }

    public event EventHandler? PinStateChanged;

    public bool IsPinned => _stateMachine.IsPinned;

    public void ShowApiError(string message)
    {
        Debug.WriteLine($"NotchBar API failed to start: {message}");
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
            _renderedVisualState = NotchState.Hidden;
            return;
        }

        if (visualState == NotchState.Hidden)
        {
            if (SystemParameters.ClientAreaAnimation
                && _renderedVisualState is NotchState.Compact or NotchState.Expanded)
            {
                RunContentDismiss(_renderedVisualState);
                _renderedVisualState = NotchState.Hidden;
            }
            else
            {
                SetContentStateImmediate(NotchState.Hidden);
                _renderedVisualState = NotchState.Hidden;
            }

            return;
        }

        if (_renderedVisualState == NotchState.Hidden)
        {
            if (visualState == NotchState.Compact && SystemParameters.ClientAreaAnimation)
            {
                RunContentReveal();
            }
            else
            {
                SetContentStateImmediate(visualState);
            }

            _renderedVisualState = visualState;
            _pendingItemTransition = false;
            return;
        }

        if (_renderedVisualState == visualState)
        {
            EnsureContentHostVisible(visualState);
            return;
        }

        var canMorph = SystemParameters.ClientAreaAnimation
            && _renderedVisualState is NotchState.Compact or NotchState.Expanded
            && visualState is NotchState.Compact or NotchState.Expanded;

        if (!canMorph)
        {
            SetContentStateImmediate(visualState);
            _renderedVisualState = visualState;
            return;
        }

        RunContentMorph(_renderedVisualState, visualState);
        _renderedVisualState = visualState;
        _pendingItemTransition = false;
    }

    private void RunContentReveal()
    {
        var transitionVersion = ++_layoutTransitionVersion;
        ResetHost(CompactHost, CompactHostTranslate, CompactHostScale);
        ResetHost(ExpandedHost, ExpandedHostTranslate, ExpandedHostScale);
        ExpandedContent.ResetLayoutTransition();

        CompactHost.Visibility = Visibility.Visible;
        ExpandedHost.Visibility = Visibility.Collapsed;
        CompactHost.Opacity = 0;
        CompactHostTranslate.Y = -5;
        CompactHostScale.ScaleX = 0.985;
        CompactHostScale.ScaleY = 0.97;

        var easing = new CriticallyDampedEase
        {
            Response = 0.28,
            EasingMode = EasingMode.EaseOut
        };
        var duration = new Duration(TimeSpan.FromMilliseconds(185));
        var opacityAnimation = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (_disposed || transitionVersion != _layoutTransitionVersion)
            {
                return;
            }

            ResetHost(CompactHost, CompactHostTranslate, CompactHostScale);
        };

        CompactHost.BeginAnimation(OpacityProperty, opacityAnimation);
        CompactHostTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-5, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
        CompactHostScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
        CompactHostScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private void RunContentDismiss(NotchState previous)
    {
        var transitionVersion = ++_layoutTransitionVersion;
        var outgoing = previous == NotchState.Compact ? CompactHost : ExpandedHost;
        var outgoingTranslate = previous == NotchState.Compact ? CompactHostTranslate : ExpandedHostTranslate;
        var outgoingScale = previous == NotchState.Compact ? CompactHostScale : ExpandedHostScale;

        StopHostAnimationsPreservingCurrent(outgoing, outgoingTranslate, outgoingScale);
        ExpandedContent.ResetLayoutTransition();
        outgoing.Visibility = Visibility.Visible;

        var outgoingOpacity = outgoing.Opacity;
        var outgoingY = outgoingTranslate.Y;
        var outgoingScaleX = outgoingScale.ScaleX;
        var outgoingScaleY = outgoingScale.ScaleY;
        var duration = new Duration(TimeSpan.FromMilliseconds(145));
        var easing = new CriticallyDampedEase
        {
            Response = 0.27,
            EasingMode = EasingMode.EaseIn
        };
        var opacityAnimation = new DoubleAnimation(outgoingOpacity, 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (_disposed || transitionVersion != _layoutTransitionVersion)
            {
                return;
            }

            outgoing.Visibility = Visibility.Collapsed;
            ResetHost(outgoing, outgoingTranslate, outgoingScale);
        };

        outgoing.BeginAnimation(OpacityProperty, opacityAnimation);
        outgoingTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(outgoingY, -4, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
        outgoingScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(outgoingScaleX, 0.985, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
        outgoingScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(outgoingScaleY, 0.97, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private void RunContentMorph(NotchState previous, NotchState next)
    {
        var transitionVersion = ++_layoutTransitionVersion;
        var expanding = next == NotchState.Expanded;

        var outgoing = previous == NotchState.Compact ? CompactHost : ExpandedHost;
        var incoming = next == NotchState.Compact ? CompactHost : ExpandedHost;
        var outgoingTranslate = previous == NotchState.Compact ? CompactHostTranslate : ExpandedHostTranslate;
        var incomingTranslate = next == NotchState.Compact ? CompactHostTranslate : ExpandedHostTranslate;
        var outgoingScale = previous == NotchState.Compact ? CompactHostScale : ExpandedHostScale;
        var incomingScale = next == NotchState.Compact ? CompactHostScale : ExpandedHostScale;

        var incomingWasVisible = incoming.Visibility == Visibility.Visible;
        StopHostAnimationsPreservingCurrent(outgoing, outgoingTranslate, outgoingScale);
        StopHostAnimationsPreservingCurrent(incoming, incomingTranslate, incomingScale);

        if (expanding)
        {
            ExpandedContent.RunLayoutTransition(expanding: true, preserveCurrent: incomingWasVisible);
        }
        else
        {
            ExpandedContent.RunLayoutTransition(expanding: false, preserveCurrent: true);
        }

        var outgoingOpacity = outgoing.Opacity;
        var outgoingY = outgoingTranslate.Y;
        var outgoingScaleX = outgoingScale.ScaleX;
        var outgoingScaleY = outgoingScale.ScaleY;

        var incomingOpacity = incomingWasVisible ? incoming.Opacity : 0d;
        var incomingY = incomingWasVisible ? incomingTranslate.Y : expanding ? 5d : -2d;
        var incomingScaleX = incomingWasVisible ? incomingScale.ScaleX : 0.99d;
        var incomingScaleY = incomingWasVisible ? incomingScale.ScaleY : expanding ? 0.965d : 0.975d;

        outgoing.Visibility = Visibility.Visible;
        incoming.Visibility = Visibility.Visible;
        outgoing.Opacity = outgoingOpacity;
        outgoingTranslate.Y = outgoingY;
        outgoingScale.ScaleX = outgoingScaleX;
        outgoingScale.ScaleY = outgoingScaleY;
        incoming.Opacity = incomingOpacity;
        incomingTranslate.Y = incomingY;
        incomingScale.ScaleX = incomingScaleX;
        incomingScale.ScaleY = incomingScaleY;

        var outgoingDuration = expanding ? ExpandOutgoingDuration : CollapseOutgoingDuration;
        var incomingDuration = expanding ? ExpandIncomingDuration : CollapseIncomingDuration;
        var incomingDelay = expanding ? ExpandIncomingDelay : CollapseIncomingDelay;
        var outgoingTargetY = expanding ? -2d : -4d;
        var outgoingTargetScaleX = expanding ? 0.985d : 0.99d;
        var outgoingTargetScaleY = expanding ? 0.955d : 0.94d;
        var incomingEase = new CriticallyDampedEase
        {
            Response = expanding ? 0.34 : 0.31,
            EasingMode = EasingMode.EaseOut
        };
        var outgoingEase = new CriticallyDampedEase
        {
            Response = expanding ? 0.28 : 0.32,
            EasingMode = EasingMode.EaseIn
        };

        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(outgoingOpacity, 0, outgoingDuration)
        {
            EasingFunction = outgoingEase,
            FillBehavior = FillBehavior.HoldEnd
        });
        outgoingTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(outgoingY, outgoingTargetY, outgoingDuration)
        {
            EasingFunction = outgoingEase,
            FillBehavior = FillBehavior.HoldEnd
        });
        outgoingScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(outgoingScaleX, outgoingTargetScaleX, outgoingDuration)
        {
            EasingFunction = outgoingEase,
            FillBehavior = FillBehavior.HoldEnd
        });
        outgoingScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(outgoingScaleY, outgoingTargetScaleY, outgoingDuration)
        {
            EasingFunction = outgoingEase,
            FillBehavior = FillBehavior.HoldEnd
        });

        var incomingOpacityAnimation = new DoubleAnimation(incomingOpacity, 1, incomingDuration)
        {
            BeginTime = incomingDelay,
            EasingFunction = incomingEase,
            FillBehavior = FillBehavior.HoldEnd
        };
        incomingOpacityAnimation.Completed += (_, _) =>
        {
            if (_disposed || transitionVersion != _layoutTransitionVersion)
            {
                return;
            }

            StopHostAnimationsPreservingCurrent(outgoing, outgoingTranslate, outgoingScale);
            StopHostAnimationsPreservingCurrent(incoming, incomingTranslate, incomingScale);
            outgoing.Visibility = Visibility.Collapsed;
            ResetHost(outgoing, outgoingTranslate, outgoingScale);
            incoming.Visibility = Visibility.Visible;
            ResetHost(incoming, incomingTranslate, incomingScale);
            ExpandedContent.ResetLayoutTransition();
        };

        incoming.BeginAnimation(OpacityProperty, incomingOpacityAnimation);
        incomingTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(incomingY, 0, incomingDuration)
        {
            BeginTime = incomingDelay,
            EasingFunction = incomingEase,
            FillBehavior = FillBehavior.HoldEnd
        });
        incomingScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(incomingScaleX, 1, incomingDuration)
        {
            BeginTime = incomingDelay,
            EasingFunction = incomingEase,
            FillBehavior = FillBehavior.HoldEnd
        });
        incomingScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(incomingScaleY, 1, incomingDuration)
        {
            BeginTime = incomingDelay,
            EasingFunction = incomingEase,
            FillBehavior = FillBehavior.HoldEnd
        });
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
        _monitorPlacementService.Dispose();
        if (_fullscreenSuppressionService is not null)
        {
            _fullscreenSuppressionService.Changed -= FullscreenSuppressionService_OnChanged;
            _fullscreenSuppressionService.Dispose();
        }
    }
}
