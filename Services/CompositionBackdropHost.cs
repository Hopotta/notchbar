using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Windows.Graphics.DirectX;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace NotchBar.Services;

/// <summary>
/// Retained compositor tree for the dedicated backdrop HWND. Its visual root
/// and backdrop sprite are sized relative to the DesktopWindowTarget, so the
/// mask follows HWND resizing in the same compositor frame without a per-frame
/// geometry or clip update.
/// </summary>
public sealed class CompositionBackdropHost : IDisposable
{
    public const double CornerRadiusDip = 20d;

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
    private SpriteVisual? _backdropVisual;
    private CompositionBackdropBrush? _hostBackdropBrush;
    private CompositionEffectFactory? _effectFactory;
    private CompositionEffectBrush? _effectBrush;
    private CompositionMaskBrush? _maskBrush;
    private CompositionNineGridBrush? _nineGridBrush;
    private CompositionSurfaceBrush? _maskSurfaceBrush;
    private CompositionDrawingSurface? _maskSurface;
    private CanvasDevice? _canvasDevice;
    private CompositionGraphicsDevice? _canvasCompositionDevice;
    private int _generation;
    private bool _samplingEnabled;
    private bool _proofCommitCompleted;
    private bool _disposed;
    private uint _dpi = 96;
    private float _dpiScale = 1f;
    private WindowEnvelopeGeometry? _geometry;

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

    public async Task<bool> PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsPrepared || _activationMode == BackdropActivationMode.Fallback)
        {
            return IsPrepared;
        }

        var generation = ++_generation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            // On Windows 10 desktop/WPF hosts, CreateHostBackdropBrush can
            // resolve to an opaque black source even when the accent host
            // backdrop policy is enabled. CreateBackdropBrush samples the
            // fully transparent desktop target correctly in that mode.
            _hostBackdropBrush = _activationMode == BackdropActivationMode.Windows10HostBackdrop
                ? _compositor.CreateBackdropBrush()
                : _compositor.CreateHostBackdropBrush();
            CreateEffectGraph();

            // Prove the selected compositor/effect stack while the target has
            // no root, preventing an unmasked frame from reaching the HWND.
            await _compositor.RequestCommitAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsGenerationCurrent(generation))
            {
                return false;
            }

            _proofCommitCompleted = true;
            cancellationToken.ThrowIfCancellationRequested();
            CreateMaskSurface();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsGenerationCurrent(generation) || !AttachMaskedTree(generation))
            {
                return false;
            }

            await _compositor.RequestCommitAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return IsPrepared && IsGenerationCurrent(generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Fail();
            return false;
        }
        catch (Exception exception)
        {
            LastFailure = exception.ToString();
            System.Diagnostics.Debug.WriteLine($"NotchBar composition backdrop unavailable: {exception}");
            Fail();
            return false;
        }
    }

    /// <summary>
    /// Keeps the fixed-envelope target's masked island visual in sync. The
    /// envelope HWND remains unchanged during animation; only this sprite's
    /// local physical-pixel offset and size move with the island.
    /// </summary>
    public void UpdateGeometry(WindowEnvelopeGeometry geometry, uint dpi)
    {
        _geometry = geometry;

        var effectiveDpi = dpi == 0 ? 96u : dpi;
        if (_dpi != effectiveDpi)
        {
            _dpi = effectiveDpi;
            _dpiScale = effectiveDpi / 96f;
            if (_nineGridBrush is not null)
            {
                _nineGridBrush.SetInsetScales(_dpiScale);
            }

            _effectBrush?.Properties.InsertScalar("Blur.BlurAmount", 20f * _dpiScale);
        }

        ApplyIslandGeometry();
    }

    public bool RefreshTheme()
    {
        if (_disposed || !IsPrepared || _effectBrush is null)
        {
            return false;
        }

        try
        {
            _effectBrush.Properties.InsertColor("Tint.Color", ResolveTint());
            return true;
        }
        catch (Exception exception)
        {
            LastFailure = exception.ToString();
            System.Diagnostics.Debug.WriteLine($"NotchBar backdrop theme composition update failed: {exception}");
            Fail();
            return false;
        }
    }

    private void CreateMaskSurface()
    {
        if (_compositor is null)
        {
            throw new InvalidOperationException("Composition is not initialized.");
        }

        // Generate the retained opacity mask with Win2D rather than a XAML
        // LoadedImageSurface. The latter requires a XAML dispatcher and throws
        // RPC_E_WRONG_THREAD in a plain WPF desktop host.
        _canvasDevice = CanvasDevice.GetSharedDevice();
        _canvasCompositionDevice = CanvasComposition.CreateCompositionGraphicsDevice(
            _compositor,
            _canvasDevice);
        _maskSurface = _canvasCompositionDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(64, 64),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

        using var drawingSession = CanvasComposition.CreateDrawingSession(_maskSurface);
        drawingSession.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        var opaqueMask = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        drawingSession.FillRoundedRectangle(
            new Rect(0, 0, 64, 64),
            20,
            20,
            opaqueMask);
        // Cover only the upper rounded corners, yielding a square top edge
        // with anti-aliased 20px lower corners.
        drawingSession.FillRectangle(new Rect(0, 0, 64, 20), opaqueMask);
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
            BlurAmount = 20f * _dpiScale,
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

    private bool AttachMaskedTree(int generation)
    {
        if (!_proofCommitCompleted || !IsGenerationCurrent(generation) ||
            _compositor is null || _target is null || _effectBrush is null || _maskSurface is null)
        {
            LastFailure = "The composition root was not eligible for masked attachment.";
            return false;
        }

        _maskSurfaceBrush = _compositor.CreateSurfaceBrush(_maskSurface);
        _maskSurfaceBrush.Stretch = CompositionStretch.Fill;

        _nineGridBrush = _compositor.CreateNineGridBrush();
        _nineGridBrush.Source = _maskSurfaceBrush;
        var maskInsets = BackdropWindowPolicy.CalculateMaskInsets(_dpi);
        _nineGridBrush.TopInset = maskInsets.TopPixels;
        _nineGridBrush.BottomInset = maskInsets.BottomPixels;
        _nineGridBrush.LeftInset = maskInsets.LeftPixels;
        _nineGridBrush.RightInset = maskInsets.RightPixels;
        _nineGridBrush.SetInsetScales(maskInsets.InsetScale);

        _maskBrush = _compositor.CreateMaskBrush();
        _maskBrush.Source = _effectBrush;
        _maskBrush.Mask = _nineGridBrush;

        _backdropVisual = _compositor.CreateSpriteVisual();
        _backdropVisual.RelativeSizeAdjustment = Vector2.Zero;
        _backdropVisual.Brush = _maskBrush;

        _root = _compositor.CreateContainerVisual();
        // Microsoft’s Win32 Composition sample uses this relationship so the
        // root tracks its HWND target's client size as it changes.
        _root.RelativeSizeAdjustment = Vector2.One;
        _root.Children.InsertAtTop(_backdropVisual);


        _target.Root = _root;
        IsPrepared = true;
        ApplyIslandGeometry();
        return true;
    }

    private void ApplyIslandGeometry()
    {
        if (_backdropVisual is null || _geometry is not { } geometry)
        {
            return;
        }

        var island = geometry.Island;
        _backdropVisual.Offset = new Vector3(island.X, island.Y, 0f);
        _backdropVisual.Size = new Vector2(island.Width, island.Height);
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
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
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
        try
        {
            if (_target is not null)
            {
                _target.Root = null;
            }
        }
        catch (Exception exception)
        {
            // Destroying the companion HWND immediately afterwards still
            // removes the surface if the compositor has already been lost.
            System.Diagnostics.Debug.WriteLine($"NotchBar backdrop detach failed: {exception}");
        }

        if (_maskSurface is not null)
        {
            try
            {
                _maskSurface.Dispose();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"NotchBar backdrop mask disposal failed: {exception}");
            }
        }

        _maskSurfaceBrush = null;
        _nineGridBrush = null;
        _maskBrush = null;
        _maskSurface = null;
        _canvasCompositionDevice?.Dispose();
        _canvasCompositionDevice = null;
        _canvasDevice?.Dispose();
        _canvasDevice = null;
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
            BackdropActivationMode.Windows10HostBackdrop => SetAccentPolicy(AccentDisabled),
            _ => true
        };
        _samplingEnabled = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ++_generation;
        IsPrepared = false;
        DetachComposition();
        DisableSampling();
        if (_dispatcherQueueController is not null)
        {
            try
            {
                _ = _dispatcherQueueController.ShutdownQueueAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"NotchBar backdrop dispatcher shutdown failed: {exception}");
            }
            _dispatcherQueueController = null;
        }

        GC.SuppressFinalize(this);
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
}
