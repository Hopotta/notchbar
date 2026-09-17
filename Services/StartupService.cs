using System.IO;
using Microsoft.Win32;

namespace NotchBar.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NotchBar";

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "Windows startup registry key could not be opened.";
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, ApplicationLaunchService.GetCurrentCommandLine(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }
}
