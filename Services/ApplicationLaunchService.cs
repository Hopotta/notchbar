using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace NotchBar.Services;

public static class ApplicationLaunchService
{
    public static bool TryOpenFile(string path, out string? error)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "The settings file does not exist.";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryStartCurrentInstance(out string? error)
    {
        try
        {
            var launch = GetCurrentLaunchSpec();
            var startInfo = new ProcessStartInfo
            {
                FileName = launch.FileName,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };

            foreach (var argument in launch.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static string GetCurrentCommandLine()
    {
        var launch = GetCurrentLaunchSpec();
        return string.Join(' ', new[] { QuoteWindowsArgument(launch.FileName) }
            .Concat(launch.Arguments.Select(QuoteWindowsArgument)));
    }

    internal static LaunchSpec GetCurrentLaunchSpec()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("The current executable path is unavailable.");
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchSpec(processPath, Array.Empty<string>());
        }

        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            throw new InvalidOperationException("The NotchBar assembly path is unavailable.");
        }

        return new LaunchSpec(processPath, new[] { assemblyPath });
    }

    internal static string QuoteWindowsArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

public sealed record LaunchSpec(string FileName, IReadOnlyList<string> Arguments);
