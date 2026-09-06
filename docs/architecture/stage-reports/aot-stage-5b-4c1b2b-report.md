# DeskBox AOT 阶段 5B-4C1B2B 完成报告

- 日期：2026-08-22
- 状态：5B-4C1B2B 已完成到定义的 x64 Native AOT 实际运行边界
- 审计配置：profile 49 / schema 46
- 本阶段范围：真实 File Widget“属性”菜单、精确目标、`SHObjectProperties`、owner 参数、系统属性页观察与受控关闭

## 1. 结论

本阶段已经闭环 File Widget 的系统“属性”产品路径。实现从真实单选菜单经 `FileItemMenuBuilder`、`FileSurfaceContent.ShowFileProperties` 和 `ShellContextMenuHelper.ShowProperties` 调用 `SHObjectProperties(SHOP_FILEPATH)`，没有增加测试专用产品入口，也没有用模拟窗口代替系统属性页。

产品修复只调整 owner 来源。旧实现从当前前台窗口推测 owner，窗口焦点变化时可能绑定到无关窗口；当前实现直接传入所属 File Widget 的 `_hostWindowHandle`。AOT fixture 在真实 P/Invoke 前后记录实际 owner、路径、返回值和时间，要求精确场景、32 位小写 run ID、隔离 preview root、唯一 owned 文件、非零 owner 以及一次调用。

本阶段没有扩展 Rust。该路径只有一个稳定的 Shell P/Invoke，没有传统 COM/AOT 硬阻断，也没有大型托管常驻或复制内存热点。改成 Rust 会增加 ABI 和行为差分，不能证明可降低 DeskBox 内存；生产模块因此继续保持 ABI 2、能力 511 和十个必需导出。

## 2. owner 语义与实测修正

微软文档把 `SHObjectProperties` 的 `hwnd` 定义为属性页的父窗口参数，并要求 `SHOP_FILEPATH` 使用完整文件路径。产品运行证据确认传入 API 的 HWND 始终等于真实 File Widget HWND，路径始终等于唯一 owned 目标。参考：[SHObjectProperties](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shobjectproperties)。

首次真实运行还证明，Windows 11 当前实现不会把系统属性页的 `GW_OWNER` 直接设置为传入的 WinUI 窗口。系统会在同一 DeskBox 进程内创建一个不可见的 `StubWindow32` 代理窗口，属性页的 direct owner 和 root owner 指向该代理。初版把 direct/root owner 必须等于 File Widget HWND 作为硬断言，因此正确打开并关闭属性页后仍判失败。

最终契约把两层事实分开：

1. 产品层严格证明实际传给 `SHObjectProperties` 的 owner 等于 File Widget HWND；
2. 系统层记录属性页、expected owner、direct owner 和 root owner 的 HWND、有效性、可见性、线程、进程、类名与标题；
3. 系统代理 owner 必须是当时仍有效的非零窗口，但不假定它在不同 Windows 版本上必须等于或不等于产品 HWND；
4. 属性页仍必须是唯一新出现、标题包含唯一文件名的可见 `#32770` 窗口。

这既保留了产品 owner 传递的硬证据，也避免把操作系统内部代理窗口策略写成错误的跨版本产品契约。

## 3. 实现与隔离

真实菜单 probe 使用 `CreateItemFlyout(target)` 创建产品单选菜单，按当前语言定位 `Common.Properties`，再通过 `MenuFlyoutItemAutomationPeer` 和 `IInvokeProvider` 进入原事件链。后台观察器在调用前冻结可见顶层窗口基线，只接受基线外、标题包含唯一文件名且类名为 `#32770` 的新窗口。

观察到目标属性页后，runner 记录完整窗口事实，向该唯一 HWND 发送 `WM_CLOSE`，等待窗口销毁，并再次枚举确认同名属性页残留为 0。操作前后独立计算目标文件长度和 SHA-256，要求文件存在、长度和哈希完全不变。

每轮使用全新的 preview root 和同级 `-Recovery` root。两个根都必须位于仓库专属 evidence 目录、不得与正式 `%LOCALAPPDATA%\DeskBox` 重叠、不得预先存在，并分别写入含仓库根、场景、run ID 和精确根路径的 ownership marker。证据归档后，runner 逐根重新核对 marker 和边界才允许删除；会话文件只有在两根均确认不存在后才写入成功状态。

## 4. 实际运行结果

最终形态连续使用两个全新的 AOT 进程通过：

| run ID | PID | File Widget HWND / API owner | 属性页 HWND | 系统代理 owner HWND | 代理类名 |
| --- | ---: | ---: | ---: | ---: | --- |
| `5bfafcc7372a447cb415395d56df1df5` | 31624 | 18681702 | 3019360 | 19271978 | `StubWindow32` |
| `60a0b5706e8f4eef9b36277eebbbb875` | 31628 | 12128546 | 5181714 | 23858812 | `StubWindow32` |

两轮共同结果：

- 属性页标题均包含完整唯一文件名，类名均为 `#32770`；
- expected owner 是有效的 `WinUIDesktopWin32WindowClass`；
- direct/root owner 是同一 AOT 进程内有效、不可见的 `StubWindow32`；
- `SHObjectProperties` 返回成功，产品没有发出 `file-properties` 错误反馈；
- `WM_CLOSE` 投递成功，属性页销毁，匹配窗口残留为 0；
- 目标文件长度均为 78 bytes，前后 SHA-256 相同；
- AOT 进程自然退出，运行错误日志为 0；
- 正式数据目录前后指纹均为 `0AF70296E094E071CE18C8D0B1629E57D4096F7C30DC72063BFB32FD778E9759`；
- 每轮 preview root 和 `-Recovery` root 均在 ownership 复核后清理，受审计 AOT 进程残留为 0。

最新结构化证据位于 `.artifacts/aot-managed-ui-smoke/win-x64/file-properties-read-only-60a0b5706e8f4eef9b36277eebbbb875/`。这是本地审计产物，不属于仓库源文件。

## 5. AOT 发布审计

profile 49 / schema 46 的完整 x64 发布审计通过：

- WMC1506 为 0，WMC1510 精确保持 1211；
- 完整 `always-throw` 为 0；
- 原始 IL2026、IL2050、IL2072、IL2075、IL3050 均为 0；
- 本阶段六个 C# 目标文件没有新增分析警告；
- Properties 的产品、fixture、菜单、场景、runner、自然退出、哈希、正式数据保护和双根清理门禁均完整；
- Picker、StorageItems、物理拖放、`IFileOperation`、回收站和新 Rust ABI 均未进入本阶段；
- Rust ABI 2、能力 511、十个导出及 staging/publish 哈希保持不变。

## 6. 证据边界

本阶段已经证明真实 File Widget、真实产品菜单、真实 P/Invoke 参数、真实系统属性页、实际窗口代理关系、受控关闭、文件只读性、进程自然退出、正式数据隔离和 owned 双根清理。

菜单入口由 WinUI Automation Invoke 驱动，不等于人工鼠标点击。`WM_CLOSE` 是测试环境对唯一 owned 属性页的受控关闭，不等于用户手动点击“确定”或“取消”。本阶段也没有替代目标 Windows 版本上的视觉检查，例如属性页内容、焦点、置顶关系、DPI、多显示器和人工关闭体验。

## 7. 回归与复盘

最终回归结果：

- 5B-4C1B2B 新合同 12/12；
- 全部 AOT 相关测试 395/395；
- x64 全量测试 2390/2390；
- Rust workspace 57/57；
- `cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、五个 PowerShell 脚本解析和本阶段文件 `git diff --check` 均通过。

代码复盘确认：普通 JIT 继续使用同一 `SHObjectProperties` 产品实现；AOT instrumentation 只在 `DESKBOX_NATIVE_AOT` 且精确场景成立时记录；系统属性页仍由真实 Shell API 创建；产品 JSON 格式和 Rust ABI 均未改变。

首次 profile 49 审计发现禁用模式把编译符号 `DESKBOX_NATIVE_AOT` 误认成 Rust 导出名，修正为大小写精确匹配。首次真实运行发现系统 `StubWindow32` 代理 owner，以及自动备份产生同级 `-Recovery` 根；最终实现分别通过完整 owner 事实和双根 ownership 清理闭环这两个遗漏。

## 8. 下一阶段建议

下一阶段进入 **5B-4C1C**，但调整为两个顺序门，不一次合并：

1. **5B-4C1C1：Picker 与 Clipboard StorageItems。** 重点验证真实 picker owner、选择/取消、文件与文件夹 StorageItems、隔离导入和无修改退出。复杂度中等，主要风险是系统 UI、异步生命周期和目标系统行为。
2. **5B-4C1C2：OLE/native drop 与真实 Explorer 物理拖放。** 重点验证真实 pointer/drag event ordering、进入/离开目标、文件夹高亮清除、外部拖出、复制/移动结果以及大文件进度层。复杂度高，必须保留人工鼠标、真实 Explorer 和视觉证据，自动化不能单独替代。

两个子阶段都不预先扩展 Rust。Picker 和 OLE 回调属于 Windows UI/WinUI 生命周期边界，现有 managed/generated-COM 实现更直接；只有后续测量发现明确的大型托管常驻或复制热点，且 Rust 能以粗粒度边界显著降低内存时，才单独评估迁移。
