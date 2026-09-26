using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace NotchBar.Services;

public sealed class ThemeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _isDark;
    private bool _started;
    private volatile bool _disposed;

    public ThemeService()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromHours(12)
        };
        _timer.Tick += Timer_OnTick;
    }

    public bool IsDark => _isDark;

    public event EventHandler? ThemeChanged;

    public static bool IsDarkHours(DateTime localTime)
    {
        return localTime.Hour >= 18 || localTime.Hour < 6;
    }

    public static DateTime NextThemeBoundary(DateTime localTime)
    {
        var nextBoundaryHour = localTime.Hour < 6
            ? 6
            : localTime.Hour < 18
                ? 18
                : 30;
        return localTime.Date.AddHours(nextBoundaryHour);
    }

    public static TimeSpan GetDelayUntilNextThemeBoundary(
        DateTime localTime,
        DateTime utcNow,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        var boundary = DateTime.SpecifyKind(NextThemeBoundary(localTime), DateTimeKind.Unspecified);
        var boundaryUtc = TimeZoneInfo.ConvertTimeToUtc(boundary, localTimeZone);
        var utcNowValue = utcNow.Kind switch
        {
            DateTimeKind.Utc => utcNow,
            DateTimeKind.Local => utcNow.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)
        };
        return boundaryUtc - utcNowValue;
    }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        SystemEvents.TimeChanged += SystemEvents_TimeChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        Apply(IsDarkHours(DateTime.Now));
        ScheduleNextBoundary();
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        RefreshThemeAndSchedule();
    }

    private void SystemEvents_TimeChanged(object? sender, EventArgs e) => QueueClockRefresh();

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            TimeZoneInfo.ClearCachedData();
            QueueClockRefresh();
        }
    }

    private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            QueueClockRefresh();
        }
    }

    private void QueueClockRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                (Action)RefreshThemeAndSchedule);
        }
        catch (InvalidOperationException)
        {
            // The dispatcher can begin shutting down after the checks above.
        }
    }

    private void RefreshThemeAndSchedule()
    {
        if (!_started || _disposed)
        {
            return;
        }

        var shouldBeDark = IsDarkHours(DateTime.Now);
        if (shouldBeDark != _isDark)
        {
            Apply(shouldBeDark);
        }

        ScheduleNextBoundary();
    }

    private void ScheduleNextBoundary()
    {
        if (!_started || _disposed)
        {
            return;
        }

        _timer.Stop();
        var interval = GetDelayUntilNextThemeBoundary(DateTime.Now, DateTime.UtcNow, TimeZoneInfo.Local);
        _timer.Interval = interval > TimeSpan.Zero
            ? interval
            : TimeSpan.FromMilliseconds(1);
        _timer.Start();
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
                ["IslandGlassWash"] = Solid(0x55, 0x14, 0x14, 0x14),
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
                ["ClockDateText"] = Solid(0xFF, 0xD7, 0xDE, 0xE7),
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
                ["IslandGlassWash"] = Solid(0x50, 0xFF, 0xFF, 0xFF),
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
                ["ClockDateText"] = Solid(0xFF, 0x38, 0x43, 0x4F),
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ThemeChanged = null;
        _timer.Tick -= Timer_OnTick;
        _timer.Stop();
        if (_started)
        {
            SystemEvents.TimeChanged -= SystemEvents_TimeChanged;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        }
        GC.SuppressFinalize(this);
    }
}
