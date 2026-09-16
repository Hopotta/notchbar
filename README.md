# NotchBar

NotchBar 是一个轻量的 Windows 顶部信息岛 MVP：主显示器顶部中央有一个约 400×38px 的无边框置顶窗口，外部程序通过 localhost REST API 推送状态，NotchBar 负责选择、展示和按 TTL 过期清理。

## 运行

环境要求：Windows、.NET 10 SDK。

```powershell
dotnet run --project .\NotchBar.csproj
```

启动后 API 默认监听：`http://127.0.0.1:32145`。程序只绑定 IPv4 loopback，不注册全宽 AppBar，也不修改 Windows 工作区。

## 四种状态

- `Hidden`：窗口移到屏幕上方，只保留约 2px 的触发条。
- `Compact`：显示一行摘要，例如 `CC · Working · 128k · 18m`。
- `Expanded`：显示 detail、secondaryText、progress 和更新时间。
- `Pinned`：保持 Expanded 内容，不因鼠标离开自动隐藏。

鼠标移入顶部中央触发条会唤醒 Compact；离开约 900ms 后自动隐藏。点击 Compact 内容进入 Expanded，按 `Esc` 收起。Pin 控件在自动隐藏和固定显示之间切换。

## 默认快捷键

`Ctrl + Alt + Space`：切换显示 / 隐藏。快捷键由 Windows `RegisterHotKey` 注册，退出时注销；如果被其他程序占用，NotchBar 会保留鼠标交互并在调试输出中记录失败。

## REST API

请求和响应均为 JSON。API 只接受状态数据，不接受 HTML、CSS、XAML 或 shell command。

### 健康检查

```powershell
Invoke-RestMethod http://127.0.0.1:32145/api/v1/health
```

### 获取当前未过期状态

```powershell
Invoke-RestMethod http://127.0.0.1:32145/api/v1/items
```

### 更新状态

`ttlSeconds` 对外部 item 必须为 1 到 86400 的正整数；每次 PUT 都会刷新更新时间。`progress` 为 0 到 1，`priority` 为 -100 到 1000。文本字段有长度限制。

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

等价的 `curl` 示例：

```powershell
curl.exe -X PUT http://127.0.0.1:32145/api/v1/items/demo `
  -H "Content-Type: application/json" `
  -d '{"title":"CC","text":"Working","secondaryText":"128k · 18m","detail":"Refactoring retrieval pipeline","progress":0.63,"priority":80,"ttlSeconds":10,"wakeOnUpdate":true}'
```

### 删除状态

```powershell
Invoke-RestMethod `
    -Method Delete `
    -Uri 'http://127.0.0.1:32145/api/v1/items/demo'
```

内置 `clock` item 不允许删除。

### 发送一次性通知

通知会生成一个短生命周期 item，默认 TTL 为 8 秒并唤醒 Compact：

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

## 示例推送脚本

程序启动后运行：

```powershell
.\scripts\push-demo.ps1
```

脚本会通过 `PUT /api/v1/items/demo` 推送一个 10 秒 TTL 的示例状态。

## 当前限制

- 第一版只定位主显示器；屏幕尺寸通过 WPF `SystemParameters` 获取，没有把坐标写死。
- API 默认端口和快捷键暂未提供设置界面，也没有持久化配置。
- UI 只展示按 priority、更新时间排序后的一个最佳 item，不是多卡片布局系统。
- API 仅绑定 `127.0.0.1`，当前没有认证；不要把监听地址改为远程网卡。
- 没有系统托盘菜单、开机自启、显示器切换跟随、Plugin SDK、Widget Marketplace、脚本运行时或 Event Bus。

## 后续可考虑但尚未实现

可在 MVP 验证后再考虑：设置文件和托盘菜单、可配置快捷键/端口、多显示器跟随、更多通知动作、状态历史和更丰富的视觉主题。
