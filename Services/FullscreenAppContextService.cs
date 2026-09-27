using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: InternalsVisibleTo("NotchBar.Tests")]

namespace NotchBar.Services;

/// <summary>
/// Tracks the foreground full-screen window for the configured monitor and resolves its app icon.
/// Native event hooks run on a dedicated message thread; only immutable snapshots reach the UI.
/// </summary>
public sealed class FullscreenAppContextService : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint PeekMessageNoRemove = 0x0000;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const int GaRoot = 2;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int BoundsTolerance = 2;
    private const int MessageQuit = 0x0012;
    private const uint PollIntervalMilliseconds = 1200;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const int WmGetIcon = 0x007F;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;
    private const int GclpHicon = -14;
    private const int GclpHiconSmall = -34;
    private const int SendMessageTimeoutMilliseconds = 90;
    private const int MaximumCachedIcons = 96;

    private readonly MonitorPlacementMode _monitorMode;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _fallbackTimer;
    private readonly WinEventDelegate _winEventCallback;
    private readonly object _iconCacheLock = new();
    private readonly Dictionary<IconCacheKey, LinkedListNode<IconCacheItem>> _iconCache = new();
    private readonly LinkedList<IconCacheItem> _iconCacheLru = new();
    private readonly Thread _hookThread;
    private FullscreenAppContext _current = FullscreenAppContext.Empty;
    private uint _hookThreadId;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private long _contextRevision;
    private int _refreshQueued;
    private int _started;
    private int _disposed;
    // These fields are owned by the UI dispatcher.
    private bool _iconLookupInFlight;
    private bool _iconRetryScheduled;
    private bool _iconRetriesExhausted;
    private int _iconRetryCount;

    public FullscreenAppContextService(MonitorPlacementMode mode)
    {
        _monitorMode = mode;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _fallbackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds)
        };
        _fallbackTimer.Tick += FallbackTimer_OnTick;
        _winEventCallback = WinEvent_OnEvent;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "NotchBar full-screen context hook"
        };
    }

    public FullscreenAppContext Current => Volatile.Read(ref _current);

    public event EventHandler<FullscreenAppContextChangedEventArgs>? Changed;

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            _fallbackTimer.Start();
            Refresh();
        }
        else
        {
            QueueRefresh();
            QueueTimerStart();
        }

        _hookThread.Start();
    }

    private void QueueTimerStart()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _fallbackTimer.Start();
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown can race service startup.
        }
    }

    private void FallbackTimer_OnTick(object? sender, EventArgs e) => Refresh();

    private void WinEvent_OnEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (Volatile.Read(ref _disposed) != 0 || window == IntPtr.Zero)
        {
            return;
        }

        if (eventType == EventObjectLocationChange &&
            (objectId != ObjIdWindow || childId != ChildIdSelf || window != GetForegroundWindow() ||
             GetAncestor(window, GaRoot) != window))
        {
            return;
        }

        if (eventType == EventSystemForeground &&
            (window != GetForegroundWindow() || GetAncestor(window, GaRoot) != window))
        {
            return;
        }

        QueueRefresh();
    }

    private void HookThreadMain()
    {
        // Create the thread queue before publishing the ID so Dispose can reliably post WM_QUIT.
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PeekMessageNoRemove);
        Volatile.Write(ref _hookThreadId, GetCurrentThreadId());
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            WineventOutOfContext);
        _locationHook = SetWinEventHook(
            EventObjectLocationChange,
            EventObjectLocationChange,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            WineventOutOfContext);

        while (Volatile.Read(ref _disposed) == 0)
        {
            var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result <= 0)
            {
                break;
            }

            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }

        if (_foregroundHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_locationHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
    }

    private void QueueRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            _dispatcher.HasShutdownStarted ||
            _dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var nextWindow = ResolveForegroundFullscreenWindow();
        var current = Current;
        if (nextWindow.Handle == IntPtr.Zero)
        {
            if (!current.IsFullscreen && current.WindowHandle == IntPtr.Zero && current.ProcessId == 0 && current.Icon is null)
            {
                return;
            }

            Interlocked.Increment(ref _contextRevision);
            ResetIconLookupState();
            Publish(FullscreenAppContext.Empty);
            return;
        }

        if (current.IsFullscreen && current.WindowHandle == nextWindow.Handle && current.ProcessId == nextWindow.ProcessId)
        {
            StartIconLookupIfNeeded(nextWindow.Handle, nextWindow.ProcessId, Interlocked.Read(ref _contextRevision));
            return;
        }

        var revision = Interlocked.Increment(ref _contextRevision);
        ResetIconLookupState();
        Publish(new FullscreenAppContext(true, nextWindow.Handle, nextWindow.ProcessId, null));
        StartIconLookupIfNeeded(nextWindow.Handle, nextWindow.ProcessId, revision);
    }

    private void ResetIconLookupState()
    {
        _iconLookupInFlight = false;
        _iconRetryScheduled = false;
        _iconRetriesExhausted = false;
        _iconRetryCount = 0;
    }

    private void StartIconLookupIfNeeded(IntPtr window, uint processId, long revision)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Read(ref _contextRevision) != revision ||
            _iconLookupInFlight ||
            _iconRetryScheduled ||
            _iconRetriesExhausted)
        {
            return;
        }

        var current = Current;
        if (!current.IsFullscreen || current.WindowHandle != window || current.ProcessId != processId || current.Icon is not null)
        {
            return;
        }

        _iconLookupInFlight = true;
        _ = ResolveIconAsync(window, processId, revision);
    }

    private (IntPtr Handle, uint ProcessId) ResolveForegroundFullscreenWindow()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return default;
        }

        var root = GetAncestor(window, GaRoot);
        if (root != IntPtr.Zero)
        {
            window = root;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        var isVisible = IsWindowVisible(window);
        var isMinimized = IsIconic(window);
        var isDesktop = IsDesktopShellWindow(window);
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || !GetWindowRect(window, out var windowBounds))
        {
            return default;
        }

        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return default;
        }

        var primaryMonitor = (monitorInfo.Flags & MonitorInfoPrimary) != 0;
        var eligible = FullscreenWindowClassifier.IsEligibleTarget(
            isVisible,
            isMinimized,
            isDesktop,
            processId,
            (uint)Environment.ProcessId,
            primaryMonitor,
            _monitorMode);
        if (!eligible || !FullscreenWindowClassifier.CoversBounds(
                ToRectangle(windowBounds), ToRectangle(monitorInfo.Monitor), BoundsTolerance))
        {
            return default;
        }

        return (window, processId);
    }

    private async Task ResolveIconAsync(IntPtr window, uint processId, long revision)
    {
        ImageSource? icon;
        try
        {
            icon = await Task.Run(() => GetOrExtractIcon(window, processId)).ConfigureAwait(false);
        }
        catch
        {
            icon = null;
        }

        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    Interlocked.Read(ref _contextRevision) != revision)
                {
                    return;
                }

                var current = Current;
                if (!current.IsFullscreen || current.WindowHandle != window || current.ProcessId != processId)
                {
                    return;
                }

                _iconLookupInFlight = false;
                if (icon is not null)
                {
                    Publish(current with { Icon = icon });
                    return;
                }

                ScheduleIconRetry(window, processId, revision);
            }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown can race an icon lookup completing.
        }
    }

    private void ScheduleIconRetry(IntPtr window, uint processId, long revision)
    {
        var delay = FullscreenIconRetryPolicy.GetDelay(_iconRetryCount);
        if (delay is null)
        {
            _iconRetriesExhausted = true;
            return;
        }

        _iconRetryCount++;
        _iconRetryScheduled = true;
        _ = RetryIconLookupAfterDelayAsync(window, processId, revision, delay.Value);
    }

    private async Task RetryIconLookupAfterDelayAsync(IntPtr window, uint processId, long revision, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    Interlocked.Read(ref _contextRevision) != revision)
                {
                    return;
                }

                var current = Current;
                if (!current.IsFullscreen || current.WindowHandle != window || current.ProcessId != processId || current.Icon is not null)
                {
                    _iconRetryScheduled = false;
                    return;
                }

                _iconRetryScheduled = false;
                StartIconLookupIfNeeded(window, processId, revision);
            }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown can race a bounded icon retry completing.
        }
    }

    private ImageSource? GetOrExtractIcon(IntPtr window, uint processId)
    {
        if (!IsWindow(window))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var currentProcessId);
        if (currentProcessId != processId)
        {
            return null;
        }

        var dpi = GetWindowDpi(window);
        var process = TryGetProcessIdentity(processId);
        var key = new IconCacheKey(window, processId, process.CreationTime, dpi);
        if (TryGetCachedIcon(key, out var cached))
        {
            return cached;
        }

        var icon = ExtractWindowIcon(window, dpi);
        if (icon is null && !string.IsNullOrWhiteSpace(process.Path))
        {
            icon = ExtractExecutableIcon(process.Path!, dpi);
        }

        if (icon is not null)
        {
            AddCachedIcon(key, icon);
        }

        return icon;
    }

    private static ImageSource? ExtractWindowIcon(IntPtr window, uint dpi)
    {
        var selected = FullscreenWindowClassifier.SelectFirstAvailableIcon(
            () => SendWindowMessageForIcon(window, IconSmall2),
            () => SendWindowMessageForIcon(window, IconSmall),
            () => SendWindowMessageForIcon(window, IconBig),
            () => GetClassIcon(window, GclpHiconSmall),
            () => GetClassIcon(window, GclpHicon));
        return selected == IntPtr.Zero ? null : CreateFrozenBitmap(selected, dpi);
    }

    private static IntPtr SendWindowMessageForIcon(IntPtr window, int iconType)
    {
        try
        {
            if (SendMessageTimeout(
                    window,
                    WmGetIcon,
                    (UIntPtr)(uint)iconType,
                    IntPtr.Zero,
                    SmtoBlock | SmtoAbortIfHung,
                    SendMessageTimeoutMilliseconds,
                    out var result) != IntPtr.Zero)
            {
                return UIntPtrToIntPtr(result);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // The supported Windows versions export SendMessageTimeoutW.
        }

        return IntPtr.Zero;
    }

    private static IntPtr GetClassIcon(IntPtr window, int index)
    {
        try
        {
            return UIntPtrToIntPtr(GetClassLongPtr(window, index));
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    private static ImageSource? ExtractExecutableIcon(string path, uint dpi)
    {
        var info = new ShellFileInfo();
        _ = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (info.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return CreateFrozenBitmap(info.Icon, dpi);
        }
        finally
        {
            _ = DestroyIcon(info.Icon);
        }
    }

    private static BitmapSource? CreateFrozenBitmap(IntPtr icon, uint dpi)
    {
        var ownedIcon = CopyIcon(icon);
        var iconToRead = ownedIcon == IntPtr.Zero ? icon : ownedIcon;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(iconToRead, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            if (bitmap.IsFrozen && Math.Abs(bitmap.DpiX - dpi) < 0.5 && Math.Abs(bitmap.DpiY - dpi) < 0.5)
            {
                return bitmap;
            }

            var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            var normalized = BitmapSource.Create(
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                dpi,
                dpi,
                bitmap.Format,
                bitmap.Palette,
                pixels,
                stride);
            normalized.Freeze();
            return normalized;
        }
        catch (Exception) when (icon != IntPtr.Zero)
        {
            return null;
        }
        finally
        {
            if (ownedIcon != IntPtr.Zero)
            {
                _ = DestroyIcon(ownedIcon);
            }
        }
    }

    private static uint GetWindowDpi(IntPtr window)
    {
        try
        {
            return Math.Max(96u, GetDpiForWindow(window));
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static (string? Path, long CreationTime) TryGetProcessIdentity(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            var path = new StringBuilder(32768);
            var pathLength = (uint)path.Capacity;
            var imagePath = QueryFullProcessImageName(process, 0, path, ref pathLength)
                ? path.ToString()
                : null;
            var creationTime = GetProcessTimes(process, out var creation, out _, out _, out _)
                ? ((long)creation.HighDateTime << 32) | (uint)creation.LowDateTime
                : 0L;
            return (imagePath, creationTime);
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private bool TryGetCachedIcon(IconCacheKey key, out ImageSource? icon)
    {
        lock (_iconCacheLock)
        {
            if (!_iconCache.TryGetValue(key, out var item))
            {
                icon = null;
                return false;
            }

            _iconCacheLru.Remove(item);
            _iconCacheLru.AddFirst(item);
            icon = item.Value.Icon;
            return true;
        }
    }

    private void AddCachedIcon(IconCacheKey key, ImageSource icon)
    {
        lock (_iconCacheLock)
        {
            if (_iconCache.Remove(key, out var existing))
            {
                _iconCacheLru.Remove(existing);
            }

            var node = _iconCacheLru.AddFirst(new IconCacheItem(key, icon));
            _iconCache[key] = node;
            while (_iconCache.Count > MaximumCachedIcons)
            {
                var last = _iconCacheLru.Last!;
                _iconCacheLru.RemoveLast();
                _iconCache.Remove(last.Value.Key);
            }
        }
    }

    private void Publish(FullscreenAppContext next)
    {
        var current = Current;
        if (current.IsFullscreen == next.IsFullscreen &&
            current.WindowHandle == next.WindowHandle &&
            current.ProcessId == next.ProcessId &&
            ReferenceEquals(current.Icon, next.Icon))
        {
            return;
        }

        Volatile.Write(ref _current, next);
        Changed?.Invoke(this, new FullscreenAppContextChangedEventArgs(next));
    }

    private static bool IsDesktopShellWindow(IntPtr window)
    {
        if (window == GetShellWindow())
        {
            return true;
        }

        var className = new StringBuilder(64);
        if (GetClassName(window, className, className.Capacity) == 0)
        {
            return false;
        }

        return className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd";
    }

    private static Rectangle ToRectangle(NativeRect rect) =>
        Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static IntPtr UIntPtrToIntPtr(UIntPtr value) =>
        IntPtr.Size == 8
            ? new IntPtr(unchecked((long)value.ToUInt64()))
            : new IntPtr(unchecked((int)value.ToUInt32()));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            _fallbackTimer.Stop();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => _fallbackTimer.Stop()));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher shutdown can race service disposal.
            }
        }

        _fallbackTimer.Tick -= FallbackTimer_OnTick;
        var hookThreadId = Volatile.Read(ref _hookThreadId);
        if (hookThreadId != 0)
        {
            _ = PostThreadMessage(hookThreadId, MessageQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        lock (_iconCacheLock)
        {
            _iconCache.Clear();
            _iconCacheLru.Clear();
        }

        Changed = null;
    }

    private readonly record struct IconCacheKey(IntPtr Window, uint ProcessId, long CreationTime, uint Dpi);
    private sealed record IconCacheItem(IconCacheKey Key, ImageSource Icon);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public int LowDateTime;
        public int HighDateTime;
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minFilter, uint maxFilter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minFilter, uint maxFilter, uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern UIntPtr GetClassLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder imageName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern UIntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);
}

internal static class FullscreenWindowClassifier
{
    internal static bool IsEligibleTarget(
        bool isVisible,
        bool isMinimized,
        bool isDesktopShell,
        uint processId,
        uint ownProcessId,
        bool monitorIsPrimary,
        MonitorPlacementMode mode)
    {
        if (!isVisible || isMinimized || isDesktopShell || processId == 0 || processId == ownProcessId)
        {
            return false;
        }

        return mode != MonitorPlacementMode.Primary || monitorIsPrimary;
    }

    internal static bool CoversBounds(Rectangle window, Rectangle monitor, int tolerance)
    {
        return window.Left <= monitor.Left + tolerance &&
               window.Top <= monitor.Top + tolerance &&
               window.Right >= monitor.Right - tolerance &&
               window.Bottom >= monitor.Bottom - tolerance;
    }

    internal static IntPtr SelectFirstAvailableIcon(params Func<IntPtr>[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var icon = candidate();
            if (icon != IntPtr.Zero)
            {
                return icon;
            }
        }

        return IntPtr.Zero;
    }
}

internal static class FullscreenIconRetryPolicy
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(2)
    ];

    internal static TimeSpan? GetDelay(int retryCount) =>
        retryCount >= 0 && retryCount < RetryDelays.Length
            ? RetryDelays[retryCount]
            : null;
}

public sealed record FullscreenAppContext(bool IsFullscreen, IntPtr WindowHandle, uint ProcessId, ImageSource? Icon)
{
    internal static FullscreenAppContext Empty { get; } = new(false, IntPtr.Zero, 0, null);
}

public sealed class FullscreenAppContextChangedEventArgs(FullscreenAppContext context) : EventArgs
{
    public FullscreenAppContext Context { get; } = context;
    public bool IsFullscreen => Context.IsFullscreen;
    public IntPtr WindowHandle => Context.WindowHandle;
    public uint ProcessId => Context.ProcessId;
    public ImageSource? Icon => Context.Icon;
}
