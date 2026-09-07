# DeskBox Rust 与 Native AOT 分阶段优化报告

- 报告日期：2026-08-23
- 代码基线：`22d29480bdcac98f636b94b2990ceff2b8f2a0de`（DeskBox 1.4.3）
- 核对范围：旧规划、当前 C#/WinUI 代码、项目与发布脚本、现有测试静态清单、多次 x64 Native AOT 实际发布审计
- 当前实施状态：阶段 0/1/2、3A、3B、3C、4A 至 4E-5、5A、5B-1 至 5B-3C、5B-4A、5B-4B1、5B-4B2A、5B-4B2B1、5B-4B2B2A、5B-4B2B2B1、5B-4B2B2B2、5B-4B2C1、5B-4B2C2A、5B-4B2C2B、5B-4C1A、5B-4C1B1、5B-4C1B2A、5B-4C1B2B、5B-4C1C1、5B-4C1C2A、5B-4C2A、5B-4C3A、5B-4C3B1、5B-4C3B2A、5B-4C3B2B1 和 5B-4C3B2B2A 已完成到各自定义的构建、测试、AOT 产物审计或实际运行边界。C1C2B 已增加真实 Explorer 窗口与注入鼠标的自动补充证据，但 `PhysicalExplorerMouseVerified` 仍为 false；C2A 已完成可自动化的主/搜索热键注册和 hook 生命周期；C3B1 已证明真实系统通知的展示与清理，C3B2A 已证明 grammar 和确定性动作路由，C3B2B1 以类型化 envelope 保存 arguments、完整 `UserInput`、来源 PID 与时序信息并通过冷启动恢复和真实第二实例 mutex/event 转发矩阵；C3B2B2A 又证明受控 activation 进入产品后能在真实 Todo HWND/XamlRoot 定位正文目标，并在 Complete/Snooze 后完成两帧可见刷新。产品 JSON 固定为 29 个文件、65/65 处 source-generated 调用和 27 个 context 所有者。普通 JIT 的四个既有原生产品边界默认仍走 C#；NativeAOT 对这些产品操作使用 Rust，并增加内部回收站精确查询/恢复边界，产品 Shell move、Properties、Picker、StorageItems、OLE drop、热键和 Todo 通知/activation 路由继续保留更简单的 C#/WinRT/source-generated COM/Win32 实现。生产模块保持 ABI 2、能力 511 和十个必需导出。最新 x64 审计为 profile 56 / schema 53，WMC1506=0、WMC1510=1211，完整 `always-throw` 及原始 IL2026、IL2050、IL2072、IL2075、IL3050 均为 0；全部 AOT 相关测试 452/452、Rust 57/57、x64 全量 2468/2468。C1C2B 与 C2B 物理输入门继续作为发布前条件；下一项为 5B-4C3B2B2B1 运行中主实例的真实 Windows 通知点击与 activation 来源证明。

> 2026-08-23 后续状态校正：5B-4C3B2B2B 的产品实现、交互 runner 和 profile 56 / schema 53
> AOT 审计已完成，当前只缺真人通知中心点击这一外部证据；代码主线已继续到 Rust SearchCore 6A。
> 6A 的独立 ABI、紧凑索引、差异门禁和内存结构证据已完成，下一代码阶段为 6B 隔离 300k Release
> 基准与 DBIX 直载原型。顶部历史长摘要中的“下一项为 B2B2B1”按实施时序保留，由本状态校正取代。

> 2026-08-23 阶段 6B 状态校正：SearchCore ABI 2 的原子 DBIX 直载、损坏/版本/取消 fallback、
> 10k/100k/300k 隔离进程基准与查询签名门禁已完成。300k resident private 增量为 C# 85.79 MiB、
> Rust 17.55 MiB，下降 79.5%；下一代码阶段调整为 6C 增量所有权、recent/frequent 和显式产品预览，
> 默认搜索后端当前仍未切换。

> 2026-08-23 阶段 6C 状态校正：SearchCore 已升到 ABI 3，完成事务化增量、recent/frequent、
> live-only DBIX 保存与压缩、单 resident owner、默认关闭的 Direct x64 产品预览、managed fallback、
> 全格子产品内存和 x64 AOT 打包审计。207,925 条真实索引、11 个启用 Widget 下，Rust 相对 managed
> 的整体进程 Private Bytes 中位数下降 12.02%，Working Set 下降 8.16%。审计现为 profile 57 /
> schema 54。下一代码阶段为 6D 长会话、运行期故障恢复和默认启用决策；Store/ARM64 仍未开放。

> 2026-08-23 阶段 6D 状态校正：SearchCore 已完成 query/projection/save/idle unload/mutation/
> reconciliation 全运行期 managed 恢复和会话隔离、真实 watcher overflow/root recovery、超过 4,096
> tombstone 的压缩与 idle reload、真实 x64 Native AOT owned 搜索结果及 6 筛选/8 排序矩阵。208,021
> 条索引、11 个启用 Widget、各三轮整进程测量中，Rust 相对 managed 的 Private Bytes 真中位数下降
> 12.85%，Working Set 下降 9.36%。Direct x64 模块构建现默认启用 Rust，已保存的用户选择不变；
> Store/ARM64 继续不打包且默认关闭。审计升为 profile 58 / schema 55。下一代码阶段为 7A ARM64
> 工具链、PE 与静态分发边界；Todo 通知中心真实点击/投递差异仍是独立外部门禁。

> 2026-08-23 阶段 7A、7B 自动化边界与 7C0 状态校正：固定 Rust 1.96.0 与 ARM64 MSVC/SDK、两个
> `0xAA64` DLL 和 ARM64 Native AOT 静态发布已完成；随后 GitHub Actions 原生 ARM64 Windows runner
> 实际加载 ABI 2/能力 511 与 ABI 3 模块，并通过 11/11 产品绑定测试。x64/ARM64 CRT A/B 最终选择
> Static，两个模块不再导入 `VCRUNTIME140.dll`。Direct x64/ARM64 现在默认启用 SearchCore，Store
> 继续使用 managed。云端 runner 不替代实体 ARM64 的交互 UI、11 Widget 整进程内存和设备差异；下一
> 代码阶段为 7C1 Direct/Store 双架构安装、升级、包内容、签名、WACK 与 flight。
>
> 2026-08-24 阶段 7C1 自动化分发矩阵完成：GitHub 原生 x64/ARM64 runner 均通过最终 Direct AOT
> publish、Inno 安装器、Store MSIX/appxsym/msixupload 与包内容/哈希审计；Store AOT 明确包含静态
> `deskbox_native.dll`，不包含 Updater、SearchCore、managed runtime 元数据或 PDB。该结果不包含签名、
> WACK、安装、覆盖升级或实体设备。原始目标加权约 98%；下一阶段为 7C2 合并 Store 上传包和外部发布证据。
>
> 2026-09-07 profile 59 修正：本地复跑 x64 audit 暴露 4E-4 契约与 0a496114（设置节惰性装载）漂移——
> 旧的 `AppearanceDetailSection.ViewModel = ViewModel;` 急切赋值已不存在；契约改为钉住
> `Bindings.Initialize()`/`Bindings.StopTracking()` 与空守卫清理时序，auditProfileVersion 58→59
> 全仓同步（schema 55 不变）。同批暴露并修复：DeskBox.Abstractions 引入后主项目
> packages.aot.lock.json 未再生成（audit restore 已补齐）。

## 1. 结论摘要

1. **Native AOT 应继续作为确定目标，但按兼容性批次推进。** 旧方案中的“冷启动收益不足 20% 就停止”不再适用。启动速度、内存和安装体积仍需测量，但这些指标用于选择发布方式和继续优化的位置，不用于决定是否放弃 AOT。
2. **当前主程序已经可以完成 x64 AOT 编译、基础运行、四个 Rust 产品边界、内部 Rust 回收站恢复边界、设置与离线内容持久化矩阵、确定性 Weather surface、owned File Widget 核心操作、菜单删除、Shell move、系统 Properties、Picker、StorageItems、程序化 OLE/native drop、主/搜索热键自动注册生命周期、Todo recurrence/reminder 确定性状态矩阵、真实 Todo 系统通知的展示/历史恢复/精确清理、通知 grammar/确定性动作路由、保留完整 `UserInput` 的冷启动与单实例转发，以及受控 activation 后的真实 Todo surface 定位与可见刷新，但还不能视为可发布。** profile 56 / schema 53 已把传统 COM `always-throw`、IL2026、IL2050、IL2072、IL2075、IL3050 和 WMC1506 全部清零，WMC1510 保持 1211。真人 Explorer 物理鼠标、物理标准/Win+Space 键盘、设置/引导录制器、真实 Windows 通知点击/activation 来源、真实媒体会话、真实天气网络/定位、Quick Capture 图片/全局剪贴板传输、附件 Undo/孤立文件回收和真实安装升级仍未完成。
3. **不需要先重做功能或测试体系。** 仓库现有测试足以作为分批修改基础；5B-4C3B2B2A 增加目标/刷新结果语义、5 条阶段契约和 1 条失败分支单元测试，并继续区分静态契约、实际 AOT 运行、真实第二应用实例、Windows 外部 activation 与目标系统人工验证。程序化 HDROP 不冒充真人 Explorer 鼠标，`SendInput` 只证明标准 `RegisterHotKey` 的 OS 分发，不冒充物理键盘或 Win+Space hook 触发；受控 activation 只证明真实 Todo surface 行为，不冒充通知中心真人点击。
4. **Rust 适合选择性引入，不适合改写 WinUI、ViewModel 或所有 Win32 代码。** Rust 现已接管 Native AOT 中全部 `CLSID_ShellLink` 产品操作、音乐音量 Core Audio、Explorer 托管启动和 Quick Access 边界，并为 C1B1 提供完整粗粒度的回收站精确查询/恢复；产品删除仍保留更简单的 C# P/Invoke，普通 JIT 默认保留四个既有产品边界的 C# oracle。SearchCore 已通过 6D 稳定性、整机内存和 7B 原生 ARM64 后端门禁，在 Direct x64/ARM64 模块构建中默认启用；Store 保持 managed。两个 Rust DLL 的生产默认采用静态 CRT，不扩大到 WinUI 重写。
5. **AOT 和 Rust 是两条可独立验收的路线。** AOT 兼容性清理不能依赖搜索核心已经 Rust 化；Rust 模块也不能以“主程序能 AOT 编译”代替自身的 ABI、架构和结果一致性验证。
6. **第一项最小 AOT 交付物应是更新器。** `DeskBox.Updater` 已单独完成无警告 AOT 发布，且代码边界很小。主项目原先没有把 `PublishAot` 传给更新器，阶段 1 已修正这一问题，并消除了 AOT 目录中混入的 CoreCLR/JIT 更新器运行时。
7. **3C-3-R 已封闭项目级 AOT/Rust 属性矛盾。** `ValidateDeskBoxNativeAotConfiguration` 在编译前要求 `Platform=x64`、`RuntimeIdentifier=win-x64` 和 `DeskBoxRustNative=true`；普通 JIT 默认仍为 `false`。审计脚本只保留受支持的 x64 路径，ARM64 会在解析 dotnet host、创建或清理产物前失败。真实 MSBuild 正反向组合、负向 `dotnet build` 和脚本提前失败均已验证。
8. **4A 已移除 FolderPicker 的传统 COM 硬阻断并完成验收。** 服务改为 Windows App SDK `FolderPicker(WindowId)` 异步 API，8 个产品入口全部显式等待并传递有效 owner；托盘与 JumpList 统一使用长期存在的托盘宿主 HWND，服务拒绝零句柄和失效句柄。自动化、AOT 审计以及设置、引导、Glance、桌面整理、托盘与 JumpList 的人工选择/取消/窗口关系矩阵均已通过。
9. **4B JSON source generation 阶段已完成。** 当时的 16 个文件、49 处产品调用已全部显式绑定 14 个分域 context；反射型调用与非泛型 converter 均为 0。后续隔离 AOT runner 继续使用显式 context。当前固定清单为 29 个文件、65/65 处调用、27 个 context 所有者；5B-4C3B2B2A 复用现有 managed UI 结果 context，没有增加 JSON 调用或反射回退。既有用户持久化格式没有变化。
10. **4C 音乐音量 COM 已完成结构迁移和真实边界验证。** 选择现有 Rust DLL 的粗粒度 Core Audio 边界而非逐接口生成式 COM；冻结并复现默认 endpoint、系统/session 音量、匹配、apartment 和失败回退语义。普通 JIT 默认仍为 C#，显式 Rust 与 AOT 使用 ABI v1 音量导出；配置 14 / schema 11 的结构审计确认 ABI 2、能力 63、七个导出、同次哈希一致和 `always-throw=0`。后续 5B-3A/3B/3C 已分别实际验证只读边界、系统主音量 setter 以及匹配 session getter/setter；5B-3C 使用测试专用 Rust 静音夹具完成应用内与独立新进程恢复，未控制用户播放器。
11. **4D-1A 低风险类型化入口已完成。** `DispatcherQueueOptions` 改用泛型静态尺寸，Markdig task-list 改用公开 `TaskList.Checked`，搜索收藏/历史推荐复用现有 `SearchRecommendationItem`。配置 15 / schema 12 的审计把三个目标文件列入零警告门禁；本批没有修改 Rust 模块、COM 边界、XAML Binding 或业务功能。
12. **4D-1B 应用内反射收口已完成。** Quick Capture 初始化失败只记录固定异常字段，`Localized` 对 `SettingsCard`、`SettingsExpander` 和 `TextBox` 直接赋值；301 个 XAML 标记由源码清单门禁冻结。配置 16 / schema 13 确认两个新目标文件为 0 告警，原始 IL2075 从 13 降为 9。完成后复盘还确认 `FileOperationHelper` 在产品与测试中没有任何调用，4D-2 应删除这段死代码，不再为不存在的产品边界扩展 Rust ABI。
13. **4D-2 死代码删除已完成。** 删除 223 行零调用的 `FileOperationHelper`，真实回收站删除和 Shell 进度移动仍由 `FileService` 承担。配置 17 / schema 14 确认删除源码、引用和对应 `IFileOperation`/`IShellItem` 告警均为 0，原始 IL2050 从 4 降为 2；Rust ABI、能力、导出和哈希均未变化。下一阶段进入 4D-3 OLE `NativeDropTarget`，优先 C# 源生成 COM，不把带高频反向回调的拖放状态机强行迁移 Rust。
14. **4D-3A OLE 数据读取侧已完成。** 删除 `COMIDataObject` 和内置 `IStream` RCW，改为回调期借用指针上的 `GetData`、`QueryGetData`、`Read` 三个固定 vtable 槽；结构布局、HRESULT、分块读取和越界返回均有真实函数指针测试。配置 18 / schema 15 确认新读取层零告警，IL2050 仍只来自待办的 `IDropTarget` 注册侧；x64 全量测试 2027/2027 通过。真实 Explorer/浏览器拖入属于进入 4D-3B 前的人工门槛。
15. **4D-3B OLE 注册与回调侧已完成。** `IDropTarget` 改为 `[GeneratedComInterface]`/`[GeneratedComClass]`，`RegisterDragDrop`/`RevokeDragDrop` 改为显式指针和 `[LibraryImport]`；真实生成 CCW 的 IID、slot 3-6、回调转发和模拟 OLE AddRef/Release 已有运行测试。配置 19 / schema 16 将 IL2050 从 2 降为 0，x64 全量测试 2036/2036 通过，Rust ABI 未扩展。后续人工复测发现并修正系统拖动视觉未及时结束、文件夹目标高亮偶发残留、进度卡层级和毛玻璃外观三项 WinUI 问题；跟进修复后的 x64 全量测试为 2038/2038，COM 与 Rust 边界未变化，人工复验通过后已开放 4D-4A。
16. **4D-4A Explorer 托管环境启动已完成自动化与 AOT 审计。** 现有 C# dynamic 链保留为普通 JIT 默认 oracle，并从 AOT 编译单元排除；Rust 使用强类型 Shell 接口在 Explorer 桌面进程中执行 `ShellExecute`，产品层的本地 ShellExecute/Open With 回退不变。新增 ABI v1 操作、能力位 `1 << 6` 和第八个导出后，配置 20 / schema 17 确认模块 ABI 2、能力 127、同次哈希一致，两个目标文件警告、Explorer 启动与完整 `always-throw` 均为 0；最终定向测试 98/98、x64 全量测试 2049/2049 通过。剩余 IL2026/IL2072/IL3050 从 44/4/77 降为 34/2/62；显式 Rust JIT 真实打开矩阵仍是进入 4D-4B 前的人工门槛。
17. **4D-4B Quick Access Rust/AOT 边界已完成自动化与 AOT 审计。** 查询、`pintohome` 和 `unpinfromhome` 使用独立强类型 Rust v1 操作；普通 JIT 默认保留 C# oracle，Native AOT 排除整段 ProgID/dynamic 代码。能力位增加 `1 << 7`，模块变为能力 255、九个必需导出；Rust 52/52、4D-4B 契约 12/12、扩大定向 89/89、x64 全量 2061/2061 通过。配置 21 / schema 18 确认目标警告、Quick Access 与完整 `always-throw` 均为 0，原始 IL2026/IL2072/IL3050 归零。自动化只做了真实只读查询，没有改变用户 Quick Access 状态；固定/取消固定矩阵仍为人工交互边界。
18. **4D-5 托盘反射收口已完成自动化与 AOT 审计。** 托盘 identity 直接读取公开 `TaskbarIcon.TrayIcon.WindowHandle/Id`；SecondWindow 菜单通过公开打开事件、菜单项 `Loaded` 与 WinUI 视觉树同步真实 presenter/Popup，不再反射私有 `ContextMenuFlyout`。4D-5 契约 6/6、x64 全量 2067/2067 通过；配置 22 / schema 19 确认旧反射模式、缺失公开契约、目标告警、非预期告警和完整 `always-throw` 均为 0，IL2075 从 9 降到 0。没有修改 Rust ABI、能力或九个导出；AOT 主程序仍未启动，Debug 右键菜单可见行为保留为人工 UI 确认项。
19. **4E-0 搜索历史 WMC1506 收口已完成自动化与 AOT 审计。** `SearchHistoryEntry.Query/DeleteLabel` 保持 `required init`，刷新、语言切换、删除和清空都通过清空并重建条目更新 UI；对应 4 条 Query 和 2 条 DeleteLabel 绑定由 `OneWay` 精确改为 `OneTime`。6 条契约在旧实现上 4 失败/2 通过，实施后 6/6；x64 全量 2073/2073 通过。配置 23 / schema 20 确认 WMC1506 6→0、WMC1510 保持 1265，旧绑定、缺失新绑定/生命周期模式、目标告警、非预期告警和完整 `always-throw` 均为 0。Rust ABI、能力和九个导出未变化；AOT 主程序仍未启动，搜索历史点击、删除和语言切换保留为人工 UI 确认项。
20. **4E-1 叶子 compiled-binding pilot 已完成自动化与 AOT 审计。** `PinStateIcon` 2 条 Foreground、`MarkdownSourceEditor` 3 条自有 DependencyProperty 及两个桌面整理路径 Tooltip 共 7 条 Binding 改为 OneWay typed `x:Bind`；生成代码确认全部注册对应 DependencyProperty 变化回调。8 条契约在旧实现上 6 失败/2 通过，实施后 8/8；x64 全量 2081/2081 通过。配置 24 / schema 21 确认 WMC1510 1265→1258，四个目标 XAML、旧绑定、缺失新绑定/生命周期、延期范围、非预期告警和完整 `always-throw` 均为 0。Rust ABI、能力和九个导出未变化；AOT 主程序仍未启动，前景色、Markdown 属性和路径 Tooltip 保留为人工 UI 确认项。
21. **4E-2 自有属性 compiled binding 已完成自动化与 AOT 审计。** `MusicTransportIcon` 的 7 条 Foreground 与 `WidgetInlineEditor` 的 8 条自身属性 Binding 全部改为 typed `x:Bind`；其中编辑器 Text 保留 TwoWay/PropertyChanged，生成代码确认 `TextBox.TextProperty` 变化即时写回控件 Text。9 条契约在旧实现上 6 失败/3 通过，实施后 9/9；x64 全量 2090/2090 通过。配置 25 / schema 22 确认 WMC1510 1258→1243，两个目标 XAML、旧绑定、缺失新绑定/行为、延期范围、非预期告警和完整 `always-throw` 均为 0。Rust ABI、能力和九个导出未变化；AOT 主程序仍未启动，音乐图标和 Quick Capture/Todo 编辑语义保留为人工 UI 确认项。
22. **4E-3 typed DataTemplate 小批次已完成自动化与 AOT 审计。** `AttachmentTileStrip` 的 7 条使用 OneWay typed `x:Bind`；`SearchPopupWindow` 四个模板中只有 Tab Count 使用 OneWay，其余 6 条不可变或显式刷新字段使用 OneTime。生成代码确认附件通知与 Count 监听准确，推荐应用延迟图标仍由两处既有代码回填。11 条契约在旧实现上 6 失败/5 通过，实施后 11/11；x64 全量 2101/2101 通过。配置 26 / schema 23 确认 WMC1510 1243→1229，目标源、旧绑定、缺失 compiled binding/类型/行为、延期范围、非预期告警和完整 `always-throw` 均为 0。Rust ABI、能力和九个导出未变化；AOT 主程序仍未启动，附件与搜索模板可见行为保留为人工 UI 确认项。
23. **4E-4 typed ViewModel 桥接已完成自动化与 AOT 审计。** `FileWidgetSettingsSection` 增加显式 `SettingsViewModel` DependencyProperty，父窗口在初始化后赋值并在 dispose 前清空；摘要和两组选项使用 3 条 OneWay，两个 `SettingsComboBox.Value` 保持 2 条 TwoWay。生成代码自动追踪 ViewModelProperty、切换嵌套 PropertyChanged 监听，并为两个附加 ValueProperty 生成目标回写；多余的手动 `Bindings.Update()` 已删除。11 条契约在旧实现上 8 失败/3 通过，实施后 11/11；扩大定向 124/124、x64 全量 2112/2112 通过。配置 27 / schema 24 确认 WMC1510 1229→1224，目标源、桥接、生命周期、行为、延期范围、非预期告警和完整 `always-throw` 均为 0。Rust ABI、能力和九个导出未变化；设置页可见行为仍是人工 UI 确认项。
24. **4E-5 搜索结果行 compiled binding 已完成自动化与 AOT 审计。** public typed Item DependencyProperty 的真实 XAML 编译会生成违反 required Kind/Title 的 `SearchResultItem` activator，并给非 observable 条目桥增加 WMC1506，因此没有削弱 DTO。最终使用 internal typed Item 投影，每次 ElementPrepared 调用 `Bindings.Update()`，8 条叶子使用 OneTime compiled `x:Bind`，并保留 Icon/Size/Date 手工刷新和异步引用一致性检查。12 条契约在旧实现上 5 失败/7 通过，实施后 12/12；扩大定向 198/198、x64 全量 2124/2124 通过。配置 28 / schema 25 确认 WMC1510 1224→1216，目标源、桥接、回收顺序、模型、生命周期、延期范围、非预期告警和完整 `always-throw` 均为 0。Rust ABI、能力和九个导出未变化。
25. **5A x64 AOT 隔离启动与基础存活已完成。** 新增仅 Debug/AOT 可显式覆盖的数据根分支和 profile 29 / schema 26 严格启动器；旧摘要、正式数据根、哈希不一致和非精确进程目标均在启动前拒绝。9 条新契约先 9/9 失败，实施后 9/9；组合定向 20/20、x64 全量 2133/2133 通过。受审计 AOT 产物在独立根中完成首次启动、单实例、托盘正常退出和重启，正式数据目录运行前后保持 122 个文件、303,016,768 bytes 和同一元数据指纹。该阶段当时开放 5B-1，没有恢复大规模 Binding 清理，也没有提前开始 Rust `SearchCore`。
26. **5B-1 shortcut AOT → Rust 真实边界冒烟已完成。** 新增 NativeAOT-only 显式场景入口与严格运行器，在五个独立 preview 根中完成 Core、有效目标、取消、同卷移动自动修复和删除；两个模态窗口的实际 owner 均与记录的托盘 HWND 相同。真实运行发现并修正同卷修复夹具、失败进程清理和 JSON 固定清单三项遗漏。Rust 52/52、x64 全量 2141/2141 通过；profile 30 / schema 27 保持 WMC1510=1216、`always-throw=0`、Rust ABI 2/能力 255/九个导出。该批当时开放 5B-2A Explorer 启动与 Quick Access 只读查询；pin/unpin 留到独立 5B-2B。
27. **5B-2A Explorer 启动与 Quick Access 只读 AOT → Rust 冒烟已完成。** Explorer 产品服务实际执行一次性命令并生成标记，原生 status/HRESULT 为 0、阶段掩码为 `0x7F`；Quick Access 公共查询前后和原生查询均为 `NotPinned`，原生阶段掩码为 `0x3F`，没有执行任何 pin/unpin。8 条新契约先 8/8 失败，实施后 8/8；组合定向 20/20、Rust 52/52、x64 全量 2149/2149 通过。profile 31 / schema 28 保持 WMC1510=1216、`always-throw=0`、Rust ABI 2/能力 255/九个导出。该批当时开放 5B-2B；补偿性 unpin 和最终 `NotPinned` 是完成门禁。
28. **5B-2B Quick Access pin/unpin 与故障补偿 AOT → Rust 冒烟已完成。** 独立 preview 根中的稳定目标先后覆盖正常 `NotPinned → Pinned → NotPinned`、固定后应用内主动失败、固定后受审计进程强制终止，以及新 AOT 进程观察 `Pinned` 后补偿到 `NotPinned`。11 条契约最终 11/11、Rust 52/52、x64 全量 2160/2160；profile 32 / schema 29 保持 WMC1510=1216、`always-throw=0`、Rust ABI 2/能力 255/九个导出。六个实际 AOT 进程均按精确 EXE 路径清理，正式数据指纹不变。下一批拆为 5B-3A 音乐音量只读 getter；系统与 session setter 分别留给有原值持久化和独立补偿的后续小批次。
29. **5B-3A 音乐音量只读 AOT → Rust 冒烟已完成。** 产品系统 getter、产品 snapshot 与直接原生明细读取均成功，系统音量前后为 `0.370000004768372`；本机无匹配 session，因此明确只证明 `HasSessionVolume=false` 路径。10 条新契约、Rust 52/52、x64 全量 2170/2170；profile 33 / schema 30 保持 WMC1510=1216、`always-throw=0`、Rust ABI 2/能力 255/九个导出。
30. **5B-3B 系统主音量 setter 与故障恢复 AOT → Rust 冒烟已完成。** 每次写入前原子持久化并回读原值，只通过产品 setter 把 `0.370000004768372` 临时改为约 `0.42`，直接 Rust getter 负责复查。应用内主动异常由 App `finally` 恢复；强制结束后，独立新 AOT 进程先读取到约 `0.4200000167` 再恢复原值。10 条新契约、Rust 52/52、x64 全量 2180/2180；profile 34 / schema 31 保持 39 个发布文件、WMC1510=1216、`always-throw=0`、Rust ABI 2/能力 255/九个导出。恢复意图最终不存在，正式数据指纹前后一致。
31. **5B-3C 可控媒体 session getter/setter 与故障恢复 AOT → Rust 冒烟已完成。** 测试专用 Rust 夹具循环播放全零 PCM，提供固定 `deskbox-audio-session-fixture` 身份且随父脚本退出；它不进入产品发布，也不控制用户播放器。产品与直接 Rust getter 均确认 match kind 4，只通过产品 session setter 完成 `1.0 → 0.92 → 1.0`；应用内异常、强制终止后的独立恢复、session 消失保留意图、系统主音量不变和最终 postflight 均有门禁。12 条新契约、Rust 54/54、x64 全量 2192/2192；profile 35 / schema 32 保持 39 个发布文件、WMC1510=1216、`always-throw=0`、生产 Rust ABI 2/能力 255/九个导出。JSON 清单为 22 个文件、57/57 处调用和 20 个 context 所有者。下一批开放 5B-4 x64 Native AOT 托管 UI 功能矩阵。
32. **5B-4A 基础托管 UI 只读矩阵已完成。** 受审计 AOT 产物在 owned preview 根恢复 File/Search 两个 Widget，装载 12 套语言资源，依次打开六个设置主分区，并对搜索六类筛选与四列各两次排序完成真实 handler 核对。首次实际运行发现并窄修复空私有 managed 数组的 WinRT `ItemsSource` 投影。12 条新契约、x64 全量 2204/2204；profile 36 / schema 33 保持 `always-throw=0`，JSON 清单更新为 23/58/21。
33. **5B-4B1 深层设置与 managed collection 只读矩阵已完成。** 非空设置搜索激活 `BackupRestoreSettings`，24 个深层设置路由、嵌套 breadcrumb/父页返回、1 条文件叠放规则和非空备份清单均由真实 AOT UI 证明。设置 ViewModel 建立 282/282 精确生成绑定清单；DataTemplate 条目和三类失败集合增加窄的生成元数据或 `object[]` UI 投影，WMC1510 降至 1211。12 条新契约、Rust 54/54、x64 全量 2216/2216；profile 37 / schema 34 保持 39 个发布文件、WMC1506=0、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批拆为 5B-4B2A 设置与 Widget 拓扑持久化/重启恢复。
34. **5B-4B2A 设置与 Widget 拓扑持久化/重启恢复已完成。** 同一受审计 AOT 产物依次启动 `Mutate`、`VerifyRestore`、`Postflight` 三个不同进程，通过真实 Settings ViewModel 和 Widget 产品路径完成四类设置以及固定 File Widget 标题、视图、锁定和 HWND 边界的写入、重载、恢复与再次重载。三次显式 flush 和 3/3 应用内正常退出均通过；Search Widget 对照项不变，owned preview 根最终清理，正式数据指纹不变。12 条新契约、全部 AOT 阶段契约 208/208、Rust 54/54、x64 全量 2228/2228；profile 38 / schema 35 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批拆为 5B-4B2B1 Quick Capture 内容 store。
35. **5B-4B2B1 Quick Capture 内容 store 已完成。** 同一受审计 AOT 产物依次启动 `Mutate`、`VerifyDelete`、`Postflight` 三个不同进程，经真实 Quick Capture surface/ViewModel 完成新建详情 pending flush、已有详情 600 ms 自动保存、托管文本附件导入、跨进程重载、第二次显式 flush、附件及物理文件删除、记录删除和空 store postflight。首次真实 AOT 运行发现附件 `IReadOnlyList` 的 WinRT `ItemsSource` 投影返回 `E_INVALIDARG`，最终只在 UI 边界改用 `object[]`，typed 业务集合和持久化格式不变。14 条新契约、全部 AOT 阶段契约 222/222、Rust 54/54、x64 全量 2242/2242；profile 39 / schema 36 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批拆为 5B-4B2B2A Todo 核心任务与备注持久化。
36. **5B-4B2B2A Todo 核心任务与备注持久化已完成。** 同一受审计 AOT 产物依次启动 `Mutate`、`VerifyDelete`、`Postflight` 三个不同进程，经真实 Todo surface/ViewModel 完成任务创建、标题修改、600 ms 备注自动保存、完成、跨进程重载、显式备注保存、恢复未完成、删除和空 store postflight。首次真实运行发现 Todo 核心 DataContext 缺少 AOT 属性提供器，最终只为 `TodoWidgetViewModel` 与 `TodoItemViewModel` 增加 NativeAOT-only 生成绑定；步骤与附件类型继续延期。自动化场景改用 Markdown 编辑器公开 `Text` 契约，并与真实编辑事件共用 `ScheduleNotesAutoSave`，不直接调用 timer tick 或 store。15 条新契约、全部 AOT 相关测试 270/270、Rust 54/54、x64 全量 2257/2257；profile 40 / schema 37 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批拆为 5B-4B2B2B1 Todo 步骤持久化。
37. **5B-4B2B2B1 Todo 步骤持久化已完成。** 同一受审计 AOT 产物以三个不同进程完成任务与步骤创建、步骤文本修改、完成、跨进程重载、恢复未完成、步骤删除、任务删除和空 store postflight；store、ViewModel 和真实 DataTemplate 行的 DataContext/文本/复选框/透明度同时取证。首次真实运行发现 typed `ObservableCollection<TodoStepViewModel>` 没有进入 WinRT `ItemsSource`，最终保留强类型业务集合，只在 UI 边界增加可刷新的 `object[] StepItemsSource`，并为实际步骤 DataContext 增加 NativeAOT-only 生成绑定。后续运行又修复共享证据硬编码上一阶段 fixture ID，以及重启取证早于实际步骤行布局完成的偶发时序；最终使用显式 widget ID 和真实行条件等待，新旧 Todo 三进程矩阵均通过。17 条新契约、全部 AOT 相关测试 287/287、Rust 54/54、x64 全量 2274/2274；profile 41 / schema 38 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批拆为 5B-4B2B2B2 Todo 托管附件生命周期。
38. **5B-4B2B2B2 Todo 托管附件生命周期已完成。** 三个全新 AOT 进程完成任务创建、owned 文本附件托管导入、SHA-256、真实附件卡片、跨进程重载、显式附件删除、物理文件清理、任务删除和空状态 postflight；新矩阵连续两轮通过，旧 Todo 核心与步骤矩阵也重新通过。实际运行修复了共享宿主未识别第三个 Todo fixture，以及 PowerShell 空/单元素文件结果没有稳定数组形状两项遗漏。21 条新契约、全部 AOT 相关测试 308/308、Rust 54/54、x64 全量 2295/2295；profile 42 / schema 39 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。附件复制和哈希均为流式路径，本轮没有可量化的 Rust 内存收益，因此不扩展 ABI。下一批拆为 5B-4B2C1 Glance 本地图片与偏好持久化。
39. **5B-4B2C1 Glance 本地图片与偏好持久化已完成。** 三个全新 AOT 进程完成 owned PNG 选择、显示/布局/播放偏好写入、真实 ImageBrush 与布局/遮罩/操作层取证、跨进程重载、空图片基线恢复和 postflight；三个进程均自然退出，正式数据指纹与图片 SHA-256 不变。Glance 运行时 Binding 只在 AOT 下增加一组 33 属性生成桥，普通 JIT 表面不变。16 条新契约、全部 AOT 相关测试 324/324、Rust 54/54、x64 全量 2313/2313；profile 43 / schema 40 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批拆为 5B-4B2C2A 天气设置与 Widget 视图元数据持久化；真实天气 surface 另设 C2B 确定性夹具门。
40. **5B-4B2C2A 天气设置与 Widget 视图元数据持久化已完成。** 三个全新 AOT 进程完成手动城市/经纬度、温度和风速单位、默认视图、皮肤、七项指标显隐、刷新间隔与固定 Weather Widget Day/Week metadata 的写入、重载、基线恢复和 postflight；全局默认与 per-widget 覆盖使用相反值证明相互独立。Weather 配置保留但 feature 在 owned fixture 中关闭，三次均没有 Weather HWND/XamlRoot，也没有 WeatherService/ViewModel 初始化日志。11 条新 AOT 契约、全部 AOT 相关测试 335/335、Rust 54/54、x64 全量 2330/2330；profile 44 / schema 41 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批开放 5B-4B2C2B 确定性天气 surface。
41. **5B-4B2C2B 确定性天气 surface 已完成。** 三个全新 AOT 进程使用严格受控的 NativeAOT-only WeatherData 夹具，真实加载 Weather HWND/XamlRoot、Expanded/Compact、Day/Week、Rich/Standard、摄氏/华氏、km/h/mph、UV/气压显隐及 24/7 非空集合；变更、重载、基线恢复和 postflight 均逐字段通过，6 条 fixture 日志、0 条网络日志，正式数据指纹不变且 preview 根已清理。完成后审计补齐 Compact 和隐藏态，并由真实运行修正三个指向标签而非数值的取证点。13 条新 AOT 契约、全部 AOT 相关测试 348/348、Rust 54/54、x64 全量 2343/2343；profile 45 / schema 42 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批开放 5B-4C1A owned 本地文件 surface 与核心文件操作。
42. **5B-4C1A owned 本地文件 surface 与核心操作已完成。** 三个全新 AOT 进程在精确 owned 树中证明真实 File Widget HWND/XamlRoot、非空 `FileItemSurface`、文件/文件夹类型、文件夹优先且同类 Name 升序、目录进入/返回、watcher、copy/move/rename、`IOException` 重名失败、递归 SHA-256、重载、恢复和 postflight；3/3 自然退出，失败/延期路径日志为 0，正式数据指纹不变且 preview 根已清理。实际运行先后修正隐藏扩展名 UI 期望、折叠导航栏缓存文本证据和 `FileItemSurface` 六项 NativeAOT 属性表，完成后审计再补齐真实排序/类型门禁；最终运行还修正了契约把文件夹优先顺序误写成纯名称顺序的问题，产品排序未改。12 条新 AOT 契约、全部 AOT 相关测试 360/360、Rust 54/54、x64 全量 2355/2355；profile 46 / schema 43 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`、Rust ABI 2/能力 255/九个导出。下一批拆为 5B-4C1B1 owned 回收站删除、精确恢复与 File Widget 菜单路由。
43. **5B-4C1B1 owned 回收站删除、精确恢复与 File Widget 菜单路由已完成。** 三个全新 AOT 进程经真实单选/多选菜单进入产品 `SHFileOperationW` 删除，新进程分别确认三个 owned 项目唯一匹配并经 Rust 粗粒度边界恢复，第三进程确认原路径、长度、SHA-256 和精确残留 0；正常矩阵连续两轮通过。代码复盘修正了恢复在首个匹配后提前执行和公共 C 头文件漏同步；首次真实运行还触发独立补偿并暴露 PowerShell 单元素数组与 recovery sibling 清理遗漏，补偿后所有项目均恢复。12 条新 AOT 契约、全部 AOT 相关测试 372/372、Rust 57/57、x64 全量 2367/2367；profile 47 / schema 44 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`，Rust ABI 2/能力 511/十个导出。下一批拆为 5B-4C1B2A Shell move/progress，再以 5B-4C1B2B 单独验证 Properties owner。
44. **5B-4C1B2A owned Shell move/progress、真实 owner、取消与延迟返回已完成。** File Widget 菜单和拖出回退现在把真实 host HWND 传过 ViewModel/Organizer 到 `SHFileOperationW`。Mutate 进程以四次真实菜单 Automation Invoke 覆盖一次实际 Shell move、2 计划/1 完成、1 计划/0 完成和文件已完成但任务晚到；结果、反馈和历史 item count 分别为 `1/1/0/1`、`Success/Success/Info/Success` 和 `1/0/1/1`。VerifyRestore 与 Postflight 在两个新进程中证明内容哈希和空历史基线；两个全新 run ID 均 3/3 自然退出、正式数据指纹不变、运行错误 0、preview/recovery 根清理。11 条新 AOT 契约、全部 AOT 相关测试 383/383、Rust 57/57、x64 全量 2378/2378；profile 48 / schema 45 保持 39 个发布文件、WMC1506=0、WMC1510=1211、`always-throw=0`，Rust ABI 2/能力 511/十个导出。下一批拆为 5B-4C1B2B 系统 Properties owner。

45. **5B-4C1B2B 系统 Properties 与窗口关闭已完成。** 两个全新 AOT 进程经真实菜单调用 `SHObjectProperties`，精确 API owner 等于 File Widget HWND，并记录系统 `#32770` 与同进程 `StubWindow32` 代理 owner；受控关闭、只读哈希、自然退出和双根清理均通过。profile 49 / schema 46、AOT 395/395、x64 2390/2390、Rust 57/57。
46. **5B-4C1C1 Picker 与 Clipboard StorageItems 已完成。** 两个独立 run ID 均由三个全新 AOT 进程完成真实现代 picker 取消/选择、精确 owner、文件和文件夹 StorageItems、产品导入、重载、恢复和 postflight。全局剪贴板未被自动化修改；12 条新契约、AOT 407/407、x64 2402/2402、Rust 57/57，profile 50 / schema 47，Rust ABI 保持不变。下一批只开放 5B-4C1C2 OLE/native drop 与真实 Explorer 物理拖放。
47. **5B-4C1C2A OLE/native drop 自动化边界已完成。** 三个全新 AOT 进程通过 generated COM CCW 和真实 HDROP vtable 覆盖 pointer 越界、`DragLeave`、Ctrl copy、无 Ctrl move、384 MiB 进度层、重载、恢复和 postflight；进度卡为 `ZIndex=1000`、`TranslationZ=64` 和 `AcrylicBrush`。10 条新契约、AOT 417/417、x64 2412/2412、Rust 57/57，profile 51 / schema 48，Rust ABI 保持不变。`PhysicalExplorerMouseVerified=false`；下一批只开放 5B-4C1C2B 真人 Explorer 物理鼠标与视觉验收。
48. **5B-4C2A 主/搜索热键与保留 hook 自动化边界已完成。** 两个全新 AOT 进程通过真实 `RegisterHotKey`、标准手势 `SendInput` 和结构化计数器证明主/搜索热键各接收与调用一次、调度失败为 0、冲突回滚、禁用/重新启用及前一进程退出后的重新注册；Win+Space 只证明 hook 线程创建与停止，不注入保留手势。搜索热键的设置提交改为以真实系统注册为提交点。6 条阶段契约、2 条产品安全契约、AOT 423/423、x64 2420/2420、Rust 57/57，profile 52 / schema 49，Rust ABI 保持不变。物理键盘和录制器归入 5B-4C2B。
49. **5B-4C3A Todo recurrence/reminder 确定性状态矩阵已完成。** 五个全新 AOT 进程使用固定 clock、owned settings/store 和 callback-only 通知边界，覆盖默认/单项偏移、reminder-off、已完成、stale overdue、snooze 前后、完成并生成次日 occurrence、下一 occurrence 提醒、跨进程 dismissal、清空和 postflight；检查计数为 `2,0 / 0,1,0 / 0,1,0 / 0 / 0`，五次自然退出、正式数据指纹不变、系统通知触发为 0。6 条新阶段契约、AOT 429/429、x64 2426/2426、Rust 57/57，profile 53 / schema 50；JSON 清单为 25/60/23，Rust ABI 2 / 能力 511 / 十个导出不变。下一批拆为 5B-4C3B1 通知注册、payload、展示与清理，再以 C3B2 验证 activation 和单实例转发。
50. **5B-4C3B1 Todo 原生通知展示与清理生命周期已完成。** 三个全新 AOT 进程使用唯一 run ID、两个 tag 和一个 group，完成单项/聚合产品通知的真实展示、系统历史枚举、单项两个动作与四个 snooze 选项核对、聚合无动作、跨进程历史恢复、逐条精确删除、注销和无残留 postflight；计数为 `0→2 / 2→0 / 0→0`，三次自然退出、正式数据指纹不变、activation 为 0。6 条新阶段契约、AOT 435/435、x64 2432/2432、Rust 57/57，profile 54 / schema 51；JSON 清单为 26/61/24，Rust ABI 2 / 能力 511 / 十个导出不变。实际 payload 使用 `;` 分隔参数，因此下一批调整为 5B-4C3B2A 先修正 grammar 并验证确定性动作路由，再以 C3B2B 验证真实 activation 和单实例转发。
51. **5B-4C3B2A activation grammar 与确定性动作路由已完成。** 三个全新 AOT 进程在固定 clock、owned store 和受控输入下覆盖 `;`/`&`、正文打开、Complete、四种 Snooze、旧动作、非法输入拒绝、幂等、跨进程持久化与清空；profile 55 / schema 52，AOT 441/441、x64 2447/2447、Rust 57/57，JSON 清单为 27/62/25。该证据不声称真实 Windows activation 或 Todo surface。
52. **5B-4C3B2B1 类型化 activation envelope 与单实例转发已完成。** 产品第二实例现在原子保存 arguments、完整 `UserInput`、来源 PID 和信封元数据，主实例在 Todo 服务就绪后排空队列；重复、损坏、旧格式、冷启动恢复、消费删除、异常退出遗留 claim 恢复和 128 条批次续排均有门禁。AOT 矩阵使用四个主进程和一个真实第二进程，精确证明 `30m` 冷启动结果与 `tomorrow` 单实例转发结果；成功 run ID `731d469f4233482d84ae9a721350c1dd`，profile 56 / schema 53，AOT 447/447、x64 2462/2462、Rust 57/57，JSON 清单为 29/65/27，Rust ABI 2 / 能力 511 / 十个导出不变。真实 Windows 通知点击和 Todo surface 可见定位/刷新留给 5B-4C3B2B2。
53. **5B-4C3B2B2A Todo activation 真实 surface 定位与可见刷新已完成。** 正文路由现在等待真实 Todo 控件 Loaded、`XamlRoot`、精确 item 定位和两帧提交后才返回成功；Complete/Snooze 分开记录业务写入与可见刷新。单进程 AOT 矩阵在同一 HWND 上证明正文选中、Complete 状态和 `30m` Snooze 的 surface 状态，成功 run ID `69402a1914814f778abdfc29daf1b4f5`，自然退出且正式数据指纹不变。profile 56 / schema 53，AOT 452/452、x64 2468/2468、Rust 57/57，JSON 仍为 29/65/27，Rust ABI 不变。受控输入明确保持 `UserClickVerified=false`；下一批为 5B-4C3B2B2B1 运行中主实例的真实 Windows 通知点击。

建议保持当前 JIT 版本为正式发布基线，逐批形成 AOT 内部预览；直到 x64 功能矩阵、安装升级和真实机器运行都通过后，再切换默认发布形态。

## 2. 旧方案与当前代码的核对

| 旧方案判断 | 当前证据 | 修订结论 |
| --- | --- | --- |
| Windows App SDK 已具备 Native AOT 条件 | 当前项目使用 Windows App SDK 2.2.0；Native AOT 支持早已在 Windows App SDK 1.6 引入 | 判断成立，不需要为了“获得 AOT 能力”先升级 SDK |
| AOT 可以做可选实验 | x64 发布已经能产出主程序，但有大量兼容性问题 | 从“可选实验”调整为“确定目标、分批迁移、JIT 保底” |
| 103 个 `[ObservableProperty]` 字段都要迁移 | 初始有 24 个 public partial property 和 79 个字段式声明；阶段 2 已迁移 `SettingsViewModel` 67 个、`SearchPopupViewModel` 12 个 | 103 处现在全部是 public partial property，最新 AOT 日志中 `MVVMTK0045` 为 0 |
| 49 处 `JsonSerializer` 调用、没有 source-generation context | 4B-0 精确冻结为 49 处、16 个文件；4B-1 至 4B-3C 已迁移全部产品调用并使用 14 个分域 context，4B-4 已在默认反射关闭的审计构建中验证；后续 AOT runner 全部显式使用独立或复用的 source-generated context | 初始判断成立；当前为 26 个文件、61/61 处 source-generated 调用、24 个 context 所有者、0 处反射型重载，产品持久化格式未因 evidence context 改变 |
| 传统 COM 是 AOT 风险 | 源码当前有 19 个 `[ComImport]`，分布在 6 个文件；初始 ILC 确认的 4 个 COM coclass `always-throw` 已由 3C-3、4A、4C 全部消除，4D-3A/3B 又完成 OLE 数据与注册侧 | 风险已由静态推测升级为逐条审计；JIT-only oracle 可以保留，但 AOT 可达的内置 COM marshalling 仍需替换或封装 |
| `DllImport` 不等于全部不兼容 | 当前有 92 个 `DllImport`、77 个 `LibraryImport`；普通 blittable Win32 调用不是主要阻断点，4D-3B 已将最后一个 COM 接口参数 IL2050 清零 | 继续保留适合的 P/Invoke；优先处理真实 dynamic/COM marshalling 阻断，不按数量机械迁移 |
| 搜索索引最适合 Rust | 当前仍有 300,000 条上限、完整路径 `Dictionary` 和查询全表扫描；但持久化已经采用紧凑二进制格式和目录池 | 性能候选判断仍成立，但 Rust 范围应聚焦常驻数据结构与查询，不要重做已经优化的持久化层 |
| Glance 调色板不值得 Rust 化 | 当前仅对最多 40×40 采样，缓存规模也很小 | 判断仍成立，保持 C# 实现 |
| AOT 主要工作是属性、JSON、COM | 当前产品 XAML 有 1,295 处 `{Binding}`、82 处 `{x:Bind}`，干净发布产生 1,211 条 `WMC1510`；5B-4B1 另为设置页建立 282/282 精确生成绑定清单 | 旧方案遗漏了 XAML/WinRT 绑定和 managed collection 投影；继续以真实 AOT 路径驱动窄修复，不按总数机械迁移全部 Binding |

## 3. 当前代码与工具链基线

### 3.1 项目配置

- 主项目和更新器目标均为 `net10.0-windows10.0.22621.0`。
- 支持普通 JIT 的 `win-x64` 和 `win-arm64`；现有 `DeskBoxAotAudit` 是显式 opt-in，不改变默认 Debug/Release。3C-3-R 后，Native AOT 暂只允许 `Platform=x64`、`RuntimeIdentifier=win-x64` 且显式启用 Rust 模块；ARM64 AOT 在阶段 7 前由项目和审计脚本共同 fail fast。
- 主项目使用 Windows App SDK 2.2.0；4A 已直接使用当前解析到的 `FolderPicker(WindowId)`、`PickSingleFolderAsync()` 和 `PickFolderResult.Path`，没有为此升级 SDK。
- `global.json` 已固定 .NET SDK 10.0.303，并允许 `latestPatch` 范围的补丁滚动。
- 仓库 override 已实际验证为 Rust 1.96.0 MSVC x64；`rustc`、`cargo`、`clippy`、`rustfmt` 和 `x86_64-pc-windows-msvc` target 均可用。`aarch64-pc-windows-msvc` 保留到阶段 7。
- 当前 Cargo workspace 包含生产 `deskbox-native` crate 和仅供 5B-3C 测试的静音音频 session 夹具；生产模块覆盖五类 shortcut 操作、音乐音量、Explorer 托管启动、Quick Access 以及内部回收站精确查询/恢复，ABI 为 2，x64 能力掩码为 511、必需导出为十个。AOT 编译以 `DESKBOX_NATIVE_AOT` 排除四类旧 C# COM/dynamic oracle，普通 JIT 默认仍保留它们；产品回收站删除仍使用 C# `SHFileOperationW`，测试夹具不进入产品发布。

后续升级 Windows App SDK 可以单独评估，但不应与首批 AOT 兼容性修改合并。当前 2.2.0 已足以推进现有 AOT 兼容性工作；分开升级更容易判断警告、运行行为和安装依赖变化由哪一项引起。

### 3.2 静态清单

| 项目 | 当前数量 | 说明 |
| --- | ---: | --- |
| `[ObservableProperty]` | 103 | 当前全部为 public partial property；字段式声明为 0 |
| `JsonSerializer.*` 调用 | 65 | 29 个文件；原 49 处产品调用及后续隔离 evidence/恢复意图/activation envelope 调用均为 source-generated，27 个 context 所有者，反射型调用为 0 |
| `[ComImport]` | 19 | 6 个文件；4D-3A 删除 `COMIDataObject`，4D-3B 删除 OLE 注册侧 `IDropTarget` |
| `[LibraryImport]` | 77 | 主要集中在 Win32/USN 边界；4D-3B 新增显式指针型 Register/Revoke 源生成 P/Invoke |
| `[DllImport]` | 92 | 需要按签名审计，不能按数量直接判定不兼容；4D-3B 删除两个内置 COM marshalling 注册签名 |
| XAML 文件 | 34 | 其中 14 个文件使用传统 Binding |
| `{Binding}` | 1,300 | 4E-1 至 4E-5 累计移除 49 条；设置、Todo、快速记录、天气、音乐和文件组件最集中 |
| `{x:Bind}` | 77 | 4E-1 至 4E-5 累计增加 49 条 compiled binding；当前有 6 处 `x:DataType` |
| 测试属性标记 | 1,430 | `[Fact]`/`[Theory]` 静态计数，不等于参数化后的实际用例数；5B-2B x64 实际执行 2160 个用例 |

### 3.3 搜索索引现状

`src/DeskBox/Services/SearchIndexService.cs` 中仍有以下特征：

- `MaxIndexEntries = 300_000`；
- 常驻索引是以完整路径为键的 `Dictionary<string, IndexedFileEntry>`；
- 查询阶段遍历 `_index`，计算相关度后用 `PriorityQueue` 保留前 N 个结果，复杂度仍接近 O(N)；
- 索引持久化已经有 `DBIX` 紧凑二进制格式、目录池和空闲卸载/恢复逻辑。

因此，后续 Rust `SearchCore` 应接管“紧凑常驻表示 + 查询 + 增量更新”，文件监控、USN、异步调度和 UI 状态继续由 C# 管理。没有必要在 Rust 中重新实现现有持久化协议，除非基准证明解析本身是主要瓶颈。

## 4. Native AOT 实际发布探测

### 4.1 探测命令

当前仓库已经提供可重复执行的审计入口。脚本会为每次审计建立独立的 .NET 中间工件、Rust staging 和 Cargo target 目录，分别恢复主程序与更新器，再执行发布，避免普通 `obj` 或共享 `native/target` 缓存掩盖 XAML、分析警告或原生模块重建：

```powershell
.\scripts\publish-aot-audit.ps1 -Platform x64
```

以下命令保留为直接 x64 AOT 发布的最小示例。必须显式带上
`DeskBoxRustNative=true`；3C-3-R 后省略该属性会由项目在编译前拒绝，不再生成编译期只
保留 Rust shortcut 路径、却没有构建/复制 Rust DLL 的不完整输出。日常审计仍应优先使用
上面的脚本：

```powershell
dotnet publish .\src\DeskBox\DeskBox.csproj `
  --configuration Release `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -p:PublishAot=true `
  -p:DeskBoxRustNative=true `
  -p:SelfContained=true `
  -p:WindowsAppSDKSelfContained=false `
  -p:PublishSingleFile=false `
  -o .\.artifacts\aot-audit\x64 `
  -v:minimal
```

初始探测结果：发布过程完成，0 个错误、449 个警告，用时约 1 分 30 秒。首次干净发布还产生了大量 XAML `WMC1510` 绑定警告。阶段 4B-4 后的最新审计见 4.4 节。

本次没有启动 AOT 主程序。因此这里的证据等级是“编译、测试、ABI 和产物检查”，不包含 AOT 运行验证。

### 4.2 已确认的硬阻断项

初始 ILC 明确报告以下构造函数会始终抛异常：

1. `ShortcutHelper.ShellLink`；
2. `FolderPickerService.FileOpenDialog`；
3. `DragDropPermissionService.ShellLink`；
4. `MusicVolumeService.MMDeviceEnumeratorComObject`。

阶段 3C-3 通过 `PublishAot=true` 驱动的编译期边界排除了第 1、3 项；阶段 4A 又以
Windows App SDK 现代 Picker 替换第 2 项；阶段 4C 以完整 Rust Core Audio 边界替换第 4 项。
最新隔离 AOT 审计的完整 `always-throw` 为 0。旧 C# Shell Link 和音乐音量实现仍保留在普通
JIT 中作为差分 oracle，不再进入 AOT 编译单元；旧 FolderPicker COM 实现则已完整删除。

此外还有：

- 4D-2 已删除零调用的 `FileOperationHelper`，其 `CoCreateInstance` 和
  `SHCreateItemFromParsingName` 两个 IL2050 来源已清零；
- 4D-3B 已把 `NativeDropTarget.RegisterDragDrop` 改为源生成 CCW 和显式接口指针，最后一个
  COM 接口参数 `IL2050` 已清零；
- `ExplorerShellLaunchService` 与 `ExplorerQuickAccessHelper` 的普通 JIT oracle 仍使用 `Shell.Application` dynamic，但 AOT 编译期只保留各自独立的 Rust 强类型 Shell 边界；两个批次的目标警告均已清零；
- 初始探测中曾由 `Microsoft.CSharp` 和 `System.Linq.Expressions` 产生 `IL3053`；最新阶段 2 审计中该代码为 0，不再列为当前警告；
- 4D-5 已将托盘 identity 改为公开强类型访问，并通过公开事件和 WinUI 视觉树配置
  SecondWindow presenter/Popup；私有 `ContextMenuFlyout` 反射及对应 IL2075 已清零；
- `Localized` 与 Markdig TaskList 已由 4D-1A/1B 改为强类型访问；
- 初始有 7 个跨 WinRT ABI 的类型没有标为 `partial`；阶段 2 已修正，最新 AOT 日志中 `CsWinRT1028` 为 0。

.NET Native AOT 没有内置 COM 支持，所以不能仅以两条 shortcut 信息消失作为完成标准。所有 AOT 可达的传统 COM 路径仍需要改成源生成 COM、手工 `ComWrappers`、现代 WinRT API，或封装在原生模块中。

### 4.3 JSON、MVVM 与 XAML

#### MVVM

阶段 2 已完成 79 个 `MVVMTK0045` 对应声明的迁移：

1. `SearchPopupViewModel` 12 个；
2. `SettingsViewModel` 67 个。

迁移保持属性名、默认值、通知钩子和 XAML 契约不变。`SettingsViewModel` 构造函数增加初始化期回调抑制，避免原先的字段直写改为属性赋值后意外触发设置持久化、启动项修改或 Widget 同步；快速记录依赖联动也改为受控属性赋值，保持只同步一次的原行为。

#### JSON

当前 49 处调用分布在 16 个文件。4B-1 已迁移 5 个叶子文件中的 8 处调用；4B-2 又迁移
6 个用户持久化域中的 22 处调用；4B-3A 迁移搜索历史、搜索索引和桌面恢复 3 个文件中的
7 处调用；4B-3B 再迁移附件健康和备份用户数据路径中的 6 处调用；4B-3C 最后迁移备份
控制文档 6 处调用。全部 49 处调用现已显式绑定 14 个分域 source-generated context。
最新 AOT 日志中与 `JsonSerializer` 或 `JsonStringEnumConverter` 直接相关的 IL2026/IL3050
从 210 条经 176、80、52、24 降到 0。生产代码的反射型 JSON 调用和非泛型
`JsonStringEnumConverter` 均为 0。

备份服务中的 `ValidateJsonFileIfPresent<T>` 实际只接收 `AppSettings`、
`QuickCaptureStoreData` 和 `TodoWidgetData`；附件健康服务的 `ReadJson<T>` 只接收后两类。
4B-3B 已要求这两个 helper 显式接收对应 `JsonTypeInfo<T>`。4B-3C 又把仅写
`PendingRestoreMarker` 的泛型 helper 收口为非泛型专用入口。完整的
16 文件清单、格式金样和 context 拆分见
`docs/architecture/json-source-generation-baseline.md`。4B-4 已在主程序和更新器的 AOT
审计条件组启用 `JsonSerializerIsReflectionEnabledByDefault=false`，并由配置 13 / schema 10
的隔离审计确认摘要值为 `false`、JSON 警告与反射回退错误均为 0；普通构建的该属性继续为空。旧版本数据
读取、迁移、备份和恢复只补针对这些格式的回归用例，不需要先调整整个测试体系。

#### XAML

不建议把全部 `{Binding}` 一次性改成 `{x:Bind}`。4E-0 已将搜索历史 6 条不可变
`OneWay x:Bind` 精确改为 `OneTime` 并清零 WMC1506；4E-1 至 4E-5 又累计完成叶子控件、
类型明确 DataTemplate、显式 ViewModel 桥接和搜索结果行的小批 compiled `x:Bind`；5B-4B1 又只把
四条适合类型化的设置命令移出运行时 Binding。当前产品 XAML 为 1,295 条 `{Binding}` 和 82 条
`{x:Bind}`。
合理顺序是：

1. 按页面列出 Binding 的根对象和实际运行类型；
2. 将会跨 WinRT ABI 的自定义类型及其父类型标为 `partial`；
3. 对需要保留反射绑定的根类型使用 `WinRT.GeneratedBindableCustomProperty`；
4. 只在类型稳定、生命周期清楚、收益明确的位置改为 `x:Bind`；
5. 逐页面消除 `WMC1510`，并验证设置、列表模板、Converter 和运行时 DataContext 切换。

这项工作应按功能页面拆分。4E-5 后 WMC1510 为 1216；5B-4B1 根据真实设置页失败建立 282/282
精确生成绑定清单，并把四条命令改为 `x:Bind`，当前 WMC1510 为 1211。实际运行同时证明元素类型
可绑定不等于 typed collection 能直接投影到 WinRT `ItemsSource`，因此集合边界需要单独核对。
全局 Style Setter、大型页面根绑定及无实际失败的功能页继续冻结；下一轮持久化矩阵仍只修复真实
AOT 运行暴露的问题，不恢复按警告总数机械迁移全部 Binding 的做法。

### 4.4 更新器与发布目录混合问题

第一轮核对时，主项目的 `PublishDeskBoxUpdater` 目标会把 `Configuration`、`Platform`、`RuntimeIdentifier` 和 `SelfContained` 传给更新器，但没有传递 `PublishAot`。因此当时的主程序 AOT 发布目录中仍包含：

- `coreclr.dll`、`clrjit.dll` 和 `System.Private.CoreLib.dll`；
- 一个仍依赖 CoreCLR 的更新器；
- 主程序约 169 MiB 的调试符号文件。

修正前的混合输出为 229 个文件、约 348.4 MiB。这个数字不能与现有约 90.4 MiB 的框架依赖输出快照直接比较，因为里面同时包含更新器运行时和大体积 PDB。

单独以 AOT 发布 `DeskBox.Updater` 已成功，0 个警告：

- `DeskBox.Updater.exe`：约 1.93 MiB；
- PDB：约 9.19 MiB；
- 不包含 `coreclr.dll` 或托管更新器 DLL。

第一批发布链已经完成以下实现：

- `DeskBoxAotAudit` 为显式 opt-in，不改变默认 Debug/Release；
- AOT publish 不再预先复制托管更新器构建输出；
- `PublishAot` 和审计配置会传递到 `DeskBox.Updater`；
- `scripts/publish-aot-audit.ps1` 使用隔离中间工件、分离 PDB，并验证 PE 架构、CoreCLR/JIT 和托管应用文件残留；
- `global.json` 固定 .NET SDK，`rust-toolchain.toml` 固定并实际验证 Rust x64 工具链；
- 增加针对发布契约的静态测试。

2026-08-20 阶段 3A 收口后的 x64 审计结果（审计配置版本 4、摘要 schema 3）：

- 发布目录 39 个文件、约 79.4 MiB；
- 独立 symbols 目录 3 个 PDB、约 164.8 MiB；
- `DeskBox.exe`、`DeskBox.Updater.exe` 和 `deskbox_native.dll` 均为 `0x8664` x64 PE；发布目录中的 Rust DLL 实际加载后返回 ABI 版本 1；
- 不含 `coreclr.dll`、`clrjit.dll`、`System.Private.CoreLib.dll`、托管应用 DLL、deps 或 runtimeconfig；
- 仍有 4 个 ILC “always throw”和 12 类警告代码；`MVVMTK0045` 与 `CsWinRT1028` 均为 0，尚不能作为可运行发布物；
- 审计摘要记录 .NET/Rust 工具链、Git 基线、dirty 状态、发布前后工作树指纹、警告出现次数、三个 PE 的 SHA-256 和完整导入依赖；本次前后指纹一致，`sourceStableDuringAudit=true`；
- Rust staging 与 Cargo target 分别位于本次 `.artifacts/aot-audit/win-x64` 下的独立目录，不再读取共享 `native/target`；架构矛盾的 `Platform=x64` + `RuntimeIdentifier=win-arm64` 已实测在 Cargo 前失败；
- `deskbox_native.dll` 导入 `vcruntime140.dll`，而主程序和更新器没有该直接导入；该事实已进入摘要，不再只依赖开发机手工 `dumpbin`；
- 带 Rust opt-in 的规范 Debug 构建为 0 错误，规范 x64 测试为 1926/1926，同一测试二进制又连续执行三轮全量并全部通过；新增契约覆盖中文转换缓冲区、属性声明、构造期副作用抑制、CsWinRT partial 类型、AOT 发布配置和 Rust 3A 集成。

这证明更新器 AOT、发布目录结构和 Rust 3A 的 x64 构建/打包链已经通过自动化审计。更新器真实替换、失败回滚和 AOT 主程序运行验证仍属于后续阶段，不能由结构检查替代。

2026-08-20 阶段 3B-2 的最新 x64 审计结果（审计配置版本 7、摘要 schema 6）：

- 发布目录仍为 39 个文件、约 79.4 MiB；独立 symbols 目录为 3 个 PDB、约 164.9 MiB；
- `DeskBox.exe`、`DeskBox.Updater.exe` 与 `deskbox_native.dll` 均为 `0x8664` x64 PE；Rust DLL 实际加载后返回 ABI 2、能力掩码 7 和四个必需导出；
- 审计记录 JIT 默认 `csharp`、显式开关 `DESKBOX_SHORTCUT_BACKEND`、Native AOT 强制 `rust`，以及原生失败不回退；锁定依赖图包含 16 个包；
- 发布前后工作树指纹一致，`sourceStableDuringAudit=true`；`MVVMTK0045` 与 `CsWinRT1028` 仍为 0；
- 仍有相同的 12 类警告代码和 4 个 ILC “always throw”。本阶段保留的 C# shortcut 写入、带 UI 修复及其他 COM 路径仍可达，因此这些结果符合 3B-2 边界，不能据此启动 AOT 主程序。

这次审计证明 ABI 2 / 能力 7 的 Rust DLL、托管安全加载器和 AOT 发布结构可以一起编译和封装；它不证明 AOT 应用已经具备运行或发布条件。

2026-08-20 阶段 3C-1 的最新 x64 审计结果（审计配置版本 8、摘要 schema 7）：

- 发布目录仍为 39 个文件、约 79.5 MiB；独立 symbols 目录为 3 个 PDB、约
  164.9 MiB；
- `DeskBox.exe`、`DeskBox.Updater.exe` 与 `deskbox_native.dll` 均为 `0x8664` x64 PE；
  Rust DLL 实际加载后返回 ABI 2、能力掩码 15 和五个必需导出；
- 审计继续记录 JIT 默认 `csharp`、显式开关 `DESKBOX_SHORTCUT_BACKEND`、Native AOT
  强制 `rust` 以及原生失败不回退；锁定依赖图仍为 16 个包；
- 发布前后工作树指纹一致，`sourceStableDuringAudit=true`；`MVVMTK0045` 与
  `CsWinRT1028` 仍为 0；
- 仍有相同的 12 类警告代码和 4 个 ILC always-throw，其中两条来自保留的旧 C#
  shortcut coclass。3C-1 没有删除 JIT oracle，也没有迁移带 UI 修复，所以不能据此
  启动 AOT 主程序。

这次审计证明写入 ABI、产品编排与 AOT 发布结构可以共同编译、封装并保持来源稳定；
它仍是产物审计，不是 AOT 运行验收。

2026-08-20 阶段 3C-2 的最新 x64 审计结果（审计配置版本 9、摘要 schema 8）：

- 发布目录仍为 39 个文件、约 79.5 MiB；独立 symbols 目录为 3 个 PDB、约
  164.9 MiB；
- `DeskBox.exe`、`DeskBox.Updater.exe` 与 `deskbox_native.dll` 均为 `0x8664` x64 PE；
  Rust DLL 实际加载后返回 ABI 2、能力掩码 31 和六个必需导出；
- 审计继续记录 JIT 默认 `csharp`、显式开关 `DESKBOX_SHORTCUT_BACKEND`、Native AOT
  强制 `rust` 以及原生失败不回退；锁定依赖图仍为 16 个包；
- 发布前后工作树指纹一致，`sourceStableDuringAudit=true`；`MVVMTK0045` 与
  `CsWinRT1028` 仍为 0；
- 仍有相同的 12 类警告代码和 4 个 ILC always-throw，其中两条来自保留的旧 C#
  shortcut coclass。3C-2 已迁移 UI 功能，但没有删除 JIT oracle，也没有让 AOT 编译
  排除旧分支，因此不能据此启动 AOT 主程序。

这次审计证明 owner HWND UI ABI、产品编排与 AOT 发布结构可以共同编译、封装并保持
来源稳定；它不证明缺失目标的模态交互已经通过，也不是 AOT 运行验收。

2026-08-20 的 3C-2 收口复盘再次逐项核对了 Rust 导出、托管加载器、
`ShortcutHelper`、`DragDropPermissionService`、`FileService`、`WidgetViewModel`、
`FileSurfaceContent`、Inno 输入规则、分离更新器和诊断包代码。结论如下：

- 产品代码中的 `CLSID_ShellLink` 仍且只存在于 `ShortcutHelper` 与
  `DragDropPermissionService` 两套 JIT oracle；五类产品操作都已有 Rust 分派，未发现
  漏接的第三套 Shell Link 调用点；
- 文件组件的单击、双击、Enter、选择栏和右键菜单最终都汇入
  `ActivateItemAsync`，再由带 `_hostWindowHandle` 的重载打开项目；宿主窗口会在内容
  接入时设置该句柄；
- 无 HWND 的 `[RelayCommand] OpenItem(WidgetItem)` 当前没有 XAML 或代码调用，但它仍是
  一个容易被未来绑定误用的旁路，3C-3 应移除该生成命令或让它在编译期不可成为产品
  激活入口；
- 最新 AOT 审计后的实现文件没有再变动。本轮重新执行 Rust 格式化、Clippy、33/33
  原生测试、44/44 shortcut/AOT 契约定向测试和显式 Rust 产品入口 3/3，均通过；
- 现有自动化不能证明缺失目标模态窗口的取消、修复和删除，也不能证明对话框确实归属
  正确 Widget。这些仍是唯一的 3C-2 开放门槛。

随后完成了 3C-2 人工门禁。记录保存在
`.artifacts/manual-shortcut-3c2/20260820-211241`，结果如下：

- 显式 Rust JIT 进程来自规范 Debug 输出，进程实际加载的也是同一输出目录中的
  `deskbox_native.dll`，不是仅凭环境变量推断后端；
- Widget HWND 为 `0x430DBE`，取消对话框 HWND 为 `0x380F62`，其 owner 为该 Widget；
  取消后磁盘 `.lnk` 与界面项目均保留；
- 把目标在同卷移动后重新触发修复，Shell 跟踪把已存储目标更新为新路径；下一次激活
  直接通过 Explorer/ShellExecute 打开新目标；
- 删除对话框 HWND 为 `0x3A0A0A`，owner 同样为该 Widget；确认删除后磁盘 `.lnk` 与
  ViewModel 项立即同时消失；
- 这组结果满足父窗口、取消、修复、删除和实际 Rust DLL 路径五项门禁，因而开放 3C-3。

2026-08-20 阶段 3C-3 的最新 x64 审计结果（审计配置版本 10、摘要 schema 9）：

- `PublishAot=true` 定义 `DESKBOX_NATIVE_AOT`。五类 shortcut 产品操作在 AOT 中直接
  调用 Rust，旧 helper、`ComImport` coclass/interface 及其调用只在非 AOT JIT 中编译；
  普通 JIT 默认仍为 C#，显式 Rust 失败仍不回退；
- 无 HWND 的 ViewModel 命令已删除，`FileService.OpenItem` 也不再提供默认空句柄；文件
  Widget 的产品激活路径只能显式传递宿主 HWND；
- 诊断 schema 升为 5，只记录策略、相对模块名、存在性、PE 架构、SHA-256，以及加载器
  已经被产品路径探测时的缓存结果；导出诊断不会创建 `Lazy.Value`、主动加载 DLL、记录
  绝对路径或把 DLL 放入诊断包；
- x64 Inno 输入显式从所选 publish 根目录收入唯一的 `deskbox_native.dll`，通配规则排除
  重复 DLL/PDB；ARM64 输入显式排除 x64 DLL，分离更新器也不会复制原生模块；
- Rust 格式化、Clippy `-D warnings` 与 33/33 原生测试通过；shortcut/AOT 契约定向测试
  85/85、显式 Rust 产品入口 3/3、DeskBox x64 全量测试 1963/1963 通过；
- 最终规范 Debug 构建为 0 错误、30 条既有警告；随后在未设置 shortcut opt-in 的环境
  启动唯一实例，进程路径精确指向规范 Debug 输出，启动阶段加载的
  `deskbox_native.dll` 数量为 0，确认普通 JIT 默认仍为 C#；
- 发布目录为 39 个文件、约 79.5 MiB，独立 symbols 目录为 3 个 PDB、约 164.9 MiB；
  ABI 2、能力 31、六个导出和 x64 PE 均通过检查。隔离 staging 与发布 DLL 的 SHA-256
  完全一致；每次审计的具体值以对应 `summary.json` 为准，不把一次构建的哈希固化为长期
  ABI 契约；
- 两次连续、源指纹均稳定的干净审计生成了不同的 Rust DLL SHA-256；PE 头显示链接时间戳
  与 CodeView RSDS GUID 随链接变化。当前只证明同一次审计的 staging/publish 身份一致，
  尚未证明跨构建字节级可复现。该差异不改变导出、ABI、能力或 PE 架构结论，但发布阶段
  若要求 reproducible build，需要单独验证 Rust/MSVC 的确定性链接配置；
- 发布前后工作树指纹一致，`sourceStableDuringAudit=true`。警告代码集合仍严格为既有
  12 类；shortcut `always-throw` 为 0，只剩
  `FolderPickerService.FileOpenDialog` 与
  `MusicVolumeService.MMDeviceEnumeratorComObject` 两条；
- AOT 主程序仍未启动。这次结果证明 shortcut 领域的 AOT 可达路径与发布边界已收口，
  不证明整个主程序已经具备 AOT 运行或发布条件。

随后完成的 3C-3-R 把审计配置升级为 11，摘要 schema 保持 9。项目级允许/拒绝组合、
ARM64 脚本提前失败、AOT 发布契约测试 19/19 和 DeskBox x64 全量测试 1970/1970 通过；
最新隔离 x64 审计继续保持 39 个发布文件、3 个分离 PDB、12 类既有警告、2 条主程序
`always-throw` 和 0 条 shortcut `always-throw`，ABI 2、能力 31 与同次 staging/publish
哈希一致性均未改变。AOT 主程序仍未启动。

阶段 4A 把审计配置升级为 12，摘要 schema 仍为 9。FolderPicker 改用 Windows App SDK
现代异步 Picker，8 个入口的 owner/await 静态契约和零句柄拒绝测试共 4/4 通过；与现有
AOT 发布契约合并执行为 23/23，DeskBox x64 全量测试为 1974/1974，规范 Debug 构建仍为
0 错误、30 条既有警告。隔离 x64 审计继续产出 39 个文件和 3 个分离 PDB，12 类警告代码
未扩张，工作树前后指纹一致；主程序 `always-throw` 从 2 条降为 1 条，唯一剩余项为
`MusicVolumeService.MMDeviceEnumeratorComObject`，shortcut 为 0。ABI 2、能力 31 和同次
staging/publish 哈希一致性不变。AOT 主程序仍未启动。此后 4A 的完整人工入口矩阵通过；
4B-0 没有修改生产 JSON 调用，新增 7 条基线契约并把 x64 全量测试提高到 1981/1981。
配置 12 / schema 9 的隔离审计再次通过，继续保持 39 个发布文件、3 个 PDB、12 类既有
警告、1 条音乐音量 `always-throw`、0 条 shortcut `always-throw`、ABI 2、能力 31、
`sourceStableDuringAudit=true` 和同次 staging/publish 哈希一致。

4B-1 随后只迁移 5 个叶子文件的 8 处 JSON 调用，增加 AppUpdate、Weather、Localization、
Diagnostics 四个 metadata context，并让城市资源复用 Weather context。定向兼容测试
105/105、x64 全量测试 1983/1983 通过；规范 Debug 构建为 0 错误、30 条既有警告，source
generator 没有新增诊断。配置 12 / schema 9 的隔离 AOT 审计继续产出 39 个发布文件、3 个
PDB 和 12 类既有警告；JSON 直接相关记录由 210 降到 176，5 个迁移文件为 0；工作树前后
指纹一致，1 条音乐音量 `always-throw`、0 条 shortcut `always-throw`、ABI 2、能力 31 和
同次 staging/publish 哈希一致。AOT 主程序仍未启动。

4B-2 随后只迁移 6 个用户持久化域的 22 处 JSON 调用，增加 Settings、Quick Capture、
Todo、Glance preferences、Glance image catalog 和 Widget metadata 六个 metadata context。
定向兼容测试 287/287、x64 全量测试 1986/1986 通过；规范 Debug 构建为 0 错误、30 条
既有警告，source generator 没有新增诊断。配置 12 / schema 9 的隔离 AOT 审计继续产出
39 个发布文件、3 个 PDB 和 12 类既有警告；JSON 直接相关记录由 176 降到 80，本轮 6 个
迁移文件为 0；工作树前后指纹一致，1 条音乐音量 `always-throw`、0 条 shortcut
`always-throw`、ABI 2、能力 31 和同次 staging/publish 哈希一致。AOT 主程序仍未启动。

4B-3A 随后只迁移搜索历史、搜索索引和桌面恢复 3 个文件的 7 处 JSON 调用，增加 3 个
metadata context。定向测试 55/55、x64 全量测试 1987/1987 通过；规范 Debug 构建为
0 错误、30 条既有警告，source generator 没有新增诊断。配置 12 / schema 9 的隔离 AOT
审计继续产出 39 个发布文件、3 个 PDB 和 12 类既有警告；JSON 直接相关记录由 80 降到
52，本轮 3 个迁移文件为 0，剩余记录只来自附件健康和备份服务；工作树前后指纹一致，
1 条音乐音量 `always-throw`、0 条 shortcut `always-throw`、ABI 2、能力 31 和同次
staging/publish 哈希一致。AOT 主程序仍未启动。

4B-3B 随后只迁移附件健康和备份用户数据路径中的 6 处 JSON 调用，没有增加 context
所有者。定向测试 210/210、x64 全量测试 1990/1990 通过；兼容金样确认混合大小写属性、
字符串/旧数字枚举、未知字段和附件路径重定位均保持。配置 12 / schema 9 的隔离 AOT
审计继续产出 39 个发布文件、3 个 PDB 和 12 类既有警告；JSON 直接相关记录由 52 降到
24，附件健康为 0，剩余记录仅来自 4B-3C 的 6 个备份控制文档调用；工作树前后指纹一致，
1 条音乐音量 `always-throw`、0 条 shortcut `always-throw`、ABI 2、能力 31 和同次
staging/publish 哈希一致。AOT 主程序仍未启动。

4B-3C 随后只迁移备份 manifest、file manifest 和 pending marker 的 6 处控制文档调用，
增加 1 个嵌套私有 metadata context。定向测试 31/31、x64 全量测试 1993/1993 通过；
控制文档金样确认 camelCase、缩进、大小写敏感、未知字段、schema 1/2 完整性清单与 pending
marker 清理行为均保持。配置 12 / schema 9 的隔离 AOT 审计继续产出 39 个发布文件、3 个
PDB 和 12 类既有警告；JSON 直接相关记录由 24 降到 0，备份服务的 IL2026/IL3050 也为
0；工作树前后指纹一致，1 条音乐音量 `always-throw`、0 条 shortcut `always-throw`、ABI 2、
能力 31 和同次 staging/publish 哈希一致。AOT 主程序仍未启动。

4B-4 没有修改生产 JSON 调用或格式，只在主程序和更新器的 `DeskBoxAotAudit=true` 条件组
关闭默认 JSON 反射，并让审计脚本在 restore/publish 中显式传入该值。新增契约先在旧实现
上按预期失败，实施后 AOT/JSON 定向契约 32/32、x64 全量测试 1994/1994 通过；普通构建
实际求值为空，审计构建为 `false`。配置 13 / schema 10 的隔离 AOT 审计继续产出 39 个
发布文件、3 个 PDB 和 12 类既有警告，摘要记录 `reflectionEnabledByDefault=false`；JSON
IL2026/IL3050 与反射回退错误均为 0，unexpected warning/always-throw 为 0。工作树前后
指纹一致，1 条音乐音量 `always-throw`、0 条 shortcut `always-throw`、ABI 2、能力 31 和
同次 staging/publish 哈希一致。AOT 主程序仍未启动，4B JSON 阶段到此收口。

规范 Debug 实例上还完成了一次真实 JumpList 转发探针：第二实例以
`--new-folder-widget` 激活主实例后，系统创建了唯一的“选择文件夹”对话框，其 owner
精确等于启动日志记录的托盘宿主 HWND；关闭对话框按取消返回，主进程继续响应且日志没有
FolderPicker 异常。该结果验证最困难的原无 owner 入口，但不替代六类入口的人工矩阵。

## 5. Rust 模块选择建议

### 5.1 候选排序

| 候选 | AOT 价值 | 性能价值 | 边界复杂度 | 建议 |
| --- | --- | --- | --- | --- |
| `.lnk` 快捷方式核心 | 高，直接替换已确认会抛异常的 COM coclass | 低 | 中，操作可做成粗粒度 API | **第一个生产 Rust 切片，已完成 3C-3** |
| 搜索 `SearchCore` | 中，不是 AOT 必需 | 高 | 中高，需要数据所有权和并发设计 | 第二个 Rust 里程碑 |
| FolderPicker | 高 | 低 | 中 | **4A 已完成**；Windows App SDK 现代 Picker 的 8 个异步调用点、2 个原无 owner 入口和完整人工矩阵均已收口 |
| `IFileOperation` 文件操作 | 无实际产品价值，helper 未被调用 | 低 | 低 | **4D-2 已完成：删除死代码**；实际文件操作在 `FileService`，未建立 Rust ABI |
| 音频会话与音量 | 高 | 低 | 中高 | COM 清理后段处理，可由原生服务完整持有对象 |
| OLE `NativeDropTarget` | 高 | 低 | 高，包含回调、线程和生命周期 | **4D-3A/3B 已完成**；读取侧用窄 vtable，注册侧用 C# 源生成 COM，未扩展 Rust |
| `Shell.Application` dynamic | 高 | 低 | 高，两个不同的 IDispatch Automation 产品边界 | **4D-4A/4D-4B 已完成**；Explorer 托管启动和 Quick Access 使用两个独立 Rust 边界 |
| 托盘私有成员反射 | 高 | 低 | 低到中，依赖 SecondWindow WinUI 生命周期 | **4D-5 已完成**；使用公开 identity、事件和视觉树，未升级依赖、未使用 Rust 或 trimming root |
| Shell 上下文菜单 | 中高 | 低 | 高，菜单消息和 UI 线程耦合 | 后续单独评估 |
| Glance 调色板 | 低 | 低 | 低 | 保持 C# |

### 5.2 已落地的第一个 Rust 切片

全部 `CLSID_ShellLink` 产品调用点已经封装进一个 Rust `cdylib`，范围同时包括
`ShortcutHelper` 和 `DragDropPermissionService`。C# 继续负责：

- `.url` 文本格式；
- 应用级缓存；
- UI 提示、日志和错误文案；
- 业务上的路径映射与降级策略。

Rust 模块完整持有 COM 对象，不把 COM 指针、Rust 字符串、异常或所有权不明确的内存跨 FFI 暴露给 C#。边界采用稳定 C ABI、`#[repr(C)]` 的 POD/状态码、调用方提供的 UTF-16 缓冲区或显式配对的释放函数。发布构建应设置明确的 panic 策略，并保证 panic 不穿越 FFI。

当前提供四类粗粒度操作：

1. 读取快捷方式元数据；
2. 无 UI 解析快捷方式目标；解析失败后仍能读取已存储元数据；
3. 创建或更新快捷方式，覆盖文件夹入口和应用桌面/开始菜单入口所需的参数、工作目录与图标；
4. 使用调用方窗口句柄修复损坏链接，保留 Windows 原生更新、提示和删除语义。

现有 C# 接口保留在 JIT 编译中作为 oracle，便于逐项比对；Native AOT 构建已经从可达
代码中排除旧 `ComImport` 后端。AOT 下 Rust DLL 缺失、ABI 版本不匹配或返回非零状态时，
转换为可诊断的功能错误，不回落到 AOT 不支持的旧 COM 路径，也不让诊断导出主动加载
模块。

快捷方式读取存在 `Task.Run` 线程池调用。Rust 层必须对每个实际使用 COM 的线程执行匹配的初始化和释放，处理 apartment 模式不兼容，不跨调用或线程保存 COM 指针。带 Windows UI 的损坏链接修复单独作为一个 ABI 操作，在调用方线程使用传入的父窗口句柄。

首批互操作已经采用固定绝对模块路径的 `LoadLibraryExW`、受限依赖搜索和静态非托管
函数指针，不使用按 DLL 名自动搜索的 `DllImport`/`LibraryImport`，也不启用 direct
P/Invoke。ABI 固定 DLL 名、导出名、版本探针、固定宽度状态码和显式 UTF-16 长度；
不允许 Rust 分配的字符串、panic、异常或 COM 指针直接跨边界。

### 5.3 `SearchCore` 的合理范围

搜索模块作为第二个 Rust 里程碑，目标应是减少常驻字符串/对象开销并降低查询延迟，而不是满足“项目里有 Rust”这一形式要求。建议边界为：

- C#：FileSystemWatcher、USN、取消、后台调度、日志、设置和 UI；
- Rust：紧凑条目存储、目录/字符串去重、增删改、查询评分、Top-N；
- FFI：批量导入、批量增量更新、一次查询返回紧凑结果，不做逐条托管/非托管往返；
- 迁移：一段时间内保留 C# 参考后端，以同一输入比较结果集合和排序。

基准至少覆盖 10k、100k、300k 三个规模，并记录冷加载、增量更新、查询 P50/P95、Private Bytes、结果一致性和空闲卸载/恢复。无需先发布一个“重写后的 C# 搜索核心”，但必须保留当前 C# 行为作为正确性参考。

## 6. AOT 与 Rust 的先后关系

### 6.1 推荐顺序

如果必须选择先开始哪一条，建议**先开始 AOT，但只先完成基础设施、更新器和问题基线，不要等待整个主程序 AOT 完成后才开始 Rust**。推荐节奏是：

1. 建立可重复的 x64 AOT 发布和警告基线；
2. 先完成更新器 AOT，以及不涉及业务重写的低风险 AOT 修改；
3. 在开始修复 `.lnk` 对应的 C# COM 实现前，落地 Rust shortcut 模块；
4. 继续处理主程序的 JSON、XAML、COM、dynamic 和反射问题；
5. 完成 x64 AOT 内部预览；
6. 再把 Rust 扩展到 `SearchCore`，或在不阻塞 AOT 主线的独立分支并行推进；
7. 最后统一进入 ARM64 和 Store 验证。

可以概括为：**AOT 先开路，Rust 在重叠功能整改前插入，随后继续 AOT；搜索 Rust 化放到第二个 Rust 里程碑。**

不建议先完成大范围 Rust 重构再开始 AOT，原因是当前 AOT 问题横跨 MVVM、JSON、XAML、COM、反射和发布链。先重写搜索并不会减少多数 AOT 警告，却会提前增加 DLL 装载、ABI、安装包和双架构变量。

也不建议先把所有 AOT 问题都用 C# 方案解决，再开始 Rust。`.lnk` 已经通过先确定 Rust
归属避免了重复实现；`IFileOperation`、音乐音量等仍有原生方案可能性的领域，则应在各自
迁移批次开始前确定由 C# 生成式 COM 还是原生服务持有，避免先完整重写一次后再换边界。

### 6.2 后续引入 Rust 会不会让 AOT 重做

一般不会。Native AOT 可以通过 P/Invoke 调用原生动态库；Rust `cdylib` 对主程序而言就是一个普通原生 DLL。只要从第一天采用 AOT 友好的互操作边界，新增或更新 Rust 模块后的工作通常是：

- 重新执行 AOT publish；
- 把对应架构的 Rust DLL 放入发布和安装目录；
- 验证 DLL 名称、导出符号、架构和装载路径；
- 执行该模块及相关功能的运行冒烟。

这属于正常的重新构建和集成验证，不等于重新进行 AOT 迁移。此前完成的 MVVM partial property、JSON source generation、XAML bindable 标注、反射清理和其他 COM 迁移仍然有效。

真正可能出现重复工作的情况只有两类：

1. 某项功能先用 C# 源生成 COM 完整重写，之后又决定整项交给 Rust；
2. Rust 边界先使用复杂对象、托管回调、隐式字符串所有权或运行时动态绑定，AOT 阶段再被迫收缩 ABI。

第一类通过提前确定模块归属避免。当前建议已明确 `.lnk` 由 Rust 接管、FolderPicker 使用现代 WinRT、OLE DropTarget 暂留 C# 生成式 COM，因此不会先做同一功能的两套正式方案。

第二类通过以下约束避免：

- C# 侧优先使用 `LibraryImport` source-generated P/Invoke；
- Rust 只导出稳定 C ABI；
- 参数使用固定宽度整数、`#[repr(C)]` POD、指针和明确长度；
- UTF-16 缓冲区由一侧分配并明确释放责任；
- 首批模块不使用跨边界回调、COM 指针或 Rust 对象；
- DLL 名称和导出符号固定，x64/ARM64 保持同一 ABI；
- Rust panic、错误和日志在原生边界内转换为状态码。

.NET 的 `LibraryImport` 会在编译时生成 marshalling 代码，避免依赖运行时生成 P/Invoke stub，适合 Native AOT。Native AOT 对外部原生库既支持运行时延迟绑定，也支持按需配置 direct P/Invoke，所以 Rust DLL 与 AOT 本身没有结构性冲突。

### 6.3 如果先做 Rust，AOT 会不会要求调整 Rust

如果 Rust 模块从一开始遵守上面的 ABI 规则，AOT 通常不要求修改 Rust 内部算法。可能调整的主要是 C# 互操作声明和发布配置，而不是 Rust 业务实现。

容易在 AOT 阶段产生返工的 Rust 设计包括：

- 每条搜索结果都进行一次 FFI 调用；
- 从 Rust 返回需要托管反射识别的复杂对象；
- 用不明确的 `char*` 同时表达 UTF-8、ANSI 和 UTF-16；
- 让 Rust 长期持有托管 delegate，并从任意线程回调 UI；
- 在运行时拼接 DLL 名称或依赖机器环境搜索路径；
- 把 x64 大小或对齐假设写入 ABI，后续无法直接生成 ARM64 DLL。

因此可以先做 Rust，但只能先做已经按 AOT 约束设计的窄模块。当前 `.lnk` 粗粒度 API 符合这一条件；大范围 `SearchCore` 涉及批量数据格式、并发、取消和双后端对照，更适合在 AOT 基线已经稳定后实施。

### 6.4 复杂度比较

以下等级是相对于当前 DeskBox 代码规模的工程复杂度，不是单纯代码行数，也不代表工期承诺。

| 工作项 | 复杂度 | 主要难点 | 返工风险 |
| --- | --- | --- | --- |
| AOT 发布基线、SDK 固定、警告留档 | 低至中 | MSBuild 属性、产物识别、可重复性 | 低 |
| AOT/Rust 发布属性 fail-fast | 低 | 属性求值、非法架构组合、负向构建契约 | 低 |
| 更新器 AOT | 低至中 | 属性传递、更新流程、符号与安装清单 | 低 |
| 79 个 MVVM partial property | 中 | 数量较多，需保持生成属性与通知语义 | 低 |
| JSON source generation | 中高 | 泛型备份接口、旧数据迁移、DTO 完整清单 | 中 |
| FolderPicker 现代化 | 中，代码、自动化与人工矩阵已完成 | 异步签名、8 个入口、2 个原无 owner 调用、取消和窗口归属 | 代码返工风险已降为低；完整人工窗口矩阵已通过 |
| XAML/WinRT Binding | 高 | 1,349 处 Binding、运行时 DataContext、Converter 和模板 | 中高 |
| COM、dynamic、反射 AOT 清理 | 很高 | 生命周期、线程、Shell/OLE 行为、裁剪后才暴露的问题 | 高 |
| x64 AOT 安装升级闭环 | 高 | 主程序、更新器、运行时检测、资源和回滚 | 中高 |
| ARM64 + Store AOT | 很高 | 双架构原生依赖、真实设备、MSIX/Store 差异 | 高 |
| Rust 阶段 3A：workspace、ABI 和 x64 打包基础 | 中 | 精确工具链、DLL/符号复制、来源留档 | 低至中 |
| Rust 阶段 3B：只读 `.lnk` 核心 | 中 | Shell COM 语义、UTF-16、线程初始化和结果对照 | 中 |
| Rust 阶段 3C：写入、原生 UI 与 AOT 可达路径收口 | 中高 | 损坏链接 UI、父窗口、取消/更新/删除语义和编译期后端隔离 | 中高 |
| Rust `SearchCore` | 高 | 数据所有权、增量更新、并发、取消、结果一致性和基准 | 中高 |
| Rust OLE DropTarget/上下文菜单 | 很高 | 反向回调、UI 线程、COM 生命周期和 Windows 消息 | 很高，不建议早做 |

整体上，**主程序 AOT 的复杂度高于第一个窄范围 Rust 模块**，因为 AOT 横跨整个应用；但 `SearchCore` 级别的 Rust 重构本身也是高复杂度项目。两条路线一起做时，最大的风险不是编译器冲突，而是同时改变业务逻辑、互操作边界和发布方式，使问题无法归因。

### 6.5 不同先后方案的判断

| 方案 | 优点 | 缺点 | 结论 |
| --- | --- | --- | --- |
| 全部 Rust 完成后再做 AOT | Rust 架构先定型 | AOT 风险暴露太晚，搜索等大改不能解决多数 AOT 问题 | 不推荐 |
| 主程序 AOT 全部完成后再做 Rust | AOT 主线最单纯 | 重叠 COM 功能可能先做 C#、后做 Rust，产生重复实现 | 不推荐作为完整顺序 |
| AOT 基线 → Rust `.lnk` → AOT 主体 → Rust `SearchCore` | 先看到真实 AOT 阻断点，又能提前锁定 Rust 边界 | 需要维护清楚的模块归属和两个构建工具链 | **推荐** |
| AOT 与大范围 Rust 同时全面推进 | 日历上看似并行 | 警告、崩溃、性能和打包问题难以归因 | 不推荐 |

## 7. 推荐实施顺序

每一阶段都应是独立合并单元，能够单独回退。AOT 清理期间正式发布仍走当前 JIT 配置。

### 阶段 0：固定可复现基线

当前进度：已完成。AOT x64 审计脚本、`.NET` SDK 固定文件和 Rust x64 固定文件均已加入；仓库目录中的 `rustc`、`cargo`、`clippy`、`rustfmt` 和 x64 target 已全部实测可用。系统默认 stable 版本相同不能替代仓库 override 的验证。

- 增加 `global.json`，固定经验证的 .NET SDK 和合理的 patch roll-forward 策略；
- 增加 `rust-toolchain.toml`，固定 Rust 版本、格式化和 lint 组件；
- 新增独立的 AOT 审计发布配置或脚本，不立刻改变默认 Release；
- 保存警告代码、全部关键 PE 导入依赖、文件数量和大小基线；
- x64 先行，ARM64 不与首批混做。

### 阶段 1：先把更新器 AOT 化

当前进度：MSBuild 属性传递、更新器 AOT、符号分离、PE/运行时文件结构检查已经完成并通过。真实更新安装与回滚冒烟仍待阶段 5 的完整 AOT 预览执行。

- 让主项目发布目标明确把 AOT 配置传给更新器；
- 主程序与更新器都验证为目标架构原生 PE；
- 安装包排除 PDB，将符号单独归档；
- 验证更新器启动、等待主进程退出、替换文件、重新启动、失败回滚和注册表路径。

这一阶段不改 UI 或业务功能，是最小、最容易独立验收的 AOT 交付。

### 阶段 2：机械性 AOT 兼容性批次

当前进度：以下两项已在工作树完成，并在阶段 3A 收口后的 1926 个 x64 全量测试和重新 AOT 发布中再次覆盖；审计日志中两类目标警告均为 0。尚未提交，也没有用 AOT 主程序做运行验证。新增契约测试主要验证源码结构，设置与搜索的交互行为仍以针对性 JIT 冒烟和阶段 5 AOT 运行矩阵为准。

实施内容：

1. 79 个 MVVM partial property；
2. 7 个 `CsWinRT1028` 类型。

本阶段保留默认 JIT 行为，并增加最小静态契约测试。不需要重建测试体系；只需在进入功能迁移前核对设置页打开不产生意外持久化、Quick Capture 依赖联动、搜索查询/筛选/排序回调等直接受迁移影响的路径。AOT 主程序的设置、搜索和绑定运行冒烟仍归入阶段 5，不用当前结构检查替代。

### 阶段 3：落地首个 Rust 原生切片

#### 阶段 3A：工具链、workspace 与 ABI，不切换产品功能

当前进度：已完成到本阶段定义的边界。Rust Debug/Release 格式、Clippy、单元测试、DLL/PDB 构建与 ABI 读取均通过；MSBuild 的 x64 opt-in 输出复制、39 文件 AOT 发布、Rust PE/PDB/哈希和工作树来源审计均通过。阶段 3A 收口又增加了审计专用 Cargo target/staging、发布前后双工作树指纹、矛盾 Platform/RID 拒绝和关键 PE 导入依赖清单。模块仍未被应用加载，`.lnk` 行为未迁移，4 个 AOT always-throw 因而按预期保持不变。

- 修复并验证仓库锁定的 Rust 1.96.0 MSVC 工具链；
- 建立单一 Cargo workspace 并生成 `Cargo.lock`；
- 建立 `deskbox_native.dll`、ABI 版本 1、固定导出名和 C 头文件；
- `cdylib` 的 Debug/Release 都采用明确的 panic 策略，Release 保留可分离的原生符号；
- 增加显式 opt-in 的 x64 MSBuild 构建/复制规则，普通 JIT 与 ARM64 默认不启用；
- x64 AOT 审计强制包含 Rust DLL/PDB，直接读取发布 DLL 的 ABI，并记录架构、哈希、工具链和工作树来源；
- 本阶段只提供 ABI 版本探针，不加载模块，也不迁移 `.lnk` 行为。

#### 进入阶段 3B 前的门禁收口

2026-08-20 复盘中发现 `ChineseTextConverter` 使用没有终止空字符的 `LCMapStringEx` 输出，却让 `StringBuilder` 封送层按空字符读取，导致全量测试偶发得到 `春節ȹ`、`臘八Ʈ` 等随机尾字符。当前实现已经改为调用方拥有的显式字符缓冲区，并严格按 Win32 返回的 `written` 长度构造字符串；新增重复双向转换回归用例。此问题与 Rust shortcut 无直接关系，但必须先消除，否则 C#/Rust 字符串差分和全量门禁本身不可靠。

门禁结论：已于 2026-08-20 完成。证据如下：

- 中文转换重复双向回归、节日繁体用例和 x64 全量测试连续稳定通过；
- 规范 Debug 构建、设置页独立 JIT 冒烟、设置文件前后哈希不变和进程路径验证通过；
- AOT 审计的发布前后指纹一致，Rust 使用本次审计独立的 Cargo target；
- 人工验证关闭“随记/Quick Capture”主开关后，“剪贴板记录”和“图片剪贴板”均置灰且不能分别开启。该结果与 XAML 的主从 `IsEnabled` 约束一致，属于预期行为，不是门禁失败；
- 人工验证搜索可同时产生文件和应用结果，筛选切换正常，名称、大小、日期、类型四种排序分别点击两次后，结果与升降序均正常。

自动化、代码静态契约和人工交互证据仍保持分层记录，不能互相替代。

#### 阶段 3B：只读操作与差分验证

当前状态：3B-0、3B-1 与 3B-2 已完成到各自定义的边界。真实 `.lnk` 读取和无 UI
Resolve 已存在于 Rust DLL，产品具有显式诊断接入，但普通 JIT 默认行为仍使用 C#。
3B 不作为一个同时定义 ABI、实现后端并切换功能的大合并单元，而是按以下三个单元
依次开放。

##### 阶段 3B-0：ABI 与行为契约

- 当前进度：已完成。ABI 2 头文件、Rust `repr(C)` 结构、能力协商和未实现导出桩已经落地，详细冻结项见 [`shortcut-native-abi-v2.md`](./shortcut-native-abi-v2.md)。Rust 格式化、Clippy 和 6 个单元测试通过；实际 Release DLL 返回 ABI 2、能力位 0 且包含四个必需导出；保留的阶段 3A DLL 因缺少能力导出被新验证器拒绝；x64 全量测试为 1927/1927；规范 Debug 构建和审计配置 5 / schema 4 的 AOT 发布结构验证通过。AOT 主程序未启动；
- ABI 版本 1 只代表阶段 3A 的版本探针。shortcut 导出已升级为 ABI 版本 2；构建同时验证版本、能力位和全部必需导出，旧 3A DLL 不能通过新后端检查；
- 阶段 3B-0 能力掩码固定为 0；两个 shortcut 导出只校验结构并返回 `NOT_IMPLEMENTED/E_NOTIMPL`，不能被产品路径调用；
- 固定结构体大小/版本、reserved 字段、固定宽度状态码、原始 HRESULT、字段级成功状态、UTF-16 缓冲区容量与返回所需长度；明确 `null`、空字符串、`S_FALSE`、失败 HRESULT 和缓冲区不足的映射；
- 把两套现有读取语义建模为不同操作或显式模式：`ShortcutHelper` 读取 `SLGP_RAWPATH` 的已存储元数据，`DragDropPermissionService` 读取普通有效路径并对目标和参数执行 trim，不能用单一接口无意合并；
- Rust 原生读取接口保持无缓存，应用级路径规范化、长度/时间戳指纹、最多 512 项缓存和 `.url` 分派继续留在 C#。差分测试使用唯一文件或绕过缓存，避免缓存掩盖后端不一致；
- 明确 UI STA、线程池 MTA、`S_OK`/`S_FALSE` 平衡 `CoUninitialize`、`RPC_E_CHANGED_MODE` 和初始化失败不释放的矩阵；COM 指针不得跨调用或线程保存；
- 产品默认后端保持 C#。`.url` 必须在加载 Rust DLL 前完成分派；显式 Rust 模式遇到 DLL、ABI 或导出缺失时返回可诊断错误，AOT 下不得回退到旧 `ComImport`。

##### 阶段 3B-1：已存储元数据只读后端

- 当前进度：实现与本阶段审计已完成。使用锁定的官方 `windows` crate 0.62.2
  实现同步、无状态的 `IShellLinkW`/`IPersistFile` 读取，能力掩码从 0 调整为 3；
  ABI 仍为 2，Resolve 导出仍是 `NOT_IMPLEMENTED` 验证桩且能力位关闭；
- `STORED_RAW` 覆盖 `ShortcutHelper.ReadStoredMetadata` 的五字段、`SLGP_RAWPATH`
  与不 Trim 语义；`EFFECTIVE_DIAGNOSTIC` 覆盖 DragDrop 权限诊断的目标/参数、
  普通 `GetPath` 与 .NET 空白 Trim 语义；
- 每次调用在原线程初始化或复用 COM，并在原线程释放对象；`S_OK`、`S_FALSE` 与
  `RPC_E_CHANGED_MODE` 的平衡规则均有真实线程测试，不保存 COM 指针或调用方缓冲区；
- 调用方长度查询、精确 required 长度、无部分写入、字段 HRESULT、损坏 Load、目标
  缺失、空可选字段与负图标索引均已覆盖；目标路径遵守 `GetPath` 的 `MAX_PATH`
  约束，参数保留 260/512 源缓冲行为，并测试 259/260/261、511/512/513 边界；
- Rust 格式化、Clippy 与显式 x64 的 19 个测试通过，DeskBox x64 全量测试为
  1927/1927，规范非平台 Debug 构建为 0 warning / 0 error；AOT 审计升级为配置 6、
  schema 5，并额外记录 Cargo 锁定包与实际启用 feature；
- 第一批没有产品加载器、差分开关、写入、损坏链接 UI、Resolve 或缓存。规范 JIT
  Debug 实例因此仍运行 C# 后端；AOT 产物只做结构验证，不启动。

##### 阶段 3B-2：JIT 差分、无 UI Resolve 与显式切换

- 当前进度：实现与自动化差分已完成。Rust 能力掩码从 3 调整为 7；无 UI Resolve
  固定使用 `SLR_NO_UI | SLR_NOSEARCH`，0 保留 Windows 默认超时，1 至 65535 写入
  flags 高 16 位，不增加 `SLR_NOTRACK`；
- Resolve 的 `S_OK`、`S_FALSE` 和失败 HRESULT 均原样记录并继续读取已 Load 的
  `STORED_RAW` 元数据。实测目标缺失时 Windows 可返回 `S_FALSE`，因此不能只把负
  HRESULT 当成“未解析”；
- 新增 x64 托管 ABI 布局、固定应用目录加载、受限 DLL 依赖搜索、ABI/导出/能力检查
  和静态非托管函数指针调用。JIT 默认 C#，启动前设置
  `DESKBOX_SHORTCUT_BACKEND=rust` 才显式选择 Rust；显式 Rust 失败不回退；
- Native AOT 通过 `RuntimeFeature.IsDynamicCodeSupported == false` 强制选择 Rust，
  但本阶段仍不运行 AOT 主程序。`.url` 保持在加载 Rust 之前由 C# 分派；
- JIT x64 差分以旧 C# COM 为 oracle，覆盖普通/长 Unicode、描述、参数、工作目录、
  负图标索引、259/260/261 与 511/512/513、相对路径、UNC、原始环境变量、目标
  缺失、损坏文件、PIDL、STA/MTA、并发读取以及有效/缺失目标 Resolve；
- 本轮 Rust 格式化、Clippy `-D warnings` 和 23/23 单元测试通过；AOT 契约与快捷
  方式差分定向测试为 33/33，DeskBox x64 全量测试为 1949/1949；规范 Debug 构建
  为 0 错误、30 条既有 C#/XAML 警告；
- 审计配置 7 / schema 6 的 x64 AOT 发布通过，ABI 2、能力 7、四个导出、39 个发布
  文件、3 个分离 PDB 和前后工作树指纹均通过检查；AOT 主程序未启动，4 个现存
  always-throw 留待 3C 及后续 COM 批次消除；
- 产品默认切换、删除旧后端、写入和带 UI 修复仍留到 3C。

#### 阶段 3C：写入、Windows UI 与 AOT 可达路径收口

##### 阶段 3C-1：写入

- 当前进度：实现完成。ABI 继续保持版本 2，以新增能力位 `1 << 3`、独立写入请求/
  结果结构和 `deskbox_shortcut_write_v2` 扩展现有边界；当前能力掩码为 15；
- Rust 每次创建新的 `IShellLinkW`，依次写入目标、描述、参数、工作目录、图标路径/
  有符号索引，再以 `IPersistFile::Save(..., TRUE)` 创建或覆盖 `.lnk`。所有可选字段
  都显式调用 setter，因此空值能清除旧元数据；只把 Save 的 `S_OK` 视为成功；
- 文件夹快捷方式和 `DragDropPermissionService` 的应用快捷方式创建/更新都接入同一
  原生写入边界。路径规范化、父目录创建和成功后的读取缓存失效仍留在 C#；
- JIT 默认仍为 C# oracle，显式 Rust 模式失败不回退；Native AOT 继续强制 Rust。
  本阶段没有迁移带 Windows UI 的损坏链接修复，也没有删除旧 C# 分支；
- 差分覆盖完整 Unicode 五字段、负图标索引、文件夹/应用产品形状、覆盖后清空旧
  字段、非法输入、Save 失败、STA/MTA、并发写入和产品写入后的缓存失效。
- 本轮 Rust 格式化、Clippy `-D warnings` 和 29/29 单元测试通过；AOT 契约与快捷
  方式差分定向测试为 41/41，显式 Rust 产品入口测试为 2/2，DeskBox x64 全量测试
  为 1957/1957；规范 Debug 构建为 0 错误、30 条既有 C#/XAML 警告；
- 审计配置 8 / schema 7 的 x64 AOT 发布通过，ABI 2、能力 15、五个导出、39 个发布
  文件、3 个分离 PDB 和前后工作树指纹均通过检查；AOT 主程序未启动，4 个现存
  always-throw 留待 3C-3 及后续 COM 批次消除。

##### 阶段 3C-2：Windows UI 修复

- 当前进度：代码、自动化与缺失目标人工交互验证均已完成。ABI 继续保持版本 2，新增
  能力位 `1 << 4`、独立的 64 字节请求/结果和
  `deskbox_shortcut_resolve_with_ui_v2`；当前能力掩码为 31；
- Rust 在调用方线程创建独立 Shell Link，按 `STGM_READ` Load，并把传入的 owner HWND
  原样交给 `Resolve`。flags 固定为 `SLR_UPDATE | SLR_NOSEARCH |
  SLR_OFFER_DELETE_WITHOUT_FILE`，明确不含 `SLR_NO_UI`，从而保留 Windows 原生更新、
  提示和删除语义；句柄、COM 对象和调用方路径均不跨调用保存；
- `FileSurfaceContent` 实际激活路径已从无 HWND 的 RelayCommand 重载切换到
  `ViewModel.OpenItem(item, _hostWindowHandle)`。这同时启用已有的删除结果同步逻辑，
  用户在 Shell UI 删除 `.lnk` 后会从 ViewModel 移除对应项目；
- C# 在 UI Resolve 后统一使已存储元数据缓存失效，再按 `.lnk` 是否存在保持原有
  `ResolvedOrKept` / `ShortcutDeleted` 语义。JIT 默认仍为 C# oracle，显式 Rust 失败
  不回退；Native AOT 继续强制 Rust；
- 自动化覆盖 ABI 尺寸、能力/导出、固定 flags、有效链接、损坏链接、非零 HWND 产品
  调用、缓存失效和宿主 HWND 静态路由。本轮 Rust 格式化、Clippy `-D warnings` 和
  33/33 单元测试通过；定向测试 44/44，显式 Rust 产品入口 3/3，x64 全量测试
  1960/1960；规范 Debug 构建为 0 错误、30 条既有 C#/XAML 警告；
- 审计配置 9 / schema 8 的 x64 AOT 发布通过，ABI 2、能力 31、六个导出、39 个发布
  文件、3 个分离 PDB 和前后工作树指纹均通过检查；AOT 主程序未启动，12 类既有
  警告和 4 个 always-throw 保持不变；
- 缺失目标的人工记录位于 `.artifacts/manual-shortcut-3c2/20260820-211241`。实际 Rust
  DLL 路径、正确父窗口、取消后保留、修复后目标更新且可再次打开、删除后磁盘文件与
  ViewModel 项同时消失均已通过。该门禁已开放 3C-3；AOT 主程序没有因此提前启动。

##### 阶段 3C-3：AOT 可达路径收口与发布边界审计

- 当前进度：实现、静态契约、差分、全量测试与隔离 AOT 产物审计均已完成；3C-2 的五项
  人工门禁在开始本阶段前已留下记录；
- “完整切换”只表示五类 shortcut 产品操作都受同一后端策略控制，且 AOT 中只保留
  Rust 实现，不表示把普通 JIT 默认后端改为 Rust。Debug/Release JIT 继续默认 C#，
  `DESKBOX_SHORTCUT_BACKEND=rust` 继续作为显式差分入口，旧 C# 实现继续作为 JIT
  oracle；
- `PublishAot=true` 现在定义 `DESKBOX_NATIVE_AOT`。两套 legacy helper、`ComImport`
  coclass/interface 及其调用引用都在 AOT 编译期排除；运行时强制 Rust 仍作为第二层
  保护，原生模块失败继续 fail closed；
- 无 HWND `OpenItemCommand` 旁路已删除，`FileService.OpenItem` 也要求调用方显式传入
  HWND；静态契约确认文件组件所有激活方式仍汇入真实宿主窗口入口；
- AOT 审计升级为配置 10 / schema 9。`ShortcutHelper.ShellLink` 与
  `DragDropPermissionService.ShellLink` 两条 `always-throw` 已消失；当前只剩
  `FolderPickerService.FileOpenDialog` 与
  `MusicVolumeService.MMDeviceEnumeratorComObject` 两条，警告代码仍严格限定为既有
  12 类；
- x64 AOT publish 从本次隔离 staging 生成且只携带一个根目录
  `deskbox_native.dll`；其 PE 架构、ABI 2、能力 31、六个导出和 SHA-256 均已复核，
  staging 与 publish 哈希一致。ARM64 Inno 输入显式排除 x64 DLL；
- 分离运行的更新器只复制 `DeskBox.Updater.*`，不会携带或加载 Rust DLL；自动更新安装器
  对主程序 DLL 的真实替换、失败回滚和文件占用验证仍归阶段 5；
- 诊断包以无副作用方式记录 shortcut 策略、相对模块名、存在性、架构和哈希。只有加载器
  已经被产品路径探测时才读取缓存中的 ABI、能力和加载结果；导出诊断不会触发
  `Lazy.Value`、主动加载 DLL、泄露绝对路径/加载详情或包含 DLL 二进制；
- Rust 原生测试 33/33、shortcut/AOT 契约定向测试 85/85、显式 Rust 产品入口 3/3、
  DeskBox x64 全量测试 1963/1963 均通过。隔离 AOT 发布为 39 个文件、3 个分离 PDB，
  发布前后工作树指纹一致；
- 最终规范 Debug 构建为 0 错误、30 条既有警告；未设置 opt-in 的新实例精确运行自
  `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，且启动阶段没有加载
  `deskbox_native.dll`；
- 本阶段仍不启动 `DeskBox.exe` 的 AOT 产物。定向 shortcut AOT 运行冒烟保留到阶段 5，
  在其余 AOT 硬阻断项清除且使用干净测试用户/虚拟机或明确隔离的数据根后执行。

##### 阶段 3C-3-R：发布属性契约加固（已完成）

3C-3 完成后的只读复盘发现，受支持 x64 脚本产物和项目级属性契约之间还有一个窄缺口；
本阶段已经按以下边界封闭：

- 新增 `ValidateDeskBoxNativeAotConfiguration`，在 `PrepareForBuild`/`Publish` 前检查
  Native AOT 必须同时满足 `Platform=x64`、`RuntimeIdentifier=win-x64` 和
  `DeskBoxRustNative=true`；普通 JIT 不进入该目标，默认仍为 C#；
- 直接 `PublishAot=true` 或 `DeskBoxAotAudit=true` 但未启用 Rust 时会在编译前失败；
  Platform/RID 冲突和 ARM64 AOT 也使用独立、可诊断的错误提前失败；
- `scripts/publish-aot-audit.ps1` 的审计配置升级为 11，当前只允许 x64。ARM64 在解析
  `DotNetPath`、采集工作树、创建或清理 `.artifacts/aot-audit` 之前失败；脚本内部
  `rustNativeEnabled` 因而固定为 `true`，摘要不会再形成“策略为 Rust、模块未启用”的矛盾；
- 自动化真实执行普通 JIT、完整 x64 direct/audit AOT、缺失 Rust、ARM64 和 Platform/RID
  冲突组合；AOT 发布契约测试 19/19、DeskBox x64 全量测试 1970/1970 通过；
- 隔离 x64 AOT 审计保持 schema 9、39 个发布文件、3 个分离 PDB、ABI 2、能力 31、
  staging/publish 同次哈希一致、12 类既有警告、2 条主程序 `always-throw` 与 0 条
  shortcut `always-throw`。本阶段没有启动 AOT 主程序，也没有开始 FolderPicker 或 JSON。

截至 3C-3-R，Rust 已进入产品并清除快捷方式领域的两个 AOT 硬阻断项，发布属性也已
fail fast，但范围仍不扩大到其他 Shell、OLE 或音频 COM 功能。这个里程碑是 shortcut
领域的 AOT 结构与发布契约收口，不是主程序 AOT 可运行声明。

### 阶段 4：完成主程序 AOT 兼容性主体

3C-3-R 的发布属性门槛已经通过。4A 已完成代码、自动化、隔离 AOT 产物和人工入口验证；
4B-0 至 4B-4 已完成格式冻结、五批迁移和默认反射关闭审计；4C 已完成音乐音量
Core Audio Rust 边界和最后一条 `always-throw` 清理，4D-1A/1B 也已完成两批低风险
类型化清理，4D 已全部收口，4E-0 至 4E-3 也已完成四批 XAML 小步治理。后续继续按页面
拆分 XAML，避免运行时 DataContext、回收模板与大型页面同时修改：

1. **4A：FolderPicker（已完成）。** 同步 `FileOpenDialog`
   已替换为 `Microsoft.Windows.Storage.Pickers.FolderPicker(WindowId)` 和
   `PickSingleFolderAsync()`；8 个调用点全部改为显式 `await`。设置、引导、Glance 和
   桌面整理继续使用各自宿主 HWND；托盘和 JumpList 统一使用应用启动时创建、全生命周期
   保留的托盘宿主 HWND。服务在 WinRT 激活前拒绝零句柄或失效句柄，取消通过空结果返回，
   不再保留旧 COM、对话框轮询置顶或无 owner 旁路。新增 4 条契约测试，AOT 配置 12 已
   确认只消除 FolderPicker 一条 `always-throw`；真实 JumpList 转发探针也确认对话框
   owner 等于托盘 HWND，取消后主进程正常。设置、引导、Glance、桌面整理、托盘和
   JumpList 的实际选择、取消、前台与父窗口关系也已由用户人工确认通过；
2. **4B：JSON source generation。** 不把 49 处调用作为一次合并：

   - **4B-0（已完成）** 冻结 16 个文件的类型、选项、当前格式、旧版本、缺字段、未知字段
     和金样；明确 7 个非泛型 `JsonStringEnumConverter` 以及 3 个泛型 helper 的实际类型
     白名单；不修改生产序列化行为；
   - **4B-1（已完成）** 已迁移更新、城市、天气、本地化和诊断 5 个叶子文件的 8 处调用；
     4 个分域 context/options 的格式测试和 AOT 警告下降均已验证；
   - **4B-2（已完成）** 已迁移设置、Quick Capture、Todo、Glance 与文件组件等持久化
     store 共 22 处调用；6 个分域 context、用户数据格式金样和 AOT 警告下降均已验证；
   - **4B-3A（已完成）** 已迁移搜索历史、搜索索引和桌面恢复 3 个文件中的 7 处调用；
   - **4B-3B（已完成）** 已迁移附件健康及备份中的用户数据读取、校验和附件路径重定位
     6 处调用；复用 4B-2 context 类型时使用保持属性名大小写不敏感的兼容 options，没有
     直接使用 `*.Default`；
   - **4B-3C（已完成）** 已迁移备份 manifest、file manifest 和 pending marker 6 处调用，并将剩余
     泛型原子写入 helper 收口为非泛型专用入口；
   - **4B-4（已完成）** 主程序与更新器只在 AOT 审计配置中关闭默认反射序列化，普通
     JIT 默认值保持未设置；配置 13 / schema 10 的隔离审计明确记录开关为 `false`，确认
     没有来源于 `System.Text.Json` 或 `JsonStringEnumConverter` 的 IL2026/IL3050，也没有
     反射序列化回退错误；

3. **4C：音乐音量 COM（已完成结构迁移，人工 setter 门槛待确认）。** 已比较生成式 COM
   与边界完整的 Rust 原生服务，选择后者并冻结默认设备、endpoint、session 匹配、COM
   apartment 与失败回退语义。Rust DLL 保持 ABI 2，新增能力位 `1 << 5`、v1 请求/结果和
   `deskbox_music_volume_v1`；完整能力掩码为 63。普通 JIT 默认仍走 C#，可用
   `DESKBOX_MUSIC_VOLUME_BACKEND=rust` 显式验证；Native AOT 编译期排除旧 coclass 与接口。
   Rust 格式、42/42 单元测试、托管契约/真实只读 ABI 探测和配置 14 / schema 11 隔离 AOT
   审计通过，最后一条 `always-throw` 已消除。自动化未主动改变系统音量，真实系统/session
   setter 和默认设备切换仍需单独人工确认；
4. **4D：其余 COM、dynamic 与 trimming 数据流（已完成）。** 4D-1A 已把
   `Win32Helper`、Markdig task-list 和搜索推荐改为强类型入口；4D-1B 又收口 Quick Capture
   诊断和 `Localized`；4D-2 删除死的 `IFileOperation` helper；4D-3A 清除 OLE 数据读取侧
   内置 RCW，4D-3B 用源生成 CCW 完成注册侧；4D-4A/4D-4B 已用两个独立的完整 Rust 边界处理
   Explorer 托管启动和 Quick Access；4D-5 最后用公开强类型 API、事件和 WinUI 视觉树删除
   托盘反射，没有使用宽泛 trimming root；
5. **4E：XAML bindable（4E-0 至 4E-5 已完成当前预览前批次）。** 4E-0 已将搜索历史 6 条不可变
   `OneWay x:Bind` 精确改为 `OneTime`，把 WMC1506 清零；4E-1 将四个叶子 XAML 的 7 条
   自有 DependencyProperty/命名元素 Binding 改为 typed `x:Bind`；4E-2 又完成
    `MusicTransportIcon` 和 `WidgetInlineEditor` 的 15 条；4E-3 完成 `AttachmentTileStrip` 与
    `SearchPopupWindow` 的 14 条类型明确 DataTemplate；4E-4 又用显式 typed ViewModel
    DependencyProperty 桥接处理 `FileWidgetSettingsSection` 的 5 条；4E-5 最后用 internal typed
    Item 投影和每次 ElementPrepared 刷新处理搜索结果行 8 条，WMC1510 降至 1216。

阶段 4D、4E-0 至 4E-5、5A、5B-1、5B-2A、5B-2B、5B-3A、5B-3B、5B-3C、5B-4A、
5B-4B1、5B-4B2A、5B-4B2B1、5B-4B2B2A、5B-4B2B2B1、5B-4B2B2B2、5B-4B2C1、5B-4B2C2A、5B-4B2C2B、5B-4C1A、5B-4C1B1、5B-4C1B2A、5B-4C1B2B、5B-4C1C1、5B-4C1C2A 与 5B-4C2A 已完成。4D 已把 COM、dynamic 与 trimming 的真实阻断清零；4E 以小批 typed binding
把 WMC1506 清零并把 WMC1510 降到 1216。5A 至 5B-3C 随后完成隔离启动与四个 Rust 产品
边界的实际 AOT 读写/故障恢复。5B-4A 与 5B-4B1 再使用真实 WinUI runner 覆盖基础窗口、语言、
搜索、六个设置主分区、24 个深层路由和非空集合投影；设置页精确生成绑定清单为 282/282，
WMC1510 进一步降到 1211。5B-4B2A 使用三个全新的 AOT 进程证明通用设置与固定 File Widget
拓扑的写入、重载、恢复和再次重载。5B-4B2B1 又以三个新进程完成 Quick Capture 核心文本、
pending flush、600 ms 自动保存、托管附件、重载、删除和 postflight；5B-4B2B2A 再完成 Todo
核心任务、标题、备注两类保存、完成状态、重载、删除和 postflight；5B-4B2B2B1 继续完成步骤创建、
文本修改、完成/恢复、非空 UI 投影、跨进程重载、步骤删除、任务删除和 postflight；5B-4B2B2B2
再完成托管附件导入、真实卡片、重载、显式删除、物理清理、任务删除和 postflight；5B-4B2C1 又完成
Glance owned PNG、显示/布局/播放偏好、真实 ImageBrush、重载、恢复与 postflight；5B-4B2C2A 完成
Weather 本地设置、全局默认与 per-widget Day/Week 覆盖、三进程重载、恢复与 postflight；5B-4B2C2B 再完成
严格离线的确定性 WeatherData、真实 Weather surface、Expanded/Compact、Day/Week、单位、皮肤和指标显隐。5B-4C1A
继续完成 owned File Widget 的非空 DataTemplate、文件/文件夹类型、Name 升序、目录导航、watcher、
copy/move/rename、重名失败、递归哈希、三进程重载、恢复和 postflight。5B-4C1B1 随后从真实单选/
多选菜单进入产品回收站删除，以每轮唯一父目录和项目名完成跨进程查询、Rust 精确恢复、内容哈希、
独立补偿和 postflight；产品删除继续保留 C# `SHFileOperationW`。5B-4C1B2A 再从真实“移回桌面”
单选/多选菜单进入产品 Shell move，补齐真实 owner HWND，并以严格 owned AOT-only 分支确定性证明部分
完成、取消、文件完成后任务晚到、历史重载、恢复和补偿。5B-4C1B2B 再从真实“属性”菜单进入
`SHObjectProperties`，证明精确 API owner/路径、系统属性页、代理 owner、受控关闭、只读哈希和双根清理。
5B-4C1C1 以两个三进程矩阵证明现代 picker 的真实取消/选择、精确 owner、文件/文件夹 StorageItems、
产品导入、重载、恢复和 postflight，并保持全局剪贴板不变。5B-4C1C2A 再以 generated COM CCW、
真实 HDROP vtable 和三个新 AOT 进程证明 pointer/leave 清理、Ctrl copy、无 Ctrl move、回调释放、
384 MiB 可见进度层、重载、恢复与 postflight；后续真实 Explorer 窗口配合注入鼠标的补充审计又覆盖
高亮离开、小文件/文件夹/跨卷大文件移动以及外部拖出/取消，但仍明确不算真人物理输入。5B-4C2A
使用两个新 AOT 进程证明主/搜索 `RegisterHotKey` 分发、冲突回滚、禁用/重新启用、跨进程释放和
Win+Space hook 生命周期；合成标准手势不算物理键盘或 recorder 证据。5B-4C3A 再用五个新 AOT
进程和固定 clock/owned store 证明 Todo due 候选、控制项、snooze、完成、次日 recurrence、下一提醒、
跨进程 dismissal、清空与 postflight。5B-4C3B1 随后用三个新 AOT 进程真实展示单项/聚合通知，检查
产品 payload，跨进程恢复系统历史，分别按 tag/group 精确删除并以第三进程确认无残留；该证据不算
activation。后续 C3B2A 已完成 grammar/动作路由，C3B2B1 已完成类型化 envelope、冷启动恢复和真实
第二实例转发。当前 profile 56 / schema 53 保持完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0、Rust ABI 2/能力 511/十个导出和 29/65/27 JSON 清单。

当前有两个并行发布前人工门：**5B-4C1C2B 真人 Explorer 物理鼠标/视觉/DPI** 与
**5B-4C2B 物理标准热键、Win+Space 和设置/引导录制器**。它们不应由注入输入代替。等待人工门时，
下一项代码批次为 **5B-4C3B2B2 真实 Windows activation 与 Todo surface**。现有产品协议已经兼容
`;`/`&`，并完整保留 `UserInput` 通过冷启动和第二实例转发；下一批只补真实通知点击、运行中/冷启动
activation，以及目标 Widget/item 的实际打开、定位和可见刷新。Todo 附件 Undo/孤立文件回收仍是独立
发布前债务。

推荐逐项选择最合适的技术，不要求统一改成 Rust：

- FolderPicker：迁移到 Windows App SDK 现代 `Microsoft.Windows.Storage.Pickers.FolderPicker`；
- OLE DropTarget：4D-3A 已用窄 vtable 调用迁移 `IDataObject`/`IStream` 读取侧；4D-3B 已用
  源生成 COM 迁移 `IDropTarget` CCW，并通过显式接口指针真正将 IL2050 清零；
- `IFileOperation`：`FileOperationHelper` 虽声明回收站删除和批量移动，但全仓库没有调用者；
  实际产品文件操作位于 `FileService` 的独立 `SHFileOperation`/托管路径。4D-2 删除死 helper
  并验证 IL2050 清零，不为未使用代码建立 Rust ABI；
- 音频：由一个边界完整的 C# 生成式 COM 服务或 Rust 原生服务持有生命周期；
- `Shell.Application` dynamic：移除 AOT 可达的运行时 Binder，改为强类型原生操作。由于
  COM 源生成器不支持 `IDispatch`，4D-4 拆为 4D-4A Explorer 托管环境启动和 4D-4B 快速
  访问；两个批次均已采用相互独立的完整 Rust 粗粒度边界并完成 AOT 审计；
- 托盘库反射：4D-5 已使用现有版本公开 identity、打开事件和 WinUI 视觉树完成，不需要
  升级依赖、扩展 Rust ABI 或为整个第三方程序集增加宽泛 root；
- Localized、Markdig 和窗口属性：改为类型化访问或窄范围生成映射；Markdig 已在
  4D-1A 完成，`Localized` 的三类目标已在 4D-1B 完成。

阶段 4 的完成标准是 `always-throw` 为 0，所有 AOT 可达路径都不再依赖 Native AOT
不支持的内置 COM marshalling，没有未经说明的裁剪抑制，并且 JSON 与 XAML 警告按各
批次契约收口。只有达到这一标准后才进入阶段 5 的真实 AOT 运行。

### 阶段 5：x64 AOT 内部预览

**5A：隔离数据入口、首次启动、退出与重启已完成。** `DESKBOX_NATIVE_AOT` 构建现在支持显式
`DESKBOX_AOT_PREVIEW_DATA_ROOT`，严格启动器会拒绝正式 `%LOCALAPPDATA%\DeskBox` 及重叠路径、
拒绝过期摘要和不匹配哈希，并且只管理可执行文件完整路径等于受审计 AOT 产物的进程。实际 x64
AOT 已在隔离根中完成首次启动、单实例、托盘正常退出和重启；正式目录运行前后保持相同文件数、
字节数和确定性元数据指纹。完整记录见 `aot-stage-5a-report.md`。

**5B-1：shortcut AOT 到 Rust 真实边界冒烟已完成。** 五个独立 preview 根已覆盖 `.lnk`
创建、读取、覆盖、无 UI Resolve、损坏、有效目标、取消、同卷移动修复和删除；实际 AOT 进程加载
的 `deskbox_native.dll` 路径与哈希和 profile 30 审计一致，两个模态窗口的 owner 也与真实托盘
HWND 相同。完整记录见 `aot-stage-5b-1-report.md`。

**5B-2A：Explorer 启动与 Quick Access 只读查询的 AOT 到 Rust 真实边界冒烟已完成。**
Explorer 产品服务实际生成外部标记，Quick Access 公共查询与原生查询前后均为 `NotPinned`；
同次审计 Rust DLL、HRESULT、阶段掩码、正式数据指纹和精确进程清理均已核对。完整记录见
`aot-stage-5b-2a-report.md`。

**5B-2B：Quick Access pin/unpin 与故障补偿的 AOT 到 Rust 真实边界冒烟已完成。** 正常链路、
固定后应用内失败、固定后强制终止及新进程恢复均已实际执行；恢复进程先读到 `Pinned`，再由产品
unpin 和原生查询共同证明最终 `NotPinned`。完整记录见 `aot-stage-5b-2b-report.md`。

**5B-3A/3B/3C：音乐音量 AOT 到 Rust 真实边界已完成。** 三个批次依次覆盖只读 getter、系统
主音量 setter 与可控媒体 session getter/setter；所有 mutation 均先持久化原值，并验证应用内
`finally`、强制终止后的独立新进程恢复和最终 postflight。完整记录见对应三份阶段报告。

**5B-4A：基础托管 UI 只读矩阵已完成。** 已覆盖两个预置 Widget、12 套语言、六个设置主分区、
搜索六类筛选和四列各两次排序。完整记录见 `aot-stage-5b-4a-report.md`。

**5B-4B1：深层设置与 managed collection 投影已完成。** 已覆盖非空设置搜索、24 个深层路由、
breadcrumb 返回、文件叠放规则和非空备份清单；真实 AOT 失败驱动了 DataTemplate 元数据、精确
282/282 设置绑定清单和三类 `object[]` UI 边界投影。完整记录见
`aot-stage-5b-4b1-report.md`。

**5B-4B2A：设置与 Widget 拓扑持久化/重启恢复已完成。** 三个新 AOT 进程已依次完成固定设置与
File Widget 的变更写入、重启核对、基线恢复和 postflight；3/3 正常退出，Search Widget 对照项、
正式数据指纹和 owned 根清理均通过。完整记录见 `aot-stage-5b-4b2a-report.md`。

**5B-4B2B1：Quick Capture 内容 store 已完成。** 三个新 AOT 进程已依次完成新建详情 pending
flush、已有详情 600 ms 自动保存、托管文本附件、跨进程重载、第二次显式 flush、附件与物理文件
删除、记录删除和空 store postflight；3/3 正常退出，正式数据指纹与 owned 根清理均通过。完整记录
见 `aot-stage-5b-4b2b1-report.md`。

**5B-4B2B2A：Todo 核心任务与备注持久化已完成。** 三个新 AOT 进程已依次完成任务创建、标题
修改、详情 Markdown 备注 600 ms 自动保存、普通完成状态、跨进程重载、显式备注保存、恢复未完成、
删除和空 store postflight；3/3 正常退出，正式数据指纹与 owned 根清理均通过。完整记录见
`aot-stage-5b-4b2b2a-report.md`。

**5B-4B2B2B1：Todo 步骤持久化已完成。** 三个新 AOT 进程已依次完成任务与步骤创建、文本修改、
完成、跨进程重载、恢复未完成、步骤删除、任务删除和空 store postflight；非空步骤的 store、ViewModel
和真实 DataTemplate 行同时通过。完整记录见 `aot-stage-5b-4b2b2b1-report.md`。

**5B-4B2B2B2：Todo 托管附件生命周期已完成。** 三个新 AOT 进程已依次完成任务创建、owned
文本附件托管导入、SHA-256、真实附件 DataTemplate、跨进程重载、显式附件删除、物理文件清理、
任务删除和空状态 postflight；最终矩阵连续两轮通过，Todo 核心与步骤矩阵也使用同一最终产物重新
通过。完整记录见 `aot-stage-5b-4b2b2b2-report.md`。

**5B-4B2C1：Glance 本地图片与偏好持久化已完成。** owned PNG、per-widget store、真实 Glance
ViewModel/ImageBrush、偏好变更、重载、恢复和 postflight 均已通过，不触发在线图片、网络、定位或
Picker。

**5B-4B2C2A：天气设置与 Widget 视图元数据持久化已完成。** 三个新 AOT 进程已验证手动城市、
坐标、单位、默认视图、皮肤、指标显隐、刷新间隔，以及固定 Weather Widget Day/Week metadata 的
写入、重载、恢复和 postflight；Weather host 在该纯本地矩阵中严格保持未加载。

**5B-4B2C2B：确定性天气 surface 已完成。** NativeAOT-only 固定夹具在三个新 AOT 进程中真实
加载 Weather HWND/XamlRoot、Expanded/Compact、Day/Week、Rich/Standard、单位、UV/气压显隐和
24/7 非空集合；重载、恢复、postflight、正常退出、正式数据保护和 owned 清理均通过，网络日志为 0。

**5B-4C1A：owned 本地文件 surface 与核心文件操作已完成。** 三个新 AOT 进程已验证真实 File
Widget HWND/XamlRoot、非空容器、文件/文件夹类型、Name 升序、目录进入/返回、watcher、
copy/move/rename、重名失败、递归哈希、重载、基线恢复和 postflight。

**5B-4C1B1：owned 回收站删除、精确恢复与 File Widget 菜单路由已完成。** 三个新 AOT 进程
经真实单选/多选菜单进入产品删除，跨进程确认三个唯一匹配并经 Rust 完整枚举后逐项恢复，第三进程
确认原路径、长度、SHA-256 和精确残留 0。失败运行的独立补偿也实际恢复了全部 owned 项目。

**5B-4C1B2A：owned Shell move/progress、真实 owner、取消与延迟返回已完成。** 三个新 AOT 进程
经四次真实“移回桌面”菜单分别证明实际 `SHFileOperationW`、部分完成、取消和晚到返回，owner 均等于
File Widget HWND；新进程继续证明历史重载、内容哈希恢复和 postflight。两个 run ID 的完整矩阵均
3/3 自然退出、正式数据指纹不变、运行错误 0。

**5B-4C1B2B：系统 Properties、真实 owner 参数与窗口关闭已完成。** 两个新 AOT 进程经真实
“属性”菜单进入 `SHObjectProperties`，API owner 均等于 File Widget HWND；系统属性页均由同进程
隐藏 `StubWindow32` 代理 owner 持有。唯一 `#32770` 属性页经 `WM_CLOSE` 销毁，文件哈希不变，进程
自然退出，正式数据指纹不变，preview/recovery 双根清理完成。

**5B-4C1C1：Picker 与 Clipboard StorageItems 已完成。** 两个独立 run ID 各用 Mutate、
VerifyRestore、Postflight 三个新 AOT 进程，完成现代 `FileOpenPicker(WindowId)` 真实取消/选择、
精确 File Widget owner、`StorageFile`/`StorageFolder`、产品 StorageItems 导入、跨进程重载、恢复和
postflight。自动化没有覆盖用户全局剪贴板；系统会话 Clipboard 传输保留为人工证据边界。

**5B-4C1C2A：OLE/native drop 自动化边界已完成。** 三个新 AOT 进程通过 generated COM CCW 和
真实 HDROP vtable 覆盖 pointer 越界、`DragLeave`、Ctrl copy、无 Ctrl move、384 MiB 进度层、重载、
恢复和 postflight。C1C2B 已在真实 Explorer 窗口上取得注入鼠标的自动补充证据，包括高亮离开、
小文件/文件夹/跨卷大文件移动和外部拖出/取消；它仍保持 `PhysicalExplorerMouseVerified=false`，
真人鼠标、物理 Ctrl、视觉与非 100% DPI 验收尚未完成。

**5B-4C2A：主/搜索热键与保留 hook 自动化边界已完成。** 两个新 AOT 进程证明主/搜索
`RegisterHotKey` 分发、冲突回滚、禁用/重新启用和进程退出后的重新注册；Win+Space 只验证 hook
线程启停。物理标准键盘、物理 Win+Space、设置/引导录制器和内部 `0xE8` 屏蔽键归入 C2B。

- 生成独立于正式 JIT 包的 x64 AOT 预览；
- 在干净测试用户/虚拟机或经代码明确支持的隔离数据根中启动，不能复用正式用户数据；
- 5B-1 已完成 shortcut 定向冒烟，确认 Rust DLL 路径、ABI/能力、读取、写入、无 UI
  Resolve 和带 owner HWND 的修复/删除；
- 5B-2A/2B 已验证 Explorer 启动以及 Quick Access 查询、变更和故障恢复；
- 5B-3A/3B/3C 已验证音乐音量 getter、系统 setter、可控 session setter 和两层恢复；
- 5B-4A/4B1 已验证 PRI、XAML、语言资源、搜索和全部设置只读路由；
- 5B-4B2A 已验证通用设置与固定 Widget 拓扑的持久化和三进程恢复；
- 5B-4B2B1 已验证 Quick Capture 核心文本与托管附件内容 store；2B2A 已验证 Todo 核心任务与备注；
  2B2B1 已验证 Todo 步骤；2B2B2 已验证 Todo 托管附件；2C1 已验证 Glance；2C2A 已验证纯本地
  天气设置/视图元数据；2C2B 已验证确定性天气真实 surface；
- 5B-4C1A 已验证 owned 本地文件 surface、类型/排序、导航、watcher、copy/move/rename 和冲突失败；
- 5B-4C1B1 已闭环唯一 owned 项目的产品回收站删除、精确恢复、内部菜单路由和失败补偿；
- 5B-4C1B2A 已验证 Shell move、真实 owner、部分/取消/延迟返回、历史重载与补偿；5B-4C1B2B 已验证系统 Properties、API owner、代理窗口与关闭；C1C1 已验证现代 Picker 与 StorageItems；
- 5B-4C1C2A 已验证程序化 OLE/native drop，C1C2B 已有真实 Explorer + 注入鼠标的补充证据，但真人物理鼠标、视觉与 DPI 门仍待完成；
- 5B-4C2A 已验证主/搜索热键自动注册分发、冲突回滚、禁用/重启释放与 Win+Space hook 生命周期，C2B 继续保留物理键盘和录制器人工门；
- 5B-4C3A 已验证 Todo recurrence/reminder 的候选、控制、snooze、完成、生成、跨进程 dismissal、清空与 postflight；
- 5B-4C3B1 已验证两条真实系统通知的产品 payload、展示、跨进程历史恢复、逐条精确清理、注销和无残留 postflight；
- 5B-4C3B2A 已验证 activation grammar 与确定性动作路由；5B-4C3B2B1 已验证类型化 `UserInput` envelope、冷启动恢复和真实第二实例转发；
- 5B-4C3B2B2A 已验证受控 activation 后的 Todo surface 打开、目标定位和可见刷新；B2B2B 的产品代码、runner 与 AOT 审计已完成，真人通知中心点击证据仍是发布前外部门，不能由注入输入代替；
- 该外部门不再阻塞其他代码，主线已进入阶段 6A SearchCore；媒体 UI、天气网络/定位与其他 OS 交互继续拆分；
- 完成安装、覆盖升级、自动更新、卸载和回滚；
- 明确发布哈希策略：当前以每次审计/发布的 `summary.json` 记录为准；若要求跨机器或跨次
  构建字节级复现，再单独验证确定性链接与签名前/签后哈希边界；
- 完成剩余真实运行关键功能矩阵；
- 保留上一版 JIT 包至少一个发布周期，作为发布级回退。

只有这一阶段通过后，才调整直接安装包的运行时检测：当主程序和更新器都 AOT 后，普通安装包不再需要下载 .NET Runtime；如果仍为 `WindowsAppSDKSelfContained=false`，则仍需检测 Windows App Runtime。Full 安装包继续负责携带 Windows App Runtime。Rust DLL 当前导入 `VCRUNTIME140.dll`；进入安装验证前必须根据审计中的 PE 导入清单明确选择 Rust 静态 CRT，或由安装器检测并安装匹配的 Visual C++ Redistributable，并在干净的受支持 Windows 环境验证，不能仅以开发机已有 DLL 作为证据。

### 阶段 6：Rust `SearchCore`

- **6A 已完成**：独立 `deskbox_search_core.dll`、ABI v1、批量 caller-owned UTF-16、目录池、连续
  arenas、有界 Top-N、取消、tracked capacity、显式 C# 所有者和真实 DLL 差异门禁；
- 6A 没有修改 `SearchIndexService` 默认路径，也没有把 SearchCore 加入普通或 AOT 产品输出，生产
  `deskbox_native.dll` 继续保持 ABI 2、能力 511 和十个导出；
- 20,000 条共享长目录样本的 Rust tracked capacity 为 1,480,124 bytes，是当前托管结构仅重复
  完整路径 UTF-16 字符载荷保守下限 3,320,000 bytes 的 44.58%；该证据支持继续推进，但不冒充
  进程工作集结果；
- **6B 已完成**：独立 SearchCore 升到 ABI 2，新增原子 DBIX v1 直载、waitable event 取消、明确的
  I/O/版本/损坏状态和无半成品句柄 fallback；Local/UTC `DateTime.ToBinary()` 排序语义已与托管对照，
  非零 Unspecified 时间拒绝近似并要求 rebuild；
- 10k/100k/300k 分别在全新 managed/Rust Release 子进程读取同一 DBIX；三档六组查询结果签名全部
  相同，300k resident private 增量 85.79→17.55 MiB（-79.5%），peak private 86.27→22.21 MiB
  （-74.3%），加载 226.51→31.17 ms，聚合查询 P95 34.436→18.052 ms；
- **6C 已完成**：SearchCore 升到 ABI 3，增加事务化 upsert/remove/tree/stale-tree、recent files、
  frequent folders、live-only DBIX 原子保存与目录压缩；watcher、bounded delta 和 reconciliation 仍由
  C# 调度；
- 一次会话严格只保留 managed 或 Rust 一个 resident owner；Direct x64 普通与 AOT 输出包含模块，
  设置型 preview 默认关闭，DLL/ABI/DBIX/时间语义失败回到可用的 managed load/rebuild；Store/ARM64
  不打包；
- 6C ABI3 隔离 300k resident private 为 managed 85.38 MiB、Rust 22.07 MiB（-74.2%），peak
  private 86.55→26.45 MiB（-69.4%）；三档六组查询签名一致。10k resident 差值受基线噪声主导，
  不作为决策依据；
- 真实 DeskBox 使用 207,925 条 DBIX、16,992 个目录和 11 个启用 Widget，各后端两次重复：Private
  Bytes 中位数 269.23→236.86 MiB（-12.02%），Working Set 387.36→355.76 MiB（-8.16%），正式
  settings/DBIX 指纹不变；
- x64 AOT 审计升级为 profile 57 / schema 54，SearchCore ABI 3、14 导出、唯一根 DLL、x64 PE、
  staging/publish 哈希和符号分离均通过；打包不改变默认关闭策略；
- **下一项 6D**：长时间 watcher/rename/delete/overflow/reconciliation churn，query/project/save 故障
  注入与 owner 恢复，tombstone/idle unload/reload/restart soak，多轮 Release/AOT 全格子内存与搜索延迟，
  并在这些门禁后决定 Direct x64 是否默认启用。

SearchCore 不作为 AOT 兼容性清理的前置条件，但已进入受审计的 Direct x64 AOT 产物。详细证据见
`search-core-native-abi-v1.md`、`search-core-native-abi-v2.md`、`search-core-native-abi-v3.md`、
`rust-stage-6a-search-core-report.md`、`rust-stage-6b-search-core-report.md` 和
`rust-stage-6c-search-core-report.md`。

### 阶段 7：ARM64 与 Store

- 安装并固定 `aarch64-pc-windows-msvc`，确认 VS C++ ARM64 链接组件；
- 同时生成 ARM64 主程序、更新器和 Rust DLL，逐个检查 PE 架构；
- 在真实 ARM64 Windows 设备验证，不以 x64 模拟或静态检查代替；
- 最后建立 Store/MSIX 的独立 AOT 配置，避免与直接安装包的运行时策略混用；
- x64 和 ARM64 都通过后，再考虑把 AOT 设为默认 Release。

## 8. 验证策略

### 8.1 不设置“先重做测试”的前置任务

现有测试继续作为每批修改的基础。只在修改暴露出缺口时补定向用例：

- MVVM：属性默认值、通知和副作用钩子；
- JSON：当前格式、旧版本迁移、缺字段、未知字段、备份与恢复；
- Rust shortcut：C# 旧实现与 Rust 结果对照；
- SearchCore：同输入的结果集合、排序和取消行为；
- 发布链：主程序、更新器和安装器的文件/架构检查。

Native AOT 的 COM、WinRT、XAML 和资源问题经常只在裁剪后的真实产物中出现，因此发布前的运行冒烟不可省略，但它属于分阶段验收，不是开始修改之前的大型测试工程。

### 8.2 x64 AOT 关键功能矩阵

在阶段 5 至少覆盖：

- 首次启动、单实例、托盘、退出与重启；
- Widget 恢复、增删、锁定、拖拽、文件拖入拖出和物理指针边界；
- 设置窗口中所有主要分区及重启后的持久化；
- 文件复制、移动、跨卷、回收站、Shell 上下文菜单；
- 快捷方式读取、创建、损坏链接和系统对话；
- FolderPicker、Quick Access/Explorer 打开与固定操作；
- 全局快捷键与拖放权限；
- 音频会话、音量和媒体控制；
- 搜索索引初始化、查询、增量更新和空闲恢复；
- Todo、快速记录、天气、Glance、备份恢复和旧数据迁移；
- 简体中文、繁体中文、英文等资源；
- 自动更新成功、失败、回滚和安装后重启。

### 8.3 发布门槛

建议将以下条件作为 AOT 默认发布门槛：

1. AOT 构建没有 ILC “always throw” 信息；
2. 没有未处理的 `IL2xxx`、`IL3xxx`、`MVVMTK0045`、`CsWinRT1028` 和 `WMC1510`；
3. 不使用宽泛的全程序集 trimming root 或 blanket suppression 掩盖问题；
4. 主程序和更新器都不携带 CoreCLR/JIT；
5. 安装包不包含 PDB，符号单独保存；
6. x64 完整功能矩阵通过；
7. ARM64 默认发布前已在真实 ARM64 设备通过；
8. 直接安装包、Full 安装包和 Store 包各自的运行时依赖与升级路径经过验证。

## 9. 性能与体积指标的使用方式

旧方案中约 446 MiB Working Set 的数字来自历史 Debug 样本，不能直接代表当前 Release、AOT 或用户机器。本次没有重新采集运行时性能数据，也没有启动 AOT 产物。

后续应在同一代码版本、同一数据集、同一机器和相同冷/热条件下比较：

- 冷启动到托盘、首个 Widget 可交互、设置窗口首次打开；
- 热启动和第二次打开窗口；
- Working Set、Private Bytes、线程数、句柄数；
- 搜索 10k/100k/300k 的 P50/P95；
- 安装包、安装后文件和独立符号体积；
- 首次编译、增量编译和 CI 时间。

这些数据的作用是发现回归和决定后续优化重点。AOT 已是确定目标，因此不再设置“收益低于某个百分比就终止 AOT”的门槛。Rust `SearchCore` 则应接受结果一致性和资源收益验证；如果它没有优于 C# 参考实现，可以保留 Rust 在 shortcut/native shell 边界，而不必强行扩大搜索迁移范围。

## 10. 近期合并单元与下一步开放条件

按当前完成度，五十七个独立合并单元已完成到各自定义的自动化、人工、结构或实际 AOT 运行验证层级：

1. **AOT 审计与更新器原生发布（已完成结构验证）**：固定 SDK、建立不改变默认 Release 的 x64 AOT 脚本、更新器 AOT、符号分离、产物检查；
2. **低风险生成代码兼容性（已完成编译与 AOT 验证）**：迁移 `SearchPopupViewModel` 的 12 个属性、`SettingsViewModel` 的 67 个属性和 7 个 `CsWinRT1028` 类型；
3. **Rust 阶段 3A（已完成构建与 AOT 验证）**：固定工具链，建立 workspace、ABI、x64 opt-in 构建和发布审计；审计收口增加独立 Rust target/staging、前后工作树指纹、架构组合保护和 PE 导入清单；没有切换产品功能。
4. **Rust 阶段 3B-0（已完成 ABI 验证）**：冻结 ABI 2、结构、状态、能力协商与
   Resolve 计划语义；能力位保持 0，没有产品调用点；
5. **Rust 阶段 3B-1（已完成只读实现验证）**：实现两套 `.lnk` 只读语义并把能力位
   调整为 3，记录锁定依赖图；仍无产品加载器或行为切换。
6. **Rust 阶段 3B-2（已完成差分与显式接入验证）**：实现无 UI Resolve、安全
   加载器、JIT C#/Rust 差分与显式 Rust opt-in；普通 JIT 默认仍为 C#。
7. **Rust 阶段 3C-1（已完成写入实现与差分验证）**：实现完整五字段创建/覆盖，
   接入文件夹和拖放权限应用快捷方式写入，并验证覆盖清空、并发和缓存失效；普通
   JIT 默认仍为 C#，旧 COM 分支尚未删除。
8. **Rust 阶段 3C-2（已完成人工与自动化验证）**：实现带 owner HWND 的 Windows UI
   Resolve，接通文件组件真实宿主窗口、删除结果同步和缓存失效；显式 Rust JIT 中的
   实际 DLL 路径、父窗口、取消、修复和删除均已人工通过。
9. **Rust 阶段 3C-3（已完成 AOT 结构收口）**：以编译期边界保留 JIT oracle 并从 AOT
   排除旧 shortcut `ComImport`，封闭无 HWND 激活旁路，增加无副作用诊断与 x64/ARM64
   打包隔离契约；shortcut `always-throw` 已清零，AOT 主程序未启动。
10. **Rust 阶段 3C-3-R（已完成发布属性收口）**：项目在编译前拒绝缺失 Rust、非 x64
    或 Platform/RID 冲突的 AOT 组合；审计脚本在触碰工具和产物前拒绝 ARM64。普通 JIT
    默认不变，配置 11 / schema 9 的隔离 x64 审计通过，AOT 主程序未启动。
11. **阶段 4A FolderPicker（已完成）**：服务迁移为现代
    WindowId Picker，8 个入口全部异步且显式绑定有效 HWND，托盘与 JumpList 共用稳定
    托盘 owner。4 条新增契约、1974/1974 全量测试和配置 12 / schema 9 隔离审计通过；
    FolderPicker `always-throw` 已消除；真实 JumpList 转发的 owner/取消探针和完整人工
    入口矩阵均通过。
12. **阶段 4B-0 JSON 迁移基线（已完成）**：冻结 16 个文件、49 处调用、7 个非泛型
    枚举 converter 和 3 个泛型 helper；补充 7 条基线契约与 Glance 数字枚举真实写入断言，
    Debug 构建无错误、x64 全量测试 1981/1981、配置 12 / schema 9 隔离 AOT 审计通过。
    本阶段没有增加 context，也没有改变生产 JSON 格式。
13. **阶段 4B-1 JSON 叶子迁移（已完成）**：只迁移更新、城市、天气、本地化和诊断 5 个
    文件中的 8 处调用，增加 4 个 metadata context；非泛型 converter 从 7 个降到 6 个。
    定向测试 105/105、x64 全量测试 1983/1983、配置 12 / schema 9 隔离 AOT 审计通过；
    JSON 直接相关警告 210→176，已迁移文件为 0，警告类别与 always-throw 集合没有扩张。
14. **阶段 4B-2 JSON 用户持久化迁移（已完成）**：只迁移 Settings、Quick Capture、
    Todo、Glance preferences、Glance image catalog 和文件组件 metadata 的 22 处调用，
    增加 6 个 metadata context；非泛型 converter 从 6 个降到 2 个。定向测试 287/287、
    x64 全量测试 1986/1986、配置 12 / schema 9 隔离 AOT 审计通过；JSON 直接相关警告
    176→80，本轮迁移文件为 0，警告类别与 always-throw 集合没有扩张。
15. **阶段 4B-3A JSON 搜索与桌面恢复迁移（已完成）**：只迁移搜索历史、搜索索引和
    桌面恢复 3 个文件中的 7 处调用，增加 3 个 metadata context。搜索历史数字枚举、旧
    JSON 索引、DBIX、紧凑 roots manifest 与恢复日志格式均保持；定向测试 55/55、x64
    全量测试 1987/1987、规范 Debug 构建和配置 12 / schema 9 隔离 AOT 审计通过。JSON
    直接相关警告 80→52，本轮迁移文件为 0，警告类别与 always-throw 集合没有扩张。
16. **阶段 4B-3B JSON 维护用户数据迁移（已完成）**：只迁移附件健康和备份服务中的
    用户数据读取、校验及附件路径重定位 6 处调用，没有新增 context 所有者。复用既有
    Settings/Quick Capture/Todo context 的兼容 options 实例，保持属性名大小写不敏感、
    字符串/旧数字枚举、未知字段及附件路径行为；读取与校验 helper 显式接收有限白名单的
    `JsonTypeInfo<T>`，非泛型 converter 从 2 个降为 0。定向测试 210/210、x64 全量测试
    1990/1990、配置 12 / schema 9 隔离 AOT 审计通过；JSON 直接相关警告 52→24，附件
    健康为 0，剩余记录只来自 4B-3C 控制文档，警告类别与 always-throw 集合没有扩张。
17. **阶段 4B-3C JSON 备份控制文档迁移（已完成）**：只迁移 manifest、file manifest 和
    pending marker 的 6 处调用，增加 1 个嵌套私有 metadata context；控制 DTO 可见性不变，
    camelCase、缩进、大小写敏感、未知字段及 schema 1/2 行为均保持。原泛型 marker 写入
    helper 已收口为非泛型专用入口。定向测试 31/31、x64 全量测试 1993/1993、配置 12 /
    schema 9 隔离 AOT 审计通过；JSON 直接相关警告 24→0，备份服务 IL2026/IL3050 为 0，
    警告类别与 always-throw 集合没有扩张。
18. **阶段 4B-4 JSON 默认反射关闭审计（已完成）**：主程序与更新器只在
    `DeskBoxAotAudit=true` 条件组设置 `JsonSerializerIsReflectionEnabledByDefault=false`，
    普通构建实际求值为空；审计脚本在 restore/publish 显式传入并在摘要中记录该值。
    新契约先失败后通过，AOT/JSON 定向契约 32/32、x64 全量测试 1994/1994、配置 13 /
    schema 10 隔离审计通过；JSON 警告和反射回退错误为 0，现有 12 类警告与 1 条音乐
    音量 `always-throw` 均未扩张，4B JSON 阶段收口。
19. **阶段 4C 音乐音量 Rust/AOT 边界（已完成结构迁移）**：新增粗粒度 Core Audio
    v1 导出、能力位 `1 << 5`、托管安全调用和 JIT/AOT 编译期路由；旧 C# COM 仅作为普通
    JIT 默认 oracle 并从 AOT 排除。新契约先在旧实现上 3/3 按预期失败，实施后 Rust
    42/42、音乐音量契约与只读真实 ABI 探测、x64 全量 2006/2006 通过；配置 14 / schema 11 隔离审计为 39 个
    发布文件、3 个 PDB、12 类既有警告、ABI 2、能力 63、七个导出、同次哈希一致，
    shortcut、音乐音量及完整 `always-throw` 集合均为 0。系统/session setter 与默认设备
    切换尚需显式 Rust JIT 人工确认，AOT 主程序仍未启动。
20. **阶段 4D-1A 低风险类型化入口（已完成）**：`Marshal.SizeOf(Type)` 改为泛型尺寸，
    Markdig task-list 改用公开强类型属性，搜索收藏/历史推荐改用已有 DTO。新增 4 条契约先
    在旧实现上按预期失败，实施后相关定向测试 49/49、x64 全量测试 2010/2010 通过；配置
    15 / schema 12 隔离审计为 39 个发布文件、3 个 PDB、ABI 2、能力 63、七个导出、
    默认 JSON 反射关闭、同次 Rust DLL 哈希一致，三个目标文件警告与完整
    `always-throw` 集合均为 0。本批没有修改 Rust ABI，也没有启动 AOT 主程序。
21. **阶段 4D-1B 应用内反射收口（已完成）**：Quick Capture 的 XAML 初始化失败诊断改为
    固定异常类型、HRESULT、消息、内部异常和堆栈字段；`Localized` 改为对
    `SettingsCard`、`SettingsExpander`、`TextBox` 的直接赋值，并冻结 301 个 XAML 标记。
    新增 3 条契约先在旧实现上 3/3 按预期失败，实施后 Quick Capture/Localization 定向测试
    205/205、x64 全量测试 2013/2013 通过；配置 16 / schema 13 隔离审计保持 39 个发布文件、
    3 个 PDB、ABI 2、能力 63、七个导出、默认 JSON 反射关闭和同次哈希一致。4D-1A/1B
    目标文件告警、未知告警和完整 `always-throw` 均为 0，原始 IL2075 从 13 降为 9。
22. **阶段 4D-2 未使用 COM helper 删除（已完成）**：先以 3 条契约冻结
    `FileOperationHelper` 零调用和 `FileService` 的四个真实入口，再删除 223 行死代码；没有
    修改产品文件操作或 Rust 模块。AOT/4D 测试 42/42、文件服务扩大定向测试 53/53、x64
    全量测试 2016/2016 通过。配置 17 / schema 14 隔离审计确认删除源码、引用和目标告警均为
    0，原始 IL2050 从 4 降为 2；39 个发布文件、3 个 PDB、ABI 2、能力 63、七个导出、默认
    JSON 反射关闭、同次哈希和 `always-throw=0` 均保持不变。
23. **阶段 4D-3A OLE 数据读取侧（已完成自动化/AOT 边界）**：删除 `COMIDataObject`、
    `Marshal.GetObjectForIUnknown`/`ReleaseComObject` 和内置 `IStream` RCW，新增回调期借用的
    `GetData`、`QueryGetData`、`Read` 三槽 vtable 读取层。4 条结构契约在旧实现上 3 失败/
    1 通过，新增 7 条 ABI 行为测试；拖放/AOT 扩大定向测试 64/64、x64 全量 2027/2027
    通过。配置 18 / schema 15 隔离审计确认旧 RCW 匹配、新读取层告警和非预期目标告警均为
    0；39 个发布文件、3 个 PDB、Rust/JSON/哈希与 `always-throw=0` 均保持不变。剩余两次
    IL2050 只对应留给 4D-3B 的 `RegisterDragDrop`。真实 OLE 拖入尚待人工确认。
24. **阶段 4D-3B OLE 注册与回调侧（已完成自动化/AOT 边界）**：传统 `[ComImport]
    IDropTarget`、`[ComVisible]` CCW 和接口参数 `DllImport` 已替换为 managed-wrapper-only
    `[GeneratedComInterface]`、`[GeneratedComClass]`、显式 `IDropTarget*` 与两个
    `[LibraryImport]`。4 条契约先在旧实现上 4/4 失败，5 条真实 CCW 测试覆盖 IID、slot 3-6、
    回调/effect 和模拟 Register/Revoke 引用顺序；x64 全量测试 2036/2036 通过。配置 19 /
    schema 16 审计确认旧模式、缺失模式、目标警告和 IL2050 均为 0；39 个发布文件、3 个 PDB、
    ABI 2、能力 63、七个导出、同次哈希与 `always-throw=0` 保持不变。
25. **阶段 4D-4A Explorer 托管环境启动（已完成自动化/AOT 边界）**：普通 JIT 默认保留原
    C# dynamic oracle，显式 Rust 与 Native AOT 使用强类型 Shell 接口的完整原生操作；产品层
    的本地 ShellExecute/Open With 回退不变。新增能力位 `1 << 6` 和 v1 导出后，Rust 45/45、
    AOT/Explorer/shortcut/music 定向测试 98/98、x64 全量测试 2049/2049 通过；配置 20 /
    schema 17 审计为 39 个
    发布文件、3 个 PDB、ABI 2、能力 127、八个导出、同次哈希一致，两个目标文件警告、
    Explorer 启动与完整 `always-throw` 均为 0。IL2026/IL2072/IL3050 从 44/4/77 降至
    34/2/62；显式 Rust JIT 真实打开与失败回退矩阵仍待人工确认。
26. **阶段 4D-4B Quick Access Rust/AOT 边界（已完成自动化/AOT 边界）**：普通 JIT 默认
    保留 C# ProgID/dynamic oracle，显式 Rust 与 Native AOT 使用独立的强类型 Shell 操作；
    查询、固定、取消固定和重复取消固定语义均已冻结。新增能力位 `1 << 7` 和第九个导出后，
    Rust 52/52、阶段契约 12/12、扩大定向 89/89、x64 全量 2061/2061 通过；配置 21 /
    schema 18 确认 39 个发布文件、3 个 PDB、ABI 2、能力 255、九个导出、同次哈希一致，
    目标警告与 Quick Access/完整 `always-throw` 均为 0。真实只读查询通过；自动化未调用
    pin/unpin，AOT 主程序仍未启动。
27. **阶段 4D-5 托盘反射收口（已完成自动化/AOT 边界）**：直接使用公开
    `TaskbarIcon.TrayIcon.WindowHandle/Id`，并通过 `SecondWindowContextMenuOpened`、菜单项
    `Loaded` 和 WinUI 公开视觉树配置真正显示的 `MenuFlyoutPresenter`/Popup；删除私有
    `ContextMenuFlyout` 及 identity 反射，不升级 H.NotifyIcon，不新增 Rust 或 trimming root。
    6 条契约在旧实现上 4 失败/2 通过，实施后 6/6；x64 全量 2067/2067 通过。配置 22 /
    schema 19 确认 39 个发布文件、3 个 PDB、源码指纹稳定，旧反射、缺失公开契约、目标告警、
    非预期告警及完整 `always-throw` 均为 0；IL2075 9→0，Rust ABI 2、能力 255、九个导出与
    staging/publish 哈希保持一致。AOT 主程序仍未启动。
28. **阶段 4E-0 搜索历史 WMC1506 收口（已完成自动化/AOT 边界）**：冻结
    `SearchHistoryEntry` 的不可变 `required init` 契约，以及刷新/语言切换/删除/清空时重建条目的
    生命周期；将 4 条 Query 和 2 条 DeleteLabel `OneWay x:Bind` 精确改为 `OneTime`。6 条契约
    在旧实现上 4 失败/2 通过，实施后 6/6；x64 全量 2073/2073 通过。配置 23 / schema 20 确认
    WMC1506 6→0、WMC1510 保持 1265，旧绑定、缺失新绑定/生命周期模式、目标告警、非预期告警
    和完整 `always-throw` 均为 0；Rust ABI 2、能力 255、九个导出和同次哈希保持一致。AOT 主程序
    仍未启动。
29. **阶段 4E-1 叶子 compiled-binding pilot（已完成自动化/AOT 边界）**：将
    `PinStateIcon` 2 条 Foreground、`MarkdownSourceEditor` 3 条自有 DependencyProperty 和两个
    桌面整理路径 Tooltip 共 7 条 Binding 改为 OneWay typed `x:Bind`；冻结 Style Setter 与运行时
    DataContext 延期范围。8 条契约在旧实现上 6 失败/2 通过，实施后 8/8；受影响既有契约 11/11、
    x64 全量 2081/2081 通过。配置 24 / schema 21 确认 WMC1510 1265→1258，目标 XAML、旧绑定、
    缺失新绑定/生命周期、延期范围、非预期告警和完整 `always-throw` 均为 0；Rust ABI 2、能力
    255、九个导出和同次哈希保持一致。AOT 主程序仍未启动。
30. **阶段 4E-2 自有属性 compiled binding（已完成自动化/AOT 边界）**：将
    `MusicTransportIcon` 7 条 Foreground 与 `WidgetInlineEditor` 8 条自身属性 Binding 改为 typed
    `x:Bind`，保留 Text 的 TwoWay/PropertyChanged。9 条契约在旧实现上 6 失败/3 通过，实施后
    9/9；AOT/4D/4E 扩大定向 102/102、x64 全量 2090/2090 通过。配置 25 / schema 22 确认
    WMC1510 1258→1243，目标 XAML、旧绑定、缺失 compiled binding/行为、延期范围、非预期告警
    和完整 `always-throw` 均为 0；39 个发布文件、3 个 PDB、源码指纹、Rust ABI 2、能力 255、
    九个导出及同次哈希保持一致。AOT 主程序仍未启动。
31. **阶段 4E-3 typed DataTemplate 小批次（已完成自动化/AOT 边界）**：将
    `AttachmentTileStrip` 7 条和 `SearchPopupWindow` 7 条 Binding 改为带明确模板类型的
    `x:Bind`。附件全部 OneWay；搜索只让可通知的 Tab Count 使用 OneWay，其余 6 条使用 OneTime，
    并保留推荐应用图标的两处显式回填。11 条契约在旧实现上 6 失败/5 通过，实施后 11/11；
    AOT/4D/4E 扩大定向 113/113、x64 全量 2101/2101 通过。配置 26 / schema 23 确认
    WMC1510 1243→1229，目标源、旧绑定、缺失 compiled binding/类型/行为、延期范围、非预期告警
    和完整 `always-throw` 均为 0；39 个发布文件、3 个 PDB、源码指纹、Rust ABI 2、能力 255、
    九个导出及同次哈希保持一致。AOT 主程序仍未启动。
32. **阶段 4E-4 typed ViewModel 桥接（已完成自动化/AOT 边界）**：给
    `FileWidgetSettingsSection` 增加 `SettingsViewModel` DependencyProperty，父窗口显式赋值并在
    ViewModel dispose 前清空；3 条 OneWay 和 2 条 TwoWay Binding 改为 compiled binding。生成代码
    自动追踪根 DP、嵌套 PropertyChanged 和两个 attached ValueProperty 回写，因此删除冗余手动
    `Bindings.Update()`。11 条契约在旧实现上 8 失败/3 通过，实施后 11/11；AOT/4D/4E 扩大定向
    124/124、x64 全量 2112/2112 通过。配置 27 / schema 24 确认 WMC1510 1229→1224，桥接、
    生命周期、行为、延期范围、目标源、非预期告警和完整 `always-throw` 均为 0；39 个发布文件、
    3 个 PDB、源码指纹、Rust ABI 2、能力 255、九个导出及同次哈希保持一致。AOT 主程序仍未启动。
33. **阶段 4E-5 搜索结果行 compiled binding（已完成自动化/AOT 边界）**：真实 XAML 编译先
    证明 public typed Item DependencyProperty 会为 required `SearchResultItem` 生成无效 activator，
    且非 observable OneWay 桥会新增 WMC1506。最终保留 required DTO，改用 internal typed Item
    投影，每次 ElementPrepared 执行 `Bindings.Update()`，8 条叶子使用 OneTime compiled `x:Bind`；
    Icon/Size/Date 手工刷新、异步引用一致性、DataContext 查找、单选/多选和关闭顺序保持不变。
    12 条契约在旧实现上 5 失败/7 通过，实施后 12/12；AOT/Rust 扩大定向 198/198、x64 全量
    2124/2124 通过。配置 28 / schema 25 确认 WMC1510 1224→1216，目标和非预期告警、完整
    `always-throw` 均为 0；39 个发布文件、3 个 PDB、源码指纹、Rust ABI 2、能力 255、九个导出
    及同次哈希保持一致。该批完成后进入阶段 5A。
34. **阶段 5A x64 AOT 隔离启动与基础存活（已完成实际运行边界）**：增加
    `DESKBOX_AOT_PREVIEW_DATA_ROOT` 的 NativeAOT-only 分支和严格启动器；旧摘要、正式数据根、
    重叠路径、产物哈希不一致和非精确进程目标均在启动前拒绝。9 条新契约先 9/9 失败，实施后
    9/9；5A/数据路径组合 20/20、x64 全量 2133/2133 通过。配置 29 / schema 26 的最终审计为
    39 个发布文件、3 个 PDB、WMC1506=0、WMC1510=1216、完整 `always-throw=0`，Rust ABI 2、
    能力 255、九个导出和 staging/publish 哈希一致。真实 AOT 在隔离根中完成首次启动、单实例、
    托盘正常退出和重启；正式目录运行前后保持 122 个文件、303,016,768 bytes 和同一元数据指纹。
35. **阶段 5B-1 shortcut AOT → Rust 真实边界冒烟（已完成实际运行边界）**：增加
    NativeAOT-only 显式场景入口与严格 runner，在五个独立 preview 根覆盖 Core、有效目标、取消、
    同卷移动修复和删除；两个模态对话框的实际 owner 与记录的托盘 HWND 相同。实现复盘修正错误的
    替代文件夹具、失败后遗留进程和 JSON 固定清单三项遗漏。Rust 52/52、x64 全量 2141/2141
    通过；profile 30 / schema 27 保持 39 个发布文件、WMC1506=0、WMC1510=1216、完整
    `always-throw=0`，Rust ABI 2、能力 255、九个导出及同次哈希一致。正式数据在五个场景前后
    均保持 122 个文件、303,080,019 bytes 和同一元数据指纹。该批当时开放 5B-2A 的只读边界验证。
36. **阶段 5B-2A Explorer 启动与 Quick Access 只读 AOT → Rust 冒烟（已完成实际运行边界）**：
    增加 NativeAOT-only 组合场景和严格 runner。Explorer 产品服务实际执行一次性命令并生成标记，
    status/HRESULT 为 0、attempted phases 为 `0x7F`；Quick Access 产品查询前后和原生查询均为
    `NotPinned`，status/HRESULT 为 0、attempted phases 为 `0x3F`，没有执行 pin/unpin。8 条新契约
    先 8/8 失败，实施后 8/8；组合定向 20/20、Rust 52/52、x64 全量 2149/2149 通过。profile 31 /
    schema 28 保持 39 个发布文件、WMC1506=0、WMC1510=1216、完整 `always-throw=0`，Rust ABI 2、
    能力 255、九个导出及同次哈希一致。正式目录前后保持 122 个文件、303,204,886 bytes 和同一
    元数据指纹。该批当时开放 5B-2B，补偿性 unpin 与最终 `NotPinned` 是硬门禁。
37. **阶段 5B-2B Quick Access pin/unpin 与故障补偿 AOT → Rust 冒烟（已完成实际运行边界）**：
    新增独立 NativeAOT-only mutation runner 和六阶段脚本，覆盖前置清理、固定后应用内故障、
    固定后强制终止、新进程恢复、正常 pin/unpin 和最终清理。强制终止证据实际停在 `Pinned`，
    恢复进程先读取到 `Pinned`，再由产品 unpin 与原生查询共同证明 `NotPinned`。11 条契约、Rust
    52/52、x64 全量 2160/2160 通过；profile 32 / schema 29 保持 39 个发布文件、WMC1506=0、
    WMC1510=1216、完整 `always-throw=0`，Rust ABI 2、能力 255、九个导出及同次哈希一致。
    正式数据指纹不变，最终没有受审计 AOT 进程遗留。下一批调整为 5B-3A 音乐音量只读 getter。
38. **阶段 5B-3A 音乐音量只读 getter AOT → Rust 冒烟（已完成实际运行边界）**：
    新增独立 NativeAOT-only runner，只调用产品 `GetSystemMasterVolumeAsync`、`GetVolumeAsync`
    和用于明细证据的直接原生 getter，源码与审计双重禁止四个 setter。真实运行中产品系统音量、
    产品 snapshot、原生 snapshot 及前后原生系统读取均为 `0.370000004768372`，系统音量未变化；
    原生 snapshot status/HRESULT 为 0、attempted phases 为 `0x1F`。本机当时没有匹配 session，
    因此只证明了无匹配 snapshot 契约，没有宣称匹配 session 已通过。10 条新契约、Rust 52/52、
    x64 全量 2170/2170 通过；profile 33 / schema 30 保持 39 个发布文件、WMC1506=0、
    WMC1510=1216、完整 `always-throw=0`，Rust ABI 2、能力 255、九个导出及同次哈希一致。
    正式数据指纹不变，最终没有受审计 AOT 进程遗留。下一批建议为 5B-3B 系统主音量 setter。
39. **阶段 5B-3B 系统主音量 setter 与故障恢复 AOT → Rust 冒烟（已完成实际运行边界）**：
    新增 NativeAOT-only mutation runner 和六阶段脚本；稳定恢复意图在任何产品 setter 前原子写入并
    source-generated 回读，只有直接 Rust getter 验证恢复到原值后才删除。真实场景覆盖无遗留意图
    preflight、变更后应用内主动异常、变更后强制终止、独立新进程恢复、正常变更/恢复和 postflight。
    原值为 `0.370000004768372`，探测值为 `0.420000004768372`；强制终止后恢复进程先观察到
    `0.420000016689301`，随后恢复到原值。10 条新契约、Rust 52/52、x64 全量 2180/2180 通过；
    profile 34 / schema 31 保持 39 个发布文件、WMC1506=0、WMC1510=1216、完整
    `always-throw=0`，Rust ABI 2、能力 255、九个导出及同次哈希一致。JSON 固定清单为 21 个
    文件、55/55 处 source-generated 调用和 19 个 context 所有者。恢复意图最终不存在，六个 AOT
    PID 均已退出，正式数据指纹前后一致。下一批建议为 5B-3C 可控媒体 session getter/setter。
40. **阶段 5B-3C 可控媒体 session getter/setter 与故障恢复（已完成实际运行边界）**：测试专用
    Rust 静音夹具提供固定 session，产品 getter/setter 与直接原生证据共同完成状态变更、应用内恢复、
    强制终止后的新进程恢复、session 消失保留意图和最终 postflight；生产 Rust ABI 未因夹具扩展。
41. **阶段 5B-4A 基础托管 UI 只读矩阵（已完成实际运行边界）**：真实 AOT 进程恢复固定
    File/Search Widget、12 套语言、六个设置主分区以及搜索筛选和双向排序；正式数据指纹不变。
42. **阶段 5B-4B1 深层设置与集合投影（已完成实际运行边界）**：覆盖非空设置搜索、24 个深层
    路由、breadcrumb、文件叠放规则和备份清单，并建立 282/282 设置生成绑定清单。
43. **阶段 5B-4B2A 设置与 Widget 拓扑持久化（已完成实际运行边界）**：三个独立进程完成
    设置和固定 File Widget 的写入、重载、基线恢复和 postflight，Search Widget 作为未修改对照项。
44. **阶段 5B-4B2B1 Quick Capture 内容 store（已完成实际运行边界）**：三个独立进程完成
    两类文本保存、托管附件、跨进程重载、附件/物理文件删除、记录删除和空状态 postflight。
45. **阶段 5B-4B2B2A Todo 核心任务与备注（已完成实际运行边界）**：三个独立进程完成任务、
    标题、备注 timer/显式保存、完成/恢复、重载、任务删除和空 store postflight。
46. **阶段 5B-4B2B2B1 Todo 步骤（已完成实际运行边界）**：三个独立进程完成步骤创建、编辑、
    完成/恢复、实际行投影、重载、步骤删除、任务删除和 postflight。
47. **阶段 5B-4B2B2B2 Todo 托管附件（已完成实际运行边界）**：三个独立进程完成 owned 文件
    托管导入、哈希、实际卡片、重载、显式附件删除、物理清理、任务删除和 postflight；最终矩阵连续
    两轮通过，Todo 核心和步骤矩阵也重新通过。profile 42 / schema 39、AOT 308/308、x64 全量
    2295/2295、Rust 54/54；下一批开放 5B-4B2C1 Glance 本地图片与偏好持久化。
48. **阶段 5B-4B2C1 Glance 本地图片与偏好（已完成实际运行边界）**：三个独立进程完成 owned
    PNG、显示/布局/播放偏好、真实 ImageBrush 与布局层取证、重载、基线恢复和 postflight。profile 43 /
    schema 40、AOT 324/324、x64 全量 2313/2313、Rust 54/54；下一批开放 5B-4B2C2A 天气设置与
    Widget 视图元数据持久化，真实天气 surface 拆为后续 5B-4B2C2B。
49. **阶段 5B-4B2C2A 天气设置与 Widget 视图元数据（已完成实际运行边界）**：三个独立进程
    完成 Weather 本地设置、全局默认视图与固定 Widget Day/Week metadata 的写入、重载、恢复和
    postflight；Weather host、网络和定位路径保持未加载。profile 44 / schema 41、AOT 335/335、
    x64 全量 2330/2330、Rust 54/54；下一批开放 5B-4B2C2B 确定性天气 surface。
50. **阶段 5B-4B2C2B 确定性天气 surface（已完成实际运行边界）**：三个独立进程使用严格门禁的
    NativeAOT-only 固定 WeatherData，真实加载 HWND/XamlRoot、Expanded/Compact、Day/Week、
    Rich/Standard、摄氏/华氏、km/h/mph、UV/气压显隐和 24/7 非空集合；三进程重载、恢复、
    postflight、6 条夹具日志、0 条网络日志和正式数据保护全部通过。profile 45 / schema 42、AOT
    348/348、x64 全量 2343/2343、Rust 54/54；下一批开放 5B-4C1A owned 本地文件 surface 与核心操作。
51. **阶段 5B-4C1A owned 本地文件 surface 与核心操作（已完成实际运行边界）**：三个独立进程
    完成真实 File Widget 类型/排序、导航、watcher、copy/move/rename、冲突失败、重载、恢复和
    postflight。profile 46 / schema 43、AOT 360/360、x64 全量 2355/2355、Rust 54/54；下一批开放
    5B-4C1B1 owned 回收站删除、精确恢复与内部菜单路由。
52. **阶段 5B-4C1B1 owned 回收站删除与精确恢复（已完成实际运行边界）**：真实单选/多选菜单
    进入产品 `SHFileOperationW` 删除；Rust 以删除前父目录和项目名完整枚举并只恢复唯一匹配，三个
    独立进程完成删除、恢复和 postflight，正常矩阵连续两轮通过。首次失败运行触发独立补偿并恢复
    三个 owned 项目。profile 47 / schema 44、AOT 372/372、x64 全量 2367/2367、Rust 57/57，
    生产模块为 ABI 2 / 能力 511 / 十个导出；下一批开放 5B-4C1B2A Shell move/progress。
53. **阶段 5B-4C1B2A owned Shell move/progress 与故障语义（已完成实际运行边界）**：真实
    File Widget 单选/多选菜单补齐 owner HWND 并覆盖实际 `SHFileOperationW`、部分完成、取消和文件
    完成后任务晚到；三个独立进程完成变更、历史重载、恢复和 postflight，两个 run ID 连续通过。
    profile 48 / schema 45、AOT 383/383、x64 全量 2378/2378、Rust 57/57，生产模块保持 ABI 2 /
    能力 511 / 十个导出；下一批开放 5B-4C1B2B 系统 Properties owner。
54. **阶段 5B-4C1B2B 系统 Properties 与窗口关闭（已完成实际运行边界）**：真实 File Widget
    单选菜单调用 `SHObjectProperties`，精确 API owner 等于 File Widget HWND；两个独立进程均观察到
    唯一系统 `#32770` 属性页及同进程隐藏 `StubWindow32` 代理 owner，并经 `WM_CLOSE` 销毁。目标文件
    SHA-256、正式数据指纹、自然退出和 preview/recovery 双根清理均通过。profile 49 / schema 46、
    AOT 395/395、x64 全量 2390/2390、Rust 57/57，生产模块保持 ABI 2 / 能力 511 / 十个导出；
    下一批拆为 5B-4C1C1 Picker/StorageItems 与 5B-4C1C2 OLE/native drop/物理拖放。
55. **阶段 5B-4C1C1 Picker 与 Clipboard StorageItems（已完成实际运行边界）**：两个独立 run ID
    各由三个新 AOT 进程完成真实现代 picker 取消/选择、精确 owner、文件和文件夹 StorageItems、
    产品导入、重载、恢复和 postflight；三个进程均自然退出，正式数据指纹不变，preview/recovery
    双根清理完成。自动化没有修改用户全局剪贴板，Rust ABI 保持 2 / 能力 511 / 十个导出。profile 50 /
    schema 47、AOT 407/407、x64 全量 2402/2402、Rust 57/57；下一批只开放 5B-4C1C2 OLE/native
    drop/真实 Explorer 物理拖放。
56. **阶段 5B-4C1C2A OLE/native drop 自动化边界（已完成实际运行边界）**：三个新 AOT 进程
    经 generated COM CCW 和真实 HDROP vtable 覆盖 pointer 越界、`DragLeave`、Ctrl copy、无 Ctrl
    move、回调释放、384 MiB 可见进度层、重载、恢复和 postflight。进度卡实际为 `ZIndex=1000`、
    `TranslationZ=64` 和 `AcrylicBrush`；正式数据指纹不变，owned 双根清理完成。profile 51 /
    schema 48、AOT 417/417、x64 全量 2412/2412、Rust 57/57；程序化来源明确记录
    `PhysicalExplorerMouseVerified=false`。C1C2B 已取得真实 Explorer + 注入鼠标的自动补充证据，
    但真人鼠标、物理 Ctrl、视觉与非 100% DPI 仍为发布前人工门。
57. **阶段 5B-4C2A 主/搜索热键与保留 hook 自动化边界（已完成实际运行边界）**：两个新 AOT
    进程通过真实 `RegisterHotKey`、标准手势 `SendInput` 和结构化计数器证明主/搜索热键各接收与调用
    一次、调度失败为 0、冲突回滚、禁用/重新启用及前一进程退出后的重新注册；Win+Space 只证明
    hook 线程创建与停止，不注入保留手势。搜索热键的设置提交改为以真实系统注册为提交点。profile 52 /
    schema 49、AOT 423/423、x64 全量 2420/2420、Rust 57/57；物理标准键盘、物理 Win+Space、
    设置/引导录制器与内部 `0xE8` 屏蔽键归入 5B-4C2B。
58. **阶段 5B-4C3A Todo recurrence/reminder 确定性状态矩阵（已完成实际运行边界）**：五个新
    AOT 进程使用固定 clock、owned settings/store 和 callback-only evidence，覆盖两个初始 due 候选、
    reminder-off/完成/stale 控制、snooze 前后、完成并生成次日 occurrence、下一提醒、跨进程 dismissal、
    清空和 postflight。五次自然退出、正式数据指纹不变、系统通知触发为 0。profile 53 / schema 50、
    AOT 429/429、x64 全量 2426/2426、Rust 57/57；下一批拆为 C3B1 通知展示/清理与 C3B2 activation。
59. **阶段 5B-4C3B1 Todo 原生通知展示与清理生命周期（已完成实际运行边界）**：三个新 AOT
    进程使用唯一 run ID、两个 tag 和一个 group，完成单项/聚合产品通知真实展示、系统历史枚举、
    两个动作与四个 snooze 选项核对、聚合无动作、跨进程历史恢复、逐条精确删除、注销和 postflight；
    通知计数为 `0→2 / 2→0 / 0→0`，三次自然退出、正式数据指纹不变、activation 为 0。profile 54 /
    schema 51、AOT 435/435、x64 全量 2432/2432、Rust 57/57；下一批拆为 C3B2A grammar/动作路由
    与 C3B2B 真实 activation/单实例转发。
60. **阶段 5B-4C3B2B2A Todo activation 真实 surface 定位与可见刷新（已完成实际运行边界）**：
    产品正文路由增加真实 Loaded/`XamlRoot`、精确 item 和两帧提交门禁，Complete/Snooze 分开记录
    持久化与可见刷新。一个受审计 AOT 进程在同一 Todo HWND 上完成正文选中、Complete 和 `30m`
    Snooze 三条路由，自然退出、正式数据指纹不变、owned root 清理。profile 56 / schema 53、AOT
    452/452、x64 全量 2468/2468、Rust 57/57；`UserClickVerified=false`，下一批拆为 B2B2B1
    运行中真实通知点击，再以 B2B2B2 验证冷启动与第二实例真实点击。

### 4D-0 复盘冻结清单（已完成）

配置 14 / schema 11 的最终日志按脚本原始出现次数记录 IL2026 44、IL2050 4、IL2072 4、
IL2075 16、IL3050 78、WMC1506 6 和 WMC1510 1265。编译器和 ILC 会重复报告同一来源，按
`文件 + 行 + 警告码` 去重后，产品源码归为：

| 领域 | 唯一来源位置 | 处理批次 |
| --- | ---: | --- |
| `ExplorerQuickAccessHelper` dynamic / ProgID | IL2026 13、IL3050 13、IL2072 1 | 4D-4B（已完成：AOT 只保留 Rust 边界） |
| `ExplorerShellLaunchService` dynamic / ProgID | IL2026 5、IL3050 5、IL2072 1 | 4D-4A（已完成：AOT 只保留 Rust 边界） |
| `IFileOperation` / `IShellItem` COM marshalling | IL2050 2 | 4D-2（已完成：删除死代码） |
| OLE `RegisterDragDrop` COM marshalling | IL2050 1 | 4D-3B（已完成：源生成 CCW 与显式指针） |
| 托盘第三方对象私有属性反射 | IL2075 4 | 4D-5（已完成：公开 identity、事件与视觉树） |
| Quick Capture 异常诊断反射 | IL2075 2 | 4D-1B（已完成） |
| 搜索推荐匿名 `Title` 反射 | IL2075 2 | 4D-1A（已完成） |
| Markdig task-list 属性反射 | IL2075 1 | 4D-1A（已完成） |
| `Localized` 通用属性反射 | IL2075 1 | 4D-1B（已完成） |
| `DispatcherQueueOptions` 非泛型 `Marshal.SizeOf` | IL3050 1 | 4D-1A（已完成） |

运行库内部 7 个 IL3050 是产品 `dynamic` 调用链的下游结果，不作为独立修复项。搜索和
Markdig 两处已改用仓库/依赖中的公开类型，Win32 尺寸入口已机械泛型化，三者没有引入
Rust、COM 生成器或 trimming root。`WMC1510` 与 `WMC1506` 明确保留到 4E。

### 4D-1A 完成复盘与下一批冻结

配置 15 / schema 12 的隔离审计确认 4D-1A 三个目标文件警告为 0；原始日志计数为
IL2026 44、IL2050 4、IL2072 4、IL2075 13、IL3050 77、WMC1506 6 和 WMC1510 1265。
原始计数包含编译器/ILC 重复报告，只用于同一审计产物留档；批次门禁以目标文件零警告、
警告代码集合不扩张和完整 `always-throw=0` 为准。

已完成的 **4D-1B 应用内反射收口** 没有修改 COM、Rust ABI 或 XAML Binding：

- Quick Capture 的 `InitializeComponent` 失败诊断不再枚举异常对象全部属性，改为固定输出
  异常类型、HRESULT、消息、内部异常和堆栈文本；
- `Localized` 源码使用面已机械冻结为 152 个 `SettingsCard`、19 个
  `SettingsExpander` 和 2 个 `TextBox`。Header/Description 改为这三类明确类型的直接赋值，
  并用契约测试拒绝新增但未映射的目标类型；
- 自动化已覆盖 x64 全量测试和配置递增后的隔离 AOT 审计；设置页语言切换后 card、
  expander、两个 TextBox header 的实际刷新仍属于人工 UI 项。

### 4D-1B 完成复盘与 4D-2 调整

配置 16 / schema 13 的隔离审计确认 4D-1A 与 4D-1B 目标文件警告均为 0；原始日志计数为
IL2026 44、IL2050 4、IL2072 4、IL2075 9、IL3050 77、WMC1506 6 和 WMC1510 1265。
警告代码集合、`always-throw` 与 Rust/JSON 契约均未扩张。

完成后全仓库引用审计推翻了此前对 4D-2 的一个前提：`FileOperationHelper` 的两个 public
方法除了该文件自身没有任何产品或测试调用。实际回收站删除、Shell 进度移动和相关超时/
完成判断由 `FileService` 的另一套实现承担。因此当前下一开发批调整为 **4D-2 死代码删除**：

- 先以契约锁定 `FileOperationHelper` 零调用和 `FileService` 真实入口仍存在；
- 删除 `FileOperationHelper.cs`，不改动 `FileService` 的现行文件操作行为；
- 将 AOT 审计 profile/schema 递增并要求 `IL2050` 对应两处 `IFileOperation` 告警消失；
- 运行文件服务定向测试、x64 全量测试和隔离 AOT 审计。

4D-2 复杂度低、行为风险低，不使用 Rust。为死代码设计 C ABI、双后端和差分测试只会增加
维护面，不能带来产品收益。OLE DropTarget 仍因反向 Shell 回调、数据对象解析和窗口生命周期
保持独立；`Shell.Application` dynamic 则在其批次先冻结真实调用面，再判断是否适合完整
Rust 原生边界。AOT 主程序与 shortcut/音乐音量 AOT 运行冒烟仍保留到阶段 5。

### 4D-2 完成复盘与 4D-3 调整

4D-2 已按冻结范围完成：3 条新增契约在旧实现上 3/3 失败，删除后 AOT/4D 定向测试 42/42、
文件服务扩大定向测试 53/53、规范 x64 全量测试 2016/2016 通过。配置 17 / schema 14 的
隔离审计保持 39 个发布文件、3 个 PDB、默认 JSON 反射关闭、Rust ABI 2、能力 63、七个导出、
staging/publish 哈希一致和完整 `always-throw=0`；已删除文件和目标告警门禁均为 0，源码指纹
在审计前后稳定。

原始日志计数现为 IL2026 44、IL2050 2、IL2072 4、IL2075 9、IL3050 77、WMC1506 6 和
WMC1510 1265。IL2050 剩余两次输出来自编译器与 ILC 对
`NativeDropTarget.RegisterDragDrop` 同一来源的重复报告。

完成后的代码复盘确认，4D-3 不能只替换这一条 P/Invoke。`NativeDropTarget.cs` 还通过
`Marshal.GetObjectForIUnknown`/`ReleaseComObject` 使用内置 `IDataObject` 和 `IStream` RCW，
并承担虚拟文件、`STGMEDIUM`、临时目录和窗口生命周期。下一开发批因此调整为两步：

- **4D-3A 数据对象读取侧**：迁移 `IDataObject`/`IStream` 的内置 RCW，保持事件和文件导入
  业务逻辑不变；
- **4D-3B 注册与回调侧**：使用源生成 `IDropTarget` CCW、显式接口指针和源生成 P/Invoke，
  冻结注册/注销与 AddRef/Release，最终要求 IL2050 为 0。

OLE 拖放仍不适合完整 Rust：高频反向回调和 UI/窗口生命周期会把现有状态机拆到 ABI 两侧。
4D-4 的 `Shell.Application` 则是粗粒度主动调用，而且 .NET COM 源生成器不支持
`IDispatch`；该批应重点比较静态 Shell API 与完整 Rust 原生边界。4D-3/4 均属高复杂度，
不与托盘反射 4D-5、XAML 4E 或阶段 5 AOT 运行合并。

### 4D-3A/3B 完成复盘与 4D-4 门槛

4D-3A 已完成数据读取侧实现。`NativeDropTarget` 不再声明 `COMIDataObject`，也不再调用
`Marshal.GetObjectForIUnknown`、`Marshal.ReleaseComObject` 或内置 `IStream`。新读取层只在
OLE 回调期间借用 `IDataObject*`，固定调用 `GetData` slot 3、`QueryGetData` slot 5 和
`ISequentialStream::Read` slot 3；不 AddRef、不 Release、不跨回调保存指针。`IStream*` 则在
外层 `STGMEDIUM` 调用 `ReleaseStgMedium` 前同步读完。

新增 4 条结构契约和 7 条真实函数指针/布局测试，覆盖 Win64 结构偏移、vtable 参数、HRESULT、
`S_FALSE`、分块读取、越界返回与空指针。拖放、虚拟文件、4D/AOT 扩大定向测试 64/64、规范
x64 全量测试 2027/2027 通过。配置 18 / schema 15 的隔离审计用时约 122.9 秒，继续产生
39 个发布文件和 3 个 PDB；4D-3A 旧 RCW 源码匹配、新读取层告警、非预期拖放告警均为 0，
JSON 默认反射、Rust ABI 2/能力 63/七个导出/同次哈希和完整 `always-throw=0` 均保持不变。

原始警告计数仍为 IL2026 44、IL2050 2、IL2072 4、IL2075 9、IL3050 77、WMC1506 6 和
WMC1510 1265。两次 IL2050 是编译器与 ILC 对 `RegisterDragDrop(IDropTarget)` 同一来源的
重复输出；4D-3A 没有抑制它，因为注册侧明确属于 4D-3B。

4D-3B 随后已完成注册侧。`INativeDropTarget` 只生成 managed-object wrapper，生成类把四个
vtable 方法转发回同一个 `NativeDropTarget` owner；`ComInterfaceMarshaller` 获取明确的接口
指针，`RegisterDragDrop` 成功后由 OLE 持有额外引用，本地引用始终在 `finally` 中释放，
`RevokeDragDrop` 再释放 OLE 引用。真实 CCW 测试已模拟这条 AddRef/Release 顺序。

配置 19 / schema 16 隔离审计用时约 153.1 秒；旧注册模式、缺失生成式 COM 模式、目标文件
警告和 IL2050 均为 0。规范 x64 全量测试 2036/2036 通过。剩余原始警告为 IL2026 44、
IL2072 4、IL2075 9、IL3050 77、WMC1506 6 和 WMC1510 1265；Rust ABI 2、能力 63、七个
导出、同次哈希和 `always-throw=0` 均保持不变。

4D-3B 后人工复测已发现并修正三项托管 UI 问题：根表面现在会在数据包完整物化后、长时间
文件传输前完成系统 Drop deferral；根表面会旁路观察已被子项处理的 DragOver/DragLeave，
清理越界的文件夹/文件堆目标；进度卡也已移出文件项视口，使用独立高层与 Acrylic 背景。
本批回归 3/3、文件表面契约 21/21、x64 全量测试 2038/2038 和规范 Debug 构建均通过，且
没有修改 4D-3A/3B 的 COM 协议、Rust ABI 或 AOT 分支。

进入下一批前的 Debug 人工确认随后由用户完成，因此开放了 4D-4A。该完成条件与当时的
4D-3B 自动化/AOT 证据保持分开；历史测试和审计数字不回写为后续阶段数字。

### 4D-4A 完成复盘与 4D-4B 门槛

`ExplorerShellLaunchService` 与 `ExplorerQuickAccessHelper` 虽都使用 `Shell.Application`，
却是两条不同产品边界。4D-4A 只迁移前者：新增 Rust v1 导出后，Rust 用类型化的
`IShellDispatch`、`IShellWindows`、`IWebBrowser`、`IShellFolderViewDual` 和
`IShellDispatch2` 复现 Explorer 托管 `ShellExecute` 链。普通 JIT 默认保留 C# oracle；
Native AOT 编译期排除整段 dynamic/RCW 代码。既有 `Process.Start` 和 `SHOpenWithDialog`
仍是产品层失败回退，不由原生边界接管。

配置 20 / schema 17 的首轮隔离 x64 审计用时约 161.1 秒，产生 39 个发布文件和 3 个 PDB；
模块为 ABI 2、能力 127、八个必需导出，staging/publish DLL 哈希一致，源码指纹稳定。
4D-4A 两个目标文件告警、Explorer 启动 `always-throw` 及完整 `always-throw` 都为 0；剩余
原始 IL2026/IL2072/IL3050 已由 44/4/77 降至 34/2/62，IL2075 9、WMC1506 6、
WMC1510 1265 保持不变。完整 ABI 与阶段证据分别见
`explorer-shell-launch-native-abi-v1.md` 和 `aot-stage-4d-4a-report.md`。

4D-4B 在开发前仍需两项门槛：先完成人工显式 Rust JIT 的文件、文件夹、URL、未知扩展名、
缺失目标和环境继承矩阵；再冻结快速访问查询、`pintohome`、`unpinfromhome`、专用 STA 线程、
重复操作和失败语义。快速访问比 4D-4A 行为面更宽，不能复用本轮导出去暗中扩展 ABI；完成
设计审计后，再决定是否采用独立的完整 Rust 粗粒度操作。托盘反射 4D-5、XAML 4E 和 AOT
应用启动阶段 5 继续保持关闭。

### 4D-4B 完成复盘与 4D-5 门槛

4D-4B 没有复用 4D-4A 的操作导出，而是在同一版本化 DLL 中增加独立的 Quick Access v1
边界。Rust 以强类型 `IShellDispatch`、`Folder`、`FolderItems`、`FolderItem` 和
`FolderItem2` 复现查询、`pintohome` 和 `unpinfromhome`；普通 JIT 默认保留原 C# oracle，
Native AOT 则在编译期排除 ProgID、dynamic 和旧 RCW 路径。公开同步/异步 API、后台 STA、
查询的 Unknown/NotPinned/Pinned 三态、重复取消固定的幂等行为及父目录回退均保持不变。

配置 21 / schema 18 的隔离 x64 审计用时约 147.4 秒，产生 39 个发布文件和 3 个 PDB；模块为
ABI 2、能力 255、九个必需导出，staging/publish DLL 哈希一致且源码指纹稳定。两个目标文件
告警、Quick Access `always-throw` 和完整 `always-throw` 均为 0；原始 IL2026、IL2072、
IL3050 已归零，剩余原始分析项为 IL2075 9、WMC1506 6 和 WMC1510 1265。Rust 52/52、阶段
契约 12/12、扩大定向 89/89、x64 全量 2061/2061 通过。真实 DLL 只读查询返回成功、命中且
状态为 Pinned；自动化没有调用 pin/unpin，真实固定、重复固定、取消固定、重复取消固定、
Explorer 刷新和失败恢复仍属于人工交互矩阵。

下一批建议为 **4D-5 托盘反射收口**，不使用 Rust。第三方库当前公开暴露
`TaskbarIcon.TrayIcon`，其 `WindowHandle` 和 `Id` 也为公开成员，因此 4D-5A 可直接改为强类型
访问并删除对应反射。`ContextMenuFlyout` 仍是第三方 `TaskbarIcon` 的私有属性，4D-5B 需先冻结
第二窗口菜单样式与位置行为，再在公开事件/API 重构和窄范围保留之间做选择；不能用整程序集
trimming root 掩盖。4D-5 完成后再进入 4E XAML，AOT 主程序启动继续留在阶段 5。

### 4D-5 完成复盘与 4E-0 调整

4D-5 实施时确认无需拆成两次产品改动。`H.NotifyIcon.WinUI 2.5.0-beta.1` 已公开
`TaskbarIcon.TrayIcon`、`TrayIcon.WindowHandle/Id` 和 `SecondWindowContextMenuOpened`；库私有
第二窗口 flyout 会搬移原菜单项，因此应用可以用已知菜单项作为公开视觉树锚点，找到真正显示的
`MenuFlyoutPresenter` 和所属 Popup。最终一次删除 identity 与私有 flyout 反射，没有升级依赖、
引入 Rust、增加 trimming root 或抑制警告。SecondWindow、图标矩形定位、fallback、无滚动样式
和 `ForceCreate(enablesEfficiencyMode: false)` 均保持。

6 条新契约在旧实现上 4 失败/2 通过，实施后 6/6；规范 x64 全量测试 2067/2067 通过。配置
22 / schema 19 的隔离审计用时约 163.2 秒，产生 39 个发布文件、3 个 PDB，源码指纹稳定；
4D-5 旧反射命中、缺失公开调用模式、目标文件告警、非预期告警和完整 `always-throw` 均为 0。
原始 IL2026、IL2050、IL2072、IL2075 和 IL3050 全部为 0。Rust 模块保持 ABI 2、能力 255、
九个必需导出及同次哈希一致。完整记录见 `aot-stage-4d-5-report.md`。

完成后对剩余 XAML 告警重新分组，下一批调整为 **4E-0 搜索历史 WMC1506 收口**，不直接
开始 1,265 条 WMC1510。6 条 WMC1506 全在 `SearchWidgetContent.xaml`，绑定源
`SearchHistoryEntry.Query/DeleteLabel` 都是 `required init`，刷新时会重建条目，存活期间不会
变更；因此将对应 `OneWay x:Bind` 精确改为 `OneTime` 即可，不需要给 DTO 增加伪通知能力。
4E-0 目标是 WMC1506 6→0、WMC1510 仍为 1265。其后 4E-1 应先选告警数 1 到 8 的叶子控件
建立 pilot，不从 `SettingsWindow.xaml` 的 321 条开始大批量改写。AOT 主程序启动仍留到阶段 5。

### 4E-0 完成复盘与 4E-1 调整

4E-0 已按冻结范围完成。`SearchHistoryEntry.Query/DeleteLabel` 继续使用 `required init`；
`UpdateHistoryList` 在历史变化、语言变化、删除和清空后清空并重建条目，因此 4 条 Query 与 2 条
DeleteLabel 绑定可安全使用 `OneTime`，无需给不可变 DTO 增加伪通知。没有触碰搜索服务、排序、
Rust ABI、父级页面或其他 XAML Binding。

6 条 4E-0 契约在旧实现上 4 失败/2 通过，实施后 6/6；与 4D-5 联合契约 12/12、规范 x64
全量测试 2073/2073 通过。配置 23 / schema 20 的隔离审计产生 39 个发布文件和 3 个 PDB，
源码指纹稳定；WMC1506 6→0、WMC1510 保持 1265，旧 OneWay、缺失 OneTime/生命周期模式、
目标告警、非预期告警和完整 `always-throw` 均为 0。Rust 模块保持 ABI 2、能力 255、九个必需
导出及 staging/publish 哈希一致。AOT 主程序没有启动。完整记录见
`aot-stage-4e-0-report.md`。

本次还加固了审计脚本的工作树快照：Git 统一使用 `core.quotepath=false`，使并发出现的中文未跟踪
路径可以按真实路径哈希。触发问题的 `备份.zip` 保持原样，未被移动、删除或发布；该修复只影响
审计基础设施，不改变产品运行行为。

剩余 WMC1510 仍为 1265，分布于 25 个 XAML 文件。下一批调整为 **4E-1 `PinStateIcon`
compiled-binding pilot**：该叶子控件只有 2 条 `Foreground` 自引用 Binding，`IsPinned` 已由依赖
属性与显式可见性更新处理，调用面只在 Quick Capture。4E-1 先冻结填充/轮廓状态和 Foreground
动态更新，再将这 2 条绑定改成窄范围 typed `x:Bind`；预期 WMC1510 1265→1263。若运行时
Foreground 更新不能维持，则只在控件内部使用明确的依赖属性同步，不扩大到父级 Quick Capture
绑定。人工确认范围是 pin 填充/轮廓、前景色和主题切换；AOT 主程序启动仍留到阶段 5。

### 4E-1 完成复盘与 4E-2 调整

4E-1 经逐条生命周期核对后，从原计划的 `PinStateIcon` 2 条扩大为四个低风险叶子 XAML 共
7 条：Pin 前景色 2 条、Markdown 编辑器自身属性 3 条，以及两个命名 TextBlock 的路径 Tooltip。
四组都使用 OneWay typed `x:Bind`；没有修改 code-behind、ViewModel、依赖属性定义或业务逻辑。
`App.xaml` 的两个 Style Setter 和 `ContentWidgetWindow` 的运行时 DataContext 绑定由审计门禁
明确延期，没有因告警数量少而混入本批。

8 条新契约在旧实现上 6 失败/2 通过，实施后 8/8；第一次全量测试发现既有 Pin 图标契约仍
断言旧 Binding 文本，更新该契约后受影响定向 11/11、规范 x64 全量 2081/2081 通过。配置 24 /
schema 21 的隔离审计用时约 331.0 秒，产生 39 个发布文件和 3 个 PDB，源码指纹稳定；WMC1506
为 0、WMC1510 1265→1258，目标 XAML、旧绑定、缺失 compiled binding/生命周期、延期范围、
非预期告警和完整 `always-throw` 均为 0。生成 C# 还确认 Foreground、Markdown 三个属性及两个
TextBlock.Text 都注册真实 DependencyProperty 变化回调。Rust 模块保持 ABI 2、能力 255、九个
必需导出及 staging/publish 哈希一致。完整记录见 `aot-stage-4e-1-report.md`。

下一批调整为 **4E-2 两个自有 DependencyProperty 叶子控件**：`MusicTransportIcon` 的 7 条
Foreground 自引用复杂度低，可直接复用 4E-1 模式；`WidgetInlineEditor` 有 8 条自身属性绑定，
其中 7 条 OneWay、1 条 Text TwoWay，复杂度低到中。若同批全部完成，WMC1510 目标为
1258→1243；必须额外冻结 Text 的即时回写、保存和取消语义。若 TwoWay x:Bind 不能保持现有
`UpdateSourceTrigger=PropertyChanged` 行为，则先交付音乐图标 7 条并拆分编辑器，不用手工事件
掩盖语义差异。AOT 主程序启动仍留到阶段 5。

### 4E-2 完成复盘与 4E-3 调整

4E-2 已按完整 15 条范围完成，不需要拆分编辑器。`MusicTransportIcon` 的 7 条 Foreground
统一使用 OneWay typed `x:Bind`；`WidgetInlineEditor` 的 7 条呈现属性使用 OneWay，Text 保留
TwoWay 和 `UpdateSourceTrigger=PropertyChanged`。生成代码确认编辑器七个源 DependencyProperty
均有变化回调，TextBox 还对 `TextProperty` 注册目标到源回调并即时写回控件 Text；音乐图标的
Foreground 回调一次同步全部 7 个 Shape。没有修改 code-behind、调用方或业务逻辑。

9 条新契约在旧实现上 6 失败/3 通过，实施后 9/9；第一次全量测试唯一失败是既有音乐布局测试
仍断言旧 Binding，更新契约后 AOT/4D/4E 扩大定向 102/102、规范 x64 全量 2090/2090 通过。
配置 25 / schema 22 的最终隔离审计用时 369,631 毫秒，产生 39 个发布文件和 3 个 PDB，源码指纹
稳定；WMC1506 为 0、WMC1510 1258→1243，目标 XAML、旧绑定、缺失 compiled binding/行为、
延期范围、非预期告警和完整 `always-throw` 均为 0。Rust 模块保持 ABI 2、能力 255、九个必需
导出及 staging/publish 哈希一致。完整记录见 `aot-stage-4e-2-report.md`。

剩余 1243 条 WMC1510 分布在 19 个 XAML 文件。逐条核对低计数文件后，下一批调整为
**4E-3 typed DataTemplate 小批次**，合并两个不需要业务 code-behind 改动的目标：

- `AttachmentTileStrip.xaml` 7 条，DataTemplate 类型固定为 `TodoAttachmentViewModel`；缩略图和
  两个 Visibility 由现有 `ObservableObject` 通知，DisplayName/Glyph 在条目存活期不变；
- `SearchPopupWindow.xaml` 7 条，分别属于 `SearchTabItem`、`SearchResultItem` 和
  `SearchRecommendationItem`。Tab Count 保留 OneWay 通知；应用 Icon 继续由现有
  `ElementPrepared`/`RefreshRecommendedAppIcons` 显式处理懒加载。

4E-3 目标为 WMC1510 1243→1229。`FileWidgetSettingsSection` 的 5 条需要父窗口运行时继承
DataContext 和两个 TwoWay attached-property；`SearchResultRowControl` 的 8 条依赖运行时
DataContext、回收行与手工图标/元数据刷新；二者继续延期。`App.xaml` Style Setter 和
`ContentWidgetWindow` 运行时 DataContext 三条也继续冻结。AOT 主程序启动仍留到阶段 5。

### 4E-3 完成复盘与 4E-4 调整

4E-3 已按 14 条冻结范围完成。`AttachmentTileStrip` 为 `TodoAttachmentViewModel` 增加明确
`x:DataType`，7 条都使用 OneWay；生成代码订阅 `INotifyPropertyChanged`，并分别刷新 Thumbnail、
两个 Visibility、DisplayName 和 Glyph。Loaded/DataContextChanged 的异步缩略图加载、打开/移除
事件与删除按钮悬停/焦点逻辑均未改动。

`SearchPopupWindow` 的 Tab、推荐应用、收藏和最近搜索四个模板分别声明 `SearchTabItem`、
`SearchResultItem` 与 `SearchRecommendationItem` 类型。只有 `Count` 使用 OneWay 并生成
`PropertyChanged` 监听；Glyph、DisplayName、Icon、AppDisplayName 和两条 Title 共 6 条使用
OneTime。非 observable 的推荐应用 Icon 仍由 `ElementPrepared` 与
`RefreshRecommendedAppIcons` 两处显式回填，没有给 DTO 增加伪通知。

11 条新契约在旧实现上 6 失败/5 通过，实施后 11/11；AOT/4D/4E 扩大定向 113/113、规范 x64
全量 2101/2101 通过。配置 26 / schema 23 的最终隔离审计用时 242,439 毫秒，产生 39 个发布
文件和 3 个 PDB，源码指纹稳定；WMC1506 为 0、WMC1510 1243→1229，目标源、旧绑定、缺失
compiled binding/类型/行为、延期范围、非预期告警和完整 `always-throw` 均为 0。Rust 模块保持
ABI 2、能力 255、九个必需导出及 staging/publish 哈希一致。完整记录见
`aot-stage-4e-3-report.md`。

剩余 1229 条 WMC1510 分布在 17 个 XAML 文件。下一批调整为 **4E-4
`FileWidgetSettingsSection` typed ViewModel 桥接**，只处理该文件 5 条，目标为 1229→1224：

- `SettingsWindow` 当前在 `InitializeComponent()` 后才给 `SettingsRoot.DataContext` 赋值，子控件
  依赖运行时继承，不能直接把表达式替换为 `x:Bind`；
- 子控件应增加类型为 `SettingsViewModel` 的 `ViewModel` DependencyProperty，由父窗口在初始化后
  显式赋值，并在 dispose 前清空；
- 摘要与两组选项使用 OneWay，两条 `SettingsComboBox.Value` 保持 TwoWay；
- 生成代码必须验证 `ViewModelProperty`、嵌套 `PropertyChanged` 和 attached `ValueProperty`
  的目标到源回调；语言切换、选项重建、摘要、低优先级选中同步与设置持久化必须冻结。

4E-4 复杂度为中等，不与 `SearchResultRowControl` 合并。后者的 8 条还涉及 ItemsRepeater 回收、
同一对象重绑、延迟 Icon/Size/Date 元数据和 `RefreshIconVisuals` 手工覆盖，应作为独立 4E-5。
`App.xaml` Style Setter 与 `ContentWidgetWindow` 的三条运行时绑定继续延期。AOT 主程序启动仍
留到阶段 5。

### 4E-4 完成复盘与 4E-5 调整

4E-4 已按 5 条冻结范围完成。`FileWidgetSettingsSection` 增加可空 `SettingsViewModel`
DependencyProperty，`SettingsWindow` 在根 DataContext 建立后显式赋值，并在 ViewModel dispose 前
清空。摘要和两组选项使用 OneWay；两个 `SettingsComboBox.Value` 保持 TwoWay，原有选择归一化、
SaveDebounced、语言切换选项重建和低优先级选择同步没有改动。

生成代码确认 `ViewModelProperty` 自带注册/注销回调，ViewModel 切换时会移除旧
`PropertyChanged` 并订阅新实例；5 个叶子各有独立更新分支，两个 attached `ValueProperty` 各有
目标到源回调。由于生成器已经负责根 DP 跟踪，实施中删除了重复的手动 `Bindings.Update()`。

11 条新契约在旧实现上 8 失败/3 通过，实施后 11/11；AOT/4D/4E 扩大定向 124/124、规范 x64
全量 2112/2112 通过。配置 27 / schema 24 的最终隔离审计用时 209,155 毫秒，产生 39 个发布
文件和 3 个 PDB，源码指纹稳定；WMC1506 为 0、WMC1510 1229→1224，目标旧绑定、缺失 compiled
binding/桥接/行为、生命周期顺序、冗余手动刷新、延期范围、目标源告警、非预期告警和完整
`always-throw` 均为 0。Rust 模块保持 ABI 2、能力 255、九个必需导出及 staging/publish 哈希一致。
完整记录见 `aot-stage-4e-4-report.md`。

剩余 1224 条 WMC1510 分布在 16 个 XAML 文件。下一批保持为 **4E-5
`SearchResultRowControl` typed Item 桥接**，只处理该控件 8 条，目标为 1224→1216：

- 父级 ResultsRepeater 模板声明 `SearchResultItem` 类型，并显式把当前条目传入行控件；
- 行控件用 typed Item DependencyProperty 驱动内部 compiled binding；先以真实 XAML 构建验证
  public typed Item 是否触发 required-member activator，再决定是否需要 object 传输加类型投影；
- Title、Subtitle、DisplayGlyph 和 TypeDisplay 随 Item 切换更新；
- 非 observable 的 Icon、SizeDisplay 和 DateDisplay 继续由每次 `ElementPrepared`、异步完成后的
  引用一致性检查和 `RefreshIconVisuals` 显式刷新，不给 DTO 增加伪通知；
- 冻结回收行同对象重新 prepare、单选/多选残留清除、文件列显隐和关闭解绑。

4E-5 复杂度为中高，不能和其他页面合并。`App.xaml` Style Setter 与 `ContentWidgetWindow` 的三条
运行时绑定继续冻结。4E-5 完成后暂停微型 Binding 清理，进入阶段 5，首次启动隔离 AOT 主程序并
执行托盘、设置、搜索、文件操作和四个 Rust 边界的真实功能矩阵。

### 4E-5 完成复盘与阶段 5A 调整

真实 XAML 编译确认，public typed Item DependencyProperty 会让类型信息生成器尝试无参数构造
带 required Kind/Title 的 `SearchResultItem`，产生 CS9035；pathless OneWay 传输还会为非
observable 条目增加 WMC1506。最终没有修改 DTO，而是保留 internal typed Item 投影，在每次
`ElementPrepared` 中设置 Item 并调用 `Bindings.Update()`。8 条叶子全部使用 OneTime compiled
`x:Bind`；Icon、Size、Date 的显式刷新、异步引用一致性和 DataContext 查找链继续保留。

12 条新契约在旧实现上 5 失败/7 通过，实施后 12/12；4E-3 至 4E-5 组合 34/34、AOT/Rust 扩大
定向 198/198、规范 x64 全量 2124/2124 通过。配置 28 / schema 25 的最终隔离审计用时
216,213 毫秒，产生 39 个发布文件和 3 个 PDB，源码指纹稳定；WMC1506 为 0、WMC1510
1224→1216，目标旧绑定、缺失 compiled binding/桥接/行为、public Item 暴露、回收与生命周期
顺序、模型约束、延期范围、目标源告警、非预期告警和完整 `always-throw` 均为 0。Rust 模块保持
ABI 2、能力 255、九个必需导出及 staging/publish 哈希一致。完整记录见
`aot-stage-4e-5-report.md`。

剩余 1216 条 WMC1510 分布在 15 个 XAML 文件。微型 Binding 清理继续暂停；阶段 5A 已按这里冻结
的隔离边界完成，后续按 5B 至 5D 分批开放 Rust 边界、托管功能矩阵、安装升级与回滚验证。

### 5A 完成复盘与阶段 5B-1 调整

5A 新增 `DESKBOX_AOT_PREVIEW_DATA_ROOT`，仅在 `DESKBOX_NATIVE_AOT` 编译中读取；普通 Release
JIT 不接受该覆盖，未显式设置时产品默认数据语义也不变。`start-aot-preview.ps1` 固定接受 profile
29 / schema 26 的稳定摘要，核对 EXE/Rust SHA-256，拒绝正式数据根和任意父子重叠路径，只停止
可执行文件完整路径等于受审计 EXE 的进程，并在调用结束后恢复环境变量。正式目录元数据记录改用
`StringComparer.Ordinal` 排序，消除了 PowerShell 5.1 与 7 的文化排序差异。

9 条新契约在旧实现上 9/9 失败，实施后 9/9；5A 与数据路径组合 20/20、规范 x64 全量
2133/2133 通过。配置 29 / schema 26 的最终审计用时 222,175 毫秒，产生 39 个发布文件、
3 个 PDB，源码指纹稳定；WMC1506=0、WMC1510=1216，5A 缺失/不安全模式、目标告警、非预期
告警和完整 `always-throw` 均为 0。Rust 保持 ABI 2、能力 255、九个必需导出和同次哈希一致。

受审计 AOT 产物已在独立根实际完成首次启动、单实例、重启和托盘菜单正常退出；日志确认
`OnLaunched completed successfully`、托盘、原生通知、OLE 拖放目标和默认 Widget 建立。正式
`%LOCALAPPDATA%\DeskBox` 运行前后均为 122 个文件、303,016,768 bytes，确定性元数据指纹均为
`254EB84254707C6A61158F5038C59ED65A9B120157C320C0EBEA1FF0430EF7F0`。完整证据见
`aot-stage-5a-report.md`。

5A 完成时的下一批调整为 **5B-1 shortcut AOT 到 Rust 真实边界冒烟**。该批只使用隔离临时 `.lnk`，覆盖
创建、读取、覆盖、无 UI Resolve、损坏、取消、修复和删除，并核对 AOT 进程实际加载的 Rust DLL
路径与哈希。Quick Access pin/unpin、音乐 setter、完整托管功能矩阵和安装状态变更不与本批合并。
5B-1 复杂度为中等；其通过后再依次进入 Explorer/Quick Access、音乐、托管 UI 功能矩阵和安装
升级/回滚门槛。Rust `SearchCore` 继续保持独立，不作为主程序 AOT 验收的前置条件。

### 5B-1 完成复盘与阶段 5B-2 调整

5B-1 新增仅在 `DESKBOX_NATIVE_AOT` 中编译的显式 shortcut smoke 入口，并在 `OnLaunched`
成功完成后调度。runner 只接受显式 AOT preview 根和五个固定场景，复用产品的
`DragDropPermissionService`、`ShortcutHelper`、真实托盘 HWND 与 Rust loader；脚本再核对 EXE/Rust
路径、SHA-256、动态代码状态、ABI/能力、非零模块句柄、场景结果和正式数据指纹。

真实 AOT 矩阵完成 Core、有效目标、取消、同卷移动自动修复和删除。复盘期间修正三项遗漏：

1. 修复夹具必须把原目标在同卷移动，保留文件身份供 Windows 分布式链接跟踪更新，不能创建无关替代文件；
2. runner 的精确进程清理必须位于外层 `finally`，确保成功、失败和超时都不遗留 AOT 进程；
3. evidence JSON 是第 50 处生产源码序列化调用，JSON 清单必须登记为第 17 个文件和第 15 个 context 所有者。

8 条新契约在旧实现上 8/8 失败，实施后 8/8；JSON/5B-1 组合 20/20、Rust 52/52、规范 x64
全量 2141/2141 通过。profile 30 / schema 27 的最终审计用时 229,238 毫秒，产生 39 个发布
文件和 3 个 PDB；WMC1506=0、WMC1510=1216，5B-1 缺失/不安全模式、目标告警、非预期告警和
完整 `always-throw` 均为 0。Rust 保持 ABI 2、能力 255、九个必需导出和同次哈希一致。

下一批调整为 **5B-2A Explorer 启动与 Quick Access 只读查询**。这两项均可复用现有隔离、哈希、
结构化证据和精确清理框架，复杂度为低至中等，可以在一个批次完成。会改变用户 Shell 状态的
Quick Access 临时目录 pin/unpin 单独留到 **5B-2B**，复杂度为中等，并要求失败补偿与最终 unpin。
音乐 getter/setter、托管 UI 功能矩阵、安装升级和 CRT 决策继续后置；Rust `SearchCore` 仍不进入
当前 AOT 发布关键依赖链。完整证据见 `aot-stage-5b-1-report.md`。

### 5B-2A 完成复盘与阶段 5B-2B 调整

5B-2A 新增仅在 `DESKBOX_NATIVE_AOT` 中编译的组合 smoke。Explorer 部分通过实际产品服务启动
一次性 `.cmd`，只有外部标记文件内容正确才算通过；Quick Access 部分依次执行公共异步查询、
原生 `QueryPinState` 明细查询和公共复查，三次均要求 `NotPinned`。runner 只接受显式 preview 根，
脚本还会隔离 shortcut/shell 两个 smoke 环境变量、核对同次审计哈希并在外层 `finally` 精确清理进程。

真实 AOT 场景中，Explorer status/HRESULT 为 0、attempted phases 为 `0x7F`，标记内容为
`explorer-shell-launch`；Quick Access status/HRESULT 为 0、attempted phases 为 `0x3F`，公共查询前后
和原生查询均为 `NotPinned`。正式目录前后保持 122 个文件、303,204,886 bytes 和相同指纹。

8 条新契约在旧实现上 8/8 失败，实施后 8/8；JSON/5B-2A 组合 20/20、Rust 52/52、规范 x64
全量 2149/2149 通过。profile 31 / schema 28 的最终审计用时 217,655 毫秒，产生 39 个发布文件
和 3 个 PDB；WMC1506=0、WMC1510=1216，缺失入口/脚本、Quick Access 非只读模式、目标告警、
非预期告警和完整 `always-throw` 均为 0。Rust 保持 ABI 2、能力 255、九个必需导出和同次哈希一致。

下一批保持为 **5B-2B Quick Access 临时目录 pin/unpin**，复杂度为中等。必须使用唯一 preview
目录，验证 `NotPinned → Pinned → NotPinned`，并同时提供 App 内 `finally` 补偿与失败/超时后的
独立补偿检查。最终状态未恢复时，脚本必须失败并给出人工清理路径，不能只停止 AOT 进程。
音乐 getter/setter 在 5B-2B 后单独进入；托管 UI 功能矩阵、安装升级和 CRT 决策继续后置。
完整证据见 `aot-stage-5b-2a-report.md`。

### 5B-2B 完成复盘与阶段 5B-3 调整

5B-2B 使用隔离 preview 根下的稳定 `mutation-target`，不删除目标本身，因此被强制结束的进程
仍能由后续进程定位同一路径。产品 pin/unpin 全部调用 `ExplorerQuickAccessHelper` 公共异步入口；
直接原生调用只用于状态复查和 HRESULT/阶段证据。App 外层 `finally` 无条件执行补偿性 unpin，
脚本还在任何主流程错误后无条件启动 postflight 补偿。

实际六阶段结果为：preflight `NotPinned`；固定后主动异常由 App `finally` 恢复 `NotPinned`；
固定后进程被精确强制结束，证据停在 `Pinned`；新恢复进程初始确认为 `Pinned`，随后恢复
`NotPinned`；正常主流程完成 `NotPinned → Pinned → NotPinned`；postflight 最后再次确认公共与
原生状态均为 `NotPinned`。六个 AOT PID 均已退出，正式数据指纹前后相同。

11 条新契约最终 11/11、Rust 52/52、规范 x64 全量 2160/2160。profile 32 / schema 29 的最终
审计用时 200,952 毫秒，产生 39 个发布文件和 3 个 PDB；WMC1506=0、WMC1510=1216，5B-2B
缺失入口/脚本、不安全清理、目标告警、非预期告警和完整 `always-throw` 均为 0。Rust 保持
ABI 2、能力 255、九个必需导出和同次哈希一致。JSON 固定清单为 19 个文件、52/52 处
source-generated 调用和 17 个 context 所有者。

下一批调整为 **5B-3A 音乐音量只读 getter**，复杂度低到中。它只通过产品服务读取系统主音量
和 session snapshot，并核对原生阶段、匹配类型、数值范围、同次哈希及生产数据隔离，不改变
系统音量。原计划中的 getter/setter 因状态恢复风险拆分：5B-3B 才验证系统 setter，并要求变更前
持久化原值、App 内恢复和独立进程恢复；5B-3C 再使用可控媒体会话验证 session setter。托管 UI
功能矩阵、安装升级、CRT 决策和 Rust `SearchCore` 继续后置。完整证据见
`aot-stage-5b-2b-report.md`。

### 5B-3A 完成复盘与阶段 5B-3B 调整

5B-3A 使用固定只读探测身份，不依赖媒体控制服务，也不尝试制造或操纵第三方播放器 session。
runner 先直接读取系统音量，再依次调用两个产品 getter、直接原生 snapshot 和最终系统 getter；
原生成功、endpoint/system/session HRESULT、阶段掩码、匹配类型、数值范围和前后容差都是硬门禁。
产品回退值 `0` 本身不能通过门禁，因此没有默认音频 endpoint 时会明确失败为环境阻断。

实际 AOT 运行中系统音量五次读取均为 `0.370000004768372`，原生 system getter 阶段为 `0x0F`，
snapshot 阶段为 `0x1F`，operation/device/system HRESULT 为 0。`CoInitializeEx` 返回
`RPC_E_CHANGED_MODE (0x80010106)`，Rust 按既有 apartment 复用契约继续；session 枚举返回
`S_FALSE`、match kind 为 0，产品和原生均报告 `HasSessionVolume=false`。这证明无匹配 session
路径正确，不等于匹配 session getter 或 session setter 已验证。

10 条新契约分两批建立：主实现前 0/9 失败，复盘补充跨 runner 隔离前 0/1 失败，最终 10/10；
AOT/JSON 组合 194/194、Rust 52/52、规范 x64 全量 2170/2170 通过。profile 33 / schema 30
的审计保持 39 个发布文件和 3 个 PDB；WMC1506=0、
WMC1510=1216，5B-3A 缺失入口/产品/脚本、不安全 setter/目录、目标告警、非预期告警和完整
`always-throw` 均为 0。Rust 保持 ABI 2、能力 255、九个必需导出和同次哈希一致。JSON 固定清单
为 20 个文件、53/53 处 source-generated 调用和 18 个 context 所有者，正式数据指纹前后相同。

下一批建议为 **5B-3B 系统主音量 setter 与故障恢复**，复杂度中高，仍不合并 session setter。
它应在任何写入前把原系统音量持久化到隔离证据，使用产品 setter 做一个小幅、非静音变更，并以
直接原生 getter 复查；App `finally` 必须恢复原值，脚本还要覆盖写入后强制终止、独立新进程恢复
和最终 postflight。只有最终值在容差内恢复、正式数据不变且无进程遗留才可完成。5B-3C 再使用
可控媒体 session 同时补齐匹配 session getter 与 session setter；托管 UI 功能矩阵、安装升级、
CRT 决策和 Rust `SearchCore` 继续后置。完整证据见 `aot-stage-5b-3a-report.md`。

### 5B-3B 完成复盘与阶段 5B-3C 调整

5B-3B 没有改写产品 `MusicVolumeService` 或 Rust Core Audio 实现，而是验证既有系统 setter 在
Native AOT 中的真实产品调用链。runner 在稳定 preview 根中保存 `recovery-intent.json`，并在
任何写入前用独立 source-generated context 原子写入、立即回读和校验。写入只调用产品
`TrySetSystemMasterVolumeAsync`；直接 `MusicVolumeNativeBackend.GetSystemVolume` 只提供 endpoint、
HRESULT、阶段掩码和数值复查。恢复只有在 getter 确认回到原值后才删除意图，损坏或恢复失败的
意图会保留并供人工读取原值。

最终六阶段实际 AOT 结果为：preflight 读取原值 `0.370000004768372`；主动异常场景临时改为
`0.420000004768372` 后由 App `finally` 恢复；强制终止 PID `28468` 时意图仍在且值停留在探测值；
独立恢复 PID `32144` 先读取到 `0.420000016689301`，再通过产品 setter 恢复；正常场景再次完成
原值 → 探测值 → 原值；postflight 最后确认无意图且仍为原值。六个 AOT PID 均已退出，正式数据
指纹前后均为 `DF25E91EF8BB726C3BDFA46B9C0CF08EEBA93F186A4B26E4045AE58321F3F976`。

10 条新契约在实现前 0/10 失败；主体与隔离实现后 9/10，通过 profile/schema 更新后 10/10。
规范 x64 全量 2180/2180、Rust 52/52、fmt 和 Clippy `-D warnings` 全部通过。profile 34 /
schema 31 的最终审计用时 200,166 毫秒，产生 39 个发布文件和 3 个 PDB；WMC1506=0、
WMC1510=1216，新阶段缺失 runner/launch/product/script、不安全直接 setter/session、恢复顺序、
目标告警、非预期告警和完整 `always-throw` 均通过。JSON 固定清单更新为 21 个文件、55/55 处
source-generated 调用和 19 个 context 所有者。

下一批调整为 **5B-3C：可控媒体 session 的匹配 getter 与 session setter**，复杂度中高。必须由
测试控制的音频 session 提供稳定身份和原始 session 音量，禁止修改任意第三方播放器；继续采用
持久原值、应用内 `finally`、强制终止后独立恢复、postflight 和同次哈希门禁。系统主音量 setter
已经完成，不在 5B-3C 重复。完成 5B-3C 后再进入托管 UI 功能矩阵；安装/覆盖升级/回滚、CRT
决策、ARM64 和 Rust `SearchCore` 继续后置。完整证据见 `aot-stage-5b-3b-report.md`。

### 5B-3C 完成复盘与阶段 5B-4 调整

5B-3C 新增的是测试基础设施和 NativeAOT-only runner，没有重写 4C 已冻结的产品 Rust Core Audio
实现。测试专用 `deskbox-audio-session-fixture` 生成并循环播放全零 PCM，以固定进程/display name
建立真实 Core Audio session；它要求绝对路径与父 PID，父进程退出或 stop marker 出现时停止，且
不会复制到产品 publish。runner 还要求精确夹具 PID、唯一同名进程、preview 根和固定 match kind 4，
因此不会把用户第三方播放器作为测试目标。

产品 `GetVolumeAsync` 和直接 Rust snapshot 都确认同一匹配 session 后，runner 先原子持久化并
source-generated 回读原值、探测值、夹具身份/PID 与初始系统音量，才允许调用产品
`TrySetSessionVolumeAsync`。正常路径完成 `1.0 → 0.92 → 1.0`；主动异常由 App `finally` 恢复；
强制终止后，独立新 AOT 进程先观察到约 `0.9200000167` 再恢复。恢复时若 session 已消失，意图
保持并报告 `session-disappeared-intent-preserved`，不会把不存在误判为成功。最终 postflight 也
使用 `RecoverOriginal`，并增加整个矩阵的系统主音量前后比较。

12 条新契约在实现前 0/12 失败；5B-3C 与 JSON 清单组合 13/13，规范 x64 全量 2192/2192，Rust
workspace 54/54，fmt、Clippy `-D warnings` 和全部 PowerShell 脚本解析均通过。profile 35 /
schema 32 保持 39 个发布文件、WMC1506=0、WMC1510=1216、完整 `always-throw=0`、生产 Rust
ABI 2/能力 255/九个导出和同次哈希一致。JSON 固定清单为 22 个文件、57/57 处 source-generated
调用和 20 个 context 所有者。结构化证据、恢复顺序、session 消失策略和实施复盘见
`aot-stage-5b-3c-report.md`。

下一批调整为 **5B-4：x64 Native AOT 托管 UI 功能矩阵**，总体复杂度高，但不应一次覆盖全部
交互。建议拆为 5B-4A 基础窗口/设置/搜索/资源与 Widget 恢复，5B-4B Widget 和 Quick Capture、
Todo、Glance、天气的持久化变更及重启恢复，5B-4C 文件拖放/复制移动/回收站/上下文菜单、快捷键、
FolderPicker、Shell 与媒体等 OS 交互。每批先在隔离 preview 根做真实 AOT 核对，只对实际发现的
问题做窄修复；自动化和用户人工 UI 证据分别记录。安装/覆盖升级/回滚、CRT、ARM64/Store 和 Rust
`SearchCore` 继续后置。

### 5B-4A 完成复盘与阶段 5B-4B 调整

5B-4A 新增 NativeAOT-only 的 `BasicReadOnly` runner 和外层隔离脚本。脚本只接受
`.artifacts/aot-managed-ui-smoke/win-x64` 下带所有权标记的 preview 根，预置固定 ID 的 File/Search
两个 Widget，并在运行前后比较正式 `%LOCALAPPDATA%\DeskBox` 指纹。runner 验证动态代码关闭、
托盘 HWND、两个已恢复且可见的 Widget、12 套已发布语言资源、设置六个主分区，以及搜索中的六种
筛选和名称/大小/日期/类型各两次排序；结果只写入隔离根，并显式使用独立 source-generated context。

第一次真实 AOT 运行在构造设置窗口时发现一个实际兼容问题：空设置搜索把私有 managed record 数组
投影到 WinRT `ItemsSource`，NativeAOT 下由 `IItemsControlMethods.set_ItemsSource` 返回
`E_INVALIDARG`。修复保持普通行为不变：空查询直接把 `ItemsSource` 设为 `null` 并关闭建议列表，
不再创建空私有数组。第二次使用重新发布的受审计产物完整通过，证明这次改动是由真实 AOT 证据
驱动的窄修复，而非预先重写设置或搜索体系。

12 条新契约在实现前 0/12 失败，实施后与 JSON 固定清单组合 13/13；规范 x64 全量
2204/2204、Rust workspace 54/54、fmt、Clippy `-D warnings`、全部 PowerShell 脚本解析和
`git diff --check` 均通过。profile 36 / schema 33 的最终审计用时 183,750 毫秒，保持 39 个发布
文件和 3 个 PDB；WMC1506=0、WMC1510=1216、完整 `always-throw=0`、阶段缺失/不安全模式、目标
警告和非预期警告均为 0。Rust 保持 ABI 2、能力 255、九个导出和同次哈希一致；JSON 固定清单更新为
23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。

真实矩阵记录 2 个已恢复表面、6 个设置主分区、12 套语言、6 次筛选切换和 8 次排序切换；搜索最终
恢复 `All/Relevance/ascending`，AOT PID 正常退出，正式数据指纹前后均为
`AEBFD3FBE8A037F9BEBCF01EA6CD1987C04EBB81D035400E14E7DF697C4C80DB`。结构化证据与完整复盘见
`aot-stage-5b-4a-report.md`。这属于真实 AOT 自动化证据，不替代用户对视觉、焦点和输入手感的人工验收。

本轮当时只开放 **5B-4B1 设置搜索、嵌套设置页与剩余 managed collection 投影的只读/隔离核对**，
不与状态写入和恢复诊断合并。该批现已完成，结果见下节。

### 5B-4B1 完成复盘与阶段 5B-4B2 调整

5B-4B1 在既有 managed UI runner 中增加 `DeepSettingsReadOnly`。它使用非空本地化设置搜索，
通过产品激活路径进入 `BackupRestoreSettings`，随后按固定顺序打开 24 个此前未覆盖的设置路由；
每页同时核对当前 section、主导航选中项、可见内容、窗口尺寸、XamlRoot 和 breadcrumb。嵌套
`CapsuleBehaviorSettings` 还通过真实 breadcrumb 返回 `CapsuleMode`。预置的 1 条文件叠放规则和
非空备份快照清单都必须产生具有 XamlRoot 和正高度的真实列表容器。

第一次完整实现审计发现设置页的运行时 Binding 不能只靠零散类型修补。现为
`SettingsViewModel` 增加仅 NativeAOT 编译的精确生成绑定清单，并由契约动态解析设置窗口和六个
设置子区的 XAML，再与 public property 反射结果比较；最终为 282 个需要项、282 个生成项、0 缺失、
0 多余。命令不进入该清单，可安全类型化的四条命令使用 `x:Bind`，WMC1510 因此由 1216 降至 1211。

真实 AOT 运行继续暴露三类投影问题：`SettingsOption.DisplayName` 缺少生成绑定元数据；文件叠放
模式与文件夹打开行为的 `IReadOnlyList<SettingsOption>` compiled ItemsSource 返回 `E_INVALIDARG`；
天气城市建议的 typed `ObservableCollection` 仍不能在该路径可靠投影。最终保留 typed collection
供业务逻辑使用，只在 UI 边界增加 `object[]` 投影，并为实际 DataTemplate 条目补齐窄范围生成
元数据。设置搜索、breadcrumb 与备份清单也在进入 WinRT `ItemsSource` 前转换为 object vector。

12 条 5B-4B1 契约、196 条全部 AOT 阶段契约、x64 全量 2216/2216、Rust workspace 54/54、fmt、
Clippy `-D warnings` 和 PowerShell 脚本解析均通过。profile 37 / schema 34 保持 39 个发布文件、
WMC1506=0、WMC1510=1211、完整 `always-throw=0`、生产 Rust ABI 2/能力 255/九个导出和同次哈希
一致；JSON 固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。
外层 runner 在 AOT 进程停止后检查完整日志，未发现未处理异常或备份清单失败，正式数据指纹不变。
完整结构与复盘见 `aot-stage-5b-4b1-report.md`。

原 5B-4B2 范围在复盘后继续拆成三个顺序门：

1. **5B-4B2A 设置与 Widget 拓扑持久化/重启恢复**：已完成。使用 owned preview 根，通过
   真实设置/ViewModel 路径修改一组可逆的 bool、enum、数值和字符串设置，以及固定 Widget 的
   锁定、可见性、位置/尺寸或标题；要求写前快照、显式 flush、正常退出、新 AOT 进程重载、逐字段
   结构化核对和最终 owned 根清理。
2. **5B-4B2B 独立内容 store**：2B1 Quick Capture 与 2B2A Todo 核心任务/备注已完成；2B2B
   经 2B2A 复盘再拆为 2B2B1 步骤与 2B2B2 托管附件。各批固定自己的范围，不在同一轮实施。
3. **5B-4B2C Glance 与天气（已完成当前离线边界）**：5B-4B2C1 已用 owned 本地图片验证 Glance
   per-widget 偏好、真实图片 surface、重载、恢复和 postflight；5B-4B2C2A 已验证 Weather 纯本地
   设置与 Widget 日/周视图元数据，5B-4B2C2B 已验证确定性非空天气真实 surface。真实定位与网络刷新
   留给后续 OS/网络矩阵。

5B-4B2A 已把所有后续组件共同依赖的 `SettingsService` save/debounce/flush、Widget 配置和跨进程
重载单独证明。5B-4C OS 交互、安装升级、CRT、ARM64/Store 与 Rust `SearchCore` 继续后置。

### 5B-4B2A 完成复盘与阶段 5B-4B2B 调整

5B-4B2A 在 managed UI runner 中增加 `SettingsWidgetPersistenceRestart`，由同一受审计 NativeAOT
产物依次启动 `Mutate`、`VerifyRestore`、`Postflight` 三个不同进程。第一进程经真实
`SettingsViewModel` 修改文件扩展名、文件名行数、文字大小和托盘图标样式，并经 Widget 产品路径
修改固定 File Widget 的标题、Icon/List 视图、位置锁、尺寸锁和实际 HWND 边界；第二进程逐字段
确认变更已经重载，再恢复原始基线；第三进程再次确认恢复结果。

每个阶段都要求 `FlushPendingSaveAsync` 成功并通过应用正常关闭路径退出。外层脚本比较
`Mutate.after == VerifyRestore.before`、`Mutate.before == VerifyRestore.after`、
`VerifyRestore.after == Postflight.before` 和 `Postflight.before == Postflight.after`，比较范围同时
包括 AppSettings、Settings ViewModel、两个 Widget 配置、File Widget ViewModel、已加载 host、
XamlRoot 与真实物理边界。Search Widget 作为未修改对照项进入相同字段门禁。

实测设置状态为 `false/2/11.5/Colorful → true/1/12.5/White → false/2/11.5/Colorful`，File Widget
实际边界为 `80,80,300,360 → 104,100,340,392 → 80,80,300,360`。三个 PID 均不同且 3/3 正常
退出，运行日志失败数为 0，最终没有受审计 preview 进程残留；结果、最终设置、日志和专属会话汇总
归档后才删除 owned preview 根，正式数据指纹保持不变。

12 条 5B-4B2A 契约、208 条全部 AOT 阶段契约、x64 全量 2228/2228、Rust workspace 54/54、fmt、
Clippy `-D warnings` 和 PowerShell 脚本解析均通过。profile 38 / schema 35 保持 39 个发布文件、
WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始 IL2026/IL2050/IL2072/IL2075/IL3050=0、
生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON 固定清单仍为 23 个文件、58/58 处
source-generated 调用和 21 个 context 所有者。`BasicReadOnly` 与 `DeepSettingsReadOnly` 也已用
profile 38 产物重新实际运行通过。完整结构与复盘见 `aot-stage-5b-4b2a-report.md`。

复盘代码后，原 5B-4B2B 先拆成两个顺序门：

1. **5B-4B2B1 Quick Capture 内容 store**：已完成。固定 Quick Capture Widget 和 owned
   文本/附件夹具，经真实 UI/ViewModel 路径验证最小记录、详情 600 ms 自动保存、显式 pending-save
   flush、受管附件、全新 AOT 进程重载、删除和文件清理。
2. **5B-4B2B2 Todo 内容 store**：2B2A 核心任务/备注已完成；2B2B 经代码复盘继续拆为 2B2B1
   步骤与 2B2B2 托管附件，避免一个矩阵同时承载集合投影与物理文件状态。

Quick Capture 的服务层还同时负责主记录、最近项、图片缓存、受管附件和软删除/恢复；Todo 则有
每 Widget 独立 store 及另一套任务、完成、步骤、复发与附件状态机。两者不是低复杂度的同类机械项，
合并会降低跨进程故障的可定位性。B2B1 没有扩展 Rust；该范围仍属于 C#/WinUI、JSON 内容模型和
文件生命周期的产品路径。

### 5B-4B2B1 完成复盘与阶段 5B-4B2B2 调整

5B-4B2B1 在 managed UI runner 中增加 `QuickCapturePersistenceRestart`，由同一受审计 NativeAOT
产物依次启动 `Mutate`、`VerifyDelete`、`Postflight` 三个不同进程。第一进程经真实 Quick Capture
surface 新建详情并执行 pending flush，再通过生产 `ScheduleDetailAutoSave` 和 600 ms debounce 修改
已有详情，最后经 ViewModel 导入托管文本附件；第二进程重载记录、详情和附件，执行另一轮显式
pending flush，再分别删除附件和记录；第三进程确认 store、surface 和附件目录保持为空。

runner 不直接调用 timer tick，而是等待 `_detailSavedRevision` 追上目标 revision，区分内存文本变化与
真实自动保存完成。外层脚本比较 `Mutate.after == VerifyDelete.before`、`VerifyDelete.after ==
Postflight.before` 和 `Postflight.before == Postflight.after`，同时核对 schema、正文、格式、来源、
附件元数据、物理文件、surface/XamlRoot、详情 dirty 状态和 pending attachment 数量。夹具与托管副本
SHA-256 一致；三个 PID 均不同且 3/3 正常退出，正式数据指纹不变，运行日志失败数和残留 AOT 进程数
均为 0，证据归档后才删除 owned preview 根。

首次真实 AOT 运行发现非空 `IReadOnlyList<TodoAttachmentViewModel>` 进入 Quick Capture 附件
`ItemsSource` 时返回 `E_INVALIDARG`。最终只在 UI 边界使用 `attachments.Cast<object>().ToArray()`；
typed 业务集合、JSON 模型和普通 JIT 默认路径均保持。14 条 5B-4B2B1 契约、222 条全部 AOT 阶段
契约、x64 全量 2242/2242、Rust workspace 54/54、fmt、Clippy `-D warnings` 和 PowerShell 脚本
解析均通过。profile 39 / schema 36 保持 39 个发布文件、WMC1506=0、WMC1510=1211、完整
`always-throw=0`、原始 IL2026/IL2050/IL2072/IL2075/IL3050=0、生产 Rust ABI 2/能力 255/九个
导出和同次哈希一致；JSON 固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context
所有者。完整结构与复盘见 `aot-stage-5b-4b2b1-report.md`。

Todo 实现复盘确认它还包含独立的 600 ms Markdown 备注 timer、`SemaphoreSlim` 保存门、selection-change
flush、完成/重复任务分支、步骤集合和托管附件物理生命周期。下一阶段因此拆为两个顺序门：

1. **5B-4B2B2A Todo 核心任务与备注持久化**：已完成。固定 Todo Widget，验证最小任务创建、
   标题修改、无 recurrence 的完成状态、备注 600 ms 自动保存与显式保存、新进程重载、任务删除和
   空 store postflight。
2. **5B-4B2B2B Todo 步骤与托管附件生命周期**：2B2A 完成复盘后再拆为 2B2B1 步骤与 2B2B2
   托管附件，避免把纯集合状态和物理文件清理合并为一个高复杂度失败面。

下一步只实施 5B-4B2B2B1，不扩展 Rust。Glance/天气继续留在 5B-4B2C；OS 交互、安装升级、CRT、
ARM64/Store 与 Rust `SearchCore` 继续后置。

### 5B-4B2B2A 完成复盘与阶段 5B-4B2B2B 调整

5B-4B2B2A 在 managed UI runner 中增加 `TodoPersistenceRestart`，由同一受审计 NativeAOT 产物
依次启动 `Mutate`、`VerifyDelete`、`Postflight` 三个不同进程。第一进程经真实 Todo detail surface
创建普通任务、修改标题、通过生产 600 ms timer 自动保存备注并设为已完成；第二进程重载上述状态，
显式保存另一份备注、恢复未完成并删除任务；第三进程确认 schema 3 store、列表和详情保持为空。

runner 不直接调用 timer tick，也不直接写 Todo store。备注场景写入 Markdown 编辑器公开 `Text` 属性，
经与真实 `EditorTextChanged` 共用的 `ScheduleNotesAutoSave` 安排 timer，并等待 timer 停止、save gate 释放、
编辑项 ID、原始文本、编辑器、Item 与 store 一致。外层脚本比较 `Mutate.after == VerifyDelete.before`、
`VerifyDelete.after == Postflight.before` 和 `Postflight.before == Postflight.after`，同时核对任务核心字段、
surface/XamlRoot、详情选择和保存状态。三个 PID 均不同且 3/3 正常退出，正式数据指纹不变，运行日志
失败数和残留 AOT 进程数均为 0，证据归档后才删除 owned preview 根。

首次真实 AOT 运行发现 `TodoWidgetViewModel` 的运行时 Binding 缺少 `ICustomProperty` 支持。最终仅为
`TodoWidgetViewModel` 和 `TodoItemViewModel` 增加 NativeAOT-only `GeneratedBindableCustomProperty`；
`TodoStepViewModel` 与 `TodoAttachmentViewModel` 继续延期到其非空 UI 场景。初版自动化直接写内部 TextBox，
没有更新 Markdown 编辑器公开 `Text` 契约；改用公开属性后，真实 timer/save/store 链完整通过。

15 条 5B-4B2B2A 契约、270 条全部 AOT 相关测试、x64 全量 2257/2257、Rust workspace 54/54、fmt、
Clippy `-D warnings` 和 PowerShell 脚本解析均通过。profile 40 / schema 37 保持 39 个发布文件、
WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始 IL2026/IL2050/IL2072/IL2075/IL3050=0、
生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON 固定清单仍为 23 个文件、58/58 处
source-generated 调用和 21 个 context 所有者。完整结构与复盘见 `aot-stage-5b-4b2b2a-report.md`。

2B2A 完成后，原 2B2B 调整为两个顺序门：

1. **5B-4B2B2B1 Todo 步骤持久化**：下一批开放。经真实详情 surface 验证步骤创建、文本修改、
   完成/恢复、跨进程重载、步骤删除、任务删除和空 store postflight；实际验证非空
   `ObservableCollection<TodoStepViewModel>` 的 AOT UI 投影。复杂度中等。
2. **5B-4B2B2B2 Todo 托管附件生命周期**：B2B2B1 通过后开放。使用 owned 文件夹具验证托管导入、
   SHA-256、非空附件 UI、跨进程重载、显式附件删除、物理文件清理、任务删除和空目录 postflight。
   `DeleteItemAsync` 不直接承担托管附件物理删除，因此必须先走 `DeleteAttachmentAsync`。复杂度高。

步骤是纯 Todo store 集合状态；附件还叠加受管副本、哈希、物理清理和另一类 WinRT 集合投影。两者
不作为可并行合并的低复杂度项。下一步只实施 5B-4B2B2B1，不扩展 Rust。

### 5B-4B2B2B1 完成复盘与阶段 5B-4B2B2B2 调整

5B-4B2B2B1 在 managed UI runner 中增加 `TodoStepsPersistenceRestart`，由同一受审计 NativeAOT
产物依次启动 `Mutate`、`VerifyDelete`、`Postflight` 三个不同进程。第一进程经真实 Todo detail
surface 创建普通任务和步骤，再通过实际实现的 DataTemplate 行修改文本并设为已完成；第二进程
重载上述状态，将步骤恢复未完成，依次删除步骤和任务；第三进程确认 schema 3 store、列表、详情和
步骤 UI 保持为空。

矩阵不直接构造步骤模型或写 Todo store。步骤创建使用详情输入和 `AddDetailStepAsync`；文本、完成和
删除使用实际行 TextBox、CheckBox、删除按钮，并与普通事件处理器共用可等待产品方法。结构化证据同时
核对 store、ViewModel、DataContext、文本、复选框和透明度。外层脚本比较 `Mutate.after ==
VerifyDelete.before`、`VerifyDelete.after == Postflight.before` 和 `Postflight.before ==
Postflight.after`，并单独保留删除步骤后、删除任务前的零步骤任务证据。三个 PID 均不同且 3/3 正常
退出，正式数据指纹不变，运行日志失败数和残留 AOT 进程数均为 0，证据归档后才删除 owned preview 根。

第一次真实运行发现 `ObservableCollection<TodoStepViewModel>` 已成功写入 store 和 ViewModel，但没有
投影到 WinRT `ItemsSource`。最终保留 typed `Steps`，只在 UI 边界增加可刷新的 `object[]
StepItemsSource`，并为实际进入 DataTemplate 的 `TodoStepViewModel` 增加 NativeAOT-only
`GeneratedBindableCustomProperty`。第二次运行越过 UI 后发现共享状态证据硬编码上一阶段 fixture ID；
改为显式传入 widget ID。最终复跑又发现详情打开后立即取证可能早于 ItemsControl 布局完成；改为复用
真实行 ID、文本、完成状态、DataContext、复选框和透明度条件等待，不使用固定 sleep。最终产物重新
通过旧 `TodoPersistenceRestart` 和新步骤矩阵。

17 条 5B-4B2B2B1 契约、287 条全部 AOT 相关测试、x64 全量 2274/2274、Rust workspace 54/54、
fmt、Clippy `-D warnings` 和 PowerShell 脚本解析均通过。profile 41 / schema 38 保持 39 个发布文件、
WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始 IL2026/IL2050/IL2072/IL2075/IL3050=0、
生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON 固定清单仍为 23 个文件、58/58 处
source-generated 调用和 21 个 context 所有者。完整结构与复盘见
`aot-stage-5b-4b2b2b1-report.md`。

### 5B-4B2B2B2 完成复盘与阶段 5B-4B2C 调整

5B-4B2B2B2 在 managed UI runner 中增加 `TodoAttachmentsPersistenceRestart`。第一进程经真实
Todo 详情入口创建任务，并经产品 `AddAttachmentPathAsync` 强制导入 owned 托管文本附件；第二进程
重载 store、ViewModel、受管文件和实际 `AttachmentTileStrip` DataTemplate，再经普通删除事件共用的
产品方法显式删除附件和物理文件，保留零附件任务中间证据后删除任务；第三进程确认空 store、空 UI
和零物理文件。夹具与托管副本 SHA-256 相同，三个 PID 均不同且 3/3 正常退出，正式数据指纹不变。

非空附件沿用已由 Quick Capture 和 Todo 步骤证明的 WinRT object-valued `ItemsSource` 结论：业务层
继续保留 typed `ObservableCollection<TodoAttachmentViewModel>`，只在 UI 边界增加可刷新的
`object[] AttachmentItemsSource`。附件模板原本已有 typed `x:Bind`，因此 generated-bindable 类型数
继续保持 3。实际 AOT 运行修复共享宿主未识别第三个 Todo fixture，以及 PowerShell 条件返回值没有
稳定数组形状两项遗漏；最终附件矩阵连续两轮通过，Todo 核心与步骤矩阵也重新通过。

21 条 5B-4B2B2B2 契约、308 条全部 AOT 相关测试、x64 全量 2295/2295、Rust workspace 54/54、
fmt、Clippy `-D warnings` 和三个 PowerShell 脚本解析均通过。profile 42 / schema 39 保持 39 个发布
文件、3 个 PDB、WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0、生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON
固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。完整结构与复盘见
`aot-stage-5b-4b2b2b2-report.md`。

本轮没有扩展 Rust。附件导入使用流式 `File.Copy`，哈希也不把完整文件载入托管内存，当前没有可量化
的常驻内存收益。审计另记录两个既有发布前债务：任务删除与 Undo 的附件文件回收尚无最终 GC；附件
元数据保存后若物理删除失败，可能留下孤立文件。两项需要独立恢复策略，不夹在下一批顺手修改。

下一阶段只开放 **5B-4B2C1 Glance 本地图片与偏好持久化**。使用 owned 小型本地图片，经与普通
设置入口共用的产品路径写入 per-widget 偏好，验证真实 Glance ViewModel、图片 surface、跨进程重载、
基线恢复和 postflight；不触发在线图片、网络、定位或 Picker。天气拆为后续 5B-4B2C2。Glance
偏好和本地图片枚举不是明确的托管内存热点，本批仍不扩展 Rust；高内存收益候选继续保留给有 300k
条目规模基准的 `SearchCore`。

### 5B-4B2C1 完成复盘与阶段 5B-4B2C2 调整

5B-4B2C1 在 managed UI runner 中增加 `GlancePersistenceRestart`，由同一受审计 NativeAOT 产物
依次启动 `Mutate`、`VerifyRestore`、`Postflight` 三个不同进程。第一进程经与普通设置入口共用的
产品方法选择 owned PNG，并修改显示元素、Editorial 布局、播放、过渡、可读性和照片操作层偏好；
第二进程重载相同 per-widget store、ViewModel 和真实 ImageBrush，再恢复空图片/Centered 基线；第三
进程确认 store、ViewModel 和 surface 继续保持基线。

结构化证据同时核对图片路径、图片数、当前图片、活动背景 Brush/Opacity/Uri/Stretch、四个布局根、
可读性层和操作层。三个 PID 均不同且 3/3 正常退出，owned PNG 前后 SHA-256 一致，正式数据指纹
不变，运行日志失败数和残留 AOT 进程数为 0，证据归档后才删除 owned preview 根。

Glance 真实 XAML 使用运行时 Binding，因此只在 `DESKBOX_NATIVE_AOT` 下增加一组允许 33 个属性的
`GeneratedBindableCustomProperty`。首次 AOT 编译发现当前特性构造函数必须显式提供空写属性清单；
修正后真实图片 surface 通过。审计把阶段 C#/IL 源警告与全局 WMC1510 精确基线分开，C1 源警告为 0，
WMC1510 仍精确保持 1211。全量回归还补齐了历史测试的当前 profile 文本和 22/14/5/8 绑定数量描述。

16 条 5B-4B2C1 契约、324 条全部 AOT 相关测试、x64 全量 2313/2313、Rust workspace 54/54、fmt、
Clippy `-D warnings` 和三个 PowerShell 脚本解析均通过。profile 43 / schema 40 保持 39 个发布文件、
3 个 PDB、WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0、生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON
固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。完整结构与复盘见
`aot-stage-5b-4b2c1-report.md`。

本轮没有扩展 Rust。实际图片字节由 WinUI 解码与呈现，托管层只保留少量路径和偏好；跨 FFI 搬运这些
状态不会形成明确内存收益。用户原图仍是外部引用而非 DeskBox 托管副本；图片移动/删除、其他格式、
大图、多图轮播、损坏图片和 Picker 人工交互继续作为后续边界。

天气代码复盘后，将原 5B-4B2C2 调整为两个顺序门：

1. **5B-4B2C2A 天气设置与 Widget 视图元数据持久化**：下一批开放。验证全局手动城市/经纬度、
   温度和风速单位、皮肤、指标显隐、刷新间隔，以及固定 Weather Widget 的日/周视图覆盖；使用三个
   新 AOT 进程完成写入、重载、恢复和 postflight，不初始化天气数据请求。复杂度中等。
2. **5B-4B2C2B 确定性天气 surface**：C2A 通过后开放。先建立严格受控的本地 `WeatherData` 夹具
   注入边界，再验证真实布局、非空小时/周集合投影、单位换算和重启一致性。复杂度中高。

拆分原因是 `InitializeAsync()` 总会进入 `GetWeatherAsync()`；即使关闭自动定位并预置经纬度也无法
保证无网络，而当前 WeatherService 缓存只存在于单个实例。真实定位、城市在线解析、MSN/Open-Meteo
切换、刷新、超时和降级继续留到 OS/网络矩阵。C2A/C2B 的状态规模很小，也没有扩展 Rust ABI 的依据。

### 5B-4B2C2A 完成复盘与阶段 5B-4B2C2B 调整

5B-4B2C2A 在 managed UI runner 中增加 `WeatherSettingsPersistenceRestart`，由同一受审计
NativeAOT 产物依次启动 `Mutate`、`VerifyRestore`、`Postflight` 三个不同进程。第一进程从上海、
摄氏、km/h、Week、Rich、60 分钟和 Widget Day 基线切换到成都、华氏、mph、Today、Standard、
15 分钟和 Widget Week；第二进程逐字段确认重载后恢复基线；第三进程确认 postflight 仍保持基线。

全局设置通过普通设置页共用的纯本地 `WeatherSettingsPolicy` 写入，固定 Widget 继续复用已有
`WeatherWidgetViewModeSettings` 和 `SettingsService.UpdateWidget`。全局默认与 per-widget 覆盖故意
使用相反值，以证明两个层级不会串写。owned fixture 保留 Weather 配置但关闭 Weather feature；三个
进程中 Weather `isLoaded=false`、HWND=0、XamlRoot=false，运行日志也没有 WeatherService 或
WeatherWidgetViewModel 初始化记录。三个 PID 均不同且 3/3 正常退出，正式数据指纹不变，证据归档
后才删除 owned preview 根。

第一次全量回归发现两个历史测试仍冻结 profile 43，修正当前版本断言后 2330/2330 通过。前两次 AOT
发布本身成功，但旧阶段审计分别把新增 `ValidateSet` 项和共享 runner 的 Weather 日志取证误判为历史
范围回归；最终将旧门禁收窄到各自场景和 C# 产品源码，同时保留 C2A 独立的网络、定位、Picker、
surface 和 Rust 禁止范围。第三次完整 profile 44 / schema 41 审计通过，所有 Missing、Forbidden 和
目标源码告警项为空。

11 条 5B-4B2C2A 契约、335 条全部 AOT 相关测试、x64 全量 2330/2330、Rust workspace 54/54、
fmt、Clippy `-D warnings` 和三个 PowerShell 脚本解析均通过。profile 44 / schema 41 保持 39 个发布
文件、3 个 PDB、WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0、生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON
固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。完整结构与复盘见
`aot-stage-5b-4b2c2a-report.md`。

下一阶段只开放 **5B-4B2C2B 确定性天气 surface**。先建立 NativeAOT-only、固定 fixture ID、
审计可冻结且默认生产路径不可达的本地 `WeatherData` 注入边界，再启用真实 Weather Widget，验证
ViewModel、HWND/XamlRoot、非空小时/周集合、布局、显隐、单位换算和跨进程一致性。真实定位、在线
城市解析、MSN/Open-Meteo 切换、刷新、超时和降级继续后置到 OS/网络矩阵。

C2B 的主要风险是 WinUI 运行时 Binding、WinRT 集合投影和异步初始化顺序，不是大量常驻天气状态。
当前没有可量化的 Rust 内存收益，因此仍不扩展 ABI；只有后续测量证明存在大型托管常驻或复制热点时
才重新评估 Rust。

### 5B-4B2C2B 完成复盘与阶段 5B-4C 调整

5B-4B2C2B 在 managed UI runner 中增加 `WeatherSurfacePersistenceRestart`。NativeAOT-only
`AotWeatherSurfaceFixture` 只有在固定场景、`Mutate`/`VerifyRestore`/`Postflight` phase 和固定
Widget ID 同时匹配时才注入 WeatherService；普通 JIT 不包含该类，其他 AOT 场景和默认产品路径仍用
真实服务。固定数据包含 1 个 current、24 个 hourly 和 7 个 daily 条目，夹具同时拒绝非固定坐标和位置。

真实 AOT 运行证明 Weather HWND、XamlRoot、DataContext、Expanded 420 x 520 与 Compact 205 x 520
均有效。基线 Day/Rich/Celsius/kmh/UV 与气压显示，变更后 Week/Standard/Fahrenheit/mph/UV 与气压
隐藏；当前分支的首项 DataTemplate、温度文本和 DataContext 都实际生成。Compact 通过真实窗口缩放
进入，并在 `finally` 中恢复原物理边界。三个 PID 均不同且 3/3 正常退出，6 条夹具请求、0 条网络日志，
正式数据指纹不变，owned preview 根最终清理。

首次实跑说明折叠分支不保证提前生成容器，因此最终只对当前可见 Day/Week 分支要求真实容器，同时
仍要求两组 ViewModel 集合非空。完成后审计又发现 Compact 和指标隐藏态未进入初版矩阵，补齐后首次
实跑进一步发现三个 `x:Name` 指向标签而非数值；移动到真实数值 TextBlock 后重新发布并完整通过。
这些调整没有放宽真实 surface、当前分支投影或跨进程一致性门禁。

13 条 5B-4B2C2B 契约、348 条全部 AOT 相关测试、x64 全量 2343/2343、Rust workspace 54/54、
fmt、Clippy `-D warnings`、PowerShell 和 Weather XAML 解析均通过。profile 45 / schema 42 保持 39 个
发布文件、3 个 PDB、WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0、生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON
固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。完整结构与复盘见
`aot-stage-5b-4b2c2b-report.md`。

5B-4B 的离线 Widget/内容持久化范围到此完成。下一阶段调整为 **5B-4C1A owned 本地文件 surface 与
核心文件操作**，不一次合并全部 OS 外部状态：

1. C1A 在 owned preview 根验证非空 File Widget、目录进入/返回、watcher 刷新、copy/move/rename、
   重名失败、磁盘/ViewModel/真实 UI 三层一致和三进程恢复；
2. C1B 再验证产品回收站、Shell 进度和上下文菜单，单独处理系统 UI、取消与补偿；
3. C1C 再验证 Picker 与真实拖放，把自动化证据和物理鼠标/Explorer 证据分开；
4. 后续 C2/C3 再处理快捷键/输入钩子、媒体 UI、Weather 网络/定位及其他环境依赖路径。

C1A 先行是因为 File Widget 是剩余面最大的核心功能，同时 owned 本地目录可哈希、可恢复且不依赖
网络、授权或真实输入。复杂度中高，风险集中在 FileSystemWatcher 时序、StorageItem/图标投影、重名
策略和磁盘/UI 双向一致性。当前文件操作以磁盘 I/O 为主，未发现大型托管常驻或复制内存热点，不先
扩展 Rust；若大目录/大文件基准显示枚举、哈希或路径规划存在明显内存峰值，再将证据明确的计算段收成
粗粒度 Rust 边界。

### 5B-4C1A 完成复盘与阶段 5B-4C1B 调整

5B-4C1A 在 managed UI runner 中增加 `LocalFileSurfacePersistenceRestart`。runner 在 preview 根内
建立固定 `sources` 与 `widget-root`，后者包含基线文件和一层子目录；真实 File Widget 使用 Embedded
导航和“文件夹优先、同类 Name 升序”，`showFileExtensions=false`。Mutate 经产品 ViewModel 完成目录进入/返回、copy、
move、rename 和 `IOException` 重名失败，再由产品外 owned 写入刺激 watcher。VerifyRestore 在新进程
确认变更重载后，由 harness 只在 owned 根内恢复基线并等待 watcher；Postflight 在第三进程再次确认。

应用证据要求真实 HWND/XamlRoot、活动 `ListViewBase`、所有已实现容器、`FileItemSurface`、匹配
DataContext、投影名称、文件/文件夹类型和原始 UI 顺序。应用与 runner 分别递归计算每个文件的相对
路径、长度和 SHA-256，再逐项比较。三个 PID 均不同且 3/3 自然退出，三次 EXE 哈希相同；失败日志和
延期路径日志为 0，正式数据指纹不变，证据归档后 preview 根已清理。

真实运行先后发现三类仅靠编译无法证明的问题：隐藏扩展名使 UI 名称不带 `.txt`；折叠导航栏在曾经
展开与新进程未展开时会有不同的内部缓存文字；`FileItemSurface` 六个 ElementName 计算属性缺少
NativeAOT `ICustomProperty` 表。最终分别按 UI/磁盘分层期望、仅记录可见导航文字，以及第三个窄生成
属性提供器修正。完成后审计还发现初版集合比较先排序实际名称，无法证明真实 UI 排序，最终改为按活动
列表原始顺序核对，并显式要求只有 `nested` 为文件夹；最终运行进一步修正契约误写的纯 Name 顺序，
以 `nested, baseline` 锁定产品实际的文件夹优先规则。

12 条 5B-4C1A 契约、360 条全部 AOT 相关测试、x64 全量 2355/2355、Rust workspace 54/54、fmt、
Clippy `-D warnings` 和三个 PowerShell 脚本解析均通过。profile 46 / schema 43 保持 39 个发布文件、
3 个 PDB、WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0、生产 Rust ABI 2/能力 255/九个导出和同次哈希一致；JSON
固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。完整结构与证据见
`aot-stage-5b-4c1a-report.md`。

原 C1B 同时包含回收站、Shell 进度和上下文菜单，完成 C1A 后确认这三项的外部状态边界不同，因此继续
拆分：

1. **5B-4C1B1** 只开放唯一 owned 项目的产品回收站删除、精确恢复和 File Widget 内部菜单路由；
2. **5B-4C1B2** 再验证 Shell move/progress、owner HWND、取消/延迟返回、补偿和系统 Properties；
3. **5B-4C1C** 再验证 Picker、剪贴板 StorageItems、OLE/native drop 和真实 Explorer 鼠标拖放。

C1B1 的复杂度为中高，风险来自全局回收站而非内存。实施结果保留了产品窄 `SHFileOperationW`
删除路径；精确恢复被确认需要一整组复杂 Shell Automation COM 接口，因此该部分按“完整 Rust 更简单”
的原则落成单个粗粒度查询/恢复边界。

### 5B-4C1B1 完成复盘与阶段 5B-4C1B2 调整

5B-4C1B1 在 managed UI runner 中增加 `RecycleBinMenuPersistenceRestart`。每轮生成 32 位小写
十六进制 run ID，并把它嵌入单选文件、多选文件、文件夹及其非空 payload 名称。Mutate 从真实 File
Widget 单选/多选 `MenuFlyout` 定位删除项，以 automation invoke 进入原产品链；VerifyRestore 在新
进程中要求三个原路径消失、回收站中各有唯一匹配，再经 Rust 逐项恢复；Postflight 在第三进程核对
原路径、长度、SHA-256 和匹配残留 0。

Rust 新增 `deskbox_recycle_bin_v1`。输入为原父目录和项目名；实现通过 Shell namespace CSIDL 10
完整枚举 `FolderItem.Name` 与 `System.Recycle.DeletedFrom`，只有完成枚举且匹配数严格为 1 才调用
`InvokeVerb("undelete")`。它不解析 `$I`/`$R`，不访问 `$Recycle.Bin`，不清空回收站，也不处理非
匹配项目。生产模块因此更新为 ABI 2、能力 511、十个必需导出；产品删除仍为 C# P/Invoke。

复盘先后发现并修正三个遗漏：初版恢复在首个匹配后提前执行，无法拒绝后续歧义；公共 C 头文件未
同步新结构和导出；runner 对单元素 PowerShell 结果的 `.Count` 及自动派生 recovery sibling 的清理
边界不完整。首次真实运行在产品删除后因第二项 runner 问题失败，独立 Compensate AOT 进程实际完成
三次 `Query 1 -> Restore 1/1`，最终原路径全部恢复且精确残留为 0；修正后两个全新 run ID 的完整
三进程矩阵连续通过。

12 条 5B-4C1B1 契约、372 条全部 AOT 相关测试、x64 全量 2367/2367、Rust workspace 57/57、fmt、
Clippy `-D warnings` 和四个 PowerShell 脚本解析均通过。profile 47 / schema 44 保持 39 个发布文件、
3 个 PDB、WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0，staging/publish Rust DLL 哈希一致；JSON 固定清单仍为
23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。完整结构与证据见
`aot-stage-5b-4c1b1-report.md` 和 `recycle-bin-native-abi-v1.md`。

下一阶段继续拆分：

1. **5B-4C1B2A** 只开放 owned Shell move/progress、真实 owner HWND、取消/部分完成、文件系统已完成但
   Shell 调用延迟返回、晚到任务观察与跨进程补偿；
2. **5B-4C1B2B** 再从真实 File Widget 菜单验证系统 Properties、目标路径、owner 与窗口关闭；
3. **5B-4C1C** 继续保留 Picker、剪贴板 StorageItems、OLE/native drop 和真实 Explorer 鼠标拖放。

B2A 的复杂度高于 B2B，且包含真实数据变更，因此优先单独闭环。当前 `SHFileOperationW` 路径尚无
内存热点或 AOT 阻断证据，不预先迁移 Rust；只有审计证明取消/延迟返回/补偿在 C# 需要扩大为复杂
COM 状态机，而粗粒度 Rust 边界能明显缩小时再重新评估。

### 5B-4C1B2A 完成复盘与阶段 5B-4C1B2B 调整

5B-4C1B2A 增加 `ShellMovePersistenceRestart`。产品修复集中在 owner 传递：File Widget 菜单和拖出
回退把 `_hostWindowHandle` 经 ViewModel、Organizer 传到 `ExecuteTransferPlanAsync`，最终写入
`SHFILEOPSTRUCT.hwnd`。普通生产路径仍使用 15 秒恢复探针和现有 `SHFileOperationW`。

每个 run ID 建立独立 `widget-root` 与 `desktop-root`。Mutate 通过四次真实菜单 Automation Invoke
覆盖 Real、Partial、Cancel 和 Late；Real 实际进入操作系统 Shell，另外三种只在精确 NativeAOT
场景、phase、32 位小写 run ID、preview root、owned 文件名和 source/destination 同时匹配时启用。
Partial 和 Cancel 模拟 Shell 的 aborted 返回语义，Late 在文件移动后延迟 800 ms，使用 150 ms 的
fixture 探针确定性进入 `RecoveredPending`，而生产探针仍为 15 秒。

两个全新 run ID 的完整三进程矩阵均一次通过。四种完成数为 `1/1/0/1`，反馈为
`Success/Success/Info/Success`，历史 item count 为 `1/0/1/1`；每轮 3/3 自然退出，VerifyRestore
与 Postflight 的路径、长度和 SHA-256 与初始基线一致，正式数据指纹不变，运行错误 0，preview 与
recovery 根全部清理。profile 48 / schema 45 保持 39 个发布文件、3 个 PDB、WMC1506=0、
WMC1510=1211、完整 `always-throw=0` 和原始 IL2026/IL2050/IL2072/IL2075/IL3050=0。

11 条新合同、全部 AOT 相关测试 383/383、x64 全量 2378/2378、Rust workspace 57/57、fmt、Clippy
和脚本解析全部通过。生产 Rust 模块保持 ABI 2、能力 511 和十个导出；Shell 文件 I/O 没有显示出
可量化的托管内存热点，继续保留 C# P/Invoke 比扩展 Rust ABI 更简单。

下一阶段只开放 **5B-4C1B2B 系统 Properties 菜单、精确目标、真实 owner 与窗口关闭**。B2B 不做
文件变更，复杂度低于 B2A，但系统窗口发现、owner 关系和自动关闭仍需严格隔离，并保留目标 Windows
人工视觉确认。Picker、StorageItems、OLE/native drop 和真实 Explorer 鼠标拖放继续留给 5B-4C1C。
完整结果见 `aot-stage-5b-4c1b2a-report.md`。

### 5B-4C1B2B 完成复盘与阶段 5B-4C1C 调整

5B-4C1B2B 增加 `FilePropertiesReadOnly`。真实 File Widget 单选菜单经产品 `ShowFileProperties`
进入 `SHObjectProperties(SHOP_FILEPATH)`；产品不再从前台窗口推测 owner，而是直接传递所属 File
Widget 的 `_hostWindowHandle`。NativeAOT-only fixture 只记录真实调用参数和结果，不替换 P/Invoke。

两个全新 run ID `5bfafcc7372a447cb415395d56df1df5` 与
`60a0b5706e8f4eef9b36277eebbbb875` 连续通过。两轮 API owner 均等于真实 File Widget HWND；当前
Windows 11 实测由同进程不可见 `StubWindow32` 作为属性页 direct/root owner，因此最终契约分别冻结
“产品传入的 owner”和“系统实际窗口关系”，不错误要求二者句柄相同。系统 `#32770` 属性页标题包含
唯一完整文件名，`WM_CLOSE` 后窗口销毁且残留为 0；文件长度与 SHA-256 不变，进程自然退出，正式数据
指纹不变，preview 与自动备份产生的 `-Recovery` 双根均经 ownership marker 复核后清理。

profile 49 / schema 46 保持 WMC1506=0、WMC1510=1211、完整 `always-throw=0` 和原始
IL2026/IL2050/IL2072/IL2075/IL3050=0。生产 Rust 模块保持 ABI 2、能力 511 和十个导出；直接 Shell
P/Invoke 没有 AOT 阻断或可量化内存热点，不扩展 Rust 比新增 ABI 更简单。完整结果见
`aot-stage-5b-4c1b2b-report.md`。

### 5B-4C1C1 完成复盘与阶段 5B-4C1C2 调整

5B-4C1C1 将 File Widget 文件选择改为 Windows App SDK 现代 `FileOpenPicker(WindowId)`，精确使用
所属 Widget HWND；普通产品入口和 AOT 场景共用同一服务。剪贴板路径拆出可复用 `DataPackageView`
解析，普通产品仍使用全局 Clipboard 与 Shell fallback，AOT probe 使用真实 `StorageFile`、
`StorageFolder`、`SetStorageItems` 和 `GetView` 进入同一 StorageItems 产品路径。

两个独立 run ID `36120dec1c4545ecae69244e49ce20e7` 与
`53654387061841fdaec1cc73468a728b` 均完成三进程矩阵。两轮取消和选择窗口都是真实同进程可见
`#32770`，owner chain 包含精确 File Widget HWND；取消无修改，选择和文件/文件夹 StorageItems 导入、
排序、SHA-256、重载、恢复、postflight、自然退出、正式数据保护与双根清理均通过。真实运行驱动修正了
公共对话框不是 UIA 根子项、控件缺少 Value/Invoke pattern 以及 HWND 快速复用三项 runner/观察遗漏。

自动化没有调用全局 `Clipboard.SetContent/GetContent`，避免覆盖用户当前剪贴板。因此 C1C1 已证明
StorageItems 数据格式和产品解析/导入链，但系统会话全局剪贴板传输仍是目标机器人工证据边界。
profile 50 / schema 47 保持 WMC1506=0、WMC1510=1211、完整 `always-throw=0`、原始
IL2026/IL2050/IL2072/IL2075/IL3050=0，Rust ABI 2 / 能力 511 / 十个导出不变。完整结果见
`aot-stage-5b-4c1c1-report.md`。

进入实现后将 C1C2 拆为两个证据门：C1C2A 先完成产品 hardening、generated CCW/HDROP 与实际
NativeAOT 三进程自动化；C1C2B 再单独保留真人 Explorer 物理鼠标和视觉验收。该拆分不降低原计划的
完成条件，只避免把程序化 OLE 调用误写成人工交互结论。

### 5B-4C1C2A 完成复盘与阶段 5B-4C1C2B 调整

5B-4C1C2A 保留 C# source-generated COM/vtable 产品边界。`NativeDropTarget` 在 OLE 回调期冻结
copy/move effect，File Widget host 订阅原生 Enter/Over/Leave/Drop；原生 pointer 路径只清除已经存在的
文件夹、文件堆和 surface 拖放状态，不在窗口层创建新高亮。异步复制/移动在 OLE callback 返回后才执行，
`WM_DROPFILES` 兼容路径保持不变。

最终 run ID `93da51cb94dc4db7a8a1a67d4511bcdf` 使用 PID `1488 / 18728 / 40732` 三个全新
AOT 进程。Mutate 进程通过 generated CCW 的真实 `IDropTarget` vtable 接收 HDROP，证明 pointer 越界和
显式 `DragLeave` 都将真实 folder border 从 `DropTarget` 恢复为 `Normal`；Ctrl copy 保留源，无 Ctrl
move 删除源，文件/文件夹长度和 SHA-256 均通过。384 MiB copy 在 165 ms 处观察到约 39.71% 的
determinate 进度，卡片可见且为 `ZIndex=1000`、`TranslationZ=64`、`AcrylicBrush`。随后两个新进程
完成重载、基线恢复和 postflight，三个进程自然退出，正式数据指纹不变，owned preview/recovery 根清理。

真实运行先后修正启动器 profile/schema 滞后、共享 runner 缺失 primary widget ID、视觉探针读取错误
状态、NativeDrop 未进入自然退出分支和一条过时历史测试契约。profile 51 / schema 48 最终保持
WMC1506=0、WMC1510=1211、完整 `always-throw=0` 和原始 IL2026/IL2050/IL2072/IL2075/IL3050=0；
10 条新契约、全部 AOT 相关测试 417/417、x64 全量 2412/2412、Rust 57/57。完整结果见
`aot-stage-5b-4c1c2a-report.md`。

本阶段来源明确记录为 `ProgrammaticGeneratedCcwHDrop`，并记录
`PhysicalExplorerMouseVerified=false`。C1C2A 没有出现可粗粒度隔离的托管内存热点，Rust ABI 不扩展；
WinUI 高亮、pointer/drag 状态机和 COM 反向回调继续留在 C#。

### 5B-4C1C2B 中间审计与人工门保留

C1C2B 的 run ID `771a45a536f84881ae89c04362c7299d` 在 profile 51 / schema 48 AOT 产物上使用
真实 Explorer 窗口和 owned 夹具取得注入鼠标补充证据：文件夹高亮在移出格子/Widget 后清除，小文件、
文件夹和 384 MiB 跨卷移动完成，外部拖出取消保持源文件，随后实际拖出移动成功。120 帧序列中，释放后
约 32 ms 仍有一个系统 drag image，约 166 ms 消失，约 183 ms 出现进度卡，本轮未观察到二者重叠。

该方法无法产生可信的物理 Ctrl copy，也不能代替人眼、真实鼠标或 DPI 验收。因此结构化会话保持
`AwaitingManualRound1`、`physicalExplorerMouseVerified=false`，所有 manual check 仍为 `Pending`。
真人小文件/文件夹/大文件拖入、物理 Ctrl copy、系统 drag image 单实例、进度卡置顶与毛玻璃、非 100%
DPI 和录像/截图仍是发布前人工门。完整边界见 `aot-stage-5b-4c1c2b-interim-audit.md`。

### 5B-4C2A 完成复盘与阶段 5B-4C2B 调整

5B-4C2A 用同一受审计 profile 52 / schema 49 产物和隔离数据根启动两个全新 AOT 进程。主热键
`Ctrl+Shift+F23` 与搜索热键 `Ctrl+Alt+F24` 各经真实 `RegisterHotKey` 收到一次 `WM_HOTKEY`、调用
一次产品动作且调度失败为 0；两个真实冲突均返回 1409 后恢复旧设置和旧注册。Primary 正常退出后，
Release 新进程在启动时重新取得两个手势，随后再次完成禁用、重新启用和自然退出。

Win+Space 只验证低级 hook 的独立消息线程、安装、停止与线程归零；不向保留 hook 注入按键。结构化
结果明确保持 `physicalStandardKeyboardVerified=false`、`physicalWinSpaceVerified=false`、
`physicalRecorderVerified=false` 和 `reservedHookSyntheticTriggerAttempted=false`。搜索热键的产品实现
已修正为以系统注册成功作为设置提交点；冲突时恢复旧设置与旧手势。6 条阶段契约、2 条产品安全契约、
AOT 423/423、x64 全量 2420/2420、Rust 57/57 全部通过，Rust ABI 2 / 能力 511 / 十个导出不变。
完整结果见 `aot-stage-5b-4c2a-report.md`。

C2B 继续保留物理标准热键、物理 Win+Space、设置/引导录制器、内部 `0xE8` 屏蔽键、禁用/重启和
一次触发语义。它与 C1C2B 并列为发布前人工门，但都不应阻塞独立的下一代码批次。

下一项独立代码批次调整为 **5B-4C3A Todo recurrence/reminder 确定性 AOT 状态矩阵**。先利用可注入
clock 和 owned store，在隔离根覆盖 due date、recurrence 生成、提醒候选、snooze、complete、重启与
恢复，不弹真实系统通知；复杂度为中等。真实通知展示与 activation 拆到 C3B。受控 GSMTC 媒体 UI、
真实 Weather 网络/定位、Quick Capture 图片/全局剪贴板、安装升级和 ARM64/Store 继续单独分批。

### 5B-4C3A 完成复盘与阶段 5B-4C3B 调整

5B-4C3A 使用 profile 53 / schema 50 的同一受审计 AOT 产物，在同一隔离 root 上启动五个全新进程。
固定 clock 与 owned settings/store 直接执行产品 `TodoReminderService`、`TodoRecurrenceService` 和
`TodoWidgetStore`，依次证明两个初始 due 候选、三类控制项跳过、snooze 期限、完成、次日 occurrence、
下一提醒、跨进程 dismissal、清空和 postflight。相邻 phase 的 store 长度与 SHA-256 连续一致，五个 PID
均不同且自然退出，正式数据指纹不变。

场景只通过 callback 捕获候选，固定记录 `SystemNotificationAttempted=false`，运行日志也没有 native 或
tray notification 展示。因此本批没有把状态机证据描述为系统通知或用户 activation。6 条新契约、全部
AOT 相关测试 429/429、x64 全量 2426/2426、Rust workspace 57/57、fmt、Clippy 和脚本解析均通过；
JSON 固定清单为 25 个文件、60/60 处 source-generated 调用和 23 个 context 所有者。生产 Rust 继续为
ABI 2、能力 511 和十个导出，Todo 小状态矩阵没有可量化的 Rust 内存收益。

原 5B-4C3B 再拆为：

1. **5B-4C3B1 原生通知注册、payload、展示与清理**：使用唯一 tag/group 和 run ID，覆盖单项动作、四个
   snooze 选项、聚合通知无动作、注册/注销、真实展示、通知中心清理和 fallback 排除；
2. **5B-4C3B2 activation 与单实例转发**：覆盖打开、Complete、Snooze 10/30/60 分钟和 tomorrow，分别
   验证运行中、冷启动和第二进程转发，以及 store、Todo surface 刷新和目标定位。

C3B1 会产生真实临时 Windows 通知，因此开始开发前先确认当前 SDK 的精确删除 API 和残留清理门禁。
真实用户点击仍与程序化 activation 分开记录。C1C2B、C2B 人工输入门继续保留为发布前条件。

### 5B-4C3B1 完成复盘与阶段 5B-4C3B2 调整

5B-4C3B1 使用 profile 54 / schema 51 的同一受审计 AOT 产物，在同一 owned preview root 上启动三个
全新进程。ShowAndInspect 真实展示单项与聚合通知并把历史计数从 0 变为 2；Cleanup 新进程重新读取
两条历史记录，分别按唯一 tag/group 删除到 0；Postflight 第三个新进程再次确认 0。三个 PID 均不同且
自然退出，EXE SHA-256 一致，正式数据前后均为 122 个文件、306,477,348 bytes 和同一指纹，preview
root 已清理。成功 run ID 为 `bac6a8ac20a84571a8fc7f97aa2e0206`。

fixture 直接调用产品 Todo 通知构造入口并解析系统历史中的真实 XML。单项 notification 含 Complete、
Snooze、selection input 和 `10m`、`30m`、`1h`、`tomorrow` 四个唯一选项，聚合 notification 不含动作。
服务只增加历史枚举、按 tag/group 精确删除和显式注销，没有使用全量或整组删除。运行日志固定排除托盘
fallback 和 activation；因此 C3B1 证明真实展示与清理，不证明按钮点击或单实例转发。

首轮真实运行已经展示并枚举两条通知，但暴露当前 SDK 的 action 参数实际使用 `;` 而非 fixture 假设的
`&`，四个 selection 的 XML 顺序也不稳定。失败路径按两个 tag/group 立即精确补偿成功，独立 cleanup
再确认历史为 0。最终 fixture 同时接受 `;`/`&` 并按集合验证选项。产品 activation 解析器仍只接受 `&`，
这不影响 C3B1 的展示/清理，但成为下一阶段必须先修正的真实产品问题。

6 条新契约、全部 AOT 相关测试 435/435、x64 全量 2432/2432、Rust workspace 57/57、fmt、Clippy、
脚本解析和 `git diff --check` 均通过；JSON 固定清单为 26 个文件、61/61 处 source-generated 调用和
24 个 context 所有者。生产 Rust 继续为 ABI 2、能力 511 和十个导出；两条 WinRT 通知没有可量化的
Rust 内存收益。完整结果见 `docs/architecture/aot-stage-5b-4c3b1-report.md`。

原 5B-4C3B2 再拆为：

1. **5B-4C3B2A activation grammar 与确定性动作路由**：产品解析同时兼容系统实际 `;` 和既有 `&`，
   在固定 clock、owned store 和受控 activation 输入下覆盖通知正文打开、Complete、Snooze 10/30/60
   分钟与 tomorrow、非法输入拒绝、幂等、持久化和运行中实例刷新；不要求真人点击或第二应用实例；
2. **5B-4C3B2B 真实 Windows activation 与单实例转发**：使用真实通知点击/按钮覆盖运行中、冷启动和
   第二进程转发，核对唯一进程、Todo surface 打开、目标定位和状态重载；真人点击与自动触发分开记录。

C3B2A 复杂度中高，C3B2B 复杂度高。先完成 grammar 和确定性业务路由，可避免把参数解析、状态修改、
Windows activation 与单实例生命周期同时混入一个 runner。C1C2B、C2B 人工输入门继续作为发布前条件。

### 5B-4C3B2A 完成复盘与阶段 5B-4C3B2B 调整

5B-4C3B2A 已完成，将产品 activation 解析器修正为同时接受 Windows App SDK 实际生成的 `;` 和既有 `&`。
新增单一 `TodoNotificationActivationRouter`，正文打开、Complete、Snooze `10m`/`30m`/`1h`/`tomorrow`
和旧版 `snooze10` 均复用产品 Todo service；缺失/未知 selection、未知 action、缺失目标和非 Todo 来源
明确拒绝且不修改 store。时钟和本地时区可注入，tomorrow 使用次日本地 09:00，不简化为固定 24 小时。

成功 run ID `2520cacfa69c4024b7210bc8629330dd` 使用同一 profile 55 / schema 52 受审计 AOT EXE
启动 PID `36404 / 3268 / 3460` 三个全新进程。RouteAndPersist 完成 18 条路由并留下 7 个 item，
VerifyAndClear 新进程重载后完成 2 条路由并清空，Postflight 再确认 0；相邻 store 长度/哈希连续，三个
PID 不同且自然退出，EXE 哈希一致，正式数据指纹不变，preview root 已清理。场景固定
`SystemNotificationAttempted=false` 和 `ExternalActivationAttempted=false`，因此只证明 grammar、业务路由、
callback 请求与持久化，不证明真实 Windows activation 或 Todo surface。

9 条路由单元测试、6 条新阶段契约、全部 AOT 相关测试 441/441、x64 全量 2447/2447、Rust workspace
57/57、fmt、Clippy 和脚本解析均通过。JSON 固定清单为 27 个文件、62/62 处 source-generated 调用和
25 个 context 所有者。生产 Rust 保持 ABI 2、能力 511 和十个导出；小字典/小状态路由没有可量化内存
热点，迁移 Rust 会扩大 WinRT、时区、异步 store 和 callback ABI，因此继续使用 C# 更简单。

代码复盘确认当前第二实例只转发 arguments 字符串，`UserInput` 没有写入 pending 文件；主实例消费后
传入空字典，真实 Snooze selection 会丢失。原 C3B2B 因此进一步拆为：

1. **5B-4C3B2B1 类型化 activation envelope 与单实例转发**：先用 source-generated JSON 原子保存
   arguments、UserInput 和来源信息，覆盖运行中主实例、冷启动、第二实例、损坏/重复 envelope、消费后
   删除和唯一进程；继续使用受控 activation，不声称真人点击；
2. **5B-4C3B2B2 真实 Windows activation 与 Todo surface**：再以真实临时通知覆盖正文、Complete、
   Snooze、运行中/冷启动/第二实例，验证目标 Widget/item、实际 UI 刷新、store 与唯一进程，并把自动
   证据和真人点击证据分开记录。

B2B1 复杂度中高，B2B2 复杂度高。下一项建议先做 B2B1，避免在已知 UserInput 丢失的协议上直接叠加
真实通知与 UI 生命周期。完整结果见 `docs/architecture/aot-stage-5b-4c3b2a-report.md`。

### 5B-4C3B2B1 完成复盘与阶段 5B-4C3B2B2 调整

5B-4C3B2B1 以 schema 1 的 `NativeNotificationActivationEnvelope` 取代单个 arguments 文本文件。每条
activation 使用独立 JSON 文件原子发布，保存唯一 ID、UTC 时间、来源 PID、arguments、完整
`UserInput` 和旧格式标记；大小、条目数与 key/value 长度均有上限。读取使用 claim 改名保证一次消费，
能够跳过损坏项、拒绝重复项、迁移旧文本，并在 claim 所属进程已退出时自动恢复遗留项，封闭主进程在
领取后异常退出的窗口。应用每批最多处理 128 条，批末仍有待处理项时重设命名 event，在启动期保留
信号或在运行期调度下一批，因此 UI 让步上限不会导致队列尾部无限期滞留。

应用启动顺序调整为 Todo reminder/native notification 服务就绪后才排空 pending envelope 并注册长期
event listener。命名 auto-reset event 保留启动期信号，无新信号的冷启动也会主动排空；第二实例实际
进入产品 mutex、完整 activation 写入、事件唤醒和退出路径。四个主进程加一个真实第二进程的 AOT
矩阵分别证明损坏/重复处理、`UserInput=30m` 冷启动恢复、`UserInput=tomorrow` 实时转发、精确来源
PID、跨进程持久化、空 spool postflight、自然退出、同一 EXE 哈希、正式数据指纹不变和 owned root
清理。成功 run ID 为 `731d469f4233482d84ae9a721350c1dd`；受控输入不调用系统通知 API，因此
不冒充 Windows 通知中心点击。

当前 profile 56 / schema 53，AOT 相关测试 447/447、x64 全量 2462/2462、Rust 57/57；JSON 固定
清单为 29 个文件、65/65 处 source-generated 调用和 27 个 context 所有者。信封数据量很小，复杂度
在 Windows App SDK activation、应用启动和 Todo 服务生命周期；
迁移 Rust 不会显著降低内存，反而会增加字典 JSON ABI 与回调边界，因此本阶段继续使用 C#。完整结果
见 `docs/architecture/aot-stage-5b-4c3b2b1-report.md`。

下一阶段只开放 **5B-4C3B2B2 真实 Windows activation 与 Todo surface**：分别点击正文、Complete 和
Snooze，覆盖运行中、冷启动与第二实例，核对真实 `AppNotificationActivatedEventArgs`/`UserInput`、
唯一进程、目标 Widget/item、可见刷新和持久化结果。自动化取证与用户人工点击继续分开记录；若系统
不提供可靠的程序化点击入口，就保留明确的人工验收门，不用受控 envelope 代替。

### 5B-4C3B2B2A 完成复盘与阶段 5B-4C3B2B2B 调整

5B-4C3B2B2A 将正文目标展示与 Complete/Snooze 可见刷新纳入产品返回值，不再以 callback 被调用作为
成功证据。`ShowTodoReminderTargetAsync` 先等待 ViewModel 内容，再等待真实 `TodoWidgetContent`
Loaded 和 `XamlRoot`，定位精确 item 后独立等待两帧合成提交。动作刷新只接受已加载的
`TodoWidgetContentAdapter`，重新读取 store 后同样等待两帧；业务写入成功与 surface 刷新结果分别记录。

首轮预运行实际暴露 fixture 配置先于 store 发布以及 `ContentReadyTask` 早于首帧两个竞态。最终改为
先写 owned store、再发布 Widget 配置，并增加 Loaded/`XamlRoot`/两帧门禁；没有通过延时常量掩盖或
放宽目标条件。测试退出前收束 fixture 的托盘动画回调，产品展示动画保持不变。

成功 run ID `69402a1914814f778abdfc29daf1b4f5` 使用 profile 56 / schema 53 的受审计 AOT EXE，
在 PID 36940 的同一真实 Todo HWND 上证明正文 item 可见且选中、Complete 可见状态刷新，以及
`UserInput=30m` 后路由与 surface 均为 2026-08-25 08:45:00 +08:00。进程自然退出、正式数据指纹
前后一致、owned root 已清理。结构化结果固定 `SystemNotificationAttempted=false`、
`ExternalWindowsActivationAttempted=false` 和 `UserClickVerified=false`，因此不冒充通知中心点击。

全部 AOT 相关测试 452/452、x64 全量 2468/2468、Rust 57/57；JSON 固定清单继续为 29/65/27，
Rust ABI 2、能力 511 和十个导出不变。完整记录见
`docs/architecture/aot-stage-5b-4c3b2b2a-report.md`。

剩余真实 Windows activation 继续拆为：

1. **5B-4C3B2B2B1 运行中主实例真实通知点击**：用唯一 tag/group 展示正文、Complete、Snooze，
   由用户实际点击，核对真实 activation args/`UserInput`、route result、目标 HWND/item、可见刷新和
   精确清理；
2. **5B-4C3B2B2B2 冷启动与第二实例真实通知点击**：分别在完全退出和已有主实例时点击，核对注册、
   唯一进程、类型化 envelope、目标 surface、持久化和重启恢复。

B2B2B1 复杂度中高，B2B2B2 复杂度高。下一项先做 B2B2B1，把剩余变量限制在 Windows 通知来源与
运行中 activation；受控输入、UI Automation 或注入点击都不能替代真人点击证据。两批继续使用 C#/WinRT，
因为问题集中在 Windows App SDK 与 WinUI 生命周期，迁移 Rust 不会降低内存或交互复杂度。

### 6C 完成复盘与阶段 6D 调整

6C 没有把 managed 与 Rust 两份索引同时常驻，也没有更改搜索 UI 的视觉结构。独立 SearchCore 升到
ABI 3 后，产品 watcher、扫描和 reconciliation 仍由 C# 控制，Rust 只拥有高占用的 entry/目录数据、
查询、投影和 DBIX 持久化。设置页提供明确的默认关闭预览开关、实际后端状态与 fallback 原因；
Direct x64 普通/AOT 输出打包 DLL，Store 与 ARM64 继续排除。

隔离 ABI3 基准在 300k 下把 resident private 从 85.38 MiB 降到 22.07 MiB，六组结果签名一致；真实
DeskBox 使用 207,925 条索引和 11 个启用 Widget，各后端两次重复后，整体 Private Bytes 中位数从
269.23 MiB 降到 236.86 MiB，Working Set 从 387.36 MiB 降到 355.76 MiB。前者证明索引结构收益，
后者证明全部格子显示且视觉不变时的整进程收益，两类百分比不混用。产品测量脚本确认实际后端日志、
规范 Debug EXE、隔离数据副本、源指纹不变和精确进程清理。

x64 AOT 审计已升为 profile 57 / schema 54，SearchCore ABI 3、14 导出、唯一根 DLL、x64 PE、同次
staging/publish 哈希和 PDB 分离通过；生产 `deskbox_native.dll` 仍为 ABI 2、能力 511、十导出。
完整证据见 `docs/architecture/search-core-native-abi-v3.md` 与
`docs/architecture/rust-stage-6c-search-core-report.md`。

下一阶段调整为 **6D SearchCore 预览 soak、故障恢复与默认决策门禁**，复杂度中高。一个较大批次内
覆盖 watcher change/rename/delete、树移动、overflow/reconciliation 长时间 churn；query/project/save
与 DBIX/DLL 重开故障注入；tombstone 压缩、idle unload/reload、重启恢复和内存回落；多轮真实
Release/AOT 全格子内存与搜索 P50/P95；最后再人工核对文件/应用结果、筛选和名称/大小/日期/类型
双向排序。全部通过后才决定 Direct x64 默认启用，不能仅凭一次内存收益提前切换。

6D 完成后进入阶段 7 ARM64 与 Store，复杂度高。需补 ARM64 Rust target/链接组件、真实 ARM64 设备、
PE/依赖、MSIX 打包和升级门禁。Todo 通知中心真实点击、冷启动与目标设备投递差异仍是独立的发布前
外部证据，不因 SearchCore 主线完成而自动关闭。

### 6D 完成复盘与阶段 7 开放

6D 已把默认决策所需的四类证据全部封闭。第一类是原生运行期恢复：query、recent/frequent projection、
save、idle unload、upsert、精确/树删除与 scan reconciliation 任一可恢复异常都会释放 Rust owner、
恢复 managed DBIX，并对本会话的原生重试做 quarantine；显式后端重配置才清除隔离。第二类是长会话
一致性：64 个 live 文件、65 轮更新、超过 4,096 tombstone、保存压缩、owner replacement、idle
unload/reload，以及真实 watcher create/rename/tree-delete 和模拟 overflow/root recovery 均保持 exact
live set。第三类是受审计 x64 Native AOT 产品界面：owned DBIX 文件实际进入结果，6 次筛选和名称/大小/
日期/类型各两次共 8 次排序完成，Rust 活跃、无 fallback、单 resident owner、生产数据不变。第四类是
完整格子三轮内存：208,021 entries、16,994 directories、11 个启用 Widget 下，Private Bytes 真中位数
269.73 → 235.08 MiB（-12.85%），Working Set 388.00 → 351.67 MiB（-9.36%）。

因此默认策略调整为：`DESKBOX_SEARCH_CORE_DEFAULT` 只在 Direct x64 且 SearchCore 模块参与构建时定义；
Store x64 与 Direct ARM64 的实际 MSBuild 求值均为模块关闭、默认关闭。已序列化的用户布尔选择优先，
新默认不会覆盖显式 `false`，也不会自动打开自定义索引器主功能。完整记录见
`docs/architecture/rust-stage-6d-search-core-report.md`。

下一阶段为 **7A ARM64 工具链、PE 和静态分发边界**，复杂度中高。先核对并补齐固定 Rust 1.96.0 的
`aarch64-pc-windows-msvc` 标准库与 Visual Studio ARM64 MSVC/SDK 链接组件，再构建两个 DLL，验证 PE
machine、导出、依赖、PDB、MSBuild 正反向组合和 x64 不回归。若本机缺组件，先给用户精确下载清单，
不使用未固定版本替代。7A 通过后才开放 7B ARM64 真机功能/内存及 7C Store MSIX/升级/WACK/flight。

原始“选择性 Rust + 主程序 Native AOT + 分发门禁”目标当前加权约 92%，剩余约 8%。剩余风险主要是
ARM64 真机与 Store 外部环境，并非继续扩大 Rust 代码量。Todo 通知中心真实点击、冷启动和不同目标设备
投递差异、真人 Explorer 拖放及物理热键仍分别保留为发布前外部证据。

### 7A 完成复盘与阶段 7B 开放

7A 已补齐固定 Rust 1.96.0 的 `aarch64-pc-windows-msvc` 标准库、Visual Studio ARM64 MSVC 和
Windows SDK ARM64 库，并将普通 PowerShell 中缺失的 ARM64 linker/`LIB`/`INCLUDE` 环境显式纳入
构建。NativeAOT 主程序和 Updater 复用同一次已校验环境，避免子发布重新依赖 Visual Studio 安装事务
中的易变注册状态。

两个 Rust DLL 的独立 Release 交叉构建均通过：`deskbox_native.dll` 为 ABI 2、能力 511、10 导出，
SearchCore 为 ABI 3、14 导出；两者 machine 均为 `0xAA64`，且没有在 x64 主机加载。Direct ARM64
Native AOT 静态审计生成 40 个发布文件（90.82 MiB）和 4 个分离 PDB（204.23 MiB），主程序、Updater、
两个 DLL 全部为 `0xAA64`，staging/publish 哈希一致，源指纹在审计期间不变。结构化证据固定标记
`cross-compiled-static-only`、`targetDeviceExecuted=false` 与 `runtimeAbiProbeExecuted=false`。

Direct ARM64 现在打包 SearchCore 供真机验证，但编译默认仍为 managed；Store 继续不包含模块。PE 依赖
库存确认两个 Rust DLL 直接导入 `VCRUNTIME140.dll`，7C 必须在静态 CRT 与安装 VC++ Redistributable
之间做出明确选择并在干净环境验证。完整记录见
`docs/architecture/rust-stage-7a-arm64-static-report.md`。

下一阶段开放 **7B ARM64 真实设备产品门禁**，复杂度高。一个较大批次内验证 ARM64 AOT 启动/退出、
真实 ABI、搜索结果/筛选/八次排序、watcher/mutation/reconciliation、DBIX/idle/故障回退，以及 11 个完整
Widget 全显示且视觉不变时的 managed/Rust 多轮内存；最后才决定 ARM64 默认值。7B 不制作正式安装包，
通过后进入 7C 的 Direct/Store x64/ARM64 安装、升级、WACK、flight 与 CRT 分发。

原始目标当前加权约完成 94%，剩余约 6%。Todo 通知投递差异、真人 Explorer 拖放和物理热键等既有
外部证据继续独立保留，不因 ARM64 静态构建成功而关闭。

### 7B 自动化边界与 7C0 CRT 决策完成复盘

在没有实体 ARM64 设备的条件下，7B 将可自动化部分迁移到 GitHub Actions 的原生 ARM64 Windows
runner。OS、PowerShell、.NET 测试宿主和固定 Rust 1.96.0 host 均为 ARM64；两个 Rust DLL 被真实
加载并完成 ABI probe，SearchCore 实际执行 Unicode add/seal/query，产品绑定测试 11/11 通过。产品
加载器中遗留的 x64-only 前置判断已修正为允许 x64/ARM64，否则正确 DLL 会在产品层被提前拒绝。

7C0 同时完成 x64/ARM64 动态与静态 CRT 对照。静态方案在两个 DLL 合计增加 187,392 B（x64）和
190,976 B（ARM64）文件体积，SizeOfImage 增加 188,416 B 与 204,800 B；两种架构都移除
`VCRUNTIME140.dll` 导入，运行时 ABI 与静态产品测试均通过。因此生产默认采用 Static：Direct 安装器
无需因 Rust 增加 VC++ Redistributable，Store 也无需因 Rust 增加 VCLibs 依赖。Windows App SDK
framework/runtime 仍是独立分发条件。

Direct ARM64 的 SearchCore 默认值据此开放，与 Direct x64 一致；Store 继续 managed。完整证据见
`docs/architecture/rust-stage-7b-arm64-actions-report.md` 和
`docs/architecture/rust-stage-7c0-crt-distribution-report.md`。

该结果没有关闭实体 ARM64 的交互窗口、托盘、系统 UI、全部 Widget 内存、GPU/驱动和休眠恢复，也没有
完成正式包。原始“选择性 Rust + 主程序 Native AOT + 分发门禁”目标当前加权约完成 96%，剩余约 4%。
下一阶段为 **7C1 Direct / Store x64 与 ARM64 分发矩阵**，复杂度高：最终 AOT publish、双架构安装器
和 MSIX/upload、文件与框架依赖、无卸载升级和数据保留、签名/WACK/package flight。实体 ARM64 交互、
真人 Explorer 拖放、物理热键和目标设备通知投递继续作为发布前外部证据，不与可自动化代码门禁混写。

### 7C1 双架构自动化分发矩阵完成复盘

7C1 新增 `distribution-audit.yml`，在 `windows-2025-vs2026` 原生 x64 与
`windows-11-vs2026-arm` 原生 ARM64 runner 并行执行。最终运行
[32650821484](https://github.com/Tianyu199509/DeskBox/actions/runs/32650821484) 对提交
`ebbb8ecf341db9068b3bbf71c7101fd9c19ff886` 的两个架构全部通过，并由第三个 job 生成只有两边均通过
才成立的跨架构摘要。

Direct 侧重新执行完整 AOT publish，核对主程序、Updater、两个 Rust DLL、PE machine、ABI/导出、
静态 CRT、PDB 排除和 publish 清单，再用 Inno 6 编译带 `NativeAot` 后缀的 x64/ARM64 安装器。AOT
安装器只跳过 .NET Desktop Runtime 检测，继续保留 Windows App Runtime 2.2 依赖处理。

Store 侧修正了 Windows App SDK AOT package payload：AOT `DeskBox.exe` 与静态
`deskbox_native.dll` 进入 MSIX，两个 PDB 只进入 `.appxsym`；`DeskBox.deps.json`、
`DeskBox.runtimeconfig.json`、Updater、SearchCore、CoreCLR/JIT 和 Direct 素材被禁止。拆包审计确认正式
Partner Center identity、x64/ARM64 architecture、`Microsoft.WindowsAppRuntime.2` framework dependency、
EXE 无 CLR header、Rust 导出/依赖、publish/package hash 和单架构 `.msixupload` 结构。

本机最终定向契约为 12/12，x64 全量为 2535/2535。完整实现和 SHA-256 见
`docs/architecture/rust-stage-7c1-distribution-report.md`。

该阶段只完成可自动化的构建与包内容门禁。`physicalUserDeviceExecuted`、`signingExecuted`、
`wackExecuted` 和 `inPlaceUpgradeExecuted` 均为 `false`。原始“选择性 Rust + 主程序 Native AOT +
双架构分发门禁”目标加权约完成 98%，剩余约 2%。下一阶段调整为 **7C2 发布外部证据与合并上传包**：
先在 Actions 复用两个已审计 MSIX 生成 x64+ARM64 `.msixbundle/.msixupload`，再做 WACK、x64 Direct
首装/覆盖升级/卸载、Partner Center package flight；实体 ARM64 交互、系统集成和整进程内存以后补齐。

## 11. 参考资料

- [.NET Native AOT deployment overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [Windows App SDK 1.6 release notes: Native AOT support](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6)
- [Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [Windows App SDK 2.0 release notes: modern Storage Pickers](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0)
- [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- [MVVMTK0045](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/generators/errors/mvvmtk0045)
- [COM interop with Native AOT](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/cominterop)
- [COM interface source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation)
- [CsWinRT AOT and trimming guidance](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md)
- [Rust Windows MSVC targets](https://doc.rust-lang.org/rustc/platform-support/windows-msvc.html)
- [rustup toolchain override](https://rust-lang.github.io/rustup/overrides.html)
- [Rust FFI guidance](https://doc.rust-lang.org/nomicon/ffi.html)
- [windows-rs](https://github.com/microsoft/windows-rs)
- [P/Invoke source generation with `LibraryImport`](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)
- [Native code interop with Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)
- [CoInitializeEx](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializeex)
- [IShellLinkW::Resolve](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-resolve)
- [IShellLinkW::GetPath](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-getpath)
- [IShellLinkW::GetArguments](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-getarguments)
- [Windows App SDK unpackaged deployment dependencies](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
- [Rust MSVC CRT linkage](https://doc.rust-lang.org/stable/reference/linkage.html)
