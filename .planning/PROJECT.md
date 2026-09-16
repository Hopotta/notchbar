# NotchBar

## What This Is

NotchBar 是一个轻量的 Windows 顶部信息岛。它在主显示器顶部中央显示一个小型、无边框、置顶状态栏，外部程序通过 loopback REST API 提供状态数据，NotchBar 负责展示、排序、唤醒和 TTL 过期。

## Core Value

外部程序更新状态后，用户能在屏幕顶部可靠地看到短摘要，并且旧状态不会永久残留。

## Requirements

### Validated

(None yet — first MVP awaits local validation)

### Active

- [ ] 四态窗口状态机 Hidden / Compact / Expanded / Pinned
- [ ] 顶部触发、延迟自动隐藏、Esc、Pin 和全局快捷键
- [ ] loopback Minimal API 与 StatusItem、priority、更新时间和 TTL
- [ ] 内置 Clock item、示例推送脚本和 README
- [ ] 退出时注销快捷键并停止 API

### Out of Scope

- Plugin SDK、Widget Marketplace、脚本运行时、Event Bus — MVP 不需要扩展平台。
- YASB、Rainmeter 或通用 Widget 平台能力 — 产品目标是单一顶部信息岛。
- 远程 API、认证和业务数据采集 — API 默认只给本机外部程序推送数据。

## Context

项目从空目录开始，目标平台为 Windows，优先使用 C#、.NET 10、WPF 和 ASP.NET Core Minimal API。第一版只考虑主显示器，但通过系统参数计算坐标；窗口不修改工作区，不注册全宽 AppBar。

## Constraints

- **技术栈**：单个 WPF 进程承载 Minimal API — 便于部署和保持 MVP 简单。
- **安全**：Kestrel 只监听 `IPAddress.Loopback`，输入长度和数值字段进行校验 — 避免无意暴露网络服务和旧状态注入。
- **可靠性**：外部 item 必须使用正 TTL — 外部程序崩溃后状态能够自动过期。

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| 单进程承载 WPF 与 Minimal API | 减少部署和通信复杂度 | — Pending |
| 使用 `RegisterHotKey` | 满足全局快捷键，不依赖第三方库 | — Pending |
| `StatusStore` 周期清理 TTL | 将过期责任放在状态源内部，避免 UI 永久展示 | — Pending |
| UI 使用 TextBlock，不解析外部标记 | API 只传数据，不直接注入 XAML/HTML | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition**:
1. Requirements invalidated? Move to Out of Scope with reason.
2. Requirements validated? Move to Validated with phase reference.
3. New requirements emerged? Add to Active.
4. Decisions to log? Add to Key Decisions.
5. Confirm that What This Is is still accurate.

**After each milestone**:
1. Review all sections.
2. Recheck Core Value.
3. Audit Out of Scope.
4. Update Context.

---
*Last updated: 2026-09-16 after initialization*
