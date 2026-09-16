# Roadmap: NotchBar MVP

## Phase 1 — Core island and local status API

Goal: deliver a small runnable Windows desktop app that reliably shows external status and cleans it up after TTL.

Scope:

- WPF borderless top island with four states and simple animations.
- Mouse wake/auto-hide, Esc, Pin, and Ctrl+Alt+Space.
- Thread-safe StatusStore with priority selection, clock item, and TTL expiry.
- Loopback Minimal API and validation.
- Lifecycle cleanup, README, and PowerShell demo.

Verification:

- Build the project with .NET 10 SDK.
- Run locally and exercise hover, click, Esc, Pin and hotkey.
- PUT a demo item, observe Compact/Expanded, then verify it disappears after TTL.
- Check health/items/delete/notify endpoints.

---
*Created: 2026-09-16*
