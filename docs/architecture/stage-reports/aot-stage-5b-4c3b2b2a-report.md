# DeskBox AOT 阶段 5B-4C3B2B2A 完成与复盘报告

- 日期：2026-08-23
- 状态：已完成；产品修正、定向测试、x64 Native AOT 审计、真实 Todo surface 运行矩阵、全量回归和 Rust 回归均通过
- 审计配置：profile 56 / schema 53
- 本阶段范围：受控 activation 输入到真实 Todo surface 的正文定位，以及 Complete/Snooze 后的可见刷新
- 明确保留：真实 Windows 通知点击、真实 `AppNotificationActivatedEventArgs` 来源证明、运行中/冷启动/第二实例的外部点击矩阵

## 1. 结论

5B-4C3B2B2A 已封闭上一阶段复盘确认的 UI 生命周期缺口。通知正文路由不再把“已请求显示 Widget”当作成功，而是等待目标 Todo 控件真正 Loaded、取得 `XamlRoot`、定位精确 item，并等待两帧合成提交后才返回 `TargetPresented=true`。Complete 与 Snooze 也不再把空刷新回调算作可见成功，而是刷新真实 `TodoWidgetContentAdapter`，等待同一可见 surface 提交后单独记录 `RefreshCompleted=true`。

新的 Native AOT 矩阵使用受控 arguments 与 `UserInput`，但所有产品处理均经过现有 `RouteTodoNotificationActivationAsync`、`TodoNotificationActivationRouter`、`TodoReminderService`、`WidgetManager` 和真实 Todo 控件。结果明确保持 `SystemNotificationAttempted=false`、`ExternalWindowsActivationAttempted=false` 和 `UserClickVerified=false`，因此本阶段证明的是 activation 进入应用后的真实 UI 行为，不冒充 Windows 通知中心点击。

## 2. 产品实现

### 2.1 路由结果不再掩盖 UI 失败

`TodoNotificationActivationRouteResult` 增加 `TargetPresented` 与 `RefreshCompleted`。正文路由只有在目标 surface 实际呈现时才返回 `Opened/Succeeded`；目标不存在、窗口未加载或超时时返回 `TargetUnavailable`。Complete/Snooze 的业务写入成功与可见刷新结果分开记录，持久化成功不会再掩盖 UI 刷新未完成。

`TodoNotificationActivationRouter` 的 UI callback 改为异步布尔结果。既有 grammar、四种 Snooze、legacy `snooze10`、时区和 Todo service 逻辑保持不变。

### 2.2 真实 Todo surface 就绪与提交

`WidgetManager.ShowTodoReminderTargetAsync` 现在返回结构化 `TodoReminderTargetPresentationResult`，包含 Widget/item、HWND、窗口可见性、`XamlRoot`、item 定位和最终呈现结果。处理顺序为：

1. 解析并显示精确 Todo Widget；
2. 等待内容 ViewModel 初始化完成；
3. 等待真实 `TodoWidgetContent` Loaded 且取得 `XamlRoot`；
4. 通过 `FocusReminderItem` 切换筛选、选中并滚动到精确 item；
5. 独立监听 `CompositionTarget.Rendering`，等待两帧稳定提交；
6. 同时满足窗口可见、surface 已提交和目标 item 已定位后才判定成功。

两帧监听独立于现有托盘动画 controller，不会取消正在排队的展示动画。等待最多 3 秒，超时明确返回失败。

Complete/Snooze 的刷新只接受已加载的 `TodoWidgetContentAdapter`。刷新后同样等待两帧提交，并要求窗口仍可见；日志分别记录 HWND、`XamlRoot`、`surfaceCommitted` 和最终结果。

## 3. 实跑发现与修正

首轮预运行没有放宽门禁，而是暴露两个真实竞态：

- fixture 先保存 Widget 配置、后写 Todo store，设置链可能提前创建空 ViewModel；最终改为先写 owned store，再发布配置；
- `ContentReadyTask` 只覆盖数据初始化和内容挂载，不等于控件已 Loaded 或首帧已提交；最终增加 Loaded/`XamlRoot` 和两帧提交门禁。

失败进程立即关闭时还观察到尚未执行的托盘展示回调进入已释放窗口。fixture 最终在正常关闭前调用 `CompleteTrayShowWithoutAnimation` 收束测试窗口动画；产品日常展示动画不被禁用或改写。

## 4. Native AOT Todo surface 矩阵

成功 run ID 为 `69402a1914814f778abdfc29daf1b4f5`，归档结果位于 `.artifacts/aot-todo-notification-surface-smoke/win-x64/runs/69402a1914814f778abdfc29daf1b4f5`。矩阵由一个受审计 Native AOT 进程完成，PID 36940，自然退出，owned preview root 已清理。

固定时钟为 2026-08-25 08:15:00 +08:00。结构化结果为：

| 路径 | 结果 |
| --- | --- |
| 正文 | HWND 4590402、窗口可见、`XamlRoot=true`、精确 item 可见且选中、筛选为 All、`TargetPresented=true` |
| Complete | 路由成功、请求刷新、两帧可见刷新完成、真实 ViewModel 条目为完成态 |
| Snooze | `UserInput=30m`，路由成功、两帧可见刷新完成；路由与 surface 均为 2026-08-25 08:45:00 +08:00 |
| 进程与隔离 | 1 个进程、1 次自然退出、3 条路由、正式数据指纹前后一致、preview root 已清理 |
| 证据边界 | 未展示系统通知、未触发外部 Windows activation、未声称用户点击 |

运行日志没有 `Unhandled exception`。正文、Complete 和 Snooze 均在同一个真实 Todo HWND 上完成。

## 5. AOT 审计与回归

`publish-aot-audit.ps1 -Platform x64` 在 profile 56 / schema 53 下通过：

- 审计耗时 261,088 ms，`sourceStableDuringAudit=true`；
- publish 39 个文件、93,040,085 bytes；symbols 3 个文件、212,709,376 bytes；
- `DeskBox.exe` SHA-256 为 `BD5B804B93F9AEE95BF5E3806ED6B13B74EAC9249AC06D8F4452A6EECDF37D66`；
- WMC1506=0、WMC1510=1211、完整 `always-throw`=0，原始 IL2026/IL2050/IL2072/IL2075/IL3050 均为 0；
- B2B2A 的 scenario/product/runner 缩减模式、越界模式和目标源码 warning 均为 0；
- JSON 固定清单继续为 29 个文件、65/65 处 source-generated 调用和 27 个 context 所有者；本阶段复用现有结果 context，没有增加反射 JSON；
- Rust ABI 2、能力 511、十个导出和 staging/publish 哈希保持不变。

自动回归结果：

- 通知路由、转发相邻链与 B2B2A 定向测试 21/21；
- 全部 AOT 相关测试 452/452；
- x64 全量测试 2468/2468；
- Rust workspace 57/57，`cargo fmt --all --check` 和 Clippy `-D warnings` 均通过；
- 24 个 PowerShell 脚本语法解析 0 错误，阶段文件 `git diff --check` 通过；
- 规范非平台 Debug 构建 0 错误、24 个工作区既有警告，本阶段文件无新增编译或 AOT warning。

## 6. Rust 决策

本阶段继续使用 C#/WinRT。处理规模只有少量字典、一个 Todo ViewModel 和三项 fixture 数据，未出现大型常驻集合或复制热点。复杂度集中在 WinUI Loaded、`XamlRoot`、合成帧、Windows activation 和异步 UI callback。迁移 Rust 需要增加 UI 反向回调、WinRT 对象生命周期和跨 ABI 状态映射，不能降低内存或整体复杂度。

该结论符合当前原则：只有完整 Rust 边界明显更简单，或实测能显著降低内存时才迁移；本阶段不满足这两个条件。

## 7. 复盘与下一阶段调整

代码、AOT 运行和测试复盘没有发现 B2B2A 范围内的未闭环项。真实 Windows activation 仍是外部生命周期边界，建议继续拆分：

1. **5B-4C3B2B2B1 运行中主实例的真实通知点击**：使用唯一 tag/group 展示正文、Complete 和 Snooze 通知，由用户实际点击；记录真实 `AppNotificationActivatedEventArgs`、`UserInput`、route result、目标 HWND/item、可见刷新和精确清理。复杂度中高；不使用受控 envelope 或注入点击冒充真人证据。
2. **5B-4C3B2B2B2 冷启动与第二实例真实点击**：在应用完全退出和主实例已运行两种状态下点击真实通知，核对冷启动注册、唯一进程、类型化 envelope、完整 `UserInput`、目标 surface、持久化和重启恢复。复杂度高。

下一项应先做 B2B2B1。它可以复用本阶段已经证明的 surface 门禁，把剩余变量限制为 Windows 通知来源与运行中 activation；通过后再加入冷启动和第二实例时序。当前不需要下载额外 SDK 或 Rust 工具。
