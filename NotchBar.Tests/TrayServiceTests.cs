using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class TrayServiceTests
{
    [Fact]
    public void RestartAndExitMenuItemsRaiseTheirRequests()
    {
        var restartRequests = 0;
        var exitRequests = 0;
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var tray = new TrayService();
                tray.RestartRequested += (_, _) => restartRequests++;
                tray.ExitRequested += (_, _) => exitRequests++;

                var menu = typeof(TrayService)
                    .GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(tray)!;
                var items = (IEnumerable)menu.GetType().GetProperty("Items")!.GetValue(menu)!;
                PerformClick(items, "Restart NotchBar");
                PerformClick(items, "Exit");
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Tray menu test thread did not finish.");
        if (threadException is not null)
        {
            ExceptionDispatchInfo.Capture(threadException).Throw();
        }

        Assert.Equal(1, restartRequests);
        Assert.Equal(1, exitRequests);
    }

    private static void PerformClick(IEnumerable items, string label)
    {
        foreach (var item in items)
        {
            var itemType = item!.GetType();
            if (string.Equals(itemType.GetProperty("Text")?.GetValue(item) as string, label, StringComparison.Ordinal))
            {
                itemType.GetMethod("PerformClick", BindingFlags.Instance | BindingFlags.Public)!
                    .Invoke(item, null);
                return;
            }
        }

        throw new InvalidOperationException($"Tray menu item '{label}' was not found.");
    }
}
