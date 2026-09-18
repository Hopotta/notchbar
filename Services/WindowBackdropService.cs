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

    private static readonly uint AcrylicTint = PackAccentColor(
        alpha: 0x3A,
        red: 0xF2,
        green: 0xF8,
        blue: 0xFC);

    private IntPtr _handle;
    private bool _disposed;

    public bool TryApply(Window window)
    {
        if (_disposed
            || !OperatingSystem.IsWindows()
            || SystemParameters.HighContrast
            || !TransparencyEffectsEnabled())
        {
            return false;
        }

        _handle = new WindowInteropHelper(window).Handle;
        if (_handle == IntPtr.Zero)
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
        if (_handle != IntPtr.Zero)
        {
            _ = TryApplyAccent(AccentDisabled, 0);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowCompositionAttribute")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(
        IntPtr windowHandle,
        ref WindowCompositionAttributeData data);

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
