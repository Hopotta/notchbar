# Changelog

The repository had no release tags or explicit application version before this changelog was introduced. The version labels below are retrospective names for verified snapshots, not a record of published releases. The previous baseline used the .NET SDK's default `1.0.0` version; the current feature stack advances that baseline to `1.1.0`.

## 1.1.0 — current merge candidate

Snapshot at `f4d330b` (2026-09-28), after the `1.0.0` baseline at `a3a664a`.

### Added

- Fullscreen presentation modes: show a compact badge with the foreground app icon when available, hide NotchBar, or keep the normal island behavior. The default mode is the app badge.
- Fullscreen app detection for true fullscreen windows and visible maximized windows that cover the display work area.
- A graphical Settings window for the API port, auto-hide delay, visibility shortcut, Windows startup, display selection, and fullscreen behavior.

### Improved

- Increased the foreground app icon from 16 × 16 to 18 × 18 device-independent pixels (~12.5% larger).
- Fullscreen badge icon transitions and lookup behavior, plus mouse wake and Pin controls while a fullscreen app is active.
- Tray restart and shutdown sequencing so the API and single-instance guard are released before the replacement process starts.
- Fullscreen wake target updates at the end of island transitions, so repeated auto-hide and mouse-wake cycles remain available.

### Fixed

- Fullscreen badge wake timing and mouse input handling, including cases where a fullscreen app captures the pointer.
- When app icon extraction fails, return to the normal interactive island without a placeholder; preserve pinned state and ordinary auto-hide behavior, then retry once after 30 seconds and restore the badge only if a real icon is found.

### Source commits

- `18d3e7f` — show the foreground fullscreen app icon badge.
- `7ceb5c9` — detect maximized apps that cover the work area.
- `afac89c` — improve fullscreen badge behavior and interaction targets.
- `93f5793` — add graphical settings.
- `dac2275` — harden tray restart and shutdown lifecycle.
- `8b324da`, `1e9f0b3`, `eae505e` — fix fullscreen wake auto-hide timing, pointer input, and target refresh.
- `73e62ae` — restore normal interaction on icon failure and retry badge recovery after 30 seconds.
- `f4d330b` — increase the foreground app icon from 16 × 16 to 18 × 18 device-independent pixels.

## 1.0.0 — fixed-envelope baseline

Snapshot at `a3a664a` (2026-09-26), the existing `master` tip before the current feature stack. This retrospective label corresponds to the SDK's default application version; it was not tagged as a release.

### Improved

- Reworked island transitions around a fixed window envelope and reduced repeated layout work during animation.
- Optimized runtime and rendering behavior while preserving compact, expanded, pinned, and hidden states.

### Source commit

- `a3a664a` — implement fixed-envelope transitions and runtime optimizations.

## 0.3.0 — glass and clock transition stability

Snapshot at `b0e929d` (2026-09-23), following the interaction and animation snapshot at `406a1f9`.

### Added and improved

- Added a compositor-backed frosted-glass surface clipped to the animated island bounds.
- Coordinated the Clock text through compact and expanded transitions with shared overlay ownership, interpolated typography, and live transition geometry.
- Corrected backdrop clipping and Clock transition handoff after the compositor implementation was refined.

### Source range

- `f870396` through `b0e929d`; snapshot boundary: `b0e929d`.

## 0.2.0 — interaction, theme, and animation milestone

Snapshot at `406a1f9` (2026-09-21), after the initial MVP at `54bdcfe`.

### Added

- Separated pinning from expansion, and added tray, single-instance, per-user startup, persistent settings, fullscreen suppression, and active-window display following with Per-Monitor V2 DPI awareness.
- Added status metadata and priority handling, reliable one-shot notifications with TTL behavior, and motion when displayed status items change.
- Added adaptive island sizing, glass visuals, light and dark themes, and smoother compact/expanded transitions.

### Improved

- Hardened settings recovery, API validation and shutdown, hotkey behavior, notification timing, layout geometry, and transition rendering.
- Added a hover-accessible Pin control and automatic theme selection.

### Source range

- `c57cdeb` through `406a1f9`; snapshot boundary: `406a1f9`.

## 0.1.0 — initial MVP

Snapshot at `54bdcfe` (2026-09-16), the first NotchBar commit.

### Added

- Added a Windows top information island with Hidden, Compact, Expanded, and Pinned states, a centered mouse wake trigger, auto-hide, and a global visibility shortcut.
- Added a loopback REST API for status items and one-shot notifications with TTL expiration, plus a built-in clock item.
- Added primary-display placement and a small PowerShell demo push script.

### Source commit

- `54bdcfe` — create the NotchBar MVP.
