# NotchBar

NotchBar is a lightweight Windows top information island. It presents a small, borderless, always-on-top status bar centered at the top of the primary display. External programs push status data through a localhost REST API; NotchBar selects, displays, and expires that data instead of collecting business data itself.

## Requirements

- Windows
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

The published executable is placed under the generated `bin\Release` publish directory. The app does not currently register itself for automatic startup; launch the executable directly or create a Windows Startup shortcut if needed.

The API listens on `http://127.0.0.1:32145` by default. It binds only to the IPv4 loopback address, does not register a full-width AppBar, and does not modify the Windows work area.

## Interaction and states

NotchBar has four user-facing states:

- `Hidden`: The window is moved above the screen and leaves only an approximately 2px trigger strip visible.
- `Compact`: A single-line summary is shown, for example `CC · Working · 128k · 18m`.
- `Expanded`: The detail text, secondary text, progress, and update time are shown.
- `Pinned`: The current Compact or Expanded visual state stays visible and does not auto-hide. Pinning changes the hide policy; it does not expand the content.

Mouse movement over the centered top trigger wakes `Compact`. Leaving the island starts an approximately 900ms auto-hide delay. Clicking the Compact content enters `Expanded`; pressing `Esc` collapses it. The Pin control only switches between pinned and auto-hide behavior, while clicking the content controls Compact/Expanded.

## Default hotkey

`Ctrl + Alt + Space` toggles visibility. The shortcut is registered through Windows `RegisterHotKey` and unregistered during shutdown. If another application already owns the shortcut, NotchBar keeps mouse interaction available and writes the registration failure to debug output.

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

The built-in `clock` item cannot be deleted.

### Send a one-shot notification

A notification creates a short-lived item. Its default TTL is 8 seconds and it wakes Compact.

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

- The first version targets the primary display only. Coordinates are calculated through WPF `SystemParameters` rather than hard-coded screen values.
- The API port and hotkey are built-in defaults; there is no settings UI or persisted configuration yet.
- The UI displays one best item selected by priority and update time rather than implementing a multi-card layout system.
- The API is loopback-only and currently has no authentication. Do not change the listener to a remote network interface without adding an explicit security design.
- There is no system tray menu, automatic startup registration, display-following behavior, Plugin SDK, Widget Marketplace, script runtime, or Event Bus.

## Possible future work

After the MVP is validated, possible follow-up work includes a settings file and tray menu, configurable port and hotkey, multi-monitor following, richer notification actions, status history, and additional visual themes.
