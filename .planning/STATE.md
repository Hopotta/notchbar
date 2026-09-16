# State: NotchBar

## Project Reference

See: `.planning/PROJECT.md`.

**Core value:** 外部程序更新状态后，用户能看到短摘要，旧状态不会永久残留。
**Current focus:** Phase 1 — Core island and local status API

## Current Position

- Phase: 1
- Status: build and API end-to-end verification complete; UI interaction still needs a manual check on the user's desktop
- Last activity: built with .NET 10.0.401 and exercised the local REST API

## Verification Notes

- `dotnet build .\NotchBar.sln` passed with 0 warnings and 0 errors.
- `/api/v1/health` returned `ok`; `/api/v1/items` initially returned the built-in `clock` item.
- A demo item with a 3-second TTL was selected, then automatically removed and replaced by `clock`.
- `/api/v1/notify` created a notification item and DELETE removed it.
- Invalid `ttlSeconds=0` returned HTTP 400.
- The active listener was confirmed as `127.0.0.1:32145`.
- The WPF process was started successfully. The restricted execution desktop exposed no main window handle, so visual hover, click, Esc, Pin and global-hotkey behavior should receive one manual check on the normal user desktop.
