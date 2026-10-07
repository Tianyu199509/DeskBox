# DeskBox 商店包与安装/卸载链路 审查·核实与解决方案

日期：2026-10-07 ｜ 基线：1.5.6 待打包工作区（HEAD=1542fcd8 + 未提交批次，4849/4849 绿）
方法：两轮审查——初审 7 个并行 Agent（商店包清单与管线 / 商店功能门控 / 商店文案与数据 / Win10 装 / Win11 装 / Win10 卸 / Win11 卸+商店卸载对照），复核 4 个并行 Agent 对全部发现做「当前代码逐行核对 + 微软官方文档与社区资料检索 + 方案合理性评估」。全程未改任何代码。

---

## 一、事实澄清（相对初审报告的修正，先读）

1. **LTSC 叙事纠正**：LTSC 2021 仍带 AppX 平台（AppXSvc 服务、Appx PowerShell 模块均在，仅移除了商店客户端）——三态守卫在真实 LTSC 上走的是 PS 探测 `exit 1` 放行（多花 1-3 秒）；「平台预判放行」分支实际服务的是 NTLite/DISM 深度精简镜像与嵌入式系统。`DeskBox.Uninstall.iss:496` 注释对 LTSC 的表述不精确（行为正确，注释可顺手修）。
2. **静默卸载行为比初审更糟，是两个缺陷**：仅 `/VERYSILENT` 时受管根确认框**照常弹出、无人值守部署会挂起等点击**（`/SUPPRESSMSGBOXES` 必须与 /SILENT//VERYSILENT 同用才抑制，Inno 源码级确认）；`/VERYSILENT /SUPPRESSMSGBOXES` 时抑制返回默认值 IDYES → 自动新建 `DeskBox Files.lnk` 桌面图标。
3. **自定义备份目录的逃生通道要加限定**：只有选在 %LOCALAPPDATA% **之外**的目录才不受 MSIX 虚拟化；选在 Local 根内仍被重定向。
4. **审计资产缺口自 1.5.x 早期即存在**：Cities/cities.json 与 WidgetTitleIcons 从未进过必备清单（1.5.5 包内实测已含）；1.5.6 真正新增的是 WeatherIcons（60 个 SVG + 2 个 LICENSE）。
5. **编码风险的真实路径是 arm64.iss**：主脚本用了 6.3.0 才有的 `x64compatible`，旧版 ISCC 会响亮报错；`arm64.iss` 用 `arm64`（6.1 即支持），旧版会**静默产出乱码安装器**。
6. **Inno `ewNoWait` 不返回进程句柄**（现代版本源码核实，社区传言过时）——PS 探测超时必须走 `ShellExecuteEx` 方案。
7. **签名方案 2026 现实**：Azure Trusted Signing 对中国个人身份**关闸**（个人仅限美/加）；EV 证书已被微软官方确认**不再即时豁免 SmartScreen**。

---

## 二、确认的问题与最合理方案

### A 档｜功能缺陷（建议尽快修）

#### A1. 商店版备份还原整链必然失败 【1.5.5 商店首发即带病，非 1.5.6 回归】

**核实**：依赖链逐行成立——`Services/AppRelaunchService.cs:19-25` 依赖 `PrepareDetachedUpdaterHelper` 找 `DeskBox.Updater.exe`；商店构建三 Target（`DeskBox.csproj:301-317`）条件排除该进程；本地还原失败即取消（`SettingsWindow.Maintenance.cs:261-271`）、云还原同链（`SettingsWindow.CloudBackup.cs:523-533` → `BackupRestoreActions.cs:103-113`）。
**关键事实**（决定方案）：还原本来就基于 `restore-pending.json` 标记「下次启动应用」（`DeskBoxDataBackupService.cs:221,541,688` + `App.xaml.cs:925-927`），重启**不带参数**（Updater restart-only 分支无参启动，已核实）。
**最合理方案（M）**：
1. `AppRelaunchService.ScheduleAfterCurrentProcessExit` 加商店分支：不再找 Updater，置 App 级 `PendingOsRestartOnShutdown` 标志并返回成功（保住 scoped 还原标记不被关机清理）。
2. `App.ShutdownApplicationAsync` 末尾（DrainLogQueue 后、Exit 前）：标志置位且商店渠道 → 调 `Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty)`（注意与已用的 `Windows.ApplicationModel.AppInstance` 是两个类型，需全限定名）。成功则进程被框架包 agent 终止；失败（返回 reason）继续 Exit——**还原标记仍在，下次启动照常应用**，自动降级为「下次启动生效」。
3. 两个调用点（本地/云）无需改动。
**外部依据**：WinAppSDK Restart 官方规格明确适用任何打包 Win32 应用，agent 在框架包内（DeskBox 商店版 `WindowsAppSDKSelfContained=false` 恰好满足）；已知 issue #2792 只影响非打包应用且 1.4 已修。
**否决**：spawn 包内 watcher 进程（重复造 agent 已处理好的轮子）；纯「下次启动应用」（商店用户往往要等下次开机，体验差——而本方案失败路径天然就是它，两者兼得）。

### B 档｜应修（小改动，建议随 1.5.6 或紧随其后）

| # | 问题（已核实） | 最合理方案 | 规模 |
|---|---|---|---|
| B1 | 商店版「自动检查更新」开关无效（可见可切，消费点商店渠道直接 return；手动检查正常） | `SettingsViewModel.AboutAndUpdates.cs:48` 改 `IsDirectInstallerUpdateDelivery ? Visible : Collapsed`（同文件已有 3 处同型先例；AOT 冻结计数无连带） | 一行 |
| B2 | 商店版「卸载即丢快照」零披露（12 语言词典「卸载」0 次；4 条备份文案沉默误导） | 经 `AppDistributionService` 渠道分流：12 词典各加 1-2 键（如 `Settings.DataBackup.StoreUninstallWarning`），备份设置页+首次开启自动备份时提示「卸载会删除本地快照；备份目录请设在 AppData 之外或使用云备份」 | 12 语言 S |
| B3 | per-machine 卸载跳过发起用户 Run 键 → 启动应用页永久死条目（默认安装即 per-machine，常态路径） | admin 分支也调 `RemoveStartupRegistryEntry`（:619-645 自带属主校验=自守，over-the-shoulder 场景无值可删或恰好正确；顺删 `StartupApproved\Run` 孤儿值） | 数行 |
| B4 | 静默卸载挂起（仅 /VERYSILENT）/自动建桌面图标（+/SUPPRESSMSGBOXES） | `OfferManagedStorageShortcut` 开头 `if UninstallSilent then Log(...) and Exit`（官方卸载期专用函数，覆盖两种静默；日志留存储根路径给部署者） | ~7 行 |
| B5 | /PURGEUSERDATA + 商店版在装：静默跳过 purge 无反馈（Unknown 反而有弹窗） | Installed 分支补 `SuppressibleMsgBox(StoreEditionDataPreserved)`（消息 12 语言已存在，零翻译） | ~5 行 |
| B6 | 商店包构建链无 git 指纹（Direct 链有；工作区脏树时包不可复现） | 把 `Get-WorkingTreeSnapshot`（`publish-aot-retail.ps1:168-190`）复制/抽共享模块进 `audit-store-native-aot-package.ps1`，写入 summary.json | S |
| B7 | 升级瞬间 ThumbnailProxy 孤儿进程可锁文件（Rust 侧无看门狗，Shell handler 挂死时无限存活） | 两个 .iss：`CloseApplicationsFilter=DeskBox.exe,DeskBox.ThumbnailProxy.exe`（官方语义=逗号分隔通配列表；代理无状态可强杀） | 1 行×2 |
| B8 | 升级清理脚本 PS 失败即中止安装（WDAC/AppLocker 机器恰好最需要升级） | `Migration.iss:45-49` 的 `RaiseException` 改降级：Log「下次升级重试」继续装（陈旧异名文件实害≈0、每次升级重跑自愈；否决反向统一为中止） | ~10 行 |
| B9 | arm64.iss 旧版 ISCC 静默乱码 | 9 个无 BOM 的 .iss 加 UTF-8 BOM（6.x 长期支持）+ CI `distribution-audit.yml` 补 Inno≥6.3.0 断言 + 顶部注释 | 9 文件+2 行 |
| B10 | 审计必备清单不覆盖 WeatherIcons 等资产（打包回归审计照样过） | 手动补清单+目录级计数断言（「WeatherIcons/ 至少 1 个 svg」等）；**否决从 csproj 自动生成**（审计价值=独立于构建系统的第二双眼睛） | S |
| B11 | combine-store 单跑不校验 PFN/发布者 | combine 内联 `Name/Publisher` 断言 + 内层 msix manifest 复验（注意目标在发版技能脚本） | ~10 行 |
| B12 | StartupTask DisplayName 字面量未本地化（Win11 启动页/任务管理器显示英文） | 官方 schema 确认支持 `ms-resource:` → 12 个 resw 加 `StartupTaskDisplayName` 键 + manifest 改引用（`com:ExeServer DisplayName` 可顺手同改） | S |
| B13 | cities.json 双重打包（运行时只读嵌入资源，松散副本纯死重） | csproj 加 `Content Remove`（与既有 wechat-qrcode 排除同模式） | 一行 |
| B14 | 商店更新内嵌摘要缺 de/ja/pt 三语（回退英文） | `StoreAppUpdateService.cs:62-73` 字典补三键（译文已备：ja「Microsoft Store でアップデートが利用可能です。ストアがダウンロードとインストールを行います。」/ de「Ein Update ist im Microsoft Store verfügbar. Der Store übernimmt Download und Installation.」/ pt「Há uma atualização disponível na Microsoft Store. A Store fará o download e a instalação.」） | 三行 |
| B15 | 设置搜索目录在商店版暴露「启动方式」（指向折叠控件） | 消费端渠道排除集（`CreateSettingItemSearchResults` 加 `IsMicrosoftStore` 跳过集；不动生成器——脚本无法解析 VM 运行时渠道语义）；B1 落地后同集加 `Settings.Update.AutoCheck` | S |
| B16 | 英文 "desktop-edition" 措辞不一致 + StateUnknown「或选择保留数据继续」在 purge 语境悬空 | en 一行改 "direct-installed edition's"；StateUnknown 12 语言就地改陈述句「如需删除，请先卸载商店版后重试」（键/占位符不变，测试天然过）；**否决拆两条消息**（成本数倍收益边际） | S/M |
| B17 | 契约测试 5 处补锁 | 按已评估写法：admin 永不 purge（位置序）、预判 AND 逻辑（两条精确串）、静默默认保留/UninstallSilent 门控（与 B4 绑定）、StoreEditionDataPreserved 计数=2（与 B5 绑定）、TEMP 清理不被 purge 门控（位置序） | 小 |

### C 档｜流程/决策类（不阻塞发版）

| # | 事项 | 结论 |
|---|---|---|
| C1 | **直装包签名路线** | 推荐按序：①申请 **SignPath Foundation** 免费签名（GPLv3+GitHub 公开项目，DeskBox 符合画像，审核制）；②被拒则 **Certum OV**（约 1200 元/年，云签名版可进 CI；注意赞赏码是否触发「商业」认定需先确认）；③保底=README/Release 说明「未签名，首次运行 SmartScreen 点仍要运行」+引导商店版（商店版由微软签名免疫）。纪律：同一身份持续签每版（换证书=声誉清零）、误报走 Security Intelligence 提交。**否决**：Azure Trusted Signing（中国个人资格不符）、EV（官方确认已无 SmartScreen 增值）、x86 包（AOT 不支持 win-x86） |
| C2 | Win10 ARM 取 x64 包=死胡同（进程起不来，无指引） | 纯文档：官网下载页加架构选择+「Win10 ARM 必须用 ARM64 包（x64 包仅支持 Win11 on ARM）」；README FAQ 补同句；issues 零 ARM 实证，不值得代码动作 |
| C3 | per-machine 静默安装默认 UAC | README 部署段补一句「免提权静默安装追加 /CURRENTUSER（拒绝提权退出码 1223）」 |
| C4 | 双渠道互斥体同名（点商店图标静默打开直装版；通知信封单向不可见） | **维持现状+文档化**：同名互斥体事实上保证整机单实例，恰好规避双 watcher 双吞桌面文件的灾难，是「意外的好属性」→ 升格为 `distribution_channel_workflow.md` 显式设计记录+安装说明「装商店版前退出直装版」。**否决**渠道后缀拆分（受管根双写风险 M-L 工程） |
| C5 | PS 商店探测无超时（AppXSvc 挂死→卸载器无响应冻结） | 与 B3/B4/B5 同批做：新增 `ExecWithTimeout`（ShellExecuteEx + WaitForSingleObject + 超时 Terminate → 返回 Unknown fail-closed），替换 :465-473 的 Exec；超时 15-30s。约 60-80 行 Inno Pascal |
| C6 | 文档小疵 | `distribution_channel_workflow.md:345` 清理命令 PFN 写错（`DeskBox.Desktop` → `D1FC332A.DeskBoxWidgets`） |

### D 档｜明确不做（已论证否决）

- **禁用 MSIX 写虚拟化**（`desktop6:FileSystemWriteVirtualization=disabled`）：官方明文仅限微软合作方游戏、需 `unvirtualizedResources` 受限能力申报（"not intended for other scenarios"），第三方工具应用基本必拒；即便过审还需自做 LocalCache 数据迁移。彻底否决，B2 文案披露是正解。
- **跨用户 per-user 安装探测**（HKU 枚举）：不同账户的 per-user 安装本就是 Windows 合法独立安装；枚举引入 `.DEFAULT`/系统 SID 过滤与陈旧 profile 误判，最坏把正常用户挡在门外。接受现状。
- **互斥体拆分 / 生成器渠道标注 / 摘要词典化 / 测试锁死 desktop-edition 字样**：各核查报告内均已论证否决理由。

---

## 三、已确认无碍（两轮审查均过）

- **商店包**：能力集与 1.5.5 已接受包逐字一致（internetClient+runFullTrust+location 各有其用）；StartupTask/toast COM 注册链闭合；**冷激活 RoFailFast 三件套防护完整在位**；MSIX 升级保留 LocalCache；版本五处一致；AOT+框架依赖配置正确（不私带 WinAppSDK）；诊断包脱敏不泄露 LocalCache。
- **门控**：双服务工厂架构健康；1.5.6 全部新功能（显示器绑定/格子外观/SafeMigration/整理时机/静默启动/跨界淡出）在商店版无需门控判定正确；捐赠 QR 不进商店包。
- **卸载**：三态守卫两 OS 判定方向全对；purge 范围与探测范围严格同账户；受管根任何分支永不清；计划任务/通知注册/AUMID 清理全带属主校验；无「清过头」。
- **安装**：零售包纯离线零系统依赖；1.5.5→1.5.6 走精确上一版清单清理（纯 ASCII 任何代码页安全）；GBK 坑根除（任务 XML UTF-16 写+COM BSTR 读）；12 语言安装器消息齐全；无系统右键菜单/shellex/服务/别名/防火墙注册；Win11 启动「禁用位」被正确尊重。

## 四、与 1.5.6 发版的关系（建议）

> **实施状态（2026-10-07）**：第四节的「随 1.5.6 一起修」与「打包流程内顺手做」两档已全部落地——A1、B1、B3、B4、B5、B7、B13、B14 + B6、B11，构建零错误、4849/4849 绿。B2/B9/B10/B12/B15/B16/B17、C5 维持 1.5.6 后批量计划；C1 签名申请（SignPath Foundation）待发起。

1. **随 1.5.6 一起修**（都是几行级）：A1（商店备份还原，唯一功能缺陷，修完商店版用户第一次能用还原）、B1/B3/B4/B5/B7/B13/B14（一行到数行，零行为风险）。
2. **1.5.6 打包流程内顺手做**：B6（audit summary 补 git 指纹）、B11（combine 身份断言，改技能脚本）。
3. **1.5.6 后批量**：B2/B16（12 语言批次）、B9/B10/B12/B15/B17、C5。
4. **独立流程线**：C1 签名（SignPath 申请可以现在就发起，审核期与发版并行）、C2/C3/C6 文档。
5. 真机验证清单不变（见 v1.5.6.md 边界清单），另加：商店版备份还原修复后的完整还原流程实测、Win11 启动页死条目修复前后对照、/VERYSILENT 卸载三形态（挂起/建图标/跳过）。

## 五、主要来源

微软官方：MSIX desktop behind-the-scenes（AppData 重定向/卸载删除语义）｜desktop6:FileSystemWriteVirtualization 元素与受限能力列表｜AppInstance.Restart API 与 WinAppSDK Restart 规格、issue #2792｜uap5:StartupTask schema（DisplayName 支持 ms-resource）｜SmartScreen reputation（2026-05）与 Code signing options（2026-08，含 Artifact Signing 地域限制）｜Win10 ARM 仅 x86 模拟。
Inno Setup 官方（jrsoftware.org）：SuppressibleMsgBox/命令行/Exec/IsAdminInstallMode/UninstallSilent/PrivilegesRequired/CloseApplicationsFilter；issrc 源码（is-6_2_2/6_5_4/6_7_3/main）核对抑制与句柄语义；6.3.0 whatsnew（BOM-less UTF-8）。
社区：Stack Overflow 10892452/32256432（Inno 带超时执行）、Winaero（死启动项）、LTSC 2021 管理指南（AppX 模块存在性）、SignPath Foundation、Certum 采购指南。
仓库证据：文中全部 file:line 均经两轮独立核对。
