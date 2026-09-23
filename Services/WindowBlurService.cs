using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NotchBar.Services;

/// <summary>
/// Coordinates the fail-closed composition-only backdrop window. The layered
/// WPF HWND remains content/input only and never receives a native backdrop.
/// </summary>
public sealed class WindowBlurService : IDisposable
{
    private readonly Window _window;
    private readonly WindowController _windowController;
    private readonly BackdropActivationGate _gate = new();
    private BackdropHostWindow? _hostWindow;
    private CompositionBackdropHost? _compositionHost;
    private bool _readyToActivate;
    private bool _registered;
    private bool _disposed;

    public WindowBlurService(Window window, WindowController windowController)
    {
        _window = window;
        _windowController = windowController;
    }

    public event EventHandler<BackdropAvailabilityChangedEventArgs>? AvailabilityChanged;

    public PointerProbeEvidence? LastProbeEvidence { get; private set; }
    public bool IsActive => _gate.CanShow && _registered;

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _readyToActivate || !OperatingSystem.IsWindows() || SystemParameters.HighContrast)
        {
            return _readyToActivate;
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

        var generation = _gate.BeginInitialization();
        try
        {
            var mainHandle = new WindowInteropHelper(_window).Handle;
            if (mainHandle == IntPtr.Zero || !GetWindowRect(mainHandle, out var rect))
            {
                FailAndDestroy();
                return false;
            }

            _hostWindow = new BackdropHostWindow();
            _compositionHost = new CompositionBackdropHost(
                _hostWindow.Handle,
                mode,
                ResolveTint);
            var geometry = new WindowPixelGeometry(
                rect.Left,
                rect.Top,
                Math.Max(1, rect.Right - rect.Left),
                Math.Max(1, rect.Bottom - rect.Top));
            _compositionHost.UpdateGeometry(geometry);
            if (!await _compositionHost.PrepareAsync() || !_gate.MarkClipCommitted(generation))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"NotchBar composition backdrop preparation failed: {_compositionHost.LastFailure}");
                FailAndDestroy();
                return false;
            }

            // A layered transparent companion is the only tested topology that
            // both preserves the committed HostBackdrop graph and allows the
            // real pointer event to reach an unrelated underlying process.
            if (!_hostWindow.EnableLayeredTransparency())
            {
                FailAndDestroy();
                return false;
            }

            // A committed HostBackdrop graph is the frost capability candidate.
            // Normal activation remains blocked on external-process delivery.
            if (!_gate.BeginProbe(generation, frostObserved: true))
            {
                FailAndDestroy();
                return false;
            }

            LastProbeEvidence = await BackdropInputProbe.RunParentAsync(_hostWindow, geometry, cancellationToken);
            if (!_gate.CompleteProbe(generation, LastProbeEvidence.Value))
            {
                FailAndDestroy();
                return false;
            }

            _readyToActivate = true;
            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar backdrop initialization failed: {exception}");
            FailAndDestroy();
            return false;
        }
    }

    public bool Activate()
    {
        if (_disposed || !_readyToActivate || !_gate.CanShow ||
            _hostWindow is null || _compositionHost is null || _registered)
        {
            return IsActive;
        }

        _windowController.RegisterCompanion(
            _hostWindow.Handle,
            _compositionHost.UpdateGeometry,
            FailAndDestroy);
        _registered = true;
        AvailabilityChanged?.Invoke(this, new BackdropAvailabilityChangedEventArgs(true));
        return true;
    }

    public bool RefreshTheme()
    {
        return !_disposed && _compositionHost?.RefreshTheme() == true && IsActive;
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_disposed) return;
        _gate.SetSuppressed(suppressed);
        _windowController.SetCompanionActive(!suppressed && _readyToActivate && _registered);
    }

    private System.Windows.Media.Color ResolveTint()
    {
        return _window.TryFindResource("IslandBackground") is SolidColorBrush brush
            ? brush.Color
            : System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20);
    }

    private void FailAndDestroy()
    {
        if (_disposed) return;
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
        if (_disposed) return;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}

public sealed class BackdropAvailabilityChangedEventArgs(bool isActive) : EventArgs
{
    public bool IsActive { get; } = isActive;
}
