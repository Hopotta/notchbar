using System.Windows;
using System.Windows.Media;

namespace NotchBar.Services;

/// <summary>
/// Coordinates a hidden composition companion. The WPF HWND owns all content
/// and input; the companion contributes only a rounded, click-through frost.
/// </summary>
public sealed class WindowBlurService : IDisposable
{
    private readonly Window _window;
    private readonly WindowController _windowController;
    private readonly BackdropActivationGate _gate = new();
    private BackdropHostWindow? _hostWindow;
    private CompositionBackdropHost? _compositionHost;
    private Task<bool>? _initializationTask;
    private long _generation;
    private bool _readyToActivate;
    private bool _registered;
    private bool _disposed;

    public WindowBlurService(Window window, WindowController windowController)
    {
        _window = window;
        _windowController = windowController;
    }

    public event EventHandler<BackdropAvailabilityChangedEventArgs>? AvailabilityChanged;

    public bool IsActive => _gate.CanShow && _registered;

    public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initializationTask is not null)
        {
            return _initializationTask;
        }

        _initializationTask = InitializeCoreAsync(cancellationToken);
        return _initializationTask;
    }

    private async Task<bool> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !OperatingSystem.IsWindows() || SystemParameters.HighContrast)
        {
            return false;
        }

        var mode = BackdropWindowPolicy.SelectActivationMode(
            isWindows: true,
            highContrast: SystemParameters.HighContrast,
            compositionEnabled: CompositionBackdropHost.IsCompositionEnabled(),
            windowsBuild: Environment.OSVersion.Version.Build);
        if (mode == BackdropActivationMode.Fallback)
        {
            return false;
        }

        _generation = _gate.BeginInitialization();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hostWindow = new BackdropHostWindow();
            _compositionHost = new CompositionBackdropHost(
                _hostWindow.Handle,
                mode,
                ResolveTint);
            _compositionHost.UpdateGeometry(
                _windowController.CurrentGeometry,
                _windowController.CurrentDpi);

            if (!await _compositionHost.PrepareAsync(cancellationToken) ||
                !_gate.MarkClipCommitted(_generation))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"NotchBar composition backdrop preparation failed: {_compositionHost.LastFailure}");
                FailAndDestroy();
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_hostWindow.EnableLayeredTransparency() ||
                !_gate.MarkInputContractVerified(_generation, _hostWindow.HasTransparentInputContract()))
            {
                FailAndDestroy();
                return false;
            }

            _readyToActivate = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FailAndDestroy();
            return false;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar backdrop initialization failed: {exception}");
            FailAndDestroy();
            return false;
        }
    }

    /// <summary>
    /// Call after the WPF window is visible. Preparation and the rounded clip
    /// commit finish first, so the companion cannot flash as a rectangle.
    /// </summary>
    public bool Activate()
    {
        if (_disposed || !_readyToActivate || !_window.IsVisible ||
            _hostWindow is null || _compositionHost is null || _registered ||
            !_gate.Activate(_generation))
        {
            return IsActive;
        }

        if (!_windowController.RegisterCompanion(
                _hostWindow.Handle,
                _compositionHost.UpdateGeometry,
                FailAndDestroy))
        {
            FailAndDestroy();
            return false;
        }

        _registered = true;
        AvailabilityChanged?.Invoke(this, new BackdropAvailabilityChangedEventArgs(true));
        return true;
    }

    public bool RefreshTheme()
    {
        if (_disposed || (!_readyToActivate && !_registered))
        {
            return false;
        }

        try
        {
            if (_compositionHost?.RefreshTheme() == true)
            {
                return IsActive;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar backdrop theme refresh failed: {exception}");
        }

        FailAndDestroy();
        return false;
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_disposed)
        {
            return;
        }

        _gate.SetSuppressed(suppressed);
        _windowController.SetCompanionActive(_gate.CanShow && _registered);
    }

    private System.Windows.Media.Color ResolveTint()
    {
        return _window.TryFindResource("IslandBackground") is SolidColorBrush brush
            ? brush.Color
            : System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20);
    }

    private void FailAndDestroy()
    {
        if (_disposed)
        {
            return;
        }

        var wasActive = IsActive;
        _readyToActivate = false;
        _registered = false;
        _gate.Fail();
        _windowController.UnregisterCompanion();
        _compositionHost?.Dispose();
        _compositionHost = null;
        _hostWindow?.Dispose();
        _hostWindow = null;
        if (wasActive)
        {
            AvailabilityChanged?.Invoke(this, new BackdropAvailabilityChangedEventArgs(false));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var wasActive = IsActive;
        _readyToActivate = false;
        _registered = false;
        _gate.Destroy();
        _windowController.UnregisterCompanion();
        _compositionHost?.Dispose();
        _compositionHost = null;
        _hostWindow?.Dispose();
        _hostWindow = null;
        _disposed = true;
        if (wasActive)
        {
            AvailabilityChanged?.Invoke(this, new BackdropAvailabilityChangedEventArgs(false));
        }
        GC.SuppressFinalize(this);
    }

}

public sealed class BackdropAvailabilityChangedEventArgs(bool isActive) : EventArgs
{
    public bool IsActive { get; } = isActive;
}
