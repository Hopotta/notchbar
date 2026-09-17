using System.Drawing;
using Forms = System.Windows.Forms;

namespace NotchBar.Services;

public sealed class TrayService : IDisposable
{
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _pinItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayService()
    {
        _icon = LoadIcon();
        _menu = new Forms.ContextMenuStrip();

        var showItem = new Forms.ToolStripMenuItem("Show");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _pinItem = new Forms.ToolStripMenuItem("Pin");
        _pinItem.Click += (_, _) => PinToggleRequested?.Invoke(this, EventArgs.Empty);

        _startupItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = false
        };
        _startupItem.Click += (_, _) => StartWithWindowsToggleRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu.Items.Add(showItem);
        _menu.Items.Add(_pinItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "NotchBar",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += NotifyIcon_OnDoubleClick;
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? PinToggleRequested;
    public event EventHandler? StartWithWindowsToggleRequested;
    public event EventHandler? ExitRequested;

    public void SetPinned(bool pinned)
    {
        if (!_disposed)
        {
            _pinItem.Text = pinned ? "Unpin" : "Pin";
        }
    }

    public void SetStartWithWindows(bool enabled)
    {
        if (!_disposed)
        {
            _startupItem.Checked = enabled;
        }
    }

    private void NotifyIcon_OnDoubleClick(object? sender, EventArgs e)
    {
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } path)
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to the standard application icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.DoubleClick -= NotifyIcon_OnDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
