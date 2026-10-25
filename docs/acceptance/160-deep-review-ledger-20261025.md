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

## 待 Simon 拍板（累积）

（暂无——D2「保持手写双壳」与 D3 修法均按显式授权范围内的合理化处理，如需推翻见上两节理由）

## 债登记（累积）

（空——按域追加）
