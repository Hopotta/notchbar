using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using NotchBar.Core;
using NotchBar.Services;

namespace NotchBar;

public partial class MainWindow : Window, IDisposable
{
    private readonly StatusStore _statusStore;
    private readonly SettingsService _settings;
    private readonly NotchStateMachine _stateMachine = new();
    private readonly MonitorPlacementService _monitorPlacementService;
    private readonly WindowController _windowController;
    private readonly AutoHideService _autoHideService;
    private readonly HotkeyService _hotkeyService = new();
    private readonly FullscreenSuppressionService? _fullscreenSuppressionService;
    private bool _isFullscreenSuppressed;
    private bool _disposed;

    public MainWindow(StatusStore statusStore, SettingsService settings)
    {
        _statusStore = statusStore;
        _settings = settings;
        InitializeComponent();

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
            ScheduleHideForDisplayItem();
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
            ScheduleHideForDisplayItem();
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
            _autoHideService.OnMouseLeave(GetAutoHideDelayForDisplayItem());
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
            ScheduleHideForDisplayItem();
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
        CompactContent.Visibility = visualState == NotchState.Compact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedContent.Visibility = visualState == NotchState.Expanded ? Visibility.Visible : Visibility.Collapsed;

        if (state is NotchState.Hidden or NotchState.Pinned || _isFullscreenSuppressed)
        {
            _autoHideService.Cancel();
        }

        RefreshItem();
        _windowController.Apply(visualState);
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
            ScheduleHideForDisplayItem();
        }
    }

    private void ScheduleHideForDisplayItem()
    {
        _autoHideService.ScheduleHide(GetAutoHideDelayForDisplayItem());
    }

    private TimeSpan GetAutoHideDelayForDisplayItem()
    {
        var item = _statusStore.GetDisplayItem();
        if (item is { IsNotification: true, TtlSeconds: > 0 })
        {
            var remaining = item.UpdatedAt.AddSeconds(item.TtlSeconds) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }
        }

        return _settings.AutoHideDelay;
    }

    private void RefreshItem()
    {
        var item = _statusStore.GetDisplayItem();
        if (item is null)
        {
            return;
        }

        CompactContent.ShowItem(item, _stateMachine.IsPinned);
        ExpandedContent.ShowItem(item, _stateMachine.IsPinned);
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
        _hotkeyService.Pressed -= HotkeyService_OnPressed;
        _hotkeyService.RegistrationFailed -= HotkeyService_OnRegistrationFailed;
        _hotkeyService.Dispose();
        _autoHideService.Dispose();
        _monitorPlacementService.Dispose();
        if (_fullscreenSuppressionService is not null)
        {
            _fullscreenSuppressionService.Changed -= FullscreenSuppressionService_OnChanged;
            _fullscreenSuppressionService.Dispose();
        }
    }
}
