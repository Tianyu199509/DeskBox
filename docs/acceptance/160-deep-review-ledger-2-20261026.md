# 160 深审第二轮 — 未覆盖代码域账本（R15 起，2026-10-26）

- 承接：第一轮（`160-deep-review-ledger-20261025.md`，R13 窗口 14 笔提交）已覆盖 1.6.0 设置拆分全链（D1-D20）+ 1.5.9 功能域（E1-E14）。本轮=同一流程（r1 读码走查→r2 冗余/临时方案评估→r3 历史修复对抗复审→r4 修复→r5 真机验证）走**其余代码**。
- 红线不变：零推送零发版；自动化一律隔离根；测试禁 AnyCPU；每批门=dev-restart+全量测试+quickcheck。

## F 域清单与进度

| 域 | 内容 | 状态 |
|---|---|---|
| F1 | 启动/关机序列、单实例、激活路由、AppLifecycleRecoveryWatcher、lifeline | 🔄 |
| F2 | 桌面小组件核心：WidgetManager 全 partial（ZOrder/FeatureWidgets/Groups/Storage/MoveAll/NewWidgetPlacement/CapsuleArrangement/QuickLook）+WidgetShell | 🔄 |
| F3 | 文件格家族：FileSurfaceContent 全 partial（拖拽/选择/图标/键盘/视觉）+FileItemSurface+叠放族+FileService.ShellTransfer+NativeDropTarget | 🔄 |
| F4 | 整理与规则：OrganizerService+DesktopAutoOrganizationWatcher+OrganizationHistory+RestoreMachineLocalPathRebase+MoveAll undo | ⬜ |
| F5 | widget 内容：TodoWidgetContent 全部+QuickCapture runtime+MusicWidgetContent(SMTC)+Weather+GlanceWidgetStore | ⬜ |
| F6 | 搜索全家：SearchPopupWindow+SearchEngineService+EverythingSearchService+SearchHotkeyService+GlobalHotkeyService 核心+ResizeGuideOverlay/snap+DesktopDoubleClick | ⬜ |
| F7 | 备份引擎+更新器：DataBackupService+CloudBackupService+BackupRuntime 主侧+AppUpdateService 核心+DeskBox.Updater 进程+installer iss | ⬜ |
| F8 | 平台层：Win32Helper+ThemeService+UiDispatch+DragDropPermissionService+DisplayTopology+JumpList+StartupService+WidgetTitleIconAssetStore+LocalizationService 核心 | ⬜ |
| F9 | 托盘+主壳杂项：App.Tray+onboarding 接线+工作集修剪+lifeline 细节 | ⬜ |

## F1：启动/关机/单实例/激活路由（修复完成，3ae9be52）

探员判定：结构成熟度高（43 步管线/看门狗阶段推进/关机记忆化+双级兜底），无 P0/P1。**P2×5 全修**：
1. 关机 settings-flush 唯一无界 IO 步 → Bounded(15s)（卡死时 backstop 永不武装的僵尸镜像窗）。
2. StartSettingsIpcServer 裸解引用 GlobalHotkeyService（R10 活解析只修搜索侧=修一半）→ null 守卫。
3. desktop-organization-target-availability 探测被管线 await（注释宣称 after startup）→ 真 fire-and-forget（原可阻塞启动尾部+90s 看门狗无人值守退出循环=#576 新入口）。
4. ENDSESSION 双相无条件全量落盘+不 Drain 日志 → HasPendingSave 快路径+尾 DrainLogQueue。
5. 陈旧 pending 激活文件启动期不消费（裸激活数小时后复活旧参数）→ else 分支同走 HandleExternalActivationAsync。
P3×6 登记不修（OnLaunched watchdog no-op 注释名实不符/三处手写 MarkStartupProgress 冗余/WriteSummary 三早退跳过/单槽文件并发可接受/subclass 失败无兜底极低概率/通知激活预热注释）。

## F2：桌面小组件核心（修复完成，3ae9be52 + 进闸后续批）

探员核心交付=**33 处未进闸写点清点与分类**（D4 登记债的细化）：
- **P1（已修）**：合组回滚跨 await 用过期 settings 局部做整域赋值=引用共享不覆盖的唯一真丢写形态（回滚静默蒸发）→ 三写点新鲜取 Settings+进闸。
- **P2（已修两件）**：DefaultManagedStorageRootPath 未入 PreserveMainOwnedState（宿主提交×迁移落盘竞态回滚根路径→孤儿检测降级）→ 入清单；迁移 PreviewAsync 缺 RequireStorageDestinationAncestry（junction 根晚失败）→ 补守卫。
- **P2（进闸批，单独批）**：热路径 H1-H6（拖拽提交/胶囊重排/组员切换/拓扑写）+冷路径（创建/重置/去重/孤儿恢复）33 处机械进闸——消灭「锁内序列化×UI 写交错→伪保存失败→组操作伪回滚」。
- 正向判定：ZOrder 镜像/看门狗/租约全活跃无死码；WidgetShell 无补丁堆叠、零订阅泄漏；胶囊×保留自洽；1.7 债「组列表跟随」主体已被 R2 落地（台账口径=残余空写噪声）；Storage 白名单/junction 硬化完好。
- P3 登记：组操作无用户 undo（与 MoveAll 不对称）；WidgetShell 单文件 5750 行可拆；manifest 三处天花板松。

## F3：文件格家族（P1-2 已修，3ae9be52）

探员判定：ShellTransfer 现代引擎健康无 legacy 残留（vtable 8 槽位对拍 SDK 头全对）；NativeDropTarget=全仓质量最高文件（源生成 COM+回调隔离+异步拖放协议+手势守卫全对）；叠放族/规则编辑器/弹窗生命周期健康。
- **P1-2（已修）**：原生拖入存在性探测 UI 内联 → Task.Run（与路由通道 GetSurfaceDropFilesAsync 同款）。
- **P1-1（登记债，需测量决策）**：拖出起点（DragStarting）UI STA 同步 Shell 束（逐条 stat+SHParseDisplayName+broker 回退+属性剥还）——6314ecfa 留下「测量后再重构」显式债，storageBrokerMs 只测 broker 腿且未闭环。待办：protocol log 分布复盘+决策记录（做或不做都写明）。
- P2 登记：跨 partial 状态所有权/reset 网络文档缺失（25+ 行为契约测试缓解）；OpenWith 枚举无缓存同步执行（与 ShellNew 5min TTL 标准不一）；首拍 IsUnsafeFolderDrop 同步 stat；StackPopover.cs:2304 机械编辑残迹；AreSameVolume junction 例外补注释。

## F4：整理与规则族（探员完成，文档/死码修于 4b00a24c）

无 P0/P1。规则求值/undo 收据拓扑/历史容量判完整（身份收据+WAL+Checked-save 三层契约每条 journal 清除边界一致）。**P2-1 已修**：前向恢复静默丢弃「无身份」条目 vs MatchesSnapshot 文档承诺相悖 → 文档对齐实际行为。**P2-2 维持 R8 债**：跨机还原 rebase 不覆盖 Everything 路径与 Glance 图片路径（类文档有豁免理由；建议最低成本=Everything 配置路径不存在时回退自动发现，非扩 rebase）。P3×2（MergeRetryHistory 窄窗双计/journal 逐项全量写=语义正确性能债）登记。死码 SaveChecked 删。

## F5：各 widget 内容（探员完成，P2-1 修于 1fbb6372）

**P2-1（已修，C3）**：todo.json 双写者（TodoReminderService 通知侧 load-modify-save vs 活跃 widget VM 全量保存=丢更新竞态，完成标记可被静默回滚）→ 活跃 VM 存在时通知侧完成/贪睡路由进 VM 单写者（FindItem/SaveAsync 放 internal+App 装配 tryGetLiveTodoViewModel）。P3（MSN 公开 key 硬编码）登记。QC 剪贴板 lease 幂等/Music SMTC 五事件轻重路径+世代防旧/天气背避+双源降级/Glance 单例字典+写闸均判无发现。

## F6：搜索全家（探员完成，P1+P2×3 修于 4b00a24c/1fbb6372）

**P1-1（已修，C1）**：Everything Query(1) 无界阻塞持全局 _nativeGate=挂死 Everything 使文件搜索永久停摆（UI 不冻但功能无感失联）→ Query(0)+IsQueryAvailable 10s 有界轮询+超时 StopQuery 走既有 IPC 失败分类。**P2-2 已修**：搜索热键 _isInvoking 重入闸丢弃快按二次（toggle 半边被吞）→ 合并重放（pendingInvoke 至多一次）。**P2-3 已修**：主热键手势占用检查只覆盖保留手势（与搜索侧不对称）→ 任意手势对称。**P2-4 已修**：121 处节标题 mojibake+孤儿注释。死码 GetRecentNotes/GetUpcomingTodos 删（零调用+绕服务层）。**P2-1 登记**：批量全部打开无上限（Ctrl+A 数百内容窗连发；建议 15 上限确认）。债⑯ IME 现状核实（未实施，维持）。第三套手势格式化器登记（hint 路径硬编码英文）。DesktopDoubleClick/ResizeGuide 判高质量无发现。

## F7：备份引擎+更新器+安装器（探员完成，P2×4 修于 4b00a24c）

正面：快照原子性/恢复事务双确认/manifest 三预算按实际膨胀字节/更新器 30 分钟超时 kill 链/安装器 13 语言三处一致——防御纵深判仓库最强档。**已修**：嵌套 summary、卸载静默中止补原因日志（VERYSILENT 空收纳目录默认 No=无声无操作无日志）、快照 prune 失败降级日志（归档已落盘不再整体报 Failed）、双 rename 失败带出 rollback 路径、WebDAV 取消 PUT best-effort 删半传远端件。登记：Dependencies.iss 双份仅架构 token 差异（平行维护可接受）。

## F8：平台层（探员完成，P2×2 修于 4b00a24c/1fbb6372）

无 P0/P1。死 P/Invoke 扫描零命中（50+ 包装逐一对拍）；DirectStartupTask 四 vtable 槽位对拍本机 SDK 头全对；启动家族 schtasks 备份-回滚-验证链完整。**P2-1（已修，C2）**：T() 每串 1-2 次注册表读（默认 System 下×~1000 调用点，Store 渠道全量命中）→ 有效文化缓存+双失效点。**P2-2（已修）**：资产店陈旧删除无重试（在飞解码句柄=选图静默失败）→ 同款 50ms 重试+best-effort。P3×5 登记（UiDispatch 旁路族门户收口清单/MSIX 裸 AUMID 待 Store 真机验证/拓扑重试耗尽日志口径/双份显示器枚举/14 懒加载属性+语言清单四重复可收敛）。EnumDisplayDevices 双声明合并、AppRuntimeHealthSnapshot 死字段族登记择机。

## F9：托盘+主壳杂项（探员完成，P2-1 修于 4b00a24c）

无 P0/P1。托盘回调隔离/二窗降级链/修剪双策略/通知激活 register-then-read/文件夹选择器 owner 链全判正向。**P2-1（已修）**：语言切换不重配 Jump List（任务栏右键旧语言残留到下次启动）→ OnLanguageChanged fire-and-forget ConfigureAsync。P3×2 登记（gate 重臂入队结果忽略/诊断字段竞态遥测级）。

## 待 Simon 拍板（累积）

1. F6-P2-1 批量全部打开上限（Explorer 同类 15 上限先例）——建议加但涉及交互文案，留拍板。
2. F3-P1-1 拖出起点 UI STA 同步 Shell 束的测量闭环决策（第一轮登记，维持）。
3. （第一轮遗留）R14 冷路径 27 处/⑤位置持久化。

## 债登记（累积）

（空）
