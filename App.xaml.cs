using System.IO;
using System.Windows;
using NotchBar.Core;
using NotchBar.Services;

namespace NotchBar;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _lifetimeCts = new();
    private SingleInstanceService? _singleInstanceService;
    private TrayService? _trayService;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private StatusStore? _statusStore;
    private ClockService? _clockService;
    private ApiService? _apiService;
    private Task? _apiStartTask;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceService = new SingleInstanceService();
        if (!_singleInstanceService.IsPrimary)
        {
            Shutdown();
            return;
        }

        _singleInstanceService.ActivationRequested += SingleInstanceService_OnActivationRequested;

        _settingsService = new SettingsService();
        if (!File.Exists(_settingsService.SettingsPath) && !_settingsService.TrySave(out var settingsError))
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar settings could not be created: {settingsError}");
        }

        _startupService = new StartupService();
        if (!_startupService.TrySetEnabled(_settingsService.StartWithWindows, out var startupError))
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar startup registration could not be synchronized: {startupError}");
        }

        _statusStore = new StatusStore();
        _clockService = new ClockService(_statusStore);
        _apiService = new ApiService(_statusStore, _settingsService);

        _mainWindow = new MainWindow(_statusStore, _settingsService);
        _mainWindow.PinStateChanged += MainWindow_OnPinStateChanged;
        MainWindow = _mainWindow;
        _mainWindow.Show();

        _trayService = new TrayService();
        _trayService.ShowRequested += TrayService_OnShowRequested;
        _trayService.PinToggleRequested += TrayService_OnPinToggleRequested;
        _trayService.StartWithWindowsToggleRequested += TrayService_OnStartWithWindowsToggleRequested;
        _trayService.ExitRequested += TrayService_OnExitRequested;
        _trayService.SetPinned(_mainWindow.IsPinned);
        _trayService.SetStartWithWindows(_settingsService.StartWithWindows);

        _singleInstanceService.StartListening();

        try
        {
            _apiStartTask = _apiService.StartAsync(_lifetimeCts.Token);
            await _apiStartTask;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Shutdown won the startup race.
        }
        catch (Exception exception)
        {
            // The UI remains useful without the API, and can still show Clock.
            _mainWindow.ShowApiError(exception.Message);
        }
    }

    private void SingleInstanceService_OnActivationRequested(object? sender, EventArgs e)
    {
        RunOnUi(() => _mainWindow?.ShowFromExternal());
    }

    private void TrayService_OnShowRequested(object? sender, EventArgs e)
    {
        RunOnUi(() => _mainWindow?.ShowFromExternal());
    }

    private void TrayService_OnPinToggleRequested(object? sender, EventArgs e)
    {
        RunOnUi(() => _mainWindow?.TogglePinnedFromExternal());
    }

    private void TrayService_OnStartWithWindowsToggleRequested(object? sender, EventArgs e)
    {
        RunOnUi(ToggleStartWithWindows);
    }

    private void ToggleStartWithWindows()
    {
        if (_settingsService is null || _startupService is null)
        {
            return;
        }

        var previous = _settingsService.StartWithWindows;
        var next = !previous;
        if (!_startupService.TrySetEnabled(next, out var registryError))
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar startup registration could not be changed: {registryError}");
            return;
        }

        if (!_settingsService.SetStartWithWindows(next, out var settingsError))
        {
            _ = _startupService.TrySetEnabled(previous, out var rollbackError);
            System.Diagnostics.Debug.WriteLine($"NotchBar startup preference could not be saved: {settingsError}");
            if (rollbackError is not null)
            {
                System.Diagnostics.Debug.WriteLine($"NotchBar startup registration rollback also failed: {rollbackError}");
            }
            return;
        }

        _trayService?.SetStartWithWindows(next);
    }

    private void TrayService_OnExitRequested(object? sender, EventArgs e)
    {
        RunOnUi(Shutdown);
    }

    private void MainWindow_OnPinStateChanged(object? sender, EventArgs e)
    {
        _trayService?.SetPinned(_mainWindow?.IsPinned == true);
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(action);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetimeCts.Cancel();

        if (_trayService is not null)
        {
            _trayService.ShowRequested -= TrayService_OnShowRequested;
            _trayService.PinToggleRequested -= TrayService_OnPinToggleRequested;
            _trayService.StartWithWindowsToggleRequested -= TrayService_OnStartWithWindowsToggleRequested;
            _trayService.ExitRequested -= TrayService_OnExitRequested;
            _trayService.Dispose();
        }

        if (_mainWindow is not null)
        {
            _mainWindow.PinStateChanged -= MainWindow_OnPinStateChanged;
            _mainWindow.Dispose();
        }

        try
        {
            _apiStartTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected when shutdown interrupts API startup.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar API startup ended during shutdown: {exception.Message}");
        }

        try
        {
            _apiService?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar API shutdown failed: {exception.Message}");
        }

        _clockService?.Dispose();
        _statusStore?.Dispose();

        if (_singleInstanceService is not null)
        {
            _singleInstanceService.ActivationRequested -= SingleInstanceService_OnActivationRequested;
            _singleInstanceService.Dispose();
        }

        _lifetimeCts.Dispose();
        base.OnExit(e);
    }
}
