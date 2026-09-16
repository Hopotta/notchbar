using System.Windows.Input;

namespace NotchBar.Services;

public sealed class SettingsService
{
    public const int DefaultApiPort = 32145;

    public int ApiPort { get; } = DefaultApiPort;
    public ModifierKeys HotkeyModifiers { get; } = ModifierKeys.Control | ModifierKeys.Alt;
    public Key HotkeyKey { get; } = Key.Space;
    public TimeSpan AutoHideDelay { get; } = TimeSpan.FromMilliseconds(900);
}
