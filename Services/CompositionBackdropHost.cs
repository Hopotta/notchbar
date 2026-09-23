using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace NotchBar.Services;

/// <summary>
/// Retained compositor tree for the dedicated backdrop HWND. The desktop
/// target remains detached until a proof commit completes and the retained
/// anti-aliased square-top/rounded-bottom clip tree is ready, so the companion
/// can never present an unclipped rectangle.
/// </summary>
public sealed class CompositionBackdropHost : IDisposable
{
    private const uint DwmUseHostBackdropBrush = 17;
    private const int WindowCompositionAttributeAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableHostBackdrop = 5;

    private readonly IntPtr _hwnd;
    private readonly BackdropActivationMode _activationMode;
    private readonly Func<System.Windows.Media.Color> _tintProvider;
    private DispatcherQueueController? _dispatcherQueueController;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private SpriteVisual? _topVisual;
    private SpriteVisual? _bottomVisual;
    private CompositionRoundedRectangleGeometry? _bottomGeometry;
    private CompositionBackdropBrush? _hostBackdropBrush;
    private CompositionEffectFactory? _effectFactory;
    private CompositionEffectBrush? _effectBrush;
    private int _generation;
    private bool _samplingEnabled;
    private bool _proofCommitCompleted;
    private bool _disposed;
    private WindowPixelGeometry _geometry;

    public CompositionBackdropHost(
        IntPtr hwnd,
        BackdropActivationMode activationMode,
        Func<System.Windows.Media.Color> tintProvider)
    {
        _hwnd = hwnd;
        _activationMode = activationMode;
        _tintProvider = tintProvider;
    }

    public bool IsPrepared { get; private set; }
    public string? LastFailure { get; private set; }

    public async Task<bool> PrepareAsync()
    {
        if (_disposed || IsPrepared || _activationMode == BackdropActivationMode.Fallback)
        {
            return IsPrepared;
        }

        var generation = ++_generation;
        try
        {
            EnsureDispatcherQueue();
            if (!EnableSampling() || !IsGenerationCurrent(generation))
            {
                LastFailure = "The OS host-backdrop sampling enabler was rejected.";
                return false;
            }

            _compositor = new Compositor();
            if (!CompositionCapabilities.GetForCurrentView().AreEffectsSupported())
            {
                LastFailure = "Windows Composition effects are unavailable.";
                return false;
            }

            _target = CreateDesktopWindowTarget(_compositor, _hwnd);
            _hostBackdropBrush = _compositor.CreateHostBackdropBrush();
            CreateEffectGraph();

            // The target still has no root: this validates the selected stack
            // without allowing a transient full-HWND frame to be presented.
            await _compositor.RequestCommitAsync();
            if (!IsGenerationCurrent(generation))
            {
                return false;
            }

            _proofCommitCompleted = true;
            if (!AttachClippedTree(generation))
            {
                return false;
            }

            await _compositor.RequestCommitAsync();
            return IsPrepared && IsGenerationCurrent(generation);
        }
        catch (Exception exception)
        {
            LastFailure = exception.ToString();
            System.Diagnostics.Debug.WriteLine($"NotchBar composition backdrop unavailable: {exception}");
            Fail();
            return false;
        }
    }

    public void UpdateGeometry(WindowPixelGeometry geometry)
    {
        _geometry = geometry;
        var width = Math.Max(1, geometry.Width);
        var height = Math.Max(1, geometry.Height);
        var dpi = GetDpi(_hwnd);
        var radius = Math.Clamp(20f * dpi / 96f, 0f, Math.Min(width, height) / 2f);
        var topHeight = Math.Max(0f, height - radius);
        if (_topVisual is not null)
        {
            _topVisual.Size = new Vector2(width, topHeight);
        }

        if (_bottomVisual is not null)
        {
            _bottomVisual.Offset = new Vector3(0, topHeight, 0);
            _bottomVisual.Size = new Vector2(width, radius);
        }

        if (_bottomGeometry is not null)
        {
            _bottomGeometry.Offset = new Vector2(0, -radius);
            _bottomGeometry.Size = new Vector2(width, radius * 2);
            _bottomGeometry.CornerRadius = new Vector2(radius, radius);
        }

        _effectBrush?.Properties.InsertScalar("Blur.BlurAmount", 20f * dpi / 96f);
    }

    public bool RefreshTheme()
    {
        if (_disposed || !IsPrepared || _effectBrush is null)
        {
            return false;
        }

        _effectBrush.Properties.InsertColor("Tint.Color", ResolveTint());
        return true;
    }

    private void EnsureDispatcherQueue()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = DispatcherQueueThreadType.Current,
            ApartmentType = DispatcherQueueApartmentType.ComSta
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var pointer));
        try
        {
            _dispatcherQueueController = MarshalInterface<DispatcherQueueController>.FromAbi(pointer);
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }
    }

    private bool EnableSampling()
    {
        _ = SetAccentPolicy(AccentDisabled);
        _samplingEnabled = _activationMode switch
        {
            BackdropActivationMode.Windows11HostBackdrop => SetHostBackdropAttribute(true),
            BackdropActivationMode.Windows10HostBackdrop => SetAccentPolicy(AccentEnableHostBackdrop),
            _ => false
        };
        return _samplingEnabled;
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
            BlurAmount = 20f,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("backdrop")
        };
        var saturation = new SaturationEffect
        {
            Saturation = 1.8f,
            Source = blur
        };
        var tint = new ColorSourceEffect
        {
            Name = "Tint",
            Color = ResolveTint()
        };
        var material = new BlendEffect
        {
            Mode = BlendEffectMode.SoftLight,
            Background = saturation,
            Foreground = tint
        };

        _effectFactory = _compositor.CreateEffectFactory(material, ["Blur.BlurAmount", "Tint.Color"]);
        _effectBrush = _effectFactory.CreateBrush();
        _effectBrush.SetSourceParameter("backdrop", _hostBackdropBrush);
    }

    private bool AttachClippedTree(int generation)
    {
        if (!_proofCommitCompleted || !IsGenerationCurrent(generation) ||
            _compositor is null || _target is null || _effectBrush is null)
        {
            LastFailure = "The clipped composition root was not eligible for attachment.";
            return false;
        }

        _topVisual = _compositor.CreateSpriteVisual();
        _topVisual.Brush = _effectBrush;
        _bottomGeometry = _compositor.CreateRoundedRectangleGeometry();
        _bottomVisual = _compositor.CreateSpriteVisual();
        _bottomVisual.Brush = _effectBrush;
        _bottomVisual.Clip = _compositor.CreateGeometricClip(_bottomGeometry);
        _root = _compositor.CreateContainerVisual();
        _root.Children.InsertAtBottom(_bottomVisual);
        _root.Children.InsertAtTop(_topVisual);
        UpdateGeometry(_geometry.Width > 0 ? _geometry : new WindowPixelGeometry(0, 0, 1, 1));
        _target.Root = _root;
        IsPrepared = true;
        return true;
    }

    private Windows.UI.Color ResolveTint()
    {
        var color = _tintProvider();
        // Low-alpha soft-light tint keeps the 1.8x saturation visible. The WPF
        // surface above supplies the existing palette, sheen, and highlights.
        return Windows.UI.Color.FromArgb(0x20, color.R, color.G, color.B);
    }

    private bool SetHostBackdropAttribute(bool enabled)
    {
        var value = enabled ? 1 : 0;
        return DwmSetWindowAttribute(_hwnd, DwmUseHostBackdropBrush, ref value, sizeof(int)) == 0;
    }

    private bool SetAccentPolicy(int state)
    {
        var policy = new AccentPolicy { State = state };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttributeAccentPolicy,
                Data = pointer,
                SizeOfData = Marshal.SizeOf<AccentPolicy>()
            };
            return SetWindowCompositionAttribute(_hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr hwnd)
    {
        var interop = compositor.As<ICompositorDesktopInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(hwnd, false, out var pointer));
        try
        {
            return MarshalInterface<DesktopWindowTarget>.FromAbi(pointer);
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
        }
    }

    private bool IsGenerationCurrent(int generation) => !_disposed && generation == _generation;

    private void Fail()
    {
        ++_generation;
        IsPrepared = false;
        DetachComposition();
        DisableSampling();
    }

    private void DetachComposition()
    {
        if (_target is not null) _target.Root = null;
        _bottomGeometry = null;
        _bottomVisual = null;
        _topVisual = null;
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
        if (!_samplingEnabled) return;
        _ = _activationMode switch
        {
            BackdropActivationMode.Windows11HostBackdrop => SetHostBackdropAttribute(false),
            BackdropActivationMode.Windows10HostBackdrop => SetAccentPolicy(AccentDisabled),
            _ => true
        };
        _samplingEnabled = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        IsPrepared = false;
        DetachComposition();
        DisableSampling();
        if (_dispatcherQueueController is not null)
        {
            _ = _dispatcherQueueController.ShutdownQueueAsync();
            _dispatcherQueueController = null;
        }
        GC.SuppressFinalize(this);
    }

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

    public static bool IsCompositionEnabled() =>
        DwmIsCompositionEnabled(out var enabled) == 0 && enabled;

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig]
        int CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
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

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
