using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace NotchBar.Services;

public sealed class ThemeService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _isDark;
    private bool _started;

    public ThemeService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _timer.Tick += Timer_OnTick;
    }

    public bool IsDark => _isDark;

    public event EventHandler? ThemeChanged;

    public static bool IsDarkHours(DateTime localTime)
    {
        return localTime.Hour >= 18 || localTime.Hour < 6;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Apply(IsDarkHours(DateTime.Now));
        _timer.Start();
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        var shouldBeDark = IsDarkHours(DateTime.Now);
        if (shouldBeDark != _isDark)
        {
            Apply(shouldBeDark);
        }
    }

    private void Apply(bool dark)
    {
        _isDark = dark;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach (var (key, brush) in CreatePalette(dark))
        {
            resources[key] = brush;
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyDictionary<string, Brush> CreatePalette(bool dark)
    {
        return dark
            ? new Dictionary<string, Brush>
            {
                ["IslandBackground"] = Solid(0xB3, 0x14, 0x14, 0x14),
                ["IslandFallbackBackground"] = Vertical(
                    (Color.FromArgb(0xE8, 0x20, 0x20, 0x20), 0),
                    (Color.FromArgb(0xD8, 0x1A, 0x1A, 0x1A), 0.52),
                    (Color.FromArgb(0xC8, 0x12, 0x12, 0x12), 1)),
                ["GlassSheen"] = Vertical(
                    (Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0),
                    (Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 0.42),
                    (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)),
                ["PanelBackground"] = Vertical(
                    (Color.FromArgb(0xA6, 0x2A, 0x2A, 0x2A), 0),
                    (Color.FromArgb(0x88, 0x20, 0x20, 0x20), 1)),
                ["GlassChipBackground"] = Vertical(
                    (Color.FromArgb(0x8A, 0x38, 0x38, 0x38), 0),
                    (Color.FromArgb(0x66, 0x2A, 0x2A, 0x2A), 1)),
                ["IslandBorder"] = Solid(0x1A, 0xFF, 0xFF, 0xFF),
                ["GlassEdge"] = Solid(0x33, 0xFF, 0xFF, 0xFF),
                ["InnerHairline"] = Solid(0x1A, 0xFF, 0xFF, 0xFF),
                ["PanelBorder"] = Solid(0x26, 0xFF, 0xFF, 0xFF),
                ["SurfaceRaised"] = Solid(0x20, 0xFF, 0xFF, 0xFF),
                ["SurfaceHover"] = Solid(0x2A, 0xFF, 0xFF, 0xFF),
                ["SurfacePressed"] = Solid(0x40, 0xFF, 0xFF, 0xFF),
                ["PrimaryText"] = Solid(0xFF, 0xF7, 0xFA, 0xFC),
                ["SecondaryText"] = Solid(0xFF, 0xD7, 0xDE, 0xE7),
                ["MutedText"] = Solid(0xFF, 0x9D, 0xA8, 0xB7),
                ["Accent"] = Solid(0xFF, 0x5D, 0xD6, 0xE4),
                ["AccentSoft"] = Solid(0x42, 0x5D, 0xD6, 0xE4),
                ["BuiltInAccent"] = Solid(0xFF, 0xA2, 0xAE, 0xBC),
                ["BuiltInAccentSoft"] = Solid(0x36, 0xA2, 0xAE, 0xBC),
                ["AccentBlue"] = Solid(0xFF, 0x74, 0xA9, 0xFF),
                ["NotificationAccent"] = Solid(0xFF, 0xF4, 0xA2, 0x61),
                ["NotificationAccentSoft"] = Solid(0x4A, 0xF4, 0xA2, 0x61),
                ["ProgressTrack"] = Solid(0x45, 0x5C, 0x68, 0x78)
            }
            : new Dictionary<string, Brush>
            {
                ["IslandBackground"] = Solid(0xBF, 0xFF, 0xFF, 0xFF),
                ["IslandFallbackBackground"] = Vertical(
                    (Color.FromArgb(0xEA, 0xF7, 0xFB, 0xFF), 0),
                    (Color.FromArgb(0xDE, 0xEA, 0xF2, 0xF7), 0.52),
                    (Color.FromArgb(0xD2, 0xDD, 0xE7, 0xEF), 1)),
                ["GlassSheen"] = Vertical(
                    (Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), 0),
                    (Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF), 0.42),
                    (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)),
                ["PanelBackground"] = Vertical(
                    (Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), 0),
                    (Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 1)),
                ["GlassChipBackground"] = Vertical(
                    (Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), 0),
                    (Color.FromArgb(0x68, 0xFF, 0xFF, 0xFF), 1)),
                ["IslandBorder"] = Solid(0x4C, 0x8F, 0xA7, 0xB5),
                ["GlassEdge"] = Solid(0x72, 0xAF, 0xC5, 0xD1),
                ["InnerHairline"] = Solid(0x88, 0xFF, 0xFF, 0xFF),
                ["PanelBorder"] = Solid(0x9A, 0xFF, 0xFF, 0xFF),
                ["SurfaceRaised"] = Solid(0x78, 0xFF, 0xFF, 0xFF),
                ["SurfaceHover"] = Solid(0xB8, 0xFF, 0xFF, 0xFF),
                ["SurfacePressed"] = Solid(0xD0, 0xFF, 0xFF, 0xFF),
                ["PrimaryText"] = Solid(0xFF, 0x17, 0x26, 0x35),
                ["SecondaryText"] = Solid(0xFF, 0x4B, 0x60, 0x72),
                ["MutedText"] = Solid(0xFF, 0x71, 0x82, 0x92),
                ["Accent"] = Solid(0xFF, 0x1D, 0x8E, 0xA3),
                ["AccentSoft"] = Solid(0x30, 0x1D, 0x8E, 0xA3),
                ["BuiltInAccent"] = Solid(0xFF, 0x6B, 0x7C, 0x8D),
                ["BuiltInAccentSoft"] = Solid(0x28, 0x6B, 0x7C, 0x8D),
                ["AccentBlue"] = Solid(0xFF, 0x3B, 0x82, 0xD6),
                ["NotificationAccent"] = Solid(0xFF, 0xCA, 0x7A, 0x24),
                ["NotificationAccentSoft"] = Solid(0x38, 0xCA, 0x7A, 0x24),
                ["ProgressTrack"] = Solid(0x45, 0x89, 0xA9, 0xBD)
            };
    }

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Vertical(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };

        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        ThemeChanged = null;
        _timer.Tick -= Timer_OnTick;
        _timer.Stop();
        GC.SuppressFinalize(this);
    }
}
