# DeskBox 文件拖拽与叠放交互契约

本文记录 `FileSurfaceContent` 中“文件拖拽、格子排序、叠放成员关系、真实文件传输”之间的边界。它既是当前实现说明，也是以后修改拖拽代码时的回归清单。

当前安全基线：2026-09-29 方案 B（容器 `CanDrag`/`StartDragAsync` 发起 + 源守卫数据对象）。普通文件对外公告 `Copy | Move`（`AllowedOperations`）且 `RequestedOperation=None`；拖到桌面/Explorer 由 Shell 按自己的同盘移动/跨盘复制语义执行。DeskBox 自身永不依据完成回执删除原件：守卫包装层吞掉所有完成回执，源端只按"文件确实消失"做显示协调。内部整理的逻辑意图与 OLE 回执分开。范围、验证和批次细节见 [文件拖出原件保护](file-drag-source-safety-20260929.md)。第 8 节保留历史故障记录，其中旧 Move/多位方案不再是当前实现。

## 1. 最重要的结论

DeskBox 的文件拖拽分为两类，绝不能混为一套操作：

1. **内部编排**：同一个格子内调整顺序、加入叠放、移出叠放、叠放成员排序、叠放整体排序。它只修改 DeskBox 的投影和持久化元数据，不移动、复制或删除磁盘文件。
2. **文件系统传输**：桌面/资源管理器与格子之间、不同格子之间的文件拖放。它会执行真实的复制、移动或创建快捷方式，并在完成后刷新源格子和目标格子。

内部编排的 `DragOver` 反馈与 `Drop` 完成都永远不能返回 `Move`——文件拖拽对外公告 `Copy | Move`，内部路由始终可用 `Copy`（叠放用 `Link`）作反馈，不需要任何临时 `Move`。这是避免 `.lnk` 被 Shell 当成“源文件移动完成”后清理进回收站的核心约束。

### 1.1 物理路径拓扑安全

外部映射格子允许使用严格的父子目录，但这不会放宽文件传输安全边界：

- 相同物理目录不能同时作为两个格子的映射根；DeskBox 托管收纳目录也不能与任何外部映射重叠；
- 目录复制或移动的目标不能等于源目录，也不能位于源目录的物理后代中；判断必须解析 junction、符号链接和其他路径别名，无法确认物理身份时按不安全处理；
- 若源项目已经直接位于有效目标目录中，根表面和叠放导入必须逐项跳过，不能通过自动改名生成 `(2)` 副本；整批均已位于目标目录时提前作为无操作处理，混合来源批次继续传输其余项目；拖入不同的子文件夹卡片仍属于有效文件传输；
- DeskBox 的托管递归复制不能跟随目录 junction 或符号链接，也必须记录已经访问过的物理源目录，防止回指祖先的目录图生成无界嵌套副本；
- 交给现代 Windows Shell 的交互式传输不得启用 `FOFX_NOSKIPJUNCTIONS`，保留 Shell 默认跳过 junction 的行为；
- 格子内打开子文件夹只切换当前浏览路径和监听目标，不创建新格子、不复制目录，也不会递归打开所有后代；同一时刻每个格子只维护当前目录的内容投影；
- 文件夹 `.lnk` 只有在目标可解析为映射根同一物理目录树内的现存文件夹时，才复用格子内导航；目标就是当前目录时，格子内没有可见的导航结果，必须回退 Windows Shell 打开原始 `.lnk`。失效、外部目录、相对路径、Shell 特殊目标或物理身份无法确认时也继续交给 Windows Shell。该快捷方式始终保留文件身份，拖拽、复制、移动和叠放使用 `.lnk` 自身路径，禁止替换成目标目录；
- 父子映射各自的文件夹监听事件只刷新投影，不能再次触发导入或传输。

以上约束适用于格子间拖动、Explorer 拖入、主表面、文件夹卡片和叠放目标。创建 `.lnk` 只创建一个快捷方式文件，不递归复制目标目录，但仍不得被误判为内部文件移动。

## 2. 两层数据模型

### 2.1 磁盘文件层

`WidgetViewModel.Items` 是格子当前目录中的真实文件/文件夹集合。文件是否存在、从格子移入或移出、空状态是否显示，都应以这一层为准。

### 2.2 叠放投影层

启用叠放后，`WidgetViewModel.VisibleItems` 使用 `_stackDisplayItems`，把多个真实文件投影为以下显示单元：

- 未分组文件；
- `WidgetStackItem` 叠放卡片；
- 非弹窗模式下展开的叠放成员。

叠放不会创建或搬移真实文件。它主要持久化三类元数据：

- `_stackMemberOverrides`：叠放成员及成员顺序；
- `_stackOrder`：叠放/独立文件这些显示单元的顺序；
- 名称、禁用状态、展开状态等其他叠放覆盖项。

自动叠放一旦发生人工加入、移出或成员排序，会按需要转为手动叠放。移出后不足两个成员时，手动叠放会解散；自动叠放会被禁用或转换，以保证剩余文件回到可见层。

### 2.3 弹窗与非弹窗只是宿主不同

两种打开模式共享同一套 `WidgetViewModel` 和叠放元数据：

| 模式 | 成员宿主 | 成员移出路径 | 成员内部排序 |
| --- | --- | --- | --- |
| 非弹窗模式 | 主格子的 `ListViewBase` | `MoveVisibleItemForReorder` 在越过叠放边界时解除成员关系 | 主表面排序路径 |
| 弹窗模式 | 独立 `StackPopoverHostWindow` 内的 `ListViewBase` | 主格子 `Root_Drop` 调用 `RemoveItemsFromStack` | `StackPopoverItems_Drop` 调用 `MoveStackMembersForReorder` |

弹窗是独立窗口，因此判断鼠标是否仍在弹窗内时，必须使用弹窗自己的窗口句柄和坐标系，不能使用主格子的 `_hostWindowHandle`。

## 3. DeskBox 拖拽载荷

文件拖拽由 `FileItemDragPackage.TryPrepare` 统一创建。除系统标准格式外，DeskBox 在 `DataPackage.Properties` 中写入自己的协议字段：

| 字段 | 含义 |
| --- | --- |
| `DeskBoxSourceWidgetId` | 源格子 ID；用来区分同格编排和跨格传输 |
| `DeskBoxSourcePaths` | 本次拖拽的完整、去重、规范化路径集合 |
| `DeskBoxInternalDragToken` | 当前文件拖拽协议标记 `DeskBox.WidgetItemDrag.v2` |
| `DeskBoxDragSessionId` | 每次拖拽唯一 GUID；约束缓存不能跨会话复用 |
| `DeskBoxStackReorderKey` | 拖动叠放卡片整体排序时的叠放键 |
| `DeskBoxSourceStackKey` | 从叠放弹窗拖出成员时的源叠放键 |

文件载荷还会提供以下一种系统数据形式：

- 所有文件、文件夹和 `.lnk` 优先使用原生 Shell `IDataObject`（`SHCreateDataObject`，格式与资源管理器拖拽完全一致：`CF_HDROP`、`Shell IDList Array`、`FileNameW` 等）；所有原生文件载荷均套 `FileDragSourceGuardDataObject`，原样持有 Preferred DropEffect 并隔离完成/清理回执（见 4.1）；
- 只有路径不在同一父目录等原生对象无法表示的情况才回退 `StorageItems`；
- 无法完整提供所选文件时取消拖拽，禁止只拖出部分选择；
- **不写入文本格式**（`SetText`）。Chromium 会把 `CF_UNICODETEXT` 里的路径同时映射成 `text/plain` 和 `text/uri-list`，Electron 应用（WorkBuddy 等）的拖放区看到这两个类型就按文本/链接处理而不是文件；资源管理器的拖拽只有 `Files`。

### 3.1 `.lnk` 的特殊边界

不要在 UI STA 上同步等待 `StorageFile` broker 解析 `.lnk`。部分 Windows 环境会拒绝快捷方式，消息循环也可能被同步等待卡住。当前实现先尝试 `NativeShellFileDragProvider`，让 Explorer 接收原始文件系统对象和系统拖拽图像。

原生 Shell 载荷同时意味着目标返回的完成操作会影响 Shell 是否清理源文件。因此 `.lnk` 最能暴露“内部编排误报为真实 Move”的问题，但这条安全规则同样适用于普通文件和文件夹。

## 4. RequestedOperation、AllowedOperations 与 AcceptedOperation

这三个值含义不同：

- `DataPackage.RequestedOperation`：**只表达默认偏好，不再决定对外允许集合**。文件拖拽按"拖出格子时"设置写入（跟随 → `None`，移动 → `Move`，复制 → `Copy`）；叠放整体拖拽为 `Link`（仅作内部路由标记）。
- `DragStartingEventArgs.AllowedOperations`：**真实的对外允许集合**。文件拖拽为 `Copy | Move`，在 `DragStarting` 同步部分写入，经 `StartDragAsync` 传给底层拖拽操作。`ListViewBase` 内置项目拖拽不会传递这个值（见 4.1），因此文件表面一律用容器级 `CanDrag` → `StartDragAsync` 发起。
- `DragEventArgs.AcceptedOperation`：目标在当前 `DragOver` 或最终 `Drop` 接受的操作。

### 4.1 Copy | Move 对外权限与发起方式解耦

发起方式是双旋钮解耦的关键。`ListViewBase` 项目拖拽（`CanDragItems`）从不写 `AllowedOperations`：对外允许集只能从 `RequestedOperation` 推导，且同一个值又被写成 Preferred DropEffect——允许集合与默认偏好被绑死成一个值，这就是 Win10 每次拖出都弹操作菜单、以及内部只能看到 `Move` 的根因。

改用容器级 `CanDrag`（由 ListView 容器样式设置，等价于内部调用 `UIElement.StartDragAsync`）后，`DragStarting` 里的两个值分别携带各自语义：

- `AllowedOperations`：**按 OS 分档**。Windows 11 为 `Copy | Move`（Explorer 执行原生移动、修饰键生效、冲突对话框原生处理，第三方按 `Copy` 也能收）。**Windows 10 必须收敛为单值**（`ResolveDragOutAllowedOperations`：移动 → `Move`；跟随/未知 → `Copy`；复制 → `Copy`。2026-10-14 勘误：原文写"移动/跟随 → Move"，与代码及测试相反——feedback 500 实测 Win10 单值 `Move` 会被 IM 式接收方解析成 Move 并自删原件入回收站，跟随档因此保守公告 `Copy`，代价是跟随档拖出到 Explorer 默认变复制；`FileItemMultiDragTests.ResolveDragOutAllowedOperations_Win10DefaultNeverOffersMove` 钉死此行为）——2026-10-01 真机实测，Win10 的 Explorer 对任何公告多效果的非 Shell 源拖放一律弹"复制/移动/取消"选择框（代理到达的拖放不含按键状态，Shell 无法确认唯一默认操作）；只有"单值效果且等于目标默认"才静默执行。偏好值在 Win10 不生效。
- `RequestedOperation`：偏好的唯一载体，只对 Windows 11 有意义（跟随 → `None`，移动 → `Move`，复制 → `Copy`）。早期"None 使 Win10 不弹菜单"的说法已于 2026-10-01 实测证伪：弹菜单的判定在允许集，不在偏好位。
- XAML 把 `Copy|Move|Link` 三者全选视为"无偏好"而跳过写入，因此 `Link` 目前不对外公告；Alt 拖出创建快捷方式与任务栏固定暂不支持。

`FileDragSourceGuardDataObject` 包装原生 Shell 对象：Preferred DropEffect 由包装层原样存储与回答（内层 Shell 对象不接受该格式的 `SetData`），Performed/Logical Performed DropEffect、Paste Succeeded、TargetCLSID 等完成回执被消费并仅记录日志，永不抵达内层对象。DeskBox 不根据 `DropResult` 或任何回执删除源文件；当目标按公告的 `Move` 自行执行移动时，由目标（如 Explorer）完成源端处理，这与从资源管理器拖出的原生语义一致。StorageItems 回退仍是 readOnly 语义（对外同样公告 `Copy | Move`，目标自己决定）。

DeskBox 私有文件载荷的移动/快捷方式意图由目标端自行执行；内部编排反馈优先 `Copy`/`Link`（Win10 单值 Move 允许集下允许临时 `Move` 反馈，仅 DragOver），完成仍返回 `None` 防止二次清理。便签/待办关联也采用可协商的 `Copy` 回执。

非文件格子的内容导出拖拽（待办条目文本、快速记录条目含附件 `StorageItems`）一律 `RequestedOperation = Copy` 单值：导出语义本来就是复制——公告 Move 在 Win10 上弹选择框，且 Explorer 真执行 Move 会把附件文件搬出受管存储。这些拖拽的内部目标（tab 拖放、行内排序）原本接受 `Move`，现经 `DeskBoxDragData.ResolveInternalMetadataOperation` 按实际允许集回退。文件拖拽落到待办/快速记录/紧凑宿主时的接受值走 `GetFileAssociationOperation(dataView, e.AllowedOperations)`——内部拖拽在 Win10 单值 Move 下同样回退到 `Move` 才能路由。

### 4.2 为什么反馈策略与完成策略必须分开

`ListViewBase` 内置拖拽的教训：内部目标有时只能看到 `RequestedOperation=Move`，曾被迫临时接受 `Move` 才能让 WinUI 路由 `Drop`。`StartDragAsync` 发起后 `AllowedOperations` 永远携带 `Copy`（叠放为 `Link`），该临时通道已删除。

因此当前策略为：

| 阶段 | 内部编排策略 |
| --- | --- |
| `DragOver` 反馈 | 优先 `Link`，其次 `Copy`；永不接受 `Move` |
| `Drop` 完成 | 优先 `Link`，其次 `Copy`；只有 `Move` 时返回 `None`，永远不授权 Shell 清理源文件 |

## 5. 完整路由矩阵

| 源 | 目标 | 路由/处理 | 是否改磁盘 | 是否改叠放/顺序 | 关键完成规则 |
| --- | --- | --- | --- | ---: | --- |
| 主表面独立文件（含多选块） | 同格主表面插入线 | `surface-reorder`（多选按稳定整块提交，见 5.3） | 否 | 是 | 内部完成不得为 `Move` |
| 非弹窗叠放成员 | 原叠放成员区域 | 主表面成员排序 | 否 | 是 | 保持成员身份，仅改 override 顺序 |
| 非弹窗叠放成员 | 叠放范围外的主表面 | `MoveVisibleItemForReorder` | 否 | 是 | 解除成员关系并插入为独立显示单元 |
| 弹窗叠放成员 | 同一弹窗列表 | `stack-reorder` | 否 | 是 | 使用弹窗插入索引；内部完成不得为 `Move` |
| 弹窗叠放成员 | 所属格子的主表面 | `stack-detach` | 否 | 是 | `RemoveItemsFromStack`，成功后关闭弹窗 |
| 同格独立文件/其他叠放成员 | 目标叠放卡片或弹窗表面 | `stack-membership` | 否 | 是 | `AddItemsToStack`；同一源叠放为无操作 |
| 叠放卡片 | 同格主表面 | `StackReorderKey` 对应的表面排序 | 否 | 是 | 只调整显示单元顺序 |
| 桌面/Explorer | 格子主表面 | 外部导入 | 是 | 可能 | 按映射目录与用户策略复制/移动/创建快捷方式 |
| 桌面/Explorer 的 Shell 命名空间对象（回收站/此电脑/控制面板等） | 格子主表面/叠放 | `namespace-materialize`（2026-10-16 批 0）：有文件系统路径→并入外部导入并去重；纯虚拟项→PIDL .lnk（SetIDList）物化后走导入；判定镜像粘贴侧 `ShellNamespaceClipboardItem` | 是（新建 .lnk） | 可能 | Link 回执优先；提取为零→`ZeroResultDropBlocked` toast（⑫） |
| 桌面/Explorer | 叠放 | 先外部导入，再加入叠放 | 是 | 是 | 只有成功导入的新项才加入叠放 |
| 其他格子 | 本格主表面/叠放 | 跨格文件传输 | 是 | 可能 | 目标完成传输后通知源格子；DeskBox 源不再由 Shell 二次清理 |
| Explorer/其他格子 | 已经直接包含源项目的物理目录 | `same-directory` 无操作 | 否 | 否 | 不生成编号副本，不通知源格子移出 |
| 任意目录 | 目录自身或其物理后代 | `unsafe-folder-transfer` | 否 | 否 | 在创建目标目录前拒绝；解析失败也拒绝 |
| 主表面/弹窗成员 | 桌面、Explorer 或其他应用 | Shell 外部拖出 | 可能 | 随刷新清理 | 源端公告 `Copy \| Move`、不给偏好；目标自行决定执行复制或原生移动，DeskBox 只按磁盘存在性协调源集合，永不代删 |
| 任意文件 | 子文件夹卡片 | 文件夹传输目标 | 是 | 否 | 子目标优先于根表面排序，不能被插入线抢走 |

### 5.1 同格与跨格的判定

同时满足协议 token、源格子 ID 等于当前目标格子 ID，并且存在路径或叠放键时，才是 `IsInternalReorder`。源格子不同，即使载荷来自 DeskBox，也必须走真实文件导入/传输路径。

### 5.2 跨格移动为什么最终也可能返回 None

跨格传输由目标格子实际执行文件移动，并通过 `NotifyItemsMovedOutAsync` 告知源格子哪些路径成功。对于 DeskBox 自己的原生 Shell 载荷，再向源端返回 `Move` 会形成第二次源清理。因此 `ResolveSafeDropCompletionOperation` 在“DeskBox 来源 + 目标已完成真实移动”时返回 `None`；目标完成通知和后续磁盘协调才是事实依据。

非 DeskBox 来源只有在请求移动数与实际完成数完全一致时才返回 `Move`。部分完成、取消或失败必须返回 `None`。

### 5.3 多选拖动排序的整块提交语义（2026-10-14）

主表面（非弹窗）的 `surface-reorder` 支持多选块提交，约束如下：

- 提交端必须解析**全部**被拖项（`ResolveSurfaceReorderItems` + `_surfaceReorderDraggedItems`），禁止回退到 `FirstOrDefault` 单锚点坍缩——那是"多选拖动只动一个"旧缺陷的根因；
- 块内相对顺序 = 拖动起算时的可见顺序；落点换算用"非移动显示单元计数"（`TryMoveDisplayUnitOverrides` / `TryComputeItemBlockReorder`），不依赖会被成员变更移位的裸索引；
- 非投影路径以一次「整块移除+整块插回」重建施加（`ApplyItemBlockReorder`，2026-10-26 起）——逐项 `Items.Move` 表达不了整块落位：移除尚未放置的被拖项会把已放置成员左移，1.5.8-1.5.9 因此每一次下移落点都错（单项落后插线一位、多项散块），且当批测试只钉了落点索引、没钉应用后的最终顺序；两条路径的多选保留统一走按路径重放（见末条）；投影路径整批只允许**常数次**投影重建 + persist（`MoveVisibleItemsForReorder` 按相位各一次），禁止对 N 个选中项循环调用单项原语——那是投影重建风暴（#576 同型）；
- 内联展开叠放的成员：插入点落在本叠放成员区内 → 复用 `MoveStackMembersForReorder` 成员整块排序；落在区外 → 先批量解除成员关系（`DetachStackMembersForBlockReorder`，语义镜像 `RemoveItemsFromStack` 的解散/转手动分支）再作为散件入块；
- 叠放卡片与文件混选拖动排序维持既有语义（锚点为叠放头时只重排叠放卡片，`GetSelectedItems` 本就剔除卡片）；
- 重排施加的集合重建（投影与扁平两路）都会丢掉拖动用的多选；主表面与弹窗的重排成功路径都必须按路径重放选择（`ScheduleSurfaceReorderSelectionRestore` / `ScheduleStackPopoverReorderSelectionRestore`，低优先级延迟——弹窗侧需双跳以落在 `ApplyStackProjectionChange` 的延迟清选之后）。

## 6. 事件链与状态清理

```text
鼠标拖动（容器级 CanDrag，内部走 UIElement.StartDragAsync）
  -> Items_DragStarting
     -> 从 e.OriginalSource 解析被拖容器/项目，合并多选
     -> 创建唯一 DragSessionId
     -> TryPrepare 生成 StorageItems 或 Shell IDataObject
     -> 写入 AllowedOperations = Copy | Move（叠放为 Link）
  -> 子目标或 Root 的 DragOver
     -> GetDragPayload 校验会话缓存
     -> 选择路由并显示高亮/插入线
     -> 反馈策略决定 AcceptedOperation
  -> Drop（正常路径）或鼠标释放恢复（WinUI 漏事件路径）
     -> 修改叠放/顺序元数据，或等待真实文件传输完成
     -> 完成策略决定最终 AcceptedOperation
  -> Items_DropCompleted
     -> 标记内部已处理，或启动外部移出协调；外部拖出注册桌面到达豁免
     -> 清理插入线、子目标高亮、载荷缓存和弹窗拖拽状态
```

### 6.1 子目标可能绕过 Root_DragEnter

文件夹和叠放子元素会将 `DragOver` 标记为 `Handled`。快速从一个目标移动到另一个目标时，新的子目标可能先收到事件，而根元素没有机会重新初始化缓存。

因此 `GetDragPayload` 每次都必须校验 `DragSessionId`。会话不同就丢弃 `_dragPayloadSnapshot`，不能仅在 `Root_DragEnter` 清缓存。没有会话 ID 的外部/旧载荷才使用路径、源格子、token 和叠放键进行兼容比较。

### 6.2 高亮条不等于 Drop 已完成

WinUI 的拖拽完成时刻可能晚于物理鼠标松开。2026-09-04 的两次列表内部排序中，恢复探针抢先提交排序，随后源端收到 `Move`，对应快捷方式在 `DragItemsCompleted` 之前进入回收站。鼠标松开不能作为系统拖拽已经结束的证据。

`FileDragSessionState` 从成功准备源载荷开始记录会话，直到 `Items_DropCompleted` 才允许释放恢复。紧凑窗口和主表面的释放探针在会话进行期间只记录 `ReleaseRecoveryPending`，不得清空 Shell 拖拽状态、重建排序投影或清理载荷缓存，也不得用任意固定延迟替代系统完成通知。当前有两个受限的恢复入口：

- `CompleteReleasedDragSession`：先确认源端系统拖拽已结束，再在主表面排序已激活、有最后位置、鼠标仍在主格子内、且没有文件夹/叠放子目标高亮时提交；
- `TryCompleteReleasedStackPopoverReorder`：只在弹窗拖拽激活、有有效插入索引、鼠标仍在弹窗列表、存在源路径、且没有其他内部目标已处理时提交；
- `ShouldLaunchFromCompletedInternalDrag` + `TryLaunchInternalDragOnShortcut`（第三条受限路径，2026-10-14 补登记）：WinUI 不向条目表面投递同 ListView 拖动的 `Drop`，完成回调内解析"内部拖拽落到快捷方式"并启动；行为安全（内部拖拽启动不清源），此前未入清单。

完成回调中，已由内部目标处理的操作不能再次恢复；未被目标处理且结果为 `None` 的操作按取消处理，不得凭最后插入线补做排序。正常 Drop 仍在目标事件内完成编排。主表面、叠放卡片和弹窗目标必须先将临时 `Move` 改为 `None`，再做任何可能刷新或回收控件的工作，最后按成功结果选择安全完成值。恢复提交后将本次拖拽标记为内部已处理，避免源端启动外部移出协调。

如果源完成回调缺失，不允许仅凭按钮状态或超时强行提交排序。这种未完成会话需从日志调查；不能以恢复交互为由重新打开 Shell 源文件清理风险。此修复仍需 Win10/Win11 的真实 Shell 拖拽验证，状态和结构测试不能替代实机验收。

当前字段 `_activeDragHandledAsStackMembership` 的历史命名偏窄，实际语义已经是“本次拖拽已由任一内部编排目标处理”。以后不能按字段名把它重新限制为“仅加入叠放”。

### 6.3 外部移出协调

`DropResult` 不是逐文件完成报告，Shell 还可能在主表面外部移动成功时返回 `None`，而优化移动也可能回报 `Copy`（目标自己完成了搬移）。观察分三档：`Move` 用完整窗口协调；`Copy` 与弹窗 `None` 只做短探测（文件仍在就直接退出，取消的拖拽不会留一个长时间运行的旧探针）；主表面 `None` 保留完整窗口。每次删除显示项前必须再次检查 `File.Exists`/`Directory.Exists`，防止文件拖出后迅速拖回时被旧任务误删。

## 7. 空状态契约

空状态回答的是“格子目录中是否还有真实项目”，不应依赖叠放投影是否已经重建。因此条件是：

```text
!ViewModel.IsLoading && ViewModel.Items.Count == 0
```

不要改回 `!ViewModel.VisibleItems.Any()`。`VisibleItems` 可能受异步/延迟的叠放投影重建影响，导致：

- 从外部拖入第一个文件后空状态仍显示；
- 移出最后一个文件后空状态没有及时出现。

导入、移出协调、文件监视刷新和集合变化都应最终调用 `UpdateEmptyState`，但可见性的事实来源始终是 `Items.Count`。

## 8. 已发生过的故障与根因

### 8.1 Win10 每次拖出都弹操作选择菜单

根因：`ListViewBase` 内置项目拖拽不写 `AllowedOperations`，对外允许集与 Preferred DropEffect 都只能由 `RequestedOperation` 推导；写 `Copy | Move | Link` 相当于同时把"默认操作"声明成三位组合，Win10 目标无法确定偏好就弹菜单。

正确做法：用容器级 `CanDrag`（`StartDragAsync`）发起拖拽，`AllowedOperations = Copy | Move` 携带真实允许集，`RequestedOperation = None` 不给偏好。Explorer 同卷移动、跨卷复制、修饰键照常；Win10/Win11 行为一致，不再需要跨进程隐藏 Preferred DropEffect。见 4.1。

### 8.1.1 格子拖出的文件微信能收，Electron 应用（WorkBuddy）和游戏收不到

根因一：`RequestedOperation=Move` 在 `ListViewBase` 项目拖拽中就是对外允许集合。用一个只回答 `Copy` 的 WinForms 探针验证：DeskBox 拖出时 `AllowedEffect=Move`、`CF_HDROP` 可读，但没有 `Drop`；资源管理器拖出同一文件为 `Copy, Move, Link` 且 `Drop` 正常。日志表现为 `stage=SourceCompleted ... dropResult=None`，且 `stage=SourceStarting` 从未出现。

根因二：载荷里的 `SetText(路径)` 被 Chromium 映射成 `text/plain` + `text/uri-list`，WorkBuddy 拖放区据此不再按文件处理（用一个打印 `dataTransfer.types` 的网页验证：DeskBox 为 `[text/plain, text/uri-list, Files]`，资源管理器为 `[Files]`）。

正确做法：普通文件走原生 Shell 载荷 + `FileDragSourceGuardDataObject`，`AllowedOperations=Copy | Move`、`RequestedOperation=None`；文件拖拽不再写入文本格式。

历史排除记录：`RequestedOperation=None` 在 `ListViewBase` 路径上会让允许集合变空、任何目标都拒收——该结论只适用于内置拖拽路径；`StartDragAsync` 下允许集来自 `AllowedOperations`，无偏好值合法。

#### 8.1.1b 自起 DoDragDrop 实验记录（2026-09-20，三轮全部失败，已归档；后续已由 StartDragAsync 解耦）

目标：绕开 WinUI"允许集=RequestedOperation=preferred"三位一体耦合——`dwOKEffects=7` 参数直给允许集 + preferred=Move 单值亲手写入，两旋钮解耦（Win10 不弹菜单 + WorkBuddy 可收同时成立）。当时实现完整落在 `NativeFileDragOut`（已删除）。

| 轮次 | 方案 | 结果 |
|---|---|---|
| 1 | UI 线程同步 DoDragDrop | 饿死：`QueryContinueDrag` 零调用、永不返回、持有系统级 OLE 拖拽锁（全系统拖拽失效，杀进程恢复） |
| 2 | 专职 STA 线程（`DeskBox-NativeDragLoop` + OleInitialize） | 同样饿死 |
| 3 | UI 线程 `ReleaseCapture()` 后再起（API 返回 True） | 同样饿死——线程捕获确实释放了，输入仍不进循环 |

**机制定论**：`DragItemsStarting.Cancel` 只取消 WinUI 会话，指针按住期间 **XAML 输入岛独占鼠标输入流且不走 Win32 capture 路由**（ReleaseCapture 成功仍饿死是直接证据）——DoDragDrop 的模态循环无论跑在哪个线程都拿不到输入。`SHDoDragDrop` 同理被否决（内部同样基于 DoDragDrop，输入模型相同）。这是 WinUI 输入架构与 WinForms/WPF 的本质差异，后两者自起拖拽的先例不适用。

**最终解法（已实施）**：上述"自起 DoDragDrop"与"完全自管手势"之间还有官方第三条路——容器级 `CanDrag`/`UIElement.StartDragAsync`。模态循环仍由 XAML 拥有（不受输入岛饿死影响），但 `AllowedOperations` 与 `RequestedOperation` 两个旋钮彻底解耦，`Copy | Move` + 无偏好在 Win10/Win11 上同时成立。三旋钮里唯一代价是 `Link` 不能对外公告（XAML 把三者全选当无偏好）。裸调 DoDragDrop 的结论依然成立：不要复活 `NativeFileDragOut` 这条路。诊断基建已沉淀：QueryContinueDrag 计数打点 + 15s 看门狗 + `DropProbe.exe`（`D:\VMShare`，外部读者探针）。

工程教训：**运行中实例锁 exe 会让 dotnet build 显示"0 个错误"但静默不写文件**——重启验证前必须核对 exe mtime > 源码 mtime（本次两轮假验证的根因）。

#### 8.1.1a 未文档化假设与探针结论（2026-09-20）

- `CoGetCallerTID` 官方文档只覆盖"正在服务 COM 调用"两态：S_OK=同进程调用方、S_FALSE=跨进程调用方（跨机器也是 S_FALSE）。**未文档化的"无 COM 调用上下文"实测返回 S_OK 且 tid=0**（裸线程与已初始化 MTA 均如此，见 `tests/DeskBox.Tests/CoGetCallerTidProbeTests.cs`）——因此进程内直调（WinUI 掩码推导、内部直调）不会被误判为跨进程，`S_FALSE` 的唯一来源就是真跨进程调用。探针以契约测试形式锁定；WinAppSDK 升级后若拖拽允许集合异常，先跑该测试与 verbose 日志（`[DragStart] Preferred DropEffect read in-process`）定位。
- 官方明示 `CoGetCallerTID` 返回信息可被伪造、不得用于安全决策。本用途是 UX 级格式过滤（隐藏与否均不构成提权面），非安全边界。
- 包装器对象由 `FileDragSourceGuardDataObject` 内静态单槽显式持有到下一场拖拽，CCW 生存不再单独依赖 WinUI `IDataObjectProvider` 未文档化的 AddRef 合约。
- 行为决策记录（已随 StartDragAsync 方案收敛）：不再写 Preferred DropEffect，拖出到资源管理器的默认操作与 Explorer 原生拖拽一致——同卷移动、跨卷复制；包装层不再做跨进程格式隐藏，`CoGetCallerTID` 从拖拽路径移除（探针测试保留作回归）。目标按公告的 `Move` 自行完成移动时，Ctrl+Z 是"撤销移动"，文件回到原处。（2026-10-14 勘误：本段"决策记录"已过时——`Items_DragStarting` 现按托管拖出档位写 `RequestedOperation`（Move/Copy 档非 None），XAML 将其落成 OLE preferred effect，`FileDragSourceGuardDataObject` 实现单效 Preferred 的存/答；以 §4 的现行语义为准。）
- 治理项（2026-10-14 更新）：`RegisterClipboardFormat` 在 `NativeDropDescriptionWriter`/`NativeDropTarget`/`ShellClipboardHelper` 的 3 处历史私有声明仍在，但 `PlatformInteropExpectedViolations` 清单已随批 31（2026-09-26）清空成**硬零法**——不再存在"预算"可缩，原文的收敛挂钩作废；新代码一律走 `Win32Helper.GetRegisteredClipboardFormat`。

### 8.2 `.lnk` 在格子/叠放内排序后进入回收站

根因：内部元数据操作最终返回 `Move`，原生 Shell 数据对象把它解释为真实文件移动成功并清理源快捷方式。

正确做法：内部 `Drop` 完成结果永不返回 `Move`（`DragOver` 的临时反馈在 Win10 单值允许集下可以为 `Move`，因为允许集里没有 `Copy` 可答——完成阶段的 `None` 是阻止清源的唯一闸门）；文件拖拽经 `AllowedOperations`（Win11 为 `Copy | Move`，Win10 为单值）发起，内部路由优先用 `Copy` 作反馈（叠放为 `Link`）。

### 8.3 修好误删后，文件无法加入叠放或从弹窗移出

根因：为了避免 `Move`，目标只接受 `Link`/`Copy`；但 `ListViewBase` 内置拖拽实际只向目标暴露 `Move`（`AllowedOperations` 不生效），于是 WinUI 不再路由 `Drop`。`StartDragAsync` 发起后内部目标始终能看到 `Copy`，临时 `Move` 通道随之删除。

正确做法：拆分“反馈操作”和“完成操作”，并且只对已识别的 DeskBox 内部载荷在反馈阶段接受 `Move`。

### 8.4 插入高亮出现，松开鼠标却没有排序

根因：WinUI 偶发漏发 `Drop` 或完成回调顺序异常，高亮只证明收到过 `DragOver`。

正确做法：保存最后有效位置，并以窗口坐标、目标高亮和内部已处理状态为边界，在鼠标释放时单次恢复提交。

### 8.5 连续拖拽时路由识别成上一次的源格子/源叠放

根因：载荷缓存只在根 `DragEnter` 初始化，子目标直接收到处理过的 `DragOver` 时复用了旧快照。

正确做法：每次拖拽生成 `DragSessionId`，每次 `GetDragPayload` 都验证缓存归属。

### 8.6 文件已拖回格子，却被旧的移出任务从界面删除

根因：延迟任务使用早期“路径缺失”快照直接裁剪集合。

正确做法：实际裁剪前再次检查路径，并只移除当前仍然不存在的项目及其叠放覆盖。

### 8.7 空状态刷新滞后

根因：空状态依赖延迟更新的 `VisibleItems` 叠放投影。

正确做法：以源集合 `Items.Count` 为准。

## 9. 一定不要踩的坑

1. 文件源对外公告 `Copy | Move`、不给偏好；DeskBox 永不根据 `DropResult` 或完成回执删除源文件。内部文件移动由目标服务执行，不通过 OLE 回执授权源端删除；不要把"目标按公告 Move 自行移动"与"DeskBox 代删"混为一谈。
2. 不要给文件拖拽 `SetText`，否则 Electron 拖放区不再把它当文件。
3. 不要让内部 `DragOver` 或 `Drop` 在任何路径上返回 `Move`。
4. 不要给文件表面恢复 `ListViewBase.CanDragItems`/`DragItemsStarting`：那条路径不写 `AllowedOperations`，会重新引入"允许集=偏好"耦合与 Win10 操作菜单。文件拖拽一律容器级 `CanDrag`（`StartDragAsync`）。
5. 不要只在 `Root_DragEnter` 清理或验证载荷缓存。
6. 不要把“出现高亮/插入线”当成元数据已经提交。
7. 不要在 `DragOver` 就设置“内部已处理”；只有 `Drop` 或受边界保护的释放恢复真正提交后才能设置。
8. 不要把 `_activeDragHandledAsStackMembership` 当成仅表示叠放加入；它当前保护所有内部编排免受源端清理。
9. 不要在内部加入/移出/排序时调用文件移动 API。
10. 不要在真实跨格移动完成前返回 `Move`，也不要在 DeskBox 已完成移动后再让 Shell 二次清理。
11. 不要对 `.lnk` 在 UI 线程同步调用 Storage broker；优先原生 Shell 载荷。
12. 不要用弹窗成员列表的坐标配合主窗口句柄判断鼠标位置。
13. 不要让根表面插入线覆盖文件夹或叠放子目标；子目标存在时必须禁止根排序恢复。
14. 不要用叠放投影集合判断格子空状态。
15. 不要只测试一种叠放打开模式，或只在 Win11 上验证 Win10 行为。
16. 不要以管理员身份启动 DeskBox 做拖放验证；不同完整性级别会让 Windows 拦截拖放，形成错误结论。
17. 不要为了允许父子映射而修改或绕过 `EnsureSafeDirectoryTransfers`；只放宽格子映射关系校验。
18. 不要让托管目录复制递归进入 junction、符号链接或已经访问过的物理目录。
19. 不要把“两个格子 ID 不同”等同于一定需要文件传输；源项目已经位于有效目标目录时必须无操作。
20. 不要为了让文件夹快捷方式可在格子内打开而把 `WidgetItem.IsFolder` 改为 `true`，也不要把拖拽源路径从 `.lnk` 改成 `TargetPath`。
21. 不要让**纯进程内状态**走 OLE 自定义格式通道运输（2026-10-13 待办色点教训）：自定义格式只能异步 `GetDataAsync` 读——Drop 处理器里无 deferral 的 await 会让拖拽事务在首 yield 即终结（丢/挂），且空自定义格式拖过桌面会被 shell 逐格式探测（0x8000FFFF 崩溃向量）。内部状态一律走进程内字段（`_draggedTodoItemId`/`_draggedColorMarker` 先例），`DragStarting` 只保留拖拽视觉（RequestedOperation/Title）。
22. Drop/DragOver 处理器里任何 `await` 都必须先 `e.GetDeferral()` 并在 finally `Complete()`；ResilientJsonStore 只重试一种 IOException，存盘类异常须在处理器内 catch 收敛，不得逃出 async void 升级为全局故障。
23. **回调帧内禁止同步重操作（2026-10-14 回调隔离批立法）**：任何消息/OLE/win32k 回调上下文（subclass proc、`IDropTarget` 四方法、H.NotifyIcon 托盘命令、输入路由事件处理器）内不得同步执行窗口手术（建窗/Show/`SetWindowPos`/`SetForegroundWindow`/`SetWindowLongPtr`，雷达另跟踪 `EndDeferWindowPos` 批次提交点）、进程启动、跨进程 COM 写或大额磁盘物化——一律经 `UiDispatch.PostAfterCallback`（或入口 `Task.Yield`）推迟出回调帧，激活类落地单元配"窗口存活 + 前台快照"守卫（纯落床/重排类落地只需窗口存活与拖动守卫，无前台语义）；`CallbackDepth` 雷达计数诊断可见。**双轨细则**：XAML 处理器沿用第 22 条 GetDeferral 铁律；原生 `IDropTarget` 没有 GetDeferral 对应物——返回值、手势认领（`MarkLaunchConsumedForCurrentContent` 先例）与乐观完成效果必须在回调内同步决定，只有副作用出回调，失败不得倒写已发出的回执；虚拟文件物化走官方异步协议（`IDataObjectAsyncCapability`：回调内 `StartOperation` → 早返回 → 单 MTA worker 提取 → 完成后写回执 + `EndOperation`），最终回执由 worker 在其自己的套间写。**豁免与注记**：①热键 `ReleaseAllModifiers` 为输入族豁免（同步保留：推迟释放会误放用户正持有的新组合）；②`TryEnqueue` 落在 CoreMessaging 私有窗口，模态循环（TrackPopupMenuEx/DoDragDrop/SC_MOVE）期间**照常派发**——推迟语义是"下一个派发点"而非"模态结束后"，落地单元必须对"菜单开着/拖拽进行中"安全；只有自起裸 DoDragDrop 的输入岛饿死（8.1.1b）与 UI 线程被占死是泵死亡场景；③深度计数雷达是同线程探测器，跨线程同步 `SendMessage` 类违规（后台线程 `SetWindowPos` 打到 UI 线程窗口）不在其视野，靠评审把关；④拖拽悬停反馈所需的跨进程**读**（DragEnter 的格式探测、路径 hints 的单次 `GetData`）豁免——OLE 悬停反馈机制本身建立在跨进程读上；本条禁止的是窗口手术、进程启动、大额物化与协议外的写；⑤协议强制的 `StartOperation`/能力协商出站 RPC 在回调帧内同步执行（官方时序唯一窗口）——**挂起/冻结源进程**（AV 隔离、沙箱冻结、调试器 attach）会以此冻结目标 UI 线程，与 Explorer 对同型源的行为一致（无浅修空间；受控对照实验在修复方案矩阵 C 附行）；⑥`ContentWidgetWindow_Activated` 的 `ReassertRaisedWidgetGroupAfterDeskBoxActivation` **必须保持同步**（2026-10-15 自激环事故立法）：`SetForegroundWindow(active)` 的 no-op 性依赖"事件处理器同步运行时 active 就是前台"这一时序，推迟一拍会把 no-op 变成真实前台切换并自生成 Activated 事件流（3ms 环扫全组）。**通用判别式：凡"以激活/抢前台收尾"的处理逻辑，推迟前必须先回答"落地时前台还是它吗"——答案不确定即禁止推迟。**反钉测试 `ContentWindowActivated_ReassertStaysSynchronous` 禁止该 handler 再出现 PostAfterCallback。

**已知未收口清单（2026-10-14 回调隔离批第五轮终扫登记，均为存量、非本批引入；⑨⑩为 2026-10-15 第六~八轮生态审查增补）**：①设置页自启开关——`DirectStartupTaskBackend` 的 `Process.Start(schtasks)`+`WaitForExit(10s)` 跑在 ToggleSwitch 回调帧内（涉渠道门控域，须专项修）；②叠放弹窗标题重命名——DoubleTapped 帧内建窗+Show+Activate、提交帧内关窗+回交激活（主表面同交互已走 TryEnqueue，见 ContentWidgetWindow.Commands.cs）；③设置页三个辅助窗口入口（立即整理/新手引导/识别显示器）Click 帧内建窗+Show+Activate——托盘同名入口已有 yield；④"选择叠放内容"菜单项 Click 帧内同步开弹窗（主开关路径已 TryEnqueue）；⑤部件关闭按钮 Click 帧内 SW_HIDE+床位手术；⑥窗口阴影"打开系统设置"按钮 Click 帧内 Process.Start（常量目标名）；⑦`WritePerformedDropEffect` 用 `AllocHGlobal` 冒充 HGLOBAL 介质的旧债（2026-09-30 审计 #17，异步 worker 跨套间场景放大了它）；⑧WM_MOUSEACTIVATE subclass 与 XAML routed 处理器同一次点击各跑一遍 256 窗覆盖遍历（结果共享待做）；⑨**新 Outlook 型源**（CF_HDROP 通告+延迟渲染+实现异步能力，含 Chromium 内核 WebView2 宿主）：被 `hasHDrop` 路由排除在异步协议外，Drop 内同步 `GetData(CF_HDROP)` 得 DV_E_FORMATETC → 悬停可放、落下静默无效（存量缺陷；官方修法=同步路径 CF_HDROP 提取失败且 `GetAsyncMode()==TRUE` 时改道 `HandleDeferredDropMaterialization`，worker 端补 CF_HDROP GetData——**待拍板后实施**）；⑩**VirtualDrops 孤儿清扫缺失**：进程死亡（更新安装/崩溃/硬终止）、`ExtractVirtualFiles` 逐文件 catch 之外的逃逸异常、Shutdown 丢弃完成单元、widget 关窗解绑事件后已入队任务的完成即弃——四类路径都会留下已物化的 temp 文件且全仓无启动清扫（修复设计=空闲期清扫，**须避开启动账本风暴熔断器管辖的启动窗口**）；⑪**XAML 输入帧存量同步开窗面**（与②③同族，统一治理）：widget 右键菜单"配置"入口（WidgetSettingsMenuHelper 的 Click→ShowSettings/ShowGlanceSettings）、设置页"查看更新日志"按钮等；⑫**完成侧反馈缺失与慢存在性检查——已于 2026-10-16 系统监控格批 0 收口（保留编号以稳定交叉引用）**：异步提取为零→`ZeroResultDropBlocked` 事件+toast（键 `Widget.ImportZeroResult`）；import-busy 拒收（同步与延期两处）→`ImportBusyDropBlocked`+toast（键 `Widget.ImportBlockedBusy`）；两键已 12 语言，事件处理器一律 `TryEnqueue` 出回调帧（§23）。`LaunchDroppedFilesDeferred` 的 `File.Exists` 门槛已移后台线程（`LaunchDroppedFilesResolved` 留 UI 线程串行）。同一批把严格 AppsFolder 分类泛化为**可物化命名空间项**：非 AppsFolder 的 CIDA 项有文件系统路径→HDROP 等价导入、纯虚拟→`CreateShellNamespaceShortcut`（SetIDList）物化，均在 UI STA 一拍执行（禁走 MTA worker，见 §8.1.1a 同族事故记录）；无可用标识的项（空/超长/含 NUL 解析名）仍如实 no-drop；zip-folder 拖拽按同一判定处理（有可物化标识即物化 .lnk，与粘贴路径行为对齐）。**presence-only 假承诺已于 2026-10-15 现场实锤并修复**（开始菜单/zip 拖拽悬停许诺后零产出——DragEnter 已恢复严格 AppsFolder 分类；AppsFolder 提取改回 UI STA 一拍执行——壳命名空间 API 在 MTA worker 上解析为零条目）。雷达计数对⑤会首先报警——这正是它的用途。

## 10. 日志判读

诊断时先按会话关联日志：

```powershell
rg -n "\[DragProtocol\]|\[FileStack\]|External drag-out reconciled|\[FileTransfer\]" DeskBox.log
```

关键阶段：

| 日志 | 关注字段 | 含义 |
| --- | --- | --- |
| `stage=PackagePrepared` | `session`、`popover`、`storage`、`nativeShell`、`requested`、`allowed` | 源载荷是否完整、允许集与偏好是否正确（文件应为 requested=None、allowed=Copy, Move） |
| `stage=CompletionReceiptIgnored` | `format`、`value` | 接收方写回完成回执被拦截（只记录，永不授权删源） |
| `stage=DesktopArrivalSuppressed` | `names` | 外部拖出完成后登记的桌面到达豁免，防止自动整理把新落桌面的文件收回格子 |
| `stage=TargetDecision` | `route`、`sourceWidget`、`sourceStack`、`allowed`、`accepted` | 目标实际采用了哪条路由 |
| `stage=PayloadCacheInvalidated` | 新旧 `session` | 防止跨会话复用；若同一次拖拽频繁出现需继续排查 |
| `stage=SourceCompleted` | `dropResult`、`internalHandled` | 源端是否会进入外部清理/协调 |
| `Recovered internal reorder after pointer release` | `reordered` | WinUI 漏事件后的受控恢复 |
| `External drag-out reconciled` | `removed`、`remaining` | 已按磁盘现状清理源格子 |

危险信号：

- 同格内部路由出现 `SourceCompleted ... dropResult=Move internalHandled=True`；
- 已出现 `TargetDecision route=...`，但完成时 `internalHandled=False`；
- 当前 `session` 却带着上一轮的 `sourceWidget` 或 `sourceStack`；
- 弹窗内取消拖拽后很久又出现来源不明的外部移出清理；
- `nativeShell=True` 的 `.lnk` 内部编排后磁盘路径消失。

## 11. 修改后的最低验证矩阵

每次触碰以下任一文件时，都应重新执行完整矩阵：

- `FileItemDragPackage.cs`
- `FileDragSourceGuardDataObject.cs`
- `NativeShellFileDragProvider.cs`
- `FileSurfaceContent.xaml` / `.xaml.cs`
- `FileSurfaceContent.ItemVisuals.cs`
- `FileSurfaceContent.StackPopover.cs`
- `DeskBoxDragData.cs`
- `DesktopAutoOrganizationSuppressionRegistry.cs`
- `WidgetViewModel.Stacks.cs`
- `WidgetShell.xaml.cs`
- 原生 Shell drop target / 文件传输服务

### 11.1 自动化验证

```powershell
dotnet test .\tests\DeskBox.Tests\DeskBox.Tests.csproj `
  --no-restore --verbosity:minimal -p:Platform=x64
```

重点测试文件：

- `FileItemMultiDragTests`：源操作（Copy|Move 公告、无偏好）、反馈/完成拆分、会话缓存、释放恢复、外部协调和空状态；
- `FileDragSafetyTests`：原生 Shell 载荷、完成回执拦截、虚报 Move 不删源；
- `DesktopAutoOrganizationSuppressionRegistryTests`：拖出到达豁免（指纹/待决/过期/到达证据）；
- `FileSurfaceParityContractTests`：主表面、叠放弹窗、原生拖放和持久化链路；
- `WidgetCompactTrayVisibilityContractTests`：鼠标释放恢复与紧凑窗口状态；
- `NativeDropEffectPolicyTests` 及文件传输相关测试：真实 Copy/Move/Link 决策。
- `FileServiceTests`：源到自身/后代拒绝、junction 别名、递归链接保护和同目录识别；
- `WidgetManagerStorageCleanupTests`：外部父子映射、相同物理目录拒绝及托管目录隔离；
- `OrganizerServiceTests`：同一物理目录导入不得产生编号副本或历史记录。

### 11.2 Win10 与 Win11 实机验证

两个系统都要覆盖弹窗模式和非弹窗模式，并至少使用一个普通文件、一个 `.lnk`、一个文件夹和一次多选：

1. 主表面内部排序，插入线出现后松开即完成，并在重启后保持。
2. 叠放成员内部排序，并在重启后保持。
3. 叠放成员移出到所属格子主表面。
4. 所属格子主表面的独立文件拖入叠放。
5. 一个叠放的成员拖入另一个叠放。
6. 文件拖到另一个格子及另一个格子的叠放。
7. 桌面/Explorer 文件拖入格子及叠放。
8. 格子和叠放成员拖到桌面/Explorer：同卷应为原生移动（原件消失于源目录、出现于落点、Ctrl+Z 为撤销移动），跨卷为复制，修饰键生效，均不出现操作选择菜单。
9. 所有内部动作确认原文件仍存在、回收站没有新增对应文件。
10. Win10 与 Win11 走同一 `StartDragAsync` 路径；左键拖出不出现操作选择菜单；右键拖拽仍按系统语义工作。
10a. 文件拖到微信等第三方：按 Copy 成功接收，源文件不消失、不进回收站。
10b. 开启桌面自动整理时把文件拖到桌面：落点文件在整理周期到达后仍留在桌面，不被收回格子。
11. 移入第一个文件后空状态立即消失；移出最后一个文件后空状态立即出现。
12. 快速跨过文件夹、叠放、主表面和窗口边缘，不残留高亮或插入线。
13. 取消拖拽后不改变顺序、不改变叠放关系，也不在数秒后误删显示项。
14. 建立外部父子映射；父格子进入子根后，两个格子之间拖动到根表面或叠放必须无操作且不生成 `(2)` 文件。
15. 从父格子或 Explorer 把子映射根拖回子格子，必须在创建第一层目标目录前拒绝。
16. 使用 junction 别名重复上述同目录和后代判断；托管复制遇到回指祖先的目录链接必须失败且不留下递归目录链。
17. 从桌面拖 Shell 命名空间对象（回收站、此电脑、控制面板）进格子主表面与叠放：悬停如实显示 Link；落下生成可双击打开的 .lnk（混合多选与文件拖入并存时按粘贴判定去重）；有文件系统路径的命名空间项并入正常导入；不可物化源仍如实 no-drop；构造零成果场景（如提取失败）出现 `Widget.ImportZeroResult` toast；连续快速拖入触发 import-busy 时出现 `Widget.ImportBlockedBusy` toast。

### 11.3 发布边界

- Debug 通过只证明 JIT 路径；GitHub Releases 的 Full Native AOT 安装包必须单独验证。
- Store 与 Direct 使用同一交互代码，但包身份、运行时和更新通道不同；Store 包仍需 flight 覆盖安装与实机拖拽。
- ARM64 静态审计不等于 ARM64 实机拖拽验证。
- 只有日志注册成功、测试通过或构建成功，都不能代替真实鼠标拖放验证。

## 12. 实现索引

| 责任 | 主要位置 |
| --- | --- |
| 源载荷与 `.lnk` Shell 旁路 | `Controls/FileItemDragPackage.cs`、`Controls/NativeShellFileDragProvider.cs` |
| 协议字段与外部载荷读取 | `Services/DeskBoxDragData.cs` |
| 主表面路由、缓存、完成策略、外部协调、空状态 | `Controls/WidgetContents/FileSurfaceContent.xaml.cs` |
| 叠放卡片目标与加入叠放 | `Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs` |
| 弹窗成员排序与释放恢复 | `Controls/WidgetContents/FileSurfaceContent.StackPopover.cs` |
| 叠放投影、成员覆盖和显示单元顺序 | `ViewModels/WidgetViewModel.Stacks.cs` |
| 多选整块排序原语与成员批量脱离 | `WidgetViewModel.Stacks.cs`（`MoveVisibleItemsForReorder`/`TryMoveDisplayUnitOverrides`/`DetachStackMembersForBlockReorder`）+ `WidgetViewModel.SortingAndWatchers.cs`（`MoveItemsForReorder`/`TryComputeItemBlockReorder`） |
| 紧凑窗口鼠标释放恢复 | `Controls/WidgetShell.xaml.cs` |
| 原生 Explorer 拖入 | `Helpers/NativeDropTarget.cs` 及关联文件传输服务 |

以后若需要重构，先保留本文的两层模型、反馈/完成分离和路由矩阵，再逐步替换具体事件实现。不要以“统一操作值”或“统一清理逻辑”为目标把内部编排和真实文件传输重新合并。
