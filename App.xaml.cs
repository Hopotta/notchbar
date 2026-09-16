using System.Windows;
using NotchBar.Core;
using NotchBar.Services;

namespace NotchBar;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetimeCts = new();
    private StatusStore? _statusStore;
    private ClockService? _clockService;
    private ApiService? _apiService;
    private Task? _apiStartTask;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = new SettingsService();
        _statusStore = new StatusStore();
        _clockService = new ClockService(_statusStore);
        _apiService = new ApiService(_statusStore, settings);

        _mainWindow = new MainWindow(_statusStore, settings);
        MainWindow = _mainWindow;
        _mainWindow.Show();

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

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetimeCts.Cancel();
        _mainWindow?.Dispose();
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
        _lifetimeCts.Dispose();
        base.OnExit(e);
    }
}
