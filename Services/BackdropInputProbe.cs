using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NotchBar.Services;

/// <summary>
/// Proves pointer transparency against a window in another process. Native
/// style inspection and HTTRANSPARENT are intentionally not accepted as proof.
/// </summary>
public static class BackdropInputProbe
{
    public const string ChildSwitch = "--backdrop-input-probe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static bool IsChildMode(string[] args) => args.Length >= 7 && args[0] == ChildSwitch;

    public static async Task<int> RunChildAsync(string[] args)
    {
        if (!IsChildMode(args) ||
            !int.TryParse(args[3], out var x) ||
            !int.TryParse(args[4], out var y) ||
            !int.TryParse(args[5], out var width) ||
            !int.TryParse(args[6], out var height))
        {
            return 2;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(Timeout);
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            using var reader = new StreamReader(pipe);
            using var receiver = new ProbeReceiverWindow(new WindowPixelGeometry(x, y, width, height));
            var ready = new ProbeMessage(args[2], "ready", Environment.ProcessId, receiver.Handle.ToInt64(), false, false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(ready));
            var arm = Deserialize(await reader.ReadLineAsync(timeout.Token), args[2], "arm");
            if (arm is null)
            {
                return 5;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                static () => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var armed = new ProbeMessage(args[2], "armed", Environment.ProcessId, receiver.Handle.ToInt64(), false, false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(armed));
            var signal = await receiver.WaitForClicksAsync(timeout.Token);
            var result = new ProbeMessage(args[2], "result", Environment.ProcessId, receiver.Handle.ToInt64(), signal.Down, signal.Up);
            await writer.WriteLineAsync(JsonSerializer.Serialize(result));
            return signal.Down && signal.Up ? 0 : 3;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar backdrop input probe child failed: {exception}");
            return 4;
        }
    }

    public static async Task<PointerProbeEvidence> RunParentAsync(
        BackdropHostWindow companion,
        WindowPixelGeometry bounds,
        CancellationToken cancellationToken = default)
    {
        var pipeName = $"NotchBar.BackdropProbe.{Environment.ProcessId}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedTimeout.CancelAfter(Timeout);

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The NotchBar executable path is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(ChildSwitch);
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add(bounds.X.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(bounds.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(bounds.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(bounds.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The backdrop input probe process could not be started.");
        var foregroundBefore = GetForegroundWindow();
        var focusBefore = GetFocusWindow(foregroundBefore);
        ProbeMessage? ready = null;
        ProbeMessage? result = null;
        var timedOut = false;
        try
        {
            await pipe.WaitForConnectionAsync(linkedTimeout.Token);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            ready = Deserialize(await reader.ReadLineAsync(linkedTimeout.Token), nonce, "ready");
            if (ready is null)
            {
                throw new InvalidDataException("The backdrop input probe returned invalid readiness evidence.");
            }

            var arm = new ProbeMessage(nonce, "arm", Environment.ProcessId, 0, false, false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(arm));
            var armed = Deserialize(await reader.ReadLineAsync(linkedTimeout.Token), nonce, "armed");
            if (armed is null || armed.Value.WindowHandle != ready.Value.WindowHandle || armed.Value.ProcessId != ready.Value.ProcessId)
            {
                throw new InvalidDataException("The backdrop input probe did not reach a live dispatcher boundary.");
            }

            // The helper is already visible at these bounds. Put the companion
            // at the top of the topmost band and exercise a transparent point
            // inside its rectangular HWND but outside the rounded clip.
            if (!companion.ShowForProbe(bounds, new IntPtr(-1)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The backdrop companion could not be shown for its pointer probe.");
            }

            if (!GetCursorPos(out var previousCursor))
            {
                previousCursor = default;
            }

            try
            {
                SendMouseClick(bounds.X + 2, bounds.Y + bounds.Height - 2);
            }
            finally
            {
                _ = SetCursorPos(previousCursor.X, previousCursor.Y);
                companion.Hide();
            }

            result = Deserialize(await reader.ReadLineAsync(linkedTimeout.Token), nonce, "result");
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            companion.Hide();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar backdrop input probe failed: {exception}");
            companion.Hide();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        var foregroundAfter = GetForegroundWindow();
        var focusAfter = GetFocusWindow(foregroundAfter);
        return new PointerProbeEvidence(
            result?.Down ?? false,
            result?.Up ?? false,
            ready?.ProcessId ?? process.Id,
            result?.ProcessId ?? 0,
            new IntPtr(ready?.WindowHandle ?? 0),
            new IntPtr(result?.WindowHandle ?? 0),
            foregroundBefore,
            foregroundAfter,
            focusBefore,
            focusAfter,
            timedOut);
    }

    private static ProbeMessage? Deserialize(string? json, string nonce, string kind)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var message = JsonSerializer.Deserialize<ProbeMessage>(json);
        return message is { } value && value.Nonce == nonce && value.Kind == kind ? value : null;
    }

    private static IntPtr GetFocusWindow(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    private static void SendMouseClick(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The backdrop input probe could not position its pointer.");
        }

        var inputs = new[]
        {
            MouseInput.Create(0, 0, 0x0002),
            MouseInput.Create(0, 0, 0x0004)
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The backdrop input probe could not synthesize its pointer click.");
        }
    }

    private readonly record struct ProbeMessage(string Nonce, string Kind, int ProcessId, long WindowHandle, bool Down, bool Up);

    private sealed class ProbeReceiverWindow : IDisposable
    {
        private const string ClassName = "NotchBar.BackdropInputProbeReceiver";
        private const uint WmLButtonDown = 0x0201;
        private const uint WmLButtonUp = 0x0202;
        private const uint WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const uint WsPopup = 0x80000000;
        private const uint ExStyle = 0x00000080 | 0x08000000;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly WndProc Procedure = WindowProc;
        private static readonly Dictionary<IntPtr, ProbeReceiverWindow> Instances = [];
        private static ushort _atom;
        private readonly TaskCompletionSource<(bool Down, bool Up)> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _down;
        private bool _up;

        public ProbeReceiverWindow(WindowPixelGeometry bounds)
        {
            EnsureClass();
            Handle = CreateWindowEx(ExStyle, ClassName, string.Empty, WsPopup, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Instances[Handle] = this;
            if (!SetWindowPos(Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate | SwpShowWindow))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public IntPtr Handle { get; private set; }

        public async Task<(bool Down, bool Up)> WaitForClicksAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return await _completion.Task;
        }

        private void OnMessage(uint message)
        {
            if (message == WmLButtonDown) _down = true;
            if (message == WmLButtonUp) _up = true;
            if (_down && _up) _completion.TrySetResult((_down, _up));
        }

        public void Dispose()
        {
            var handle = Handle;
            Handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                Instances.Remove(handle);
                _ = DestroyWindow(handle);
            }
        }

        private static void EnsureClass()
        {
            if (_atom != 0) return;
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = Procedure,
                Instance = GetModuleHandle(null),
                ClassName = ClassName
            };
            _atom = RegisterClassEx(ref windowClass);
            if (_atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (Instances.TryGetValue(hwnd, out var instance)) instance.OnMessage(message);
            if (message == WmMouseActivate) return new IntPtr(MaNoActivate);
            return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInputData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private static class MouseInput
    {
        public static Input Create(int x, int y, uint flags) => new()
        {
            Type = 0,
            Mouse = new MouseInputData { X = x, Y = y, Flags = flags }
        };
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
