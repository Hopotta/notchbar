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
    private readonly WindowController _windowController;
    private readonly AutoHideService _autoHideService;
    private readonly HotkeyService _hotkeyService = new();
    private bool _disposed;

    public MainWindow(StatusStore statusStore, SettingsService settings)
    {
        _statusStore = statusStore;
        _settings = settings;
        InitializeComponent();

        _windowController = new WindowController(this);
        _autoHideService = new AutoHideService(_stateMachine, _settings.AutoHideDelay);

        _stateMachine.StateChanged += StateMachine_OnStateChanged;
        _statusStore.Changed += StatusStore_OnChanged;
        CompactContent.PinClicked += Pin_OnClicked;
        ExpandedContent.PinClicked += Pin_OnClicked;

        RefreshItem();
        ApplyVisualState(_stateMachine.Current);
    }

    public void ShowApiError(string message)
    {
        Debug.WriteLine($"NotchBar API failed to start: {message}");
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _hotkeyService.Pressed += HotkeyService_OnPressed;
        _hotkeyService.RegistrationFailed += HotkeyService_OnRegistrationFailed;
        _hotkeyService.Attach(this, _settings.HotkeyModifiers, _settings.HotkeyKey);
    }

    private void HotkeyService_OnPressed(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _autoHideService.Cancel();
        _stateMachine.ToggleVisibility();
    }

    private void HotkeyService_OnRegistrationFailed(object? sender, EventArgs e)
    {
        Debug.WriteLine("NotchBar global hotkey could not be registered.");
    }

    private void Window_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _autoHideService.OnMouseEnter();
        _stateMachine.Wake();
    }

    private void Window_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_disposed)
        {
            _autoHideService.OnMouseLeave();
        }
    }

    private void ContentRoot_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_disposed && _stateMachine.VisualState == NotchState.Compact)
        {
            _autoHideService.Cancel();
            _stateMachine.Expand();
            e.Handled = true;
        }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_disposed && e.Key == Key.Escape && _stateMachine.VisualState == NotchState.Expanded)
        {
            _stateMachine.Collapse();
            e.Handled = true;
        }
    }

    private void Pin_OnClicked(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _autoHideService.Cancel();
        _stateMachine.TogglePinned();
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
        HiddenTrigger.Visibility = state == NotchState.Hidden ? Visibility.Visible : Visibility.Collapsed;
        CompactContent.Visibility = visualState == NotchState.Compact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedContent.Visibility = visualState == NotchState.Expanded ? Visibility.Visible : Visibility.Collapsed;

        if (state is NotchState.Hidden or NotchState.Pinned)
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
        if (e.WakeOnUpdate && !_stateMachine.IsPinned)
        {
            _autoHideService.Cancel();
            _stateMachine.Set(NotchState.Compact);
        }
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
        CompactContent.PinClicked -= Pin_OnClicked;
        ExpandedContent.PinClicked -= Pin_OnClicked;
        _hotkeyService.Pressed -= HotkeyService_OnPressed;
        _hotkeyService.RegistrationFailed -= HotkeyService_OnRegistrationFailed;
        _hotkeyService.Dispose();
        _autoHideService.Dispose();
    }
}
