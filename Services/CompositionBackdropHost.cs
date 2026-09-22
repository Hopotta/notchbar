using System.Numerics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.Xaml.Media;
using WinRT;
using MediaColor = System.Windows.Media.Color;

namespace NotchBar.Services;

/// <summary>
/// Owns one retained, masked Windows composition backdrop tree. The desktop
/// target remains detached until both the compositor proof commit and the
/// anti-aliased mask load have succeeded, so an unmasked HWND-sized frame can
/// never be presented.
/// </summary>
public sealed class CompositionBackdropHost : IDisposable
{
    public const double CornerRadiusDip = 20d;

    private const string MaskResourceName = "NotchBar.Assets.IslandBackdropMask.png";
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000L;
    private const uint DwmUseHostBackdropBrush = 17;
    private const int WindowCompositionAttributeAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableHostBackdrop = 5;
    private const float MaskCornerRadiusPixels = 20f;

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly BackdropActivationMode _activationMode;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    private DispatcherQueueController? _dispatcherQueueController;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private Windows.UI.Composition.ContainerVisual? _root;
    private SpriteVisual? _backdropVisual;
    private CompositionBackdropBrush? _hostBackdropBrush;
    private CompositionEffectFactory? _effectFactory;
    private CompositionEffectBrush? _effectBrush;
    private CompositionMaskBrush? _maskBrush;
    private CompositionNineGridBrush? _nineGridBrush;
    private CompositionSurfaceBrush? _maskSurfaceBrush;
    private LoadedImageSurface? _maskSurface;
    private InMemoryRandomAccessStream? _maskStream;
    private int _generation;
    private bool _samplingEnabled;
    private bool _proofCommitCompleted;
    private bool _initializationStarted;
    private bool _disposed;
    private BackdropMetrics _metrics;

    public CompositionBackdropHost(Window window, IntPtr hwnd, BackdropActivationMode activationMode)
    {
        _window = window;
        _hwnd = hwnd;
        _activationMode = activationMode;
        _dispatcher = window.Dispatcher;
    }

    public event EventHandler<BackdropAvailabilityChangedEventArgs>? AvailabilityChanged;

    public bool IsActive { get; private set; }

    public bool Start()
    {
        if (_disposed || _initializationStarted || _activationMode == BackdropActivationMode.Fallback)
        {
            return false;
        }

        if (!_window.AllowsTransparency || !IsLayeredWindow(_hwnd))
        {
            return false;
        }

        _initializationStarted = true;
        var generation = ++_generation;
        _ = InitializeAsync(generation);
        return true;
    }

    public void UpdateGeometry()
    {
        if (_disposed || _hwnd == IntPtr.Zero || !GetClientRect(_hwnd, out var clientRect))
        {
            return;
        }

        var metrics = CalculateMetrics(
            clientRect.Right - clientRect.Left,
            clientRect.Bottom - clientRect.Top,
            GetDpi(_hwnd));
        if (metrics == _metrics)
        {
            return;
        }

        _metrics = metrics;
        if (_backdropVisual is not null)
        {
            _backdropVisual.Size = metrics.VisualSizePixels;
        }

        if (_nineGridBrush is not null)
        {
            var insetScale = metrics.BottomLeftRadiusPixels / MaskCornerRadiusPixels;
            _nineGridBrush.SetInsetScales(insetScale, insetScale, insetScale, insetScale);
        }

        _effectBrush?.Properties.InsertScalar("Blur.BlurAmount", 20f * metrics.DpiScale);
    }

    public bool RefreshTheme()
    {
        if (_disposed || !IsActive || _effectBrush is null)
        {
            return false;
        }

        _effectBrush.Properties.InsertColor("Tint.Color", ResolveCompositionTint());
        return true;
    }

    public static BackdropActivationMode SelectActivationMode(
        bool isWindows,
        bool highContrast,
        bool compositionEnabled,
        int windowsBuild)
    {
        if (!isWindows || highContrast || !compositionEnabled)
        {
            return BackdropActivationMode.Fallback;
        }

        if (windowsBuild >= 22000)
        {
            return BackdropActivationMode.Windows11HostBackdrop;
        }

        return windowsBuild is >= 19041 and <= 19045
            ? BackdropActivationMode.Windows10HostBackdrop
            : BackdropActivationMode.Fallback;
    }

    public static BackdropMetrics CalculateMetrics(
        int clientWidthPixels,
        int clientHeightPixels,
        uint dpi)
    {
        var width = Math.Max(1, clientWidthPixels);
        var height = Math.Max(1, clientHeightPixels);
        var effectiveDpi = dpi == 0 ? 96u : dpi;
        var dpiScale = effectiveDpi / 96f;
        var requestedRadius = (float)(CornerRadiusDip * dpiScale);
        var radius = Math.Clamp(requestedRadius, 0f, Math.Min(width, height) / 2f);

        return new BackdropMetrics(
            new Vector2(width, height),
            dpiScale,
            0,
            0,
            radius,
            radius);
    }

    private async Task InitializeAsync(int generation)
    {
        try
        {
            EnsureDispatcherQueue();
            if (!EnableSampling() || !IsGenerationCurrent(generation))
            {
                FailInitialization(generation);
                return;
            }

            _compositor = new Compositor();
            if (!CompositionCapabilities.GetForCurrentView().AreEffectsSupported())
            {
                FailInitialization(generation);
                return;
            }

            _target = CreateDesktopWindowTarget(_compositor, _hwnd);
            _hostBackdropBrush = _compositor.CreateHostBackdropBrush();
            CreateEffectGraph();

            // Proof commit: the target still has no root, so no rectangular
            // material can be presented while effects are being validated.
            await _compositor.RequestCommitAsync();
            if (!IsGenerationCurrent(generation))
            {
                return;
            }

            _proofCommitCompleted = true;
            UpdateGeometry();
            await BeginMaskLoadAsync(generation);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar composition backdrop unavailable: {exception}");
            FailInitialization(generation);
        }
    }

    private void EnsureDispatcherQueue()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            DwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = DispatcherQueueThreadType.Current,
            ApartmentType = DispatcherQueueApartmentType.ComSta
        };
        var result = CreateDispatcherQueueController(options, out var controllerPointer);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            _dispatcherQueueController = MarshalInterface<DispatcherQueueController>.FromAbi(controllerPointer);
        }
        finally
        {
            if (controllerPointer != IntPtr.Zero)
            {
                Marshal.Release(controllerPointer);
            }
        }
    }

    private bool EnableSampling()
    {
        // Clear a legacy Accent policy before enabling sampling. State 0 is
        // non-painting; states 3/4 are intentionally never installed.
        _ = SetAccentPolicy(AccentDisabled, 0);
        var enabled = _activationMode switch
        {
            BackdropActivationMode.Windows11HostBackdrop => SetHostBackdropAttribute(true),
            BackdropActivationMode.Windows10HostBackdrop => SetAccentPolicy(AccentEnableHostBackdrop, 0),
            _ => false
        };
        _samplingEnabled = enabled;
        return enabled;
    }

    private void CreateEffectGraph()
    {
        if (_compositor is null || _hostBackdropBrush is null)
        {
            throw new InvalidOperationException("Composition is not initialized.");
        }

        var blur = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = 20f * (_metrics.DpiScale <= 0 ? 1f : _metrics.DpiScale),
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("backdrop")
        };
        var saturation = new SaturationEffect
        {
            Name = "Saturation",
            Saturation = 1.8f,
            Source = blur
        };
        var tint = new ColorSourceEffect
        {
            Name = "Tint",
            Color = ResolveCompositionTint()
        };
        var material = new BlendEffect
        {
            Name = "Material",
            Mode = BlendEffectMode.SoftLight,
            Background = saturation,
            Foreground = tint
        };

        _effectFactory = _compositor.CreateEffectFactory(material, ["Blur.BlurAmount", "Tint.Color"]);
        _effectBrush = _effectFactory.CreateBrush();
        _effectBrush.SetSourceParameter("backdrop", _hostBackdropBrush);
    }

    private async Task BeginMaskLoadAsync(int generation)
    {
        await using var resource = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(MaskResourceName)
            ?? throw new InvalidOperationException($"Embedded backdrop mask '{MaskResourceName}' was not found.");

        _maskStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(_maskStream))
        {
            var bytes = new byte[resource.Length];
            var totalRead = 0;
            while (totalRead < bytes.Length)
            {
                var read = await resource.ReadAsync(bytes.AsMemory(totalRead));
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead != bytes.Length)
            {
                throw new EndOfStreamException("The embedded backdrop mask could not be read completely.");
            }

            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        if (!IsGenerationCurrent(generation))
        {
            return;
        }

        _maskStream.Seek(0);
        _maskSurface = LoadedImageSurface.StartLoadFromStream(_maskStream);
        TypedEventHandler<LoadedImageSurface, LoadedImageSourceLoadCompletedEventArgs>? handler = null;
        handler = (surface, args) =>
        {
            surface.LoadCompleted -= handler;
            _ = _dispatcher.BeginInvoke(new Action(() =>
            {
                if (args.Status == LoadedImageSourceLoadStatus.Success)
                {
                    AttachMaskedTree(generation);
                }
                else
                {
                    FailInitialization(generation);
                }
            }));
        };
        _maskSurface.LoadCompleted += handler;
    }

    private void AttachMaskedTree(int generation)
    {
        if (!CanAttachMaskedRoot(
                _proofCommitCompleted,
                maskLoadSucceeded: true,
                generationCurrent: IsGenerationCurrent(generation)) ||
            _compositor is null ||
            _target is null ||
            _effectBrush is null ||
            _maskSurface is null)
        {
            return;
        }

        _maskSurfaceBrush = _compositor.CreateSurfaceBrush(_maskSurface);
        _maskSurfaceBrush.Stretch = CompositionStretch.None;
        _nineGridBrush = _compositor.CreateNineGridBrush();
        _nineGridBrush.Source = _maskSurfaceBrush;
        _nineGridBrush.SetInsets(
            MaskCornerRadiusPixels,
            MaskCornerRadiusPixels,
            MaskCornerRadiusPixels,
            MaskCornerRadiusPixels);

        _maskBrush = _compositor.CreateMaskBrush();
        _maskBrush.Source = _effectBrush;
        _maskBrush.Mask = _nineGridBrush;

        _backdropVisual = _compositor.CreateSpriteVisual();
        _backdropVisual.Brush = _maskBrush;
        _backdropVisual.Opacity = 0;
        _root = _compositor.CreateContainerVisual();
        _root.Children.InsertAtBottom(_backdropVisual);
        UpdateGeometry();

        _target.Root = _root;
        _backdropVisual.Opacity = 1;
        IsActive = true;
        AvailabilityChanged?.Invoke(this, new BackdropAvailabilityChangedEventArgs(true));
    }

    public static bool CanAttachMaskedRoot(
        bool proofCommitSucceeded,
        bool maskLoadSucceeded,
        bool generationCurrent) =>
        proofCommitSucceeded && maskLoadSucceeded && generationCurrent;

    private Windows.UI.Color ResolveCompositionTint()
    {
        var color = _window.TryFindResource("IslandBackground") is System.Windows.Media.SolidColorBrush brush
            ? brush.Color
            : MediaColor.FromRgb(0x20, 0x20, 0x20);
        return Windows.UI.Color.FromArgb(0x18, color.R, color.G, color.B);
    }

    private bool IsGenerationCurrent(int generation) => !_disposed && generation == _generation;

    private void FailInitialization(int generation)
    {
        if (!IsGenerationCurrent(generation))
        {
            return;
        }

        ++_generation;
        IsActive = false;
        DetachAndDisposeComposition();
        DisableSampling();
        AvailabilityChanged?.Invoke(this, new BackdropAvailabilityChangedEventArgs(false));
    }

    private void DetachAndDisposeComposition()
    {
        if (_target is not null)
        {
            _target.Root = null;
        }

        (_maskSurface as IDisposable)?.Dispose();
        _maskStream?.Dispose();
        _maskSurface = null;
        _maskStream = null;
        _maskSurfaceBrush = null;
        _nineGridBrush = null;
        _maskBrush = null;
        _backdropVisual = null;
        _root = null;
        _effectBrush = null;
        _effectFactory = null;
        _hostBackdropBrush = null;
        _target = null;
        _compositor = null;
        _proofCommitCompleted = false;
    }

    private void DisableSampling()
    {
        if (!_samplingEnabled)
        {
            return;
        }

        _ = _activationMode switch
        {
            BackdropActivationMode.Windows11HostBackdrop => SetHostBackdropAttribute(false),
            BackdropActivationMode.Windows10HostBackdrop => SetAccentPolicy(AccentDisabled, 0),
            _ => true
        };
        _samplingEnabled = false;
    }

    private bool SetHostBackdropAttribute(bool enabled)
    {
        var value = enabled ? 1 : 0;
        return DwmSetWindowAttribute(_hwnd, DwmUseHostBackdropBrush, ref value, sizeof(int)) == 0;
    }

    private bool SetAccentPolicy(int state, uint gradientColor)
    {
        var policy = new AccentPolicy { State = state, GradientColor = gradientColor };
        var policyPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, policyPointer, false);
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

    private static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr hwnd)
    {
        var interop = compositor.As<ICompositorDesktopInterop>();
        var result = interop.CreateDesktopWindowTarget(hwnd, false, out var targetPointer);
        Marshal.ThrowExceptionForHR(result);
        try
        {
            return MarshalInterface<DesktopWindowTarget>.FromAbi(targetPointer);
        }
        finally
        {
            if (targetPointer != IntPtr.Zero)
            {
                Marshal.Release(targetPointer);
            }
        }
    }

    private static bool IsLayeredWindow(IntPtr hwnd) =>
        (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExLayered) != 0;

    private static uint GetDpi(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96u;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ++_generation;
        var wasActive = IsActive;
        IsActive = false;
        DetachAndDisposeComposition();
        DisableSampling();
        if (_dispatcherQueueController is not null)
        {
            _ = _dispatcherQueueController.ShutdownQueueAsync();
            _dispatcherQueueController = null;
        }

        if (wasActive)
        {
            AvailabilityChanged?.Invoke(this, new BackdropAvailabilityChangedEventArgs(false));
        }

        GC.SuppressFinalize(this);
    }

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig]
        int CreateDesktopWindowTarget(
            IntPtr hwndTarget,
            [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
            out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int DwSize;
        public DispatcherQueueThreadType ThreadType;
        public DispatcherQueueApartmentType ApartmentType;
    }

    private enum DispatcherQueueThreadType { Dedicated = 1, Current = 2 }
    private enum DispatcherQueueApartmentType { None = 0, AstA = 1, Sta = 2, Mta = 3, ComSta = 4, ComMta = 5 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        out IntPtr dispatcherQueueController);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static bool IsCompositionEnabled() =>
        DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
}

public enum BackdropActivationMode
{
    Fallback,
    Windows11HostBackdrop,
    Windows10HostBackdrop
}

public readonly record struct BackdropMetrics(
    Vector2 VisualSizePixels,
    float DpiScale,
    float TopLeftRadiusPixels,
    float TopRightRadiusPixels,
    float BottomLeftRadiusPixels,
    float BottomRightRadiusPixels);

public sealed class BackdropAvailabilityChangedEventArgs(bool isActive) : EventArgs
{
    public bool IsActive { get; } = isActive;
}
