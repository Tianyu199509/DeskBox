# R10 真机自动化验收报告（1.6.0 设置拆进程）

- 日期：2026-10-25（会话始于 10-24 深夜，跨日完成）
- 分支：`feat/160-kickoff`，验收基线 commit `865b0274`（R10 修复批）
- 方法：隔离测试数据根（`DESKBOX_DEV_DATA_ROOT=%TEMP%\deskbox-r10-accept\root`）+ PowerShell UIA 自动化 + Win32 探针 + 活体 dump 取证
- 工具偏差声明：Computer Use SDK 本会话不可用（node-repl broker 环境缺失），全部降级为 PowerShell System.Windows.Automation（FromHandle）+ 物理鼠标/键盘注入 + CopyFromScreen 截图；视觉判读用 analyze_image（CDN 链接）

## 总览结论

**验收发现 5 个真缺陷，全部定位到根因并修复，已提交（865b0274）并过完整门（5847/5847 全量测试绿 + AOT 编译门绿）。** 其中最严重的是一族"宿主 UI 线程同步阻塞 IPC → COM 模态循环不泵窗口消息 → 设置窗永久不可关、进程不可退"的 P1 级楔死（三次不同触发点三次复现三次 dump 实锤），修复后 B10/B12/B13 全链路绿。拆分核心价值（关窗即退宿主、单写者、双进程模型、管道会话隔离）验收通过。

## 阶段 A：进程/管道级（全绿）

| 项 | 判定 | 证据 |
|---|---|---|
| A1 双进程模型 | ✅ | `--settings <mainPid>` 宿主由主进程 spawn，命令行核验 |
| A2 管道名会话隔离（R9-⑧） | ✅ | `\\.\pipe\DeskBox_Settings_Ipc_61DE491B0D99C99D_S1` |
| A3 宿主 boot 领养（R8-④） | ✅ | DeskBox.Settings.log 无 LoadAsync 期写盘；boot 即用主进程快照 |
| A4 管道探针 | ✅ | ping ok / settings.snapshot 21785B schema15 无 layout 域 / search-hotkey.state=true |
| A5 单写者 | ✅ | B8/B10 实证 settings/widget-layout 仅主进程写盘；宿主日志零写盘行 |

## 阶段 B：UI 流（自动化逐项）

| 项 | 判定 | 说明 |
|---|---|---|
| B1 全新引导 | ✅ | 隔离根首启走完 onboarding，桌面出格 |
| B2 打开设置 | ✅ | `--open-settings` spawn 宿主窗口 |
| B3 搜索热键卡（R9-①） | ✅（修复后） | 抓到缺陷②：Alt+D 实测空闲（RegisterHotKey 探针）但 UI 报"被占用"=null 服务快照；修复后注册成功 |
| B4 自启开关↔注册表 | ✅ | Run 键/计划任务真删真建双向跟随；终态恢复基线（Run ABSENT+任务 Ready） |
| B5 待办页主开关（R8-①） | ✅ | 开关→桌面格出现/消失，无回跳 |
| B6 随记页主开关 | ✅ | 同上含自动启格链 |
| B7 功能卡片开关回归 | ✅ | 时光卡启用→格子即时出现 |
| B8 时光节即时联动（R8-③） | ✅ | 布局下拉选"沉浸式"→数据文件由主进程写 `layout=Immersive` + 截图视觉确认时间叠壁左下角 |
| B9 清除每格背景（R8-②） | ⚠️ 未自动化 | 需格子右键设背景（原生菜单深导航），列入手工清单 D9 |
| B10 恢复默认（R9-③） | ✅ | 导航默认改"折叠显示"（UI+文件 Stack 双证）→恢复默认→回 Tabs+GroupsEnabled=true（文件级）+UI"并排显示" |
| B11 重走引导前台（R9-⑨） | ⚠️ 未自动化 | 搜索"重走引导"无命中（真名"新手引导"卡片），两轮入口导航未达，列入手工清单 D11 |
| B12 1s 防抖窗口（R5） | ✅ | 显示密度改"紧凑"→1s 内关窗→重开 `layoutDensity=Compact` 落盘；复核改回"标准"同绿 |
| B13 关闭即退出 | ✅（修复后） | 修复前三次楔死（缺陷③④⑤）；修复后 X 按钮→宿主进程退出、主进程存活，三轮验证 |
| B14 布局跟随推送 | ⚠️ 未自动化 | 拖格子+设置窗跟随需实时双窗观察，列入手工清单 D14 |

## 发现的缺陷与修复（commit 865b0274）

1. **宿主启动窗崩溃 0xc000027b（P0）**：主进程 boot 期更新检查推送落在宿主 spawn 后 ≈8s 窗口，About 分节模板未实例化时 `BringUpdateStatusIntoView` 排队 lambda 解引用 null `UpdateCheckActionsPanel`（stowed E_POINTER→NRE，sos 定位）。修=null 守卫+一拍后重试。两轮干净启动验证。
2. **R9-① 半修（P1）**：`InProcessSettingsHotkeyActions` 持构造期快照 null `SearchHotkeyService`（服务在功能首次启用才惰性创建，路由器在主 boot 构造）→搜索热键五命令族静默 no-op。修=构造注入 `searchHotkeyProvider: () => _searchHotkeyService` 惰性解析+路由器三命令 `UiDispatch.RunAsync` 封送（SetWindowSubclass 须窗口线程）。
3. **同步 IPC 楔死族（P1，三次复现）**：XAML 绑定 setter（组导航 ComboBox `set_DefaultNavigationStyle`→`NotifyWidgetGroupPresentationSettingsChanged`）、恢复默认链（`RestoreDefaultPreferencesAsync`→`SetSnapEnabled`）、主进程推送事件（`OnSettingsHostCommandEvent`→`ShowWindow`→`BringSettingsWindowToRaisedBand`，与主进程并发推送成双向 RPC 互锁）——三处在 UI 线程 `.GetAwaiter().GetResult()` 等管道应答，COM 模态循环不泵窗口消息：X 按钮、WM_CLOSE 全部无效，弹层吞鼠标；dump 实锤被等 Task **已完成**（m_result 已设、无异常）仍不醒（模态循环嵌套派发永不返回的 UIA 死客户端回呼）；主进程退出后宿主僵尸（watchdog 决定关停也走不动）。修=10 个纯通知方法 fire-and-forget+失败落日志（widget 面 2 + app 面 5 + hotkey 面 3），bool 读接口保留同步有界等待契约；测试断言改 `UntilAsync` 有界轮询。
4. 宿主 UnhandledException 只记类型不记详情——顺修。

**楔死族机理备注**：`CCliModalLoop::BlockFn`（CLR 对 STA 线程 sync-over-async 的等待）只泵 COM/RPC 消息；一旦有永不返回的入站 COM 调用（本例=UIA provider 对已死亡自动化客户端的同步回呼，uiautomationcore!ChannelBasedServerConnection::OnData）被嵌套派发，句柄唤醒永不被检查。触发前提是宿主 UI 线程先被同步 IPC 拖进模态循环——所以"UI 线程零同步跨进程 IPC"是结构性不变式，不是性能偏好。

## 阶段 C：未执行

C1（~2000 文件映射文件夹迁移不假失败）/C2（备份+还原 prepare）：本轮时间被缺陷修复消耗，未执行。R9-④ 的超时表修复已有单测钉，真机大数据量验证列入待办。

## 阶段 D：手工清单（请 Simon 验收，每项 ≤1 分钟）

- **D1 快速用户切换**：第二账户登入→打开设置（验 R9-⑧ 管道会话隔离）。
- **D2 拔插显示器**：设置窗回屏+"识别显示器"数字窗自动关（R9-⑩ 拓扑半边）。
- **D9 清除每格背景（B9）**：桌面任一格右键→设纯色背景→设置→外观→"清除每格背景"→颜色背景消失且重启不复活。
- **D11 重走引导前台（B11）**：设置→常规→新手引导→"打开 DeskBox 新手引导"→引导窗应出现在设置窗前台（不再被大窗挡住）。
- **D14 布局跟随（B14）**：开着设置窗，桌面拖动一个格子→设置窗显示器/组列表应跟随更新。
- **D5/D3/D4/D6/D7/D8**：真实数据升级链/商店 MSIX 首验/Win10 VM 矩阵/应用内更新安装/热键录制器交互/多显示器高 DPI——按原计划不变。

## 债与观察项登记

- **同步 IPC 残余家族（债，~20 处）**：Remote* 门面其余 `.GetAwaiter().GetResult()`（bool/值读接口为主：IsFeatureWidgetEnabled、HasManagedStorageDesktopShortcut、ScheduleRestartAfterCurrentProcessExit、TryRelaunchAsExplorerUser、GetRuntimeState、hotkey 读族、backup 族、update 族等）。同模态循环机理下（需 UIA 死客户端叠加）仍有理论楔死面+实测已证 15s 超时在该状态下不引爆。建议专项批异步化（VM 层 async 化）。
- UpdateTrayIcon fire-and-forget 后曾录得一次空错误消息失败（宿主退出竞态，管道 dispose 竞争）——日志噪音级，观察。
- 一次宿主 boot 即窗隐藏后经 --open-settings 推送恢复可见（PID 复用期跳表转发乱象相关，未能定案）——观察项。
- aot-quickcheck 脚本经 bash/pwsh 调用引号穿透失效（MSBuild 分号拆分）；本轮 rsp 绕法=`-p:DefineConstants="A;B"` 写 rsp 一行 **必须同时 `-p:DeskBoxAotSmokeHarness=true`**（csproj 无开关时移除 smoke 源=CS0103 假错真因）。脚本本身待修。

## 环境还原与事故披露

- 测试根 `%TEMP%\deskbox-r10-accept\root` **保留**供复查（dumps 目录含三份楔死 dump 共 ~2.3GB，可删）。
- **真实数据意外触碰披露（操作失误，已评估）**：一轮 `--open-settings` 重开命令漏带 `DESKBOX_DEV_DATA_ROOT` 环境变量→以真实数据根启动了一对主+宿主进程约 2 分钟（满屏真实格子），发现后立即杀掉。影响=`%LOCALAPPDATA%\DeskBox` 的 settings.json 与 .bak 被 boot 归一化重写（**尺寸与基线完全一致 10477B**，设计上保键归一保存）+日志追加（DeskBox.log/DeskBox.Settings.log/startup-ledger.json）。无删改内容、无布局域触碰。基线清单在 `baseline-real.txt`（未记哈希，无法逐字节比对——已如实记录此局限）。
- HKLM LocalDumps 取证键已删除（提权 reg delete，验证无残留）。
- 真实实例已按 dev-restart 恢复：**PID 25500**，规范 Debug 路径，启动日志零失败行；自启终态=基线（Run 键 ABSENT+计划任务 Ready）。

## 证据目录

`%TEMP%\deskbox-r10-accept\`：uia2.ps1/win-list.ps1（探针工具）、shots（截图）、dumps（三份楔死 dump）、hang-stacks/host-uithread-clr 等分析输出、baseline-real.txt、各阶段脚本。
