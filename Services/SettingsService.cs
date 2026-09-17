using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace NotchBar.Services;

public sealed class SettingsService
{
    public const int DefaultApiPort = 32145;
    public const int DefaultAutoHideDelayMs = 900;
    public const string DefaultHotkey = "Ctrl+Alt+Space";
    public const string DefaultMonitorMode = "primary";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private NotchBarSettings _settings;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? GetDefaultSettingsPath();
        _settings = LoadAndNormalize(_settingsPath);
        (HotkeyModifiers, HotkeyKey) = ParseHotkeyOrDefault(_settings.Hotkey);
        MonitorMode = ParseMonitorModeOrDefault(_settings.MonitorMode);
    }

    public int ApiPort => _settings.ApiPort;
    public ModifierKeys HotkeyModifiers { get; private set; }
    public Key HotkeyKey { get; private set; }
    public TimeSpan AutoHideDelay => TimeSpan.FromMilliseconds(_settings.AutoHideDelayMs);
    public bool StartWithWindows => _settings.StartWithWindows;
    public bool HideInFullscreen => _settings.HideInFullscreen;
    public MonitorPlacementMode MonitorMode { get; }
    public string SettingsPath => _settingsPath;

    public bool SetStartWithWindows(bool enabled, out string? error)
    {
        var previous = _settings;
        _settings = _settings with { StartWithWindows = enabled };
        if (TrySave(out error))
        {
            return true;
        }

        _settings = previous;
        return false;
    }

    public bool TrySave(out string? error)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_settings, JsonOptions));
            File.Move(tempPath, _settingsPath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryParseHotkey(string? value, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Alt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Shift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Windows;
            }
            else
            {
                modifiers = ModifierKeys.None;
                return false;
            }
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out key) ||
            !Enum.IsDefined(typeof(Key), key) ||
            key == Key.None ||
            KeyInterop.VirtualKeyFromKey(key) == 0)
        {
            modifiers = ModifierKeys.None;
            key = Key.None;
            return false;
        }

        return true;
    }

    public static bool TryParseMonitorMode(string? value, out MonitorPlacementMode mode)
    {
        if (string.Equals(value, "primary", StringComparison.OrdinalIgnoreCase))
        {
            mode = MonitorPlacementMode.Primary;
            return true;
        }

        if (string.Equals(value, "activeWindow", StringComparison.OrdinalIgnoreCase))
        {
            mode = MonitorPlacementMode.ActiveWindow;
            return true;
        }

        mode = MonitorPlacementMode.Primary;
        return false;
    }

    private static (ModifierKeys Modifiers, Key Key) ParseHotkeyOrDefault(string value)
    {
        if (TryParseHotkey(value, out var modifiers, out var key))
        {
            return (modifiers, key);
        }

        _ = TryParseHotkey(DefaultHotkey, out modifiers, out key);
        return (modifiers, key);
    }

    private static MonitorPlacementMode ParseMonitorModeOrDefault(string value)
    {
        return TryParseMonitorMode(value, out var mode) ? mode : MonitorPlacementMode.Primary;
    }

    private static NotchBarSettings LoadAndNormalize(string path)
    {
        NotchBarSettings? loaded = null;
        try
        {
            if (File.Exists(path))
            {
                loaded = JsonSerializer.Deserialize<NotchBarSettings>(File.ReadAllText(path), JsonOptions);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"NotchBar settings could not be loaded: {exception.Message}");
        }

        var settings = loaded ?? new NotchBarSettings();
        var apiPort = settings.ApiPort is >= 1024 and <= 65535 ? settings.ApiPort : DefaultApiPort;
        var autoHideDelayMs = settings.AutoHideDelayMs is >= 100 and <= 10000
            ? settings.AutoHideDelayMs
            : DefaultAutoHideDelayMs;
        var hotkey = TryParseHotkey(settings.Hotkey, out _, out _) ? settings.Hotkey : DefaultHotkey;
        var monitorMode = TryParseMonitorMode(settings.MonitorMode, out var parsedMonitorMode)
            ? ToSettingValue(parsedMonitorMode)
            : DefaultMonitorMode;

        return settings with
        {
            ApiPort = apiPort,
            AutoHideDelayMs = autoHideDelayMs,
            Hotkey = hotkey,
            MonitorMode = monitorMode
        };
    }

    private static string ToSettingValue(MonitorPlacementMode mode) =>
        mode == MonitorPlacementMode.ActiveWindow ? "activeWindow" : "primary";

    private static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "NotchBar", "settings.json");
    }
}

public sealed record NotchBarSettings
{
    public int ApiPort { get; init; } = SettingsService.DefaultApiPort;
    public int AutoHideDelayMs { get; init; } = SettingsService.DefaultAutoHideDelayMs;
    public string Hotkey { get; init; } = SettingsService.DefaultHotkey;
    public bool StartWithWindows { get; init; }
    public bool HideInFullscreen { get; init; } = true;
    public string MonitorMode { get; init; } = SettingsService.DefaultMonitorMode;
}
