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
    public const string DefaultFullscreenMode = "badge";

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
        FullscreenMode = ParseFullscreenModeOrDefault(_settings.FullscreenMode);
    }

    public int ApiPort => _settings.ApiPort;
    public ModifierKeys HotkeyModifiers { get; private set; }
    public Key HotkeyKey { get; private set; }
    public TimeSpan AutoHideDelay => TimeSpan.FromMilliseconds(_settings.AutoHideDelayMs);
    public bool StartWithWindows => _settings.StartWithWindows;
    public FullscreenPresentationMode FullscreenMode { get; }
    public bool HideInFullscreen => FullscreenMode == FullscreenPresentationMode.Hide;
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

    public static bool TryParseFullscreenMode(string? value, out FullscreenPresentationMode mode)
    {
        if (string.Equals(value, "badge", StringComparison.OrdinalIgnoreCase))
        {
            mode = FullscreenPresentationMode.Badge;
            return true;
        }

        if (string.Equals(value, "hide", StringComparison.OrdinalIgnoreCase))
        {
            mode = FullscreenPresentationMode.Hide;
            return true;
        }

        if (string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase))
        {
            mode = FullscreenPresentationMode.Normal;
            return true;
        }

        mode = FullscreenPresentationMode.Badge;
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

    private static FullscreenPresentationMode ParseFullscreenModeOrDefault(string value)
    {
        return TryParseFullscreenMode(value, out var mode) ? mode : FullscreenPresentationMode.Badge;
    }

    private static NotchBarSettings LoadAndNormalize(string path)
    {
        NotchBarSettings? loaded = null;
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    loaded = ReadSettings(document.RootElement);
                }
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
        var fullscreenMode = TryParseFullscreenMode(settings.FullscreenMode, out var parsedFullscreenMode)
            ? ToSettingValue(parsedFullscreenMode)
            : DefaultFullscreenMode;

        return settings with
        {
            ApiPort = apiPort,
            AutoHideDelayMs = autoHideDelayMs,
            Hotkey = hotkey,
            MonitorMode = monitorMode,
            FullscreenMode = fullscreenMode
        };
    }

    private static NotchBarSettings ReadSettings(JsonElement root)
    {
        var defaults = new NotchBarSettings();

        return defaults with
        {
            ApiPort = TryGetInt32(root, "apiPort", out var apiPort) ? apiPort : defaults.ApiPort,
            AutoHideDelayMs = TryGetInt32(root, "autoHideDelayMs", out var autoHideDelayMs)
                ? autoHideDelayMs
                : defaults.AutoHideDelayMs,
            Hotkey = TryGetString(root, "hotkey", out var hotkey) ? hotkey : defaults.Hotkey,
            StartWithWindows = TryGetBoolean(root, "startWithWindows", out var startWithWindows)
                ? startWithWindows
                : defaults.StartWithWindows,
            MonitorMode = TryGetString(root, "monitorMode", out var monitorMode)
                ? monitorMode
                : defaults.MonitorMode,
            FullscreenMode = ReadFullscreenMode(root, defaults.FullscreenMode)
        };
    }

    private static string ReadFullscreenMode(JsonElement root, string fallback)
    {
        if (TryGetString(root, "fullscreenMode", out var fullscreenMode))
        {
            return TryParseFullscreenMode(fullscreenMode, out var parsed)
                ? ToSettingValue(parsed)
                : DefaultFullscreenMode;
        }

        // New installs default to the app badge, including existing settings
        // files that only contain the old default hideInFullscreen:true.
        // Preserve the old explicit opt-in to normal behavior when it was false.
        return TryGetBoolean(root, "hideInFullscreen", out var legacyHide) && !legacyHide
            ? "normal"
            : fallback;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } stringValue)
        {
            value = stringValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string ToSettingValue(MonitorPlacementMode mode) =>
        mode == MonitorPlacementMode.ActiveWindow ? "activeWindow" : "primary";

    private static string ToSettingValue(FullscreenPresentationMode mode) => mode switch
    {
        FullscreenPresentationMode.Hide => "hide",
        FullscreenPresentationMode.Normal => "normal",
        _ => "badge"
    };

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
    public string MonitorMode { get; init; } = SettingsService.DefaultMonitorMode;
    public string FullscreenMode { get; init; } = SettingsService.DefaultFullscreenMode;
}

public enum FullscreenPresentationMode
{
    Badge,
    Hide,
    Normal
}
