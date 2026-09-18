using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NotchBar.Services;

public static class WindowBackdropService
{
    private const int WindowCompositionAttributeAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const uint LightAcrylicTint = 0xD8FDF8F2;

    public static void ApplyAcrylic(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var accentPolicy = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = LightAcrylicTint
        };

        var accentPolicySize = Marshal.SizeOf<AccentPolicy>();
        var accentPolicyPointer = Marshal.AllocHGlobal(accentPolicySize);
        try
        {
            Marshal.StructureToPtr(accentPolicy, accentPolicyPointer, false);
            var compositionAttribute = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttributeAccentPolicy,
                Data = accentPolicyPointer,
                SizeOfData = accentPolicySize
            };
            _ = SetWindowCompositionAttribute(handle, ref compositionAttribute);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPolicyPointer);
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
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