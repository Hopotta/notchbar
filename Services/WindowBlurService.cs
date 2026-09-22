using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NotchBar.Services;

/// <summary>WPF lifecycle facade for the retained compositor backdrop.</summary>
public sealed class WindowBlurService : IDisposable
{
    private const uint DwmNcRenderingPolicy = 6;
    private const uint DwmWindowCornerPreference = 33;
    private const int DwmNcRenderingDisabled = 2;
    private const int DwmCornerDoNotRound = 1;

    private readonly Window _window;
    private CompositionBackdropHost? _host;
    private IntPtr _hwnd;
    private bool _disposed;

    public WindowBlurService(Window window)
    {
        _window = window;
    }

    public event EventHandler<BackdropAvailabilityChangedEventArgs>? AvailabilityChanged;

    public bool TryApply()
    {
        if (_disposed || !OperatingSystem.IsWindows() || SystemParameters.HighContrast)
        {
            return false;
        }

        if (_host is not null)
        {
            _host.UpdateGeometry();
            return _host.IsActive;
        }

        try
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            SetWindowAttribute(DwmNcRenderingPolicy, DwmNcRenderingDisabled);
            SetWindowAttribute(DwmWindowCornerPreference, DwmCornerDoNotRound);
            var mode = CompositionBackdropHost.SelectActivationMode(
                true,
                false,
                CompositionBackdropHost.IsCompositionEnabled(),
                Environment.OSVersion.Version.Build);
            if (mode == BackdropActivationMode.Fallback)
            {
                return false;
            }

            _host = new CompositionBackdropHost(_window, _hwnd, mode);
            _host.AvailabilityChanged += Host_OnAvailabilityChanged;
            _window.SizeChanged += Window_OnGeometryChanged;
            _window.DpiChanged += Window_OnDpiChanged;
            _host.UpdateGeometry();
            if (!_host.Start())
            {
                DisposeHost();
                return false;
            }

            return _host.IsActive;
        }
        catch (Exception exception) when (
            exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            DisposeHost();
            return false;
        }
    }

    public bool RefreshTheme() => _host?.RefreshTheme() == true;

    private void Host_OnAvailabilityChanged(object? sender, BackdropAvailabilityChangedEventArgs e) =>
        AvailabilityChanged?.Invoke(this, e);

    private void Window_OnGeometryChanged(object sender, SizeChangedEventArgs e) =>
        _host?.UpdateGeometry();

    private void Window_OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e) =>
        _host?.UpdateGeometry();

    private void SetWindowAttribute(uint attribute, int value)
    {
        _ = DwmSetWindowAttribute(_hwnd, attribute, ref value, sizeof(int));
    }

    private void DisposeHost()
    {
        _window.SizeChanged -= Window_OnGeometryChanged;
        _window.DpiChanged -= Window_OnDpiChanged;
        if (_host is not null)
        {
            _host.AvailabilityChanged -= Host_OnAvailabilityChanged;
            _host.Dispose();
            _host = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeHost();
        _hwnd = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
}
