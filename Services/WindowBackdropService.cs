using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace NotchBar.Services;

public sealed class WindowBackdropService : IDisposable
{
    private const int WindowCompositionAttributeAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentUseGradientColor = 2;
    private const int RegionCombineOr = 2;

    private static readonly uint AcrylicTint = PackAccentColor(
        alpha: 0x3A,
        red: 0xF2,
        green: 0xF8,
        blue: 0xFC);

    private IntPtr _handle;
    private Window? _window;
    private bool _disposed;

    public bool TryApply(Window window)
    {
        if (_disposed || !OperatingSystem.IsWindows())
        {
            return false;
        }

        _window = window;
        _handle = new WindowInteropHelper(window).Handle;
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        _window.SizeChanged += Window_OnSizeChanged;
        UpdateRoundedRegion();

        if (SystemParameters.HighContrast || !TransparencyEffectsEnabled())
        {
            return false;
        }

        var preferredState = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)
            ? AccentEnableAcrylicBlurBehind
            : AccentEnableBlurBehind;

        if (TryApplyAccent(preferredState, AcrylicTint))
        {
            return true;
        }

        return preferredState != AccentEnableBlurBehind
            && TryApplyAccent(AccentEnableBlurBehind, AcrylicTint);
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRoundedRegion();

    private void UpdateRoundedRegion()
    {
        if (_handle == IntPtr.Zero || !GetClientRect(_handle, out var clientRect))
        {
            return;
        }

        var width = Math.Max(1, clientRect.Right - clientRect.Left);
        var height = Math.Max(1, clientRect.Bottom - clientRect.Top);
        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var radius = Math.Clamp(
            (int)Math.Round(20d * dpi / 96d),
            1,
            Math.Min(width, height) / 2);
        var roundedRegion = CreateRoundRectRgn(
            0,
            0,
            width + 1,
            height + 1,
            radius * 2,
            radius * 2);
        if (roundedRegion == IntPtr.Zero)
        {
            return;
        }

        // The WPF surface only rounds the bottom corners because the top edge
        // meets the screen. Square the top part of the native backdrop as well.
        var topRegion = CreateRectRgn(0, 0, width + 1, Math.Min(radius, height));
        if (topRegion != IntPtr.Zero)
        {
            _ = CombineRgn(roundedRegion, roundedRegion, topRegion, RegionCombineOr);
            _ = DeleteObject(topRegion);
        }

        if (!SetWindowRgn(_handle, roundedRegion, true))
        {
            _ = DeleteObject(roundedRegion);
        }
    }

    private bool TryApplyAccent(int accentState, uint gradientColor)
    {
        var policy = new AccentPolicy
        {
            AccentState = accentState,
            AccentFlags = accentState == AccentDisabled ? 0 : AccentUseGradientColor,
            GradientColor = gradientColor,
            AnimationId = 0
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttributeAccentPolicy,
                Data = pointer,
                SizeOfData = size
            };

            return SetWindowCompositionAttribute(_handle, ref data);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool TransparencyEffectsEnabled()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency",
                1);

            return value is not int enabled || enabled != 0;
        }
        catch
        {
            return true;
        }
    }

    private static uint PackAccentColor(byte alpha, byte red, byte green, byte blue) =>
        ((uint)alpha << 24)
        | ((uint)blue << 16)
        | ((uint)green << 8)
        | red;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_window is not null)
        {
            _window.SizeChanged -= Window_OnSizeChanged;
        }

        if (_handle != IntPtr.Zero)
        {
            _ = TryApplyAccent(AccentDisabled, 0);
            _handle = IntPtr.Zero;
        }

        _window = null;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowCompositionAttribute")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(
        IntPtr windowHandle,
        ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowRgn(
        IntPtr windowHandle,
        IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(
        int left,
        int top,
        int right,
        int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(
        IntPtr destination,
        IntPtr sourceOne,
        IntPtr sourceTwo,
        int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
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
}