# DeskBox Z-Order 系统分析报告

## 一、当前 Z-Order 架构

### 核心状态标志

| 标志 | 位置 | 含义 |
|------|------|------|
| `_widgetsRaisedFromTray` | WidgetManager | 批量唤起是否激活 |
| `_focusClickedMode` | WidgetManager | 是否处于"仅保留点击格子"模式 |
| `_isAtDesktopLayer` | 每个窗口 | 窗口是否在桌面层（底层） |
| `_keepRaisedUntilDeactivate` | 每个窗口 | 交互期间保持置顶 |
| `_restoreDesktopLayerWhenIdle` | 每个窗口 | 等待恢复到桌面层 |

### 批量唤起流程

```
用户按快捷键/点击托盘
  → RaiseWidgetsFromTrayAsync()
    → _widgetsRaisedFromTray = true
    → ShowPreparedRaisedFromTray() × N 个格子
      → HoldTemporaryTopMost() → SetWindowTopMost()
    → 启动确认定时器 (0/40/140/320/640ms)
    → 安装鼠标钩子
```

### 鼠标钩子流程

```
用户点击任意位置
  → TrayLayerMouseHookProc()
    → if (!_widgetsRaisedFromTray && !_focusClickedMode) return  ← 关键守卫
    → 判断点击位置:
       - 任务栏: 忽略
       - 格子上 + focusClicked模式: 焦点点击处理
       - 格子上 + 全部可见模式: 忽略
       - 外部: RestoreRaisedWidgetsToDesktopLayer(force: true) → 全部推到底层
```

### 失焦处理流程

```
窗口失去焦点
  → WidgetWindow_Activated(Deactivated)
    → if (Visible && !_isAtDesktopLayer && !WidgetsRaisedFromTray && !FocusClickedMode)
       → QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
         → 80ms 后检查前台窗口
           → 不是 DeskBox 窗口 → RestoreDesktopLayer(force: true)
```

---

## 二、问题分析

### 问题描述
全屏浏览器下唤起格子，点击一个格子后，部分格子留在浏览器上方无法关闭。

### 根因分析

从日志看，点击格子时 `ElevateForInteraction` 被调用：

```
[08:15:44.166] Widget HoldTemporaryTopMost hwnd=0x891A3A raised=False from=WidgetWindow.ElevateForInteraction
```

`ElevateForInteraction` 来自格子内容区域的点击事件（如标题栏拖拽开始、右键菜单等），而不是来自 `PointerActivated` handler。

**关键问题**：`ElevateForInteraction` 在 "全部可见" 模式下被调用，将格子提升为 topmost。之后用户点击浏览器时：
- 鼠标钩子检测到点击外部 → `RestoreRaisedWidgetsToDesktopLayer(force: true)` → 全部推到底层
- 但 `ElevateForInteraction` 将 `_isAtDesktopLayer = false`，`_keepRaisedUntilDeactivate = true`
- 这导致部分格子的内部状态与实际层级不一致

### 不一致的根源

| 操作 | Win32 层级 | `_isAtDesktopLayer` | `_keepRaisedUntilDeactivate` |
|------|-----------|---------------------|------------------------------|
| 批量唤起 | Topmost | false | true |
| 用户点击格子 | Topmost (ElevateForInteraction) | false | true |
| 鼠标钩子推底层 | Bottom | **未更新** | **未更新** |
| 失焦恢复检查 | - | false → 触发恢复 | true → 阻止恢复 |

鼠标钩子用 `Win32Helper.SetWindowToBottom` 直接操作 Win32 层级，但**不更新格子内部状态**。导致后续的失焦/恢复逻辑基于错误的状态运行。

---

## 三、修复方案

### 方案 A：鼠标钩子中同步更新内部状态（推荐）

在 `RestoreRaisedWidgetsToDesktopLayer(force: true)` 调用 `ForceRestoreDesktopLayerFromManager()` 而不是直接操作 Win32 API。`ForceRestoreDesktopLayerFromManager` 会：
- 更新 `_isAtDesktopLayer = true`
- 更新 `_keepRaisedUntilDeactivate = false`
- 调用 `PushToBottom()` (Win32 操作)

**优点**：状态一致，后续逻辑正确
**风险**：低，只改变更新顺序

### 方案 B：禁止 ElevateForInteraction 在批量唤起期间

在 `ElevateForInteraction` 中加 `_widgetsRaisedFromTray` 守卫，批量唤起期间不提升格子。

**优点**：简单，减少干扰
**风险**：用户在批量唤起期间无法与格子交互（拖拽、右键等）

### 方案 C：组合方案（推荐）

1. 鼠标钩子中用 `ForceRestoreDesktopLayerFromManager` 替代直接 Win32 调用
2. `ElevateForInteraction` 中加 `_widgetsRaisedFromTray` 守卫
3. 确保失焦处理器在 `_widgetsRaisedFromTray` 期间跳过

---

## 四、亚克力控制器与 Z-Order 的关系

亚克力控制器复用本身不会影响 z-order。之前回滚是因为同时做了多个改动，无法确定是哪个导致问题。

**安全的做法**：先修复 z-order 问题，验证稳定后，再单独尝试亚克力复用。

---

## 五、执行顺序建议

1. **先修复 z-order**（方案 C）— 这是基础，必须先稳定
2. **验证"全部可见"模式** — 唤起/点击格子/点击浏览器/全部消失
3. **验证"仅保留点击格子"模式** — 唤起/点击一个/其他消失/点击外部/全部消失
4. **验证后再做亚克力复用** — 单独改动，单独测试
