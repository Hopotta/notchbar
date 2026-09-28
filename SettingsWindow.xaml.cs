using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotchBar.Services;

namespace NotchBar;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly StartupService _startupService;

    public SettingsWindow(SettingsService settingsService, StartupService startupService)
    {
        _settingsService = settingsService;
        _startupService = startupService;
        InitializeComponent();
        LoadCurrentSettings();
    }

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    private void LoadCurrentSettings()
    {
        var settings = _settingsService.CurrentSettings;
        ApiPortTextBox.Text = settings.ApiPort.ToString(CultureInfo.InvariantCulture);
        HotkeyTextBox.Text = settings.Hotkey;
        AutoHideDelaySlider.Value = settings.AutoHideDelayMs;
        MonitorModeComboBox.SelectedValue = settings.MonitorMode;
        FullscreenModeComboBox.SelectedValue = settings.FullscreenMode;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
    }

    private void AutoHideDelaySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AutoHideValueText is null)
        {
            return;
        }

        var seconds = AutoHideDelaySlider.Value / 1000d;
        AutoHideValueText.Text = $"{seconds.ToString("0.0", CultureInfo.CurrentCulture)} sec";
    }

    private void HotkeyTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            StatusText.Text = "A shortcut must include Ctrl, Alt, Shift, or the Windows key.";
            return;
        }

        if (key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            StatusText.Text = "That key cannot be used in a shortcut.";
            return;
        }

        var shortcut = SettingsService.FormatHotkey(modifiers, key);
        if (!SettingsService.TryParseHotkey(shortcut, out _, out _))
        {
            StatusText.Text = "That shortcut is not supported.";
            return;
        }

        HotkeyTextBox.Text = shortcut;
        StatusText.Text = "Shortcut selected. Save to keep this change.";
    }

    private void UseDefaultHotkey_OnClick(object sender, RoutedEventArgs e)
    {
        HotkeyTextBox.Text = SettingsService.DefaultHotkey;
        StatusText.Text = "Default shortcut selected. Save to keep this change.";
    }

    private void Save_OnClick(object sender, RoutedEventArgs e) => Save(restartRequested: false);

    private void SaveAndRestart_OnClick(object sender, RoutedEventArgs e) => Save(restartRequested: true);

    private void Save(bool restartRequested)
    {
        if (!TryCreateDraft(out var draft, out var validationError))
        {
            StatusText.Text = validationError;
            return;
        }

        var previous = _settingsService.CurrentSettings;
        var startupChanged = previous.StartWithWindows != draft.StartWithWindows;
        if (startupChanged && !_startupService.TrySetEnabled(draft.StartWithWindows, out var startupError))
        {
            StatusText.Text = $"Windows startup could not be updated: {startupError}";
            return;
        }

        if (!_settingsService.TryUpdate(draft, out var settingsError))
        {
            if (startupChanged && !_startupService.TrySetEnabled(previous.StartWithWindows, out var rollbackError))
            {
                StatusText.Text = $"Settings could not be saved: {settingsError} Windows startup could not be restored: {rollbackError}";
            }
            else
            {
                StatusText.Text = $"Settings could not be saved: {settingsError}";
            }

            return;
        }

        var current = _settingsService.CurrentSettings;
        var restartRequired = previous.ApiPort != current.ApiPort ||
                              previous.AutoHideDelayMs != current.AutoHideDelayMs ||
                              previous.Hotkey != current.Hotkey ||
                              previous.MonitorMode != current.MonitorMode ||
                              previous.FullscreenMode != current.FullscreenMode;
        StatusText.Text = restartRequired
            ? "Saved. Restart NotchBar to apply these changes."
            : startupChanged ? "Saved. Windows startup preference is updated." : "Settings are up to date.";

        var savedHandler = SettingsSaved;
        if (restartRequested)
        {
            Close();
        }

        savedHandler?.Invoke(this, new SettingsSavedEventArgs(previous, current, restartRequested));
    }

    private bool TryCreateDraft(out NotchBarSettings settings, out string error)
    {
        settings = _settingsService.CurrentSettings;
        if (!int.TryParse(ApiPortTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var apiPort))
        {
            error = "Enter a whole number for the local API port.";
            ApiPortTextBox.Focus();
            return false;
        }

        settings = settings with
        {
            ApiPort = apiPort,
            AutoHideDelayMs = (int)Math.Round(AutoHideDelaySlider.Value),
            Hotkey = HotkeyTextBox.Text.Trim(),
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            MonitorMode = MonitorModeComboBox.SelectedValue as string ?? string.Empty,
            FullscreenMode = FullscreenModeComboBox.SelectedValue as string ?? string.Empty
        };

        if (settings.ApiPort is < 1024 or > 65535)
        {
            error = "The local API port must be between 1024 and 65535.";
            ApiPortTextBox.Focus();
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;
}

public sealed class SettingsSavedEventArgs(
    NotchBarSettings previous,
    NotchBarSettings current,
    bool restartRequested) : EventArgs
{
    public NotchBarSettings Previous { get; } = previous;
    public NotchBarSettings Current { get; } = current;
    public bool RestartRequested { get; } = restartRequested;
}
