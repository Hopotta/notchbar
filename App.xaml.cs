using System.IO;
using System.Windows;
using System.Windows.Interop;
using NotchBar.Core;
using NotchBar.Services;

namespace NotchBar;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan ApiShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _lifetimeCts = new();
    private SingleInstanceService? _singleInstanceService;
    private TrayService? _trayService;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private StatusStore? _statusStore;
    private ClockService? _clockService;
    private ThemeService? _themeService;
    private ApiService? _apiService;
    private Task? _apiStartTask;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private bool _restartRequested;
    private bool _shutdownRequested;

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
        _themeService = new ThemeService();
        _themeService.ThemeChanged += ThemeService_OnChanged;
        _themeService.Start();
        _apiService = new ApiService(_statusStore, _settingsService);

        _mainWindow = new MainWindow(_statusStore, _settingsService);
        _mainWindow.PinStateChanged += MainWindow_OnPinStateChanged;
        MainWindow = _mainWindow;
        _ = new WindowInteropHelper(_mainWindow).EnsureHandle();
        await _mainWindow.InitializeBackdropAsync();
        if (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.ActivateBackdrop();

        _trayService = new TrayService();
        _trayService.ShowRequested += TrayService_OnShowRequested;
        _trayService.PinToggleRequested += TrayService_OnPinToggleRequested;
        _trayService.StartWithWindowsToggleRequested += TrayService_OnStartWithWindowsToggleRequested;
        _trayService.OpenSettingsRequested += TrayService_OnOpenSettingsRequested;
        _trayService.RestartRequested += TrayService_OnRestartRequested;
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

    private void TrayService_OnOpenSettingsRequested(object? sender, EventArgs e)
    {
        RunOnUi(OpenSettingsWindow);
    }

    private void TrayService_OnRestartRequested(object? sender, EventArgs e)
    {
        RunOnUi(RequestRestart);
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

    private void OpenSettingsWindow()
    {
        if (_settingsService is null || _startupService is null || _mainWindow is null || _shutdownRequested)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settingsService, _startupService)
        {
            Owner = _mainWindow
        };
        window.SettingsSaved += SettingsWindow_OnSettingsSaved;
        window.Closed += SettingsWindow_OnClosed;
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    private void SettingsWindow_OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        _trayService?.SetStartWithWindows(e.Current.StartWithWindows);
        if (e.RestartRequested)
        {
            _ = Dispatcher.BeginInvoke(new Action(RequestRestart), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow window)
        {
            window.SettingsSaved -= SettingsWindow_OnSettingsSaved;
            window.Closed -= SettingsWindow_OnClosed;
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        }
    }

    private void RequestRestart()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        _restartRequested = true;
        Shutdown();
    }

    private void TrayService_OnExitRequested(object? sender, EventArgs e)
    {
        RunOnUi(() =>
        {
            _shutdownRequested = true;
            Shutdown();
        });
    }

    private void ThemeService_OnChanged(object? sender, EventArgs e)
    {
        RunOnUi(() => _mainWindow?.RefreshTheme());
    }

    private void MainWindow_OnPinStateChanged(object? sender, EventArgs e)
    {
        _trayService?.SetPinned(_mainWindow?.IsPinned == true);
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            try
            {
                _ = Dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                // A native callback raced application shutdown.
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _lifetimeCts.Cancel();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar lifetime cancellation failed: {exception.Message}");
        }

        try
        {
            Cleanup("settings window", () =>
            {
                if (_settingsWindow is not null)
                {
                    _settingsWindow.SettingsSaved -= SettingsWindow_OnSettingsSaved;
                    _settingsWindow.Closed -= SettingsWindow_OnClosed;
                    _settingsWindow.Close();
                    _settingsWindow = null;
                }
            });

            Cleanup("tray icon", () =>
            {
                if (_trayService is null)
                {
                    return;
                }

                _trayService.ShowRequested -= TrayService_OnShowRequested;
                _trayService.PinToggleRequested -= TrayService_OnPinToggleRequested;
                _trayService.StartWithWindowsToggleRequested -= TrayService_OnStartWithWindowsToggleRequested;
                _trayService.OpenSettingsRequested -= TrayService_OnOpenSettingsRequested;
                _trayService.RestartRequested -= TrayService_OnRestartRequested;
                _trayService.ExitRequested -= TrayService_OnExitRequested;
                _trayService.Dispose();
                _trayService = null;
            });

            Cleanup("main window", () =>
            {
                if (_mainWindow is null)
                {
                    return;
                }

                _mainWindow.PinStateChanged -= MainWindow_OnPinStateChanged;
                _mainWindow.Dispose();
                _mainWindow = null;
            });

            StopApiWithinDeadline();

            Cleanup("theme service", () =>
            {
                if (_themeService is null)
                {
                    return;
                }

                _themeService.ThemeChanged -= ThemeService_OnChanged;
                _themeService.Dispose();
                _themeService = null;
            });

            Cleanup("clock service", () =>
            {
                _clockService?.Dispose();
                _clockService = null;
            });

            Cleanup("status store", () =>
            {
                _statusStore?.Dispose();
                _statusStore = null;
            });
        }
        finally
        {
            Cleanup("single-instance service", () =>
            {
                if (_singleInstanceService is null)
                {
                    return;
                }

                _singleInstanceService.ActivationRequested -= SingleInstanceService_OnActivationRequested;
                _singleInstanceService.Dispose();
                _singleInstanceService = null;
            });

            _lifetimeCts.Dispose();

            Cleanup("restart", () =>
            {
                if (_restartRequested && !ApplicationLaunchService.TryStartCurrentInstance(out var restartError))
                {
                    System.Diagnostics.Debug.WriteLine($"NotchBar could not restart: {restartError}");
                }
            });

            base.OnExit(e);
        }
    }

    private void StopApiWithinDeadline()
    {
        if (_apiService is null)
        {
            return;
        }

        try
        {
            if (!BoundedOperationRunner.TryRun(_apiService.StopAsync, ApiShutdownTimeout))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"NotchBar API shutdown exceeded the {ApiShutdownTimeout.TotalSeconds:0}-second timeout");
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar API shutdown failed: {exception.Message}");
        }
    }

    private static void Cleanup(string name, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar {name} cleanup failed: {exception.Message}");
        }
    }
}
