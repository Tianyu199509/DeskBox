# 架构统一候选增量复审（2026-09-24）

本轮审阅对象为隔离目录 `C:/Users/simon/.codex/worktrees/deskbox-surface-group/wingezi` 的 `codex/architecture-surface-group-segment` 分支。HEAD `f3f357f1` 是第 1–21 批的本地检查点；工作区增量承载 A+B/C/D 评审修复、第 22 批以及审计契约更新。原共享目录 `D:/project/wingezi` 没有被本轮修改。审阅开始时，增量为 36 个已跟踪文件和 4 个新文件；本文为新增的审阅记录。

## 范围与判断

| 段 | 增量内容 | 复核结论 |
|---|---|---|
| A+B 功能运行时与设置写入 | 远端备份操作注册、取消和退出排空；未确认 scoped restore 标记阻止下次启动自动应用；备份与 QuickCapture 退出步骤有上限，裁剪支持取消；Todo 非有限字号拒写，随记窗口内开启记录保持先保存后启动监听 | 边界与用户可见时序一致。内部待恢复 marker 新增可空确认字段；旧 marker 缺字段时保留原合并语义，新 marker 未确认时丢弃，测试覆盖两种路径。超时后不合作的 I/O 仍可能短暂运行，退出日志会指出这一点。 |
| C 窗口登记 | 拒绝零 HWND 占用格子 ID 或窗口句柄声明 | 测试验证拒绝后仍可正常登记已就绪窗口。 |
| D Surface/格子组 | 合并保存成功而回滚保存失败时，对幸存目标做精确声明转移；无目标时重新提升或隔离并留待重启重建；复用拆离回滚按恢复后的 `IsVisible` 退役替换窗；缺失活动成员配置显式失败 | 注册声明测试、双故障真实窗口测试、无目标隔离后重启和真实拖离回滚均通过。隐藏成员的持久化前提经启动归一化证伪，详见下节。 |
| 第 22 批 | QuickCapture 两个字号覆盖值改由协调器写入，设置页仍展示继承后的有效值 | 与原共享目录核对的五个相关文件逐字节相同；协调器及真实设置门面往返测试、设置页滑块和重启验收通过。 |
| AOT 审计契约 | 修正主 ViewModel `Dispose` 子串误匹配，并钉住 Todo 提醒“入口包装＋异步核心”的现有结构 | 完整 Native AOT publish/link 和审计通过；没有通过放宽检查数量或忽略源文件来消除误报。 |

增量没有触碰版本号、安装器、Release 文档、多语言资源或正式分发清单。`MemoryDestroyProbe` 文件及调用在候选中均不存在。原始第 1–21 批检查点的范围仍应按其独立审阅记录判断；本轮检查的是该检查点之后的增量，并非重审整个应用。

## 复用拆离隐藏成员假设

评审报告把“可见组中有一个 `IsVisible=false` 成员”作为 P2-3 的设备验收前提。但 `WidgetGroupSettings.Normalize` 在读取布局时把每个成员可见性对齐组级值，`ApplyGroupLayoutToMember` 和 `SetWidgetGroupVisibility` 也维持这个约束；复用窗口的拖离分支还要求物理宿主可见。现有 `Normalize_SynchronizesMemberVisibilityWithTheVisibleGroupSurface` 测试复跑 1/1 通过。

独立数据目录被故意写成“组可见、一个成员隐藏”；启动后布局自动修复为两个成员都可见，组和两份样例文件完好，35 个启动步骤无失败。因此无法通过有效持久化布局构造该状态。真实拖离故障已命中回滚生产路径；`IsVisible` 传值保留为对异常瞬时内存状态的防御性修复。若将来发现正常 UI 路径能制造组/成员可见性分歧，再为该具体路径补设备测试。

## 验证与交付边界

- 统一候选全量 x64 测试 4,226/4,226 通过；最后把“先保存后启动监听”的断言加强为读取磁盘启用值，随后定向 1/1 通过。`git diff --check` 通过。
- Release AOT audit+smoke 条件构建 0 错误；真正的 x64 Native AOT publish/link、完整审计和隔离根启动通过，审计记录 `sourceStableDuringAudit=true`、`AlwaysThrowCount=0`。后续只改了测试断言与文档，没有改应用代码。
- x64 测试 MSIX 静态审计、包身份/哈希、Medium 权限、可交互设置窗口均验收；测试包已卸载，临时证书在当前用户与整机信任存储均不存在。
- 候选仍是本地未提交增量；没有远程 PR/CI。`f3f357f1` 本身是 100 文件的回退检查点，不宜直接当作单一小 PR。已有 A+B 本地提交 `61f05c5d` 可作首个可编译段，C、D 与第 22 批仍需按依赖顺序提取和逐段审阅。

## 下一批

1. 以 A+B `61f05c5d` 为起点对齐本轮 A+B 修复和对应测试，确认备份 marker 兼容、退出与随记交互的最终 diff。
2. 在该基础上提取 C 窗口登记、再提取 D Surface/格子组；第 22 批字号与 AOT 审计契约各自保持独立增量。跨段的 `App.xaml.cs`、`WidgetManager.FeatureWidgets.cs` 和设置页 partial 必须按 hunk 核对，不能整文件覆盖。
3. 每段形成可审阅差异后执行各段定向/full x64 与最终 AOT 门禁；远程 CI、PR 或主干集成留在用户明确要求提交/推送时执行。云同步、设备层 store、Generic Host 和物理拆工程继续按原路线图的立项条件推进。

## 分段对齐结果（本轮续做）

- A+B 工作树 `C:/Users/simon/.codex/worktrees/deskbox-feature-segment/wingezi`（HEAD `61f05c5d`）已补入最终 QuickCapture 裁剪取消/退出边界、随记窗口启用记录的先保存语义与 Todo 非有限字号拒写，未混入第 22 批字号。全量 x64 **4,167/4,167**、AOT 条件构建 0 错误、canonical Debug 0 错误；独立根启动 PID 31512，36 步 0 degraded / 0 failed，已结束。
- C 工作树 `C:/Users/simon/.codex/worktrees/deskbox-window-registration/wingezi`（HEAD `61f05c5d`）已逐文件对齐该 A+B 最终增量，同时保留自己的内容/文件窗口登记改动和零 HWND 防护。与统一候选比较，`WidgetManager.cs` 尚缺的正是 D 段 Surface 提升候选参数，未提前复制。全量 x64 **4,177/4,177**、AOT 条件构建 0 错误、canonical Debug 0 错误；独立根启动 PID 34056，36 步 0 degraded / 0 failed，已结束。
- A+B 与 C 的各自审阅说明已更新，并同步进统一候选。两段都保持本地未提交/未推送；不存在运行中的 DeskBox 测试进程。最后对统一候选的“先保存再启用监听”测试加强为读取已落盘布尔值，定向 1/1 通过，未改应用代码。

下一步的实际交付工作集中在把 D 与第 22 批按上述顺序叠到可审阅的提交链上，并由远程 CI 复验；当前 `f3f357f1` 的整批回退锚点仍不宜直接作为一个 PR。没有明确的提交/推送指示前，继续保留本地差异和现有回退锚点。

## 2026-09-25：获授权后的提交链

用户明确授权提交、推送和创建 PR 后，按可编译依赖顺序完成：

| 段 | 基线 | 提交／审阅入口 | 本地验收 |
|---|---|---|---|
| A+B | `main` 的 `44e7a0d4` | `61f05c5d` + `c966a9a0`；PR [#423](https://github.com/Tianyu199509/DeskBox/pull/423) | x64 4,167/4,167，Native AOT publish/link 与审计通过 |
| C | A+B 的 `c966a9a0` | `6728c2e4`；PR [#424](https://github.com/Tianyu199509/DeskBox/pull/424) | x64 4,177/4,177，AOT 条件构建通过 |
| D | C 的 `6728c2e4` | `a3809579`；PR [#425](https://github.com/Tianyu199509/DeskBox/pull/425) | x64 4,224/4,224，Native AOT publish/link 与审计通过 |
| 第 22 批 | D 的 `a3809579` | `codex/architecture-quickcapture-text-size-review` 分支，独立于 D 代码 | 最终叠加态 x64 4,229/4,229，Native AOT publish/link 与审计通过；canonical Debug 恢复随记设置，35 步 0 degraded / 0 failed |

第 22 批叠加后的整个 `src/DeskBox`、测试及脚本与先前实机验收的统一候选按归一化文本逐文件比对，差异为 0。`MemoryDestroyProbe`、版本/安装器/正式发布物料仍未进入提交链。此前关于三个本地工作树“未提交”的叙述是 9 月 24 日的历史快照，以本节状态为准。后续只需逐个审阅 PR 和核对远程 CI，再决定是否按 #423 → #424 → #425 → 第 22 批的顺序合并；本轮未获合并或正式发版授权。
