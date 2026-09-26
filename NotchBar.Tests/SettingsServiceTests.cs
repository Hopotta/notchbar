using System.IO;
using System.Windows.Input;
using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void MissingFile_UsesDefaults()
    {
        using var file = new TemporarySettingsFile();

        AssertDefaults(new SettingsService(file.SettingsPath));
    }

    [Fact]
    public void SaveAndReload_PreservesCorePreferences()
    {
        using var file = new TemporarySettingsFile();
        file.Write("""
        {
          "apiPort": 41234,
          "autoHideDelayMs": 1400,
          "hotkey": "Shift+F8",
          "startWithWindows": true,
          "hideInFullscreen": false,
          "monitorMode": "activeWindow"
        }
        """);
        var settings = new SettingsService(file.SettingsPath);

        Assert.True(settings.TrySave(out var error), error);
        AssertPersistedPreferences(new SettingsService(file.SettingsPath), startWithWindows: true);

        Assert.True(settings.SetStartWithWindows(false, out error), error);
        var reloaded = new SettingsService(file.SettingsPath);
        AssertPersistedPreferences(reloaded, startWithWindows: false);
    }

    [Fact]
    public void TypeInvalidFields_FallBackAndKeepValidPeers()
    {
        using var file = new TemporarySettingsFile();
        file.Write("""
        {
          "apiPort": "invalid",
          "autoHideDelayMs": 2500,
          "hotkey": true,
          "startWithWindows": "yes",
          "hideInFullscreen": false,
          "monitorMode": "activeWindow"
        }
        """);

        var settings = new SettingsService(file.SettingsPath);

        Assert.Equal(SettingsService.DefaultApiPort, settings.ApiPort);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), settings.AutoHideDelay);
        AssertDefaultHotkey(settings);
        Assert.False(settings.StartWithWindows);
        Assert.False(settings.HideInFullscreen);
        Assert.Equal(MonitorPlacementMode.ActiveWindow, settings.MonitorMode);
    }

    [Fact]
    public void MalformedJson_FallsBackToDefaults()
    {
        using var file = new TemporarySettingsFile();
        file.Write("{ definitely-not-json }");

        AssertDefaults(new SettingsService(file.SettingsPath));
    }

    [Fact]
    public void OutOfRangeNumbers_UseDefaultsAndKeepValidPeers()
    {
        using var file = new TemporarySettingsFile();
        file.Write("""
        {
          "apiPort": 1023,
          "autoHideDelayMs": 10001,
          "startWithWindows": true,
          "monitorMode": "activeWindow"
        }
        """);

        var settings = new SettingsService(file.SettingsPath);

        Assert.Equal(SettingsService.DefaultApiPort, settings.ApiPort);
        Assert.Equal(TimeSpan.FromMilliseconds(SettingsService.DefaultAutoHideDelayMs), settings.AutoHideDelay);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(MonitorPlacementMode.ActiveWindow, settings.MonitorMode);
    }

    private static void AssertDefaults(SettingsService settings)
    {
        Assert.Equal(SettingsService.DefaultApiPort, settings.ApiPort);
        Assert.Equal(TimeSpan.FromMilliseconds(SettingsService.DefaultAutoHideDelayMs), settings.AutoHideDelay);
        AssertDefaultHotkey(settings);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.HideInFullscreen);
        Assert.Equal(MonitorPlacementMode.Primary, settings.MonitorMode);
    }

    private static void AssertPersistedPreferences(SettingsService settings, bool startWithWindows)
    {
        Assert.Equal(41234, settings.ApiPort);
        Assert.Equal(TimeSpan.FromMilliseconds(1400), settings.AutoHideDelay);
        Assert.Equal(ModifierKeys.Shift, settings.HotkeyModifiers);
        Assert.Equal(Key.F8, settings.HotkeyKey);
        Assert.Equal(startWithWindows, settings.StartWithWindows);
        Assert.False(settings.HideInFullscreen);
        Assert.Equal(MonitorPlacementMode.ActiveWindow, settings.MonitorMode);
    }

    private static void AssertDefaultHotkey(SettingsService settings)
    {
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, settings.HotkeyModifiers);
        Assert.Equal(Key.Space, settings.HotkeyKey);
    }

    private sealed class TemporarySettingsFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            Path.GetTempPath(),
            $"notchbar-settings-tests-{Guid.NewGuid():N}");

        public string SettingsPath => System.IO.Path.Combine(_directory, "settings.json");

        public void Write(string contents)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(SettingsPath, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
