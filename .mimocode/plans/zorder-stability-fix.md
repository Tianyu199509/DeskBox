# z-order 稳定性整治方案

## 问题根因

当前架构有三条独立的 z-order 恢复路径（鼠标钩子、Deactivated handler、Timer），各自有不同判断条件，互相竞争。`IsDeskBoxWindow` 按进程 ID 判断，无法区分格子和设置页面，导致点击设置页面时所有恢复路径都被跳过。

## 架构改动：WidgetManager 统一协调

### 1. 窗口分类（WidgetManager.cs）

新增 `IsWidgetWindow(IntPtr)` 方法，通过句柄精确判断是否为格子窗口。替换所有 `IsDeskBoxWindow` 调用点：

| 调用点 | 当前行为 | 改为 |
|--------|---------|------|
| 鼠标钩子 `RestoreRaisedWidgetsForExternalMouseDown` | `IsDeskBoxWindow` → 点击设置页面跳过 | `IsWidgetWindow` → 点击设置页面执行恢复 |
| 格子 `QueueRestoreDesktopLayerIfForegroundLeavesDeskBox` | `IsDeskBoxWindow` → 前台是设置页面跳过 | `IsWidgetWindow` → 前台是设置页面执行恢复 |
| Timer `TryRestoreRaisedWidgetsAfterInteraction` | `IsDeskBoxWindow` → 前台是设置页面跳过 | `IsWidgetWindow` → 前台是设置页面执行恢复 |

### 2. 统一恢复入口（WidgetManager.cs）

将 `RestoreRaisedWidgetsForExternalMouseDown`、`TryRestoreRaisedWidgetsAfterInteraction`、格子的 `QueueRestoreDesktopLayerIfForegroundLeavesDeskBox` 合并为单一判断逻辑：

```
触发 → 检查前台窗口 → 是格子？跳过 → 是任务栏？跳过 → 否则：批量恢复所有格子
```

格子的 `QueueRestoreDesktopLayerIfForegroundLeavesDeskBox` 改为仅向 WidgetManager 发请求，不再独立执行恢复。

### 3. 设置页面联动（SettingsWindow.xaml.cs）

`SettingsWindow_Activated` 中 Deactivated 分支：
- 清除自身 `_keepTopMostUntilDeactivate` + `ClearWindowTopMost`
- 调用 `widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer("settings-deactivated")`

`ActivateFromTray` 中加 5 秒超时兜底：
```csharp
DispatcherQueue.TryEnqueue(async () =>
{
    await Task.Delay(5000);
    if (_keepTopMostUntilDeactivate)
    {
        _keepTopMostUntilDeactivate = false;
        Win32Helper.ClearWindowTopMost(_hWnd);
    }
});
```

### 4. 格子 Deactivated handler 简化（两个格子）

当前 `QueueRestoreDesktopLayerIfForegroundLeavesDeskBox` 有 80ms 延迟 + 独立判断。改为：
- 保留 80ms 延迟（避免瞬态焦点切换触发误恢复）
- 移除独立的 `IsDeskBoxWindow` 判断
- 改为调用 `WidgetManager.TryRestoreFromWidget(reason)`，由 WidgetManager 统一判断

### 5. RestoreDesktopLayer guard 条件确认

已有的 `!force &&` 修复确保 `force=true` 时跳过所有 guard。需确认以下场景不会阻止恢复：
- `_isDeleteWidgetFlyoutOpen`（删除确认弹窗打开中）
- `_isInlineFlyoutOpen`（内联弹窗打开中）
- `_deletePending`（删除操作进行中）
- `TitleEditBox.Visibility`（标题编辑中）

`force=true` 时这些 guard 全部跳过，格子无条件 PushToBottom。

## 涉及文件

| 文件 | 改动 |
|------|------|
| `Services/WidgetManager.cs` | 新增 `IsWidgetWindow`、统一恢复判断逻辑、更新鼠标钩子和 Timer |
| `Views/WidgetWindow.xaml.cs` | `QueueRestoreDesktopLayerIfForegroundLeavesDeskBox` 改为走 WidgetManager |
| `Views/QuickCaptureWidgetWindow.xaml.cs` | 同上 |
| `Views/SettingsWindow.xaml.cs` | Deactivated 时请求恢复 + 超时兜底 |

## 验证场景

| 场景 | 预期 |
|------|------|
| 托盘唤起 → 点击外部窗口 | 所有格子同时恢复桌面层 |
| 托盘唤起 → 点击设置页面 | 格子恢复桌面层，设置页面保持正常 |
| 托盘唤起 → 设置页面 → 点击外部 | 设置页面清除 topmost，格子恢复桌面层 |
| 托盘唤起 → Alt+Tab | 格子恢复桌面层 |
| 托盘唤起 → 拖拽格子 → 松手 | 拖拽结束后格子恢复桌面层 |
| 托盘唤起 → 右键格子 → 点击外部 | 菜单关闭，格子恢复桌面层 |
| 托盘唤起 → 点击任务栏 | 格子保持置顶 |
| 托盘唤起 → 5 秒无操作 | 格子自动恢复桌面层（超时兜底） |
