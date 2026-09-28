# NotchBar

NotchBar is a lightweight Windows top information island. It presents a small, borderless, always-on-top status bar centered at the top of a configured display. External programs push status data through a localhost REST API; NotchBar selects, displays, and expires that data instead of collecting business data itself.

See [changelog.md](changelog.md) for the version history.

## Requirements

- Windows 10 or later
- .NET 10 SDK
- WPF desktop support included with the .NET SDK

## Build and run

From the project directory:

```powershell
dotnet build .\NotchBar.sln
dotnet run --project .\NotchBar.csproj
```

To create a Release executable:

```powershell
dotnet publish .\NotchBar.csproj -c Release -r win-x64 --self-contained false
```

The published executable is placed under the generated `bin\Release` publish directory.

The API listens on `http://127.0.0.1:32145` by default. It binds only to the IPv4 loopback address, does not register a full-width AppBar, and does not modify the Windows work area.

## Interaction and states

NotchBar has four user-facing states:

- `Hidden`: The window is moved above the target display and leaves only an approximately 2px trigger strip visible.
- `Compact`: A single-line summary is shown, for example `CC · Working · 128k · 18m`.
- `Expanded`: The detail text, secondary text, progress, and update time are shown.
- `Pinned`: The current Compact or Expanded visual state stays visible and does not auto-hide. Pinning changes the hide policy; it does not expand the content.

Mouse movement over the centered top trigger wakes `Compact`. Leaving the island starts an approximately 900ms auto-hide delay. Clicking the Compact content enters `Expanded`; pressing `Esc` collapses it. The Pin control only switches between pinned and auto-hide behavior, while clicking the content controls Compact/Expanded.

## Hotkey and settings

`Ctrl + Alt + Space` is the default visibility shortcut. To choose another shortcut, open the tray menu and select `Open Settings`, click the shortcut field, and press the key combination you want to use. The shortcut is registered through Windows `RegisterHotKey`. If another application already owns it, NotchBar keeps mouse interaction available. While the fullscreen app badge is active, the shortcut toggles the badge's visibility.

The Settings window provides controls for the local API port, auto-hide delay, visibility shortcut, startup behavior, display selection, and fullscreen behavior. The descriptions in the window explain each choice. The API port must be between 1024 and 65535 and is usually best left unchanged. The auto-hide delay can be set from 0.1 to 10 seconds.

For fullscreen behavior, choose `Show the app badge` (the default) to show a compact status or clock badge with the foreground app icon when available, `Hide NotchBar` to fully hide it, or `Show NotchBar normally` to keep the regular island behavior. In badge mode, the badge auto-hides after the configured delay; moving the pointer over the centered top trigger wakes it again. Hover over the visible island to reveal its Pin control and keep it open. The badge icon itself is passive and click-through. For display selection, choose the primary display or follow the display containing the active app.

Settings are stored for the current Windows user in `%LOCALAPPDATA%\NotchBar\settings.json`; NotchBar creates and updates this file automatically. You do not need to open or edit it. Changes are saved with `Save` and take effect after a restart. `Save & Restart` saves the changes and relaunches NotchBar. The Windows startup choice is updated as soon as it is saved.

## Multi-monitor and DPI behavior

NotchBar declares Per-Monitor V2 DPI awareness. WPF continues to lay out the content in device-independent units, while the top-level window is positioned with native Win32 screen coordinates. This avoids treating physical monitor coordinates as WPF logical coordinates when displays use different scale factors such as 100%, 125%, or 150%.

When moving between displays, the window is re-centered using the target display's real bounds. The current implementation supports primary-display placement and active-window following; selecting an arbitrary display by device name is intentionally deferred.

Fullscreen behavior follows `fullscreenMode` on the relevant display. In `primary` mode, only eligible foreground windows on the primary display count; in `activeWindow` mode, the foreground display is used. Detection includes true fullscreen windows and visible OS-maximized windows covering the display's work area, so a maximized Codex window can trigger the badge even when the taskbar remains visible. `badge` shows the compact status/clock and, when available, the foreground app icon at the right with a smooth transition. The app icon is rendered at 18 × 18 device-independent pixels, up from 16 × 16 (~12.5% larger). The badge is passive and click-through. `hide` fully hides the island while an eligible fullscreen or maximized app is active, while `normal` keeps the regular island behavior. Context is refreshed from Windows foreground and window-location events, with a slower polling fallback. Detection uses window bounds rather than app-specific game or media integration. If icon lookup exhausts its retries, NotchBar temporarily returns to the normal interactive island instead of showing a placeholder icon; pinned state is preserved, and unpinned content follows its usual auto-hide behavior. It makes one recovery lookup after 30 seconds and restores the badge only if it finds a real icon. A true exclusive-fullscreen game may not display a desktop overlay at all.

## Tray, startup, and single-instance behavior

NotchBar exposes a system tray icon with `Show`, `Pin` / `Unpin`, `Start with Windows`, `Open Settings`, `Restart NotchBar`, and `Exit` actions. Double-clicking the tray icon shows the island.

`Start with Windows` creates a per-user entry under the Windows `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key, so it does not require administrator rights. The preference is also stored in `settings.json`. If changing one side fails, NotchBar avoids silently leaving the setting half-applied.

`Restart NotchBar` uses the same launch specification as Start with Windows. The current process first performs its normal shutdown sequence, including stopping the API and releasing the single-instance guard, and only then launches the replacement process. This avoids a restart being rejected as a duplicate instance. The launch helper supports both a published `NotchBar.exe` and development execution through `dotnet <NotchBar.dll>`.

Only one NotchBar instance is allowed per Windows session. Starting NotchBar again signals the existing process to show its island and then exits, instead of creating a second window or competing for the localhost API port.

## REST API

Requests and responses use JSON. The API accepts status data only; it does not accept HTML, CSS, XAML, shell commands, or arbitrary UI descriptions.

### Health check

```powershell
Invoke-RestMethod http://127.0.0.1:32145/api/v1/health
```

### List active items

This endpoint returns items that have not expired, ordered by priority and then update time.

```powershell
Invoke-RestMethod http://127.0.0.1:32145/api/v1/items
```

### Update an item

External items must use a positive `ttlSeconds` between 1 and 86400. Every PUT refreshes the item's update time. `progress` must be between 0 and 1, and `priority` must be between -100 and 1000. Text fields have length limits.

```powershell
$body = @{
    title = 'CC'
    text = 'Working'
    secondaryText = '128k · 18m'
    detail = 'Refactoring retrieval pipeline'
    progress = 0.63
    priority = 80
    ttlSeconds = 10
    wakeOnUpdate = $true
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Put `
    -Uri 'http://127.0.0.1:32145/api/v1/items/demo' `
    -ContentType 'application/json' `
    -Body $body
```

Equivalent `curl` example:

```powershell
curl.exe -X PUT http://127.0.0.1:32145/api/v1/items/demo `
  -H "Content-Type: application/json" `
  -d '{"title":"CC","text":"Working","secondaryText":"128k · 18m","detail":"Refactoring retrieval pipeline","progress":0.63,"priority":80,"ttlSeconds":10,"wakeOnUpdate":true}'
```

### Delete an item

```powershell
Invoke-RestMethod `
    -Method Delete `
    -Uri 'http://127.0.0.1:32145/api/v1/items/demo'
```

The built-in `clock` item cannot be deleted or overwritten.

### Send a one-shot notification

A notification creates a short-lived item. Its default TTL is 8 seconds and it wakes Compact unless the current fullscreen policy hides the island.

```powershell
$notify = @{
    title = 'Build'
    text = 'Succeeded'
    detail = 'Release build completed.'
    ttlSeconds = 8
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri 'http://127.0.0.1:32145/api/v1/notify' `
    -ContentType 'application/json' `
    -Body $notify
```

## StatusItem shape

```json
{
  "id": "cc-statusboard",
  "title": "CC",
  "text": "Working",
  "secondaryText": "128k · 18m",
  "detail": "Refactoring retrieval pipeline",
  "progress": 0.63,
  "priority": 80,
  "ttlSeconds": 10,
  "wakeOnUpdate": false
}
```

The store adds `updatedAt` when the item is accepted. TTL expiration is handled internally so a crashed producer cannot leave stale status visible forever.

## Example push script

After starting NotchBar, run:

```powershell
.\scripts\push-demo.ps1
```

The script sends a demo status through `PUT /api/v1/items/demo` with a 10-second TTL.

## Current limitations

- `monitorMode` currently supports only the primary display or active-window following; choosing a fixed non-primary display by device name is not implemented yet.
- Display placement is polling-based rather than event-hook based.
- Saved settings take effect after restarting NotchBar; the Settings window provides a `Save & Restart` action. Windows startup registration updates immediately.
- The UI displays one best item selected by priority and update time rather than implementing a multi-card layout system.
- The API is loopback-only and currently has no authentication. Do not change the listener to a remote network interface without adding an explicit security design.
- There is no Plugin SDK, Widget Marketplace, script runtime, or Event Bus.

## Possible future work

The next product-level work is interaction polish around item transitions, relative update times, and notification behavior. Richer notification actions, status history, and additional visual themes can come later.
