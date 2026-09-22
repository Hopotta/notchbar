using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace NotchBar.Services;

/// <summary>
/// Installs a compositor-owned backdrop for the lifetime of the window. The
/// current WPF window is layered (<see cref="Window.AllowsTransparency"/>), so
/// Accent acrylic is used on Windows 10 and 11; DWM system backdrops are kept
/// only for a future non-layered window where they are supported.
/// </summary>
public sealed class WindowBlurService : IDisposable
{
    private const uint DwmNcRenderingPolicy = 6;
    private const uint DwmWindowCornerPreference = 33;
    private const uint DwmSystemBackdropType = 38;
    private const int DwmNcRenderingDisabled = 2;
    private const int DwmCornerDoNotRound = 1;
    private const int DwmSystemBackdropNone = 1;
    private const int DwmSystemBackdropTransientWindow = 3;
    private const int WindowCompositionAttributeAccentPolicy = 19;
    private const byte AccentTintAlpha = 0x1C;

    private readonly Window _window;
    private IntPtr _hwnd;
    private WindowBackdropMode _mode;
    private bool _disposed;

    public WindowBlurService(Window window)
    {
        _window = window;
    }

    public bool IsActive => _mode is not WindowBackdropMode.Fallback;

    public bool TryApply()
    {
        if (_disposed || !OperatingSystem.IsWindows() || SystemParameters.HighContrast)
        {
            return false;
        }

        if (IsActive)
        {
            return true;
        }

        try
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            if (_hwnd == IntPtr.Zero || !IsCompositionEnabled())
            {
                return false;
            }

            SetWindowAttribute(DwmNcRenderingPolicy, DwmNcRenderingDisabled);
            SetWindowAttribute(DwmWindowCornerPreference, DwmCornerDoNotRound);

            var requestedMode = SelectBackdropMode(
                isWindows: true,
                highContrast: false,
                isLayeredWindow: _window.AllowsTransparency,
                osVersion: Environment.OSVersion.Version);

            _mode = requestedMode switch
            {
                WindowBackdropMode.SystemDesktopAcrylic when TryApplySystemBackdrop() =>
                    WindowBackdropMode.SystemDesktopAcrylic,
                WindowBackdropMode.SystemDesktopAcrylic when TryApplyAccentAcrylic() =>
                    WindowBackdropMode.AccentAcrylic,
                WindowBackdropMode.AccentAcrylic when TryApplyAccentAcrylic() =>
                    WindowBackdropMode.AccentAcrylic,
                _ => WindowBackdropMode.Fallback
            };

            return IsActive;
        }
        catch (DllNotFoundException)
        {
            _mode = WindowBackdropMode.Fallback;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _mode = WindowBackdropMode.Fallback;
            return false;
        }
    }

    /// <summary>
    /// Refreshes only the theme-dependent native tint. Acrylic is HWND state
    /// and deliberately is not reapplied during size, position, or DPI changes.
    /// </summary>
    public bool RefreshTheme()
    {
        if (_disposed || _mode is not WindowBackdropMode.AccentAcrylic)
        {
            return IsActive;
        }

        try
        {
            if (TryApplyAccentAcrylic())
            {
                return true;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        DisableAccentAcrylic();
        _mode = WindowBackdropMode.Fallback;
        return false;
    }

    internal static WindowBackdropMode SelectBackdropMode(
        bool isWindows,
        bool highContrast,
        bool isLayeredWindow,
        Version osVersion)
    {
        if (!isWindows || highContrast || osVersion.Major < 10)
        {
            return WindowBackdropMode.Fallback;
        }

        // DWM can report success for a system backdrop on a layered HWND while
        // presenting no material. Accent acrylic is the deterministic path for
        // NotchBar's current AllowsTransparency window on both Windows 10/11.
        if (isLayeredWindow)
        {
            return WindowBackdropMode.AccentAcrylic;
        }

        return osVersion.Build >= 22621
            ? WindowBackdropMode.SystemDesktopAcrylic
            : WindowBackdropMode.AccentAcrylic;
    }

    internal static uint PackAccentColor(byte alpha, byte red, byte green, byte blue) =>
        ((uint)alpha << 24) |
        ((uint)blue << 16) |
        ((uint)green << 8) |
        red;

    private bool TryApplyAccentAcrylic()
    {
        var policy = new AccentPolicy
        {
            State = AccentState.EnableAcrylicBlurBehind,
            Flags = 0,
            GradientColor = ResolveAccentGradientColor(),
            AnimationId = 0
        };

        return SetAccentPolicy(policy);
    }

    private bool TryApplySystemBackdrop()
    {
        if (_window.AllowsTransparency || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return false;
        }

        var value = DwmSystemBackdropTransientWindow;
        return DwmSetWindowAttribute(_hwnd, DwmSystemBackdropType, ref value, sizeof(int)) == 0;
    }

    private uint ResolveAccentGradientColor()
    {
        var tint = _window.TryFindResource("IslandBackground") is SolidColorBrush brush
            ? brush.Color
            : MediaColor.FromRgb(0x20, 0x20, 0x20);
        return PackAccentColor(AccentTintAlpha, tint.R, tint.G, tint.B);
    }

    private bool SetAccentPolicy(AccentPolicy policy)
    {
        var policyPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, policyPointer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttributeAccentPolicy,
                Data = policyPointer,
                SizeOfData = Marshal.SizeOf<AccentPolicy>()
            };
            return SetWindowCompositionAttribute(_hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(policyPointer);
        }
    }

    private static bool IsCompositionEnabled() =>
        DwmIsCompositionEnabled(out var enabled) == 0 && enabled;

    private void SetWindowAttribute(uint attribute, int value)
    {
        _ = DwmSetWindowAttribute(_hwnd, attribute, ref value, sizeof(int));
    }

    private void DisableAccentAcrylic()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = SetAccentPolicy(new AccentPolicy
            {
                State = AccentState.Disabled
            });
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private void DisableSystemBackdrop()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var value = DwmSystemBackdropNone;
            _ = DwmSetWindowAttribute(_hwnd, DwmSystemBackdropType, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_mode is WindowBackdropMode.AccentAcrylic)
        {
            DisableAccentAcrylic();
        }
        else if (_mode is WindowBackdropMode.SystemDesktopAcrylic)
        {
            DisableSystemBackdrop();
        }

        _mode = WindowBackdropMode.Fallback;
        _hwnd = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableAcrylicBlurBehind = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState State;
        public int Flags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);
}

internal enum WindowBackdropMode
{
    Fallback,
    AccentAcrylic,
    SystemDesktopAcrylic
}
