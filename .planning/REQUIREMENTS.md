# Requirements: NotchBar

**Defined:** 2026-09-16
**Core Value:** 外部程序更新状态后，用户能在屏幕顶部可靠地看到短摘要，并且旧状态不会永久残留。

## v1 Requirements

### Window and state

- [ ] **WIN-01**: The app shows a borderless always-on-top island centered on the primary display.
- [ ] **WIN-02**: The app implements Hidden, Compact, Expanded, and Pinned states.
- [ ] **WIN-03**: Mouse hover wakes Compact and mouse leave hides after a short delay.
- [ ] **WIN-04**: Ctrl+Alt+Space toggles visibility, Compact click expands, Esc collapses, and Pin toggles fixed display.

### Status data

- [ ] **STA-01**: StatusItem supports id, title, text, secondaryText, detail, progress, priority, ttlSeconds, wakeOnUpdate and updatedAt.
- [ ] **STA-02**: The store selects active items by priority then update time.
- [ ] **STA-03**: External items automatically expire after ttlSeconds.
- [ ] **STA-04**: A built-in Clock item is available without external setup.

### API and lifecycle

- [ ] **API-01**: Minimal API exposes health, list, upsert, delete, and notify endpoints on 127.0.0.1:32145.
- [ ] **API-02**: API validates ids, lengths, progress, priority and positive TTL without accepting UI markup or commands.
- [ ] **API-03**: UI updates remain dispatcher-safe while API requests run independently.
- [ ] **API-04**: Shutdown unregisters the global hotkey and stops the API server.

### Documentation

- [ ] **DOC-01**: README explains run steps, states, hotkey, API, examples, limitations and future ideas.
- [ ] **DOC-02**: A PowerShell demo script pushes `/api/v1/items/demo`.

## v2 Requirements

- Persisted settings, tray menu, configurable port and hotkey.
- Multi-monitor tracking and richer layouts.
- Authentication or a deliberately configured LAN mode.
- Plugin SDK, marketplace, script runtime, event bus, and business data collectors.

## Out of Scope

| Feature | Reason |
|---------|--------|
| Full-screen AppBar/work-area changes | The product is a small top island, not a desktop shell replacement. |
| Arbitrary HTML/CSS/XAML from API | Keeps the API data-only and avoids UI injection. |
| Remote network access | The MVP is local-only by design. |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| WIN-01..04 | Phase 1 | Pending |
| STA-01..04 | Phase 1 | Pending |
| API-01..04 | Phase 1 | Pending |
| DOC-01..02 | Phase 1 | Pending |

**Coverage:** 13 v1 requirement groups mapped to Phase 1; 0 unmapped.

---
*Requirements defined: 2026-09-16*
*Last updated: 2026-09-16 after initialization*
