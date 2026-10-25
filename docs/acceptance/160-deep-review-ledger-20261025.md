# 160 全分支深审·合理化重构·真机回归 — 账本（R13 长跑）

- 日期：2026-10-25 起（长程自主批，Simon 批准计划在案）
- 范围：`feat/160-kickoff` 领先 main 全部 79 提交 = 1.6.0 设置拆分线 45 条 + 1.5.9 发布线 34 条
- 目标：逐功能域深挖 2-5 轮——查问题 + 评「冗余 vs 合理」，临时方案按实际需求合理化/重构；C 节债 ①②③④+R14 全部实施；最后本机全量自动化回归
- 红线：零推送、零发版工程；`store-assets/` 永不动永不入库（backup-20261023 含真实用户数据）；自动化一律 `DESKBOX_DEV_DATA_ROOT` 隔离根（真实数据只以副本进场）；测试禁 AnyCPU

## 工作假设（离场前呈报，一次）

1. 每域轮次：r1 读码走查（数据流+时序）→ r2 冗余/临时方案评估（不确定查微软文档/PowerToys 等开源对照）→ r3 对抗复审（git show 历史修复批，查修一半/引入回归）→ r4 实施修复/重构 → r5 真机探针验证。
2. 发现三级：bug（直接修）/冗余可简化（安全即改，重构已获显式授权）/架构债（登记）。根修 vs 浅修真分歧时：默认浅修+登记债+标记拍板，不替离场 Simon 选根修。
3. 每域门：dev-restart 规范构建（dll 时效门+启动窗健康扫描）+ `dotnet test -p:Platform=x64` 全绿（通过=总数）+ aot-quickcheck + JSON 基线棘轮同步。
4. 每域独立本地提交（Simon 已拍板），批次编号从 R13 起（R14 专留同步读取异步化专项）；提交用 commit-with-reflow.ps1，只署 Simon。
5. 否定结论（零调用/不存在）必须 git log -S 全史考古后才进定案。
6. 每 1-2 批记忆锚定；真机矩阵累积制——全部汇总在本账本与最终回归报告。

## 域清单与进度

| 域 | 内容 | 状态 |
|---|---|---|
| 阶段0 | 基线锁定+aot-quickcheck 实测+账本+隔离根启动器 | 🔄 |
| D1 | IPC 协议命令面（141 方法死命令普查/超时表完备/棘轮） | 🔄 |
| D2 | InProcess/Remote 门面对冗余度 | ⬜ |
| D3 | 回声抑制 commitId 机制 | ⬜ |
| D4 | layout 域保留与写通道合理性 | ⬜ |
| D5 | 宿主生命周期对抗复审 | ⬜ |
| D6 | 保存管线 | ⬜ |
| D7 | 管道安全+线程模型不变式+SACL 方案 | ⬜ |
| D8 | #if 老路径+1.7 候选债核对 | ⬜ |
| D9-D20 | 逐设置节深审 ×12 | ⬜ |
| 债① | 保存失败用户可见面 | ⬜ |
| 债② | 布局标量丢写+写命令保序闸 | ⬜ |
| 债③ | 下载节流 | ⬜ |
| 债④ | 管道 SACL | ⬜ |
| R14 | 26 处同步读取异步化 | ⬜ |
| E1-E14 | 1.5.9 功能域重审 ×14 | ⬜ |
| 阶段5 | 真机自动化回归全量 | ⬜ |
| 阶段6 | 收尾（矩阵更新/记忆/最终门） | ⬜ |

## 阶段 0：基线锁定与基建（2026-10-25 完成）

- [x] deskbox-dev skill 载入；batch-lifecycle（长程批/审查对码/深挖纪律）+ build-verify-loop 通读
- [x] 全量测试基线：**5849/5849 绿**（0 失败 0 跳过，汇总行亲验，1m41s）
- [x] aot-quickcheck 实测：基础模式绿；**-Harness 模式红——R10「脚本待修」债当场实锤并根治**。三层考古：①cmd /c 整开关引号经 bash→powershell -File 链穿透（MSB1006）；②改 rsp 后发现 dotnet CLI 自身的 rsp 解析剥值引号再按分号重切（MSB1008，转发命令行实锤裸 token）；③终解=MSBuild 官方分号转义 `%3B`（任何 shell 层不特殊），双模式复验 GREEN（45.6s）。脚本已修（skill 目录），判别式已回流 toolchain-survival.md
- [x] dev-restart：构建 0 错误、canonical dll 时效门过（23:31:23）、PID 18648 规范路径、启动窗健康扫描 0 失败行
- [x] 本账本建立；隔离根启动器 `%TEMP%\deskbox-review160\launch-isolated.ps1` + `stop-review.ps1`（语法校验过）
- [x] 首批提交：docs/acceptance/ 三件 + 本账本

### 阶段 0 附记

- R10 真机自动化验收报告所列 aot-quickcheck 债正式闭环（该报告 :68 行的「脚本本身待修」）。
- 隔离纪律：所有真机自动化一律走 launch-isolated.ps1（env 硬编码进脚本），杜绝 R10 真实数据触碰事故重演。

## D1：IPC 协议命令面（完成，提交 db91f6f0）

普查（agent）+亲验：141 常量 / 124 路由 case / 22 超时项；事件通道 18 条全配对；wire record 22 个零死项。

- **B 类（有发送方无路由，运行时会 unavailable）= 0**——命令面健康，无「过度扩张」证据。
- 死命令 4 个：`widget.is-feature-enabled`（R11 改本地镜像后的路由 case 残留，ever-wired）/ `app.schedule-light-cleanup`、`app.schedule-hidden-cleanup`（never-wired，宿主侧本就是 no-op）/ `ping`（测试+探针专用，保留）。前三者常量+case 已删。
- **超时表漏网 5 个长任务**（R9 超时假失败族同类）：restore.inventory=10m、widget.get-orphans=10m、data.qc-clear(-recent)=2m、app.force-external-recovery=60s——已补。
- 新增 `SettingsIpcCommandSurfaceContractTests` 面棘轮：请求型常量必须有路由 case+生产发送方（ping 白名单）；事件通道必须双向引用；面缩水地板 120。
- 冗余判定：命令面不冗余（每常量都活、与契约一一对应）；固定税见 D2。

## D2：InProcess/Remote 门面对（修复中）

分析（agent）结论：**双壳无逻辑双写**——widget/backup 的 InProcess 侧本就是服务本体；app/data/hotkey/restore 四族双薄壳约 1040 行；Remote 壳 75-80% 机械转发，20-25% 是带批次编号（R4-R12）的刻意偏离（线程规则/缓存规则/失败合成）。逐方法 6 个手写落点（契约/InProcess/Remote/路由/协议常量/JsonSerializable）。

- **已收冗余**：`SettingsIpcShell` 共享壳（NotifyWithoutBlockingAsync ×4 逐字拷贝 + RequestVoidAsync ×5 签名漂移拷贝 → 一行委托，净删约 90 行，单一事实源）。两份 RequestBoolAsync 语义不同（一个只回 ok、一个解码布尔）**刻意不并**。
- **判定：保持手写双壳**（生成器方案会吃掉 20-25% 逐方法策略或回退 R10/R11 死锁修复；复杂度只是换形态）。理由已量化记录，不另立项。
- **缺口登记（债）**：①无 InProcess↔Remote 等价性测试钉（现 loopback 用 Fake，InProcess 改坏 wire 链照常绿）——以「InProcess 侧行为由既有 SettingsService/协调器测试+AOT 冒烟覆盖」为现状辩护，等价性钉成本高收益低，暂不做；②loopback 覆盖洞：widget 24/36（缺 SetFeatureWidgetEnabled/5 个 SendLayoutWrite/3 组操作等）、app 19/26、backup 漏 SaveAsync、update 漏 Clear/Prune——随各节深审（D9-D20）按需补。

## D3：回声抑制机制（修复中）

r1-r2 读码+普查：commitId/AsyncLocal 机制本身健全（R4 作用域化、R5 接线、R7 pending 守卫、R12 竞态全修过；host 侧 commit 严格串行）。

- **真 bug（已修）**：宿主全图提交的「主进程侧保留清单」是枚举式，Backup/CloudBackup 域全部写者都在主进程侧（备份协调器命令、云备份运行时 ticks、还原 rebase），宿主 UI 只读不写——保留缺失导致「宿主 pending save 跳过 reload 窗口内，主进程侧刚写的备份参数/云备份 ticks 被宿主下一笔提交静默回滚」。修=`PreserveMainOwnedState` 共享 helper（Backup/CloudBackup 域 CopyFrom + 胶囊顺序/自由放置集合保留），**提交路径与外观预览路径统一**（预览路径原有独立手抄清单，是 R7 抓过的「修一半」形态——滑杆预览换图后下一笔落盘会把陈旧备份域持久化）。两 slice 补 CopyFrom（沿用 WidgetLayout 引用共享模式）。测试 +2（提交路径+预览路径）。
- **共享域镜像分歧（登记，随 D9/D10 细审）**：主题/热键/双击/搜索路径 VM 做本地镜像+命令双写；命令失败回滚时（如双击激活注册失败）镜像与权威可能短暂分歧，收敛依赖 reload push——各节深审时逐个核对命令应答是否带 applied 值（widget 族已带）。
- 其余普查残留：Maintenance 时间戳写者归属未彻查（D16 常规节处理）。

## D4：layout 域保留与写通道（完成，含债②实施）

r1-r3：四条写命令全链读码（宿主 SendLayoutWrite 权威切片合并+兜底直拉→路由→InProcess UiDispatch 编组写）+ R12 定夺记录考古。

- **设计判定：四命令+域保留=正解不冗余**。layout 域主进程独占（快照剥离+保留清单双保险），宿主只能经窄写命令+权威切片应答；对照 PowerToys 全量 JSON 消息模型，本仓字段级 change record 更精确。1.7 候选债里的「layout-changed 空写噪声/组列表不跟随」维持 1.7 排期不动。
- **债② 病A（布局标量丢写）已实施**：`SettingsService.MutateGraphUnderLock` 锁暴露（R12 定案方向）；InProcess 七个写体进闸（组呈现/组导航默认/布局偏好/胶囊重置/清背景两段式[图锁内收集+锁外删资产文件]/功能开关字典）。重要发现：保留式换图（CopyFrom 引用共享）使布局域跨换图写天然存活——锁的真正价值=消灭「序列化撕裂」窗口（UI 改图 vs 池线程锁内序列化）。
- **债② 病C/F（写命令保序闸）已实施**：SettingsIpcServer 有序道（六写命令族进 FIFO 单消费 lane，读循环按到达序入队=线缆序；bounded 64 背压；其余命令维持并发分发 16 上限）。
- 测试+2：`OrderedWriteCommands_ApplyInWireOrder`（slow 被压住时 fast 不得先应用——无 lane 必红）/`LockedGraphMutations_NeverTearConcurrentSerializations`（150 锁内加格子×15 次并发保存零丢零异常）。
- **登记债（机械后续批）**：WidgetManager ~20 处 UI 图写点（Widgets/WidgetGroups 增删、拖动落位）未进闸——引用共享使跨换图写存活，剩余窗口=与主进程自身锁内序列化的撕裂竞争（拖格子恰逢落盘），窄；后续批逐入口包 MutateGraphUnderLock。

## D5：宿主生命周期对抗复审（完成）

探员七角度猎杀（排除清单=R11/R12 已修项）。B（垂死宿主在途写入：单实例管道+flush await 顺序保证）/C（关机序列：commit 链不经 UiDispatch 无死锁）/F（会话隔离）判**干净**。四个真发现，三修一挪：

- **E（已修，用户可见面最宽）**：新生宿主 boot 期推送三段丢窗（服务端未连/宿主未订阅/窗口未建各一段）——注释自认"pipe is connected"是错的（IsSettingsHostRunning 只验进程）。修=host.boot-ready 协议通知+协调器意图队列（64 上限 FIFO，boot 确认后按序补推；spawn 意图仍走命令线）。
- **A（已修）**：farewell 池线程 vs UI spawn 对 _settingsHostProcess 的 check-then-act TOCTOU——读旧宿主→UI 装新宿主→池线程清字段=新宿主被逐出注册表→身份门拒其管道→闪退。修=Interlocked.CompareExchange 原子化"确认即驱逐"。
- **D（已修）**：Exited 热键自愈跨纪元误伤（旧宿主迟到的 Exited 恢复了新宿主刚挂起的录制）——世代计数守卫 + 宿主 boot 末尾无条件 hotkey.resume（清掉崩溃纪元卡死的挂起，幂等）。
- **G1（挪 D7）**：身份门/驱逐钉的 PID 复用面（看门狗有 StartTime 快照，门没有）——可利用性极低（同用户+pid 彩票），并入 D7 管道安全批统一做（provider 返回 pid+StartTime 双素）。
- 附带小修：stale farewell 日志文案误导（双字段皆空也印"current host still registered"）。
- 风格债登记：_settingsIpcServer 无 DisposeAsync 调用（进程退出 OS 收管，无害）。

## D6：保存管线深审（完成，含债①，提交 052c8964）

装载链（ResilientJson 主/备/默认三源+16 个 Normalize 有序编排+layout 设备层合并+organizer 延后归一化防误删 #553 族）读码判定：结构合理、每步带历史注释、无冗余。RecentOrganizationHistory 确认已迁独立 store（settings 内为清除残留）。

- **债① 已实施**：PersistenceFailed 上报唯一所有权归调用方（sink 返回 false 的拒绝路径统一上报——原先 exception-only+RemoteSink 只落日志，用户在不会持久化的面上继续编辑）；宿主设置窗壳层 SaveFailedInfoBar（复用既有 13 语言键 Settings.Persistence.SaveFailedTitle/Body；成功提交收敛关闭；shell 声明无需画刷钉扎）。主进程侧本就有通知面（App.xaml.cs:4081，5 分钟节流）不动。
- 测试+1（RefusingSink 拒绝必触发）；TodoReminder flaky 重跑即绿（已知名单）。

## D7：管道安全+线程不变式+债④ SACL+G1（完成）

- **债④ 已实施（原生创建路径）**：`Win32Helper.TryCreateSettingsIpcPipe` 原生 CreateNamedPipeW+单 SDDL 描述符（`D:(A;;GWGR;;;<sid>)S:(ML;;NW;;;ME)`=用户独占 DACL+中级完整性 no-write-up 标签，**创建时原子生效**）。三层探针考古：①事后 SetKernelObjectSecurity 需句柄带 WRITE_OWNER（托管创建不给）→失败实锤；②SDDL 别名 MW 非法（ERROR_INVALID_SID）应为 ME；③PIPE_TYPE_BYTE=0x0 非 0x1（0x1=MESSAGE|NOWAIT→ConnectNamedPipe 即时 ERROR_PIPE_LISTENING）；FILE_FLAG_FIRST_PIPE_INSTANCE 在本机原生调用恒 ERROR_INVALID_PARAMETER（弃用，管道名 scope+session 唯一+maxInstances=1+PID 门已覆盖其价值）。真机验证：新码零 Accept failed+宿主全链绿；新增 `SettingsIpcNativePipeCreationTests` 回声钉。
- **G1 已实施**：身份门升级 pid+StartTime 双素（spawn 时快照；垂死宿主从其 Process 对象现取防跨字段配对竞态；>500ms 漂移拒连）。provider 签名改为 identity 元组（无测试用旧签名，零破坏）。
- **线程不变式已钉**：`SettingsIpcThreadingContractTests` 冻结计数 32（Remote* 门面 GetResult 调用点只减不增，R14 落地后降门槛）。
- 中介管道既有防线复核维持 R12 结论（32MB 行限/4096 队列/16 并发上限+有序道），无新增发现。

## 债③：下载进度节流（完成，提交 14ab863e）

128KB/条逐块上报收敛为整数百分比步进+100ms 地板（首末必达+回退丢弃+进度永不倒退），`AppUpdateDownloadProgressThrottle` 抽类+6 测试。IPC 推送道与关于页停止刷屏。

**四债收账总览：①=052c8964 ②=df686e63 ③=14ab863e ④=d8ccabb7；⑤位置持久化按 Simon 拍板维持只记录。**

## R14 前备（等节组探员返回后按族分批）

32 处同步调用点分布：RemoteSettingsHotkeyActions 8 / RemoteSettingsAppActions 6 / RemoteSettingsWidgetActions 7（部分或为本地镜像读，无需过线）/ RemoteBackupSettings 3+RemoteBackupCommands 1+RemoteBackupRestoreActions 2+RemoteSearchConnectionClient 1 / RemoteAppUpdateService 4。策略=按族改接口签名（sync→Async）+InProcess 包 Task.FromResult+VM 消费端 await 化+smoke harness 适配，每族一批全门。冻结计数棘轮（32）随族落地降门槛。

## 阶段5 复用资产索引

- 隔离根启动器/停止：`%TEMP%\deskbox-review160\launch-isolated.ps1`、`stop-review.ps1`（按路径杀，注意会杀 Simon 实例——定向清理用 PID）
- SACL/boot 冒烟探针：`probe-sacl-bootready.ps1`（先清日志防陈旧行误读；定向按 PID 清理）
- 管道复现脚本族：`pipe-repro*.ps1`（D7 考古用，可删）
- R10 脚本库：`%TEMP%\deskbox-r10-accept\`（uia2/探针/repro/b10-nav/b12-final/内存矩阵）
- 提交辅助：`git-commit.ps1`（Mimosa 绕行：数组拆分+消息文件）

## D8-D20：节组深审与修复（完成，探员四路+复核修复批）

四探员普查（D8 老路径/债清单、D9-D12、D13-D16、D17-D20 三节组）共报 P1×4+P2×15+P3×10，修复批落地：

**已修（本批）**：
- P1 搜索热键挂起无自愈（R3/R13 修一半：退出守卫只救全局热键）→ 守卫双连 resume。
- P1 云备份三假成功（凭证保存/probe/云端删快照=ok 被门面吞）→ RequireOkAsync 抛出恢复进程内语义；本地 DeleteSnapshotAsync 同修。
- P2 QC 开关回滚缺失（R8/R9「修一半」实锤：lambda 丢 applied）→ Todo 同款乐观镜像+回滚。
- P2 applied 应答回声失实 → InProcess 链尾锁内读回权威字典（UI 线程 await 后竞态论据不成立）。
- P2 热键 set-enabled 双命令族并入有序道（病C/F 同族）。
- P2 搜索开关读己之写假失败 → 不即时判注册（推送收敛；R14 恢复真应答）。
- P2 Glance 中继失败/layout 写失败零用户面 → 接 SaveFailedInfoBar（Relay 拒绝即横幅；SendLayoutWrite 注入 onWriteFailure）。
- P2 update.check 超时 60s→110s（对齐内层 HTTP 100s）+ VM 检查失败 catch 转 Failed（原卡「正在检查」）。
- P2 BackupSettingsCoordinator.Update 进图锁（split 引入的池线程无锁写窗）。
- 死码清理：update.clear-installer/prune-stale 死线（接口成员+线+case 全删，主进程直调保留）、Geometry 死命令（无绑定+不发命令）、SetLegacyWideDetailPane 死兼容面、D8 两处零触达 #if 隔离块（:3463/:34 改无条件零售分支）。
- 测试适配×2（搜索开关键新契约、QC 接线锚点 async 形态）。

**登记（维持/后批）**：
- R14 归口：Glance 节 6 处同步往返热点、IsCurrentEndpoint 3 连击、录制器每键冲突检查、搜索 Read() IsRegistered 往返、宿主下载无进度（VM in-flight 丢事件+门面丢 IProgress 双因素）。
- 1.7 维持：组列表跟随/layout-changed 空写噪声（捆绑）、B3 预览含 layout 域、AOT 冒烟宿主场景、QC 诊断行/captureCurrent、跨机还原 rebase 面。
- raised-band 宿主 hwnd（D8 给了记忆法方案，z-order 契约敏感单独立项）；数据清理命令族失败面（DataTools catch，随 R14 或手工矩阵批）；宿主侧 layout 写 SaveDebounced 写放大（GroupNavigationSettingsCoordinator 可条件化，P3）；预测计数死值（P3）。
- D8 结论：1.7 债清单 3 项已被 R13 覆盖（host-ready 队列/B1 保留块抽取/Todo checkNow——后者 R8 就修了）。

## 待 Simon 拍板（累积）

（暂无）

## 债登记（累积）

（空——按域追加）
