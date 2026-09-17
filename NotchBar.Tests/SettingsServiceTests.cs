using System.IO;
using System.Text.Json;
using System.Windows.Input;
using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"notchbar-tests-{Guid.NewGuid():N}");

    [Fact]
    public void MissingFile_UsesDefaults()
    {
        var settings = new SettingsService(SettingsPath());

        Assert.Equal(SettingsService.DefaultApiPort, settings.ApiPort);
        Assert.Equal(TimeSpan.FromMilliseconds(SettingsService.DefaultAutoHideDelayMs), settings.AutoHideDelay);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, settings.HotkeyModifiers);
        Assert.Equal(Key.Space, settings.HotkeyKey);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.HideInFullscreen);
    }

    [Fact]
    public void ValidFile_LoadsPersistedValues()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath(), """
        {
          "apiPort": 41234,
          "autoHideDelayMs": 1400,
          "hotkey": "Shift+F8",
          "startWithWindows": true,
          "hideInFullscreen": false
        }
        """);

        var settings = new SettingsService(SettingsPath());

        Assert.Equal(41234, settings.ApiPort);
        Assert.Equal(TimeSpan.FromMilliseconds(1400), settings.AutoHideDelay);
        Assert.Equal(ModifierKeys.Shift, settings.HotkeyModifiers);
        Assert.Equal(Key.F8, settings.HotkeyKey);
        Assert.True(settings.StartWithWindows);
        Assert.False(settings.HideInFullscreen);
    }

    [Fact]
    public void InvalidValues_FallBackWithoutDiscardingValidBooleans()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath(), """
        {
          "apiPort": 80,
          "autoHideDelayMs": 20,
          "hotkey": "Banana+Space",
          "startWithWindows": true,
          "hideInFullscreen": false
        }
        """);

        var settings = new SettingsService(SettingsPath());

        Assert.Equal(SettingsService.DefaultApiPort, settings.ApiPort);
        Assert.Equal(TimeSpan.FromMilliseconds(SettingsService.DefaultAutoHideDelayMs), settings.AutoHideDelay);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, settings.HotkeyModifiers);
        Assert.Equal(Key.Space, settings.HotkeyKey);
        Assert.True(settings.StartWithWindows);
        Assert.False(settings.HideInFullscreen);
    }

    [Fact]
    public void MalformedJson_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath(), "{ definitely-not-json }");

        var settings = new SettingsService(SettingsPath());

        Assert.Equal(SettingsService.DefaultApiPort, settings.ApiPort);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.HideInFullscreen);
    }

    [Fact]
    public void SetStartWithWindows_PersistsPreference()
    {
        var path = SettingsPath();
        var settings = new SettingsService(path);

        Assert.True(settings.SetStartWithWindows(true, out var error), error);
        Assert.True(settings.StartWithWindows);
        Assert.True(File.Exists(path));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.GetProperty("startWithWindows").GetBoolean());

        var reloaded = new SettingsService(path);
        Assert.True(reloaded.StartWithWindows);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", ModifierKeys.Control | ModifierKeys.Alt, Key.Space)]
    [InlineData("Win+Shift+F12", ModifierKeys.Windows | ModifierKeys.Shift, Key.F12)]
    [InlineData("F7", ModifierKeys.None, Key.F7)]
    public void TryParseHotkey_ParsesSupportedForms(string value, ModifierKeys expectedModifiers, Key expectedKey)
    {
        Assert.True(SettingsService.TryParseHotkey(value, out var modifiers, out var key));
        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Banana+Space")]
    [InlineData("Ctrl+NotAKey")]
    public void TryParseHotkey_RejectsInvalidForms(string value)
    {
        Assert.False(SettingsService.TryParseHotkey(value, out _, out _));
    }

    private string SettingsPath() => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
