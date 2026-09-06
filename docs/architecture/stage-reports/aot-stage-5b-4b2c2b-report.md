# DeskBox Native AOT 阶段 5B-4B2C2B 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT 确定性 Weather surface、真实 HWND/XamlRoot、Compact/Expanded、Day/Week、单位/皮肤/指标显隐、三进程重载、基线恢复与 postflight
- 平台：x64 / `win-x64`
- 结论：5B-4B2C2B 已完成。本结论不包含真实网络、系统定位、在线城市解析、数据源切换、Picker、物理拖放、安装升级、ARM64、Store 或正式发布验证

## 1. 本阶段结论

5B-4B2C2B 在既有 managed UI runner 中增加 `WeatherSurfacePersistenceRestart`，使用同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从摄氏、km/h、Rich、Day、显示 UV/气压的基线开始，加载固定非空天气数据和真实 Weather Widget，再切换为华氏、mph、Standard、Week，并隐藏 UV/气压；
2. `VerifyRestore` 在新进程中确认变更后的设置、ViewModel 和真实控件文本/可见性全部恢复，再经产品路径恢复基线；
3. `Postflight` 在第三个新进程中确认设置、Widget metadata、真实 Expanded/Compact surface 和集合投影继续保持基线。

最终矩阵证明：

- 三个 PID 互不相同，均由应用正常关闭路径自然退出；
- 三次启动使用同一受审计 EXE 及同一 SHA-256；
- Weather Widget 的 HWND、XamlRoot、DataContext、可见状态和非空数据均真实存在；
- Expanded 布局中的 Day/Week 分支、Segmented 选中项、24 条小时数据和 7 条日数据按当前可见分支生成真实容器；
- Compact 布局由真实窗口临时缩放到 205 logical px 后取证，随后在 `finally` 中恢复原物理边界并精确比较；
- 摄氏/华氏、km/h/mph、Rich/Standard、UV/气压显示与隐藏同时核对设置、ViewModel 和真实控件；
- `Mutate.after == VerifyRestore.before`，恢复后的状态与 `Postflight` 前后状态逐字段一致；
- 每个进程由固定夹具服务两次确定性 WeatherData，请求日志共 6 条，网络/定位日志为 0；
- 正式数据目录前后指纹一致，证据归档后 owned preview 根已清理。

本阶段没有扩展 Rust 产品边界。天气数据只有 1 个 current、24 个 hourly 和 7 个 daily 条目，实际风险集中在 WinUI Binding、WinRT 集合投影、响应式布局和异步生命周期。把这些小型状态搬到 Rust 会增加 FFI 映射与 UI 同步，不会形成可量化内存收益。生产 Rust 模块继续保持 ABI 2、能力 255 和九个必需导出。

## 2. 备份与工作区保护

本轮沿用开始开发前已验证的仓库外本地备份：

```text
D:\project\wingezi-local-backups\20260822-143347-pre-5b4b2c2a
```

该检查点约 1.745 GiB，包含 repository bundle、tracked 工作树补丁、源码快照、未跟踪文件归档、状态与 SHA-256 清单，并已通过临时 clone、补丁重放和逐文件哈希验证。开发与审计过程中没有执行 commit、push、reset、stash 或删除用户产物，也没有把无关工作区变更合入本阶段。

## 3. 实现结构

| 文件 | 职责 |
| --- | --- |
| `AotWeatherSurfaceFixture.cs` | NativeAOT-only 固定 WeatherData；同时要求精确场景、phase 和 owned Widget ID，越界坐标/位置立即失败 |
| `WeatherWidgetContentProvider.cs` | 只在上述严格门禁成立时注入夹具 WeatherService；普通 JIT 继续创建原生产服务 |
| `WeatherService.cs` | 增加 AOT-only 窄数据工厂，并将天气/城市响应绑定到 source-generated JSON context |
| `WeatherWidgetViewModel.cs` / `WeatherWidgetViewModel.DataProcessing.cs` | 为 hourly/daily 保留 typed 业务集合，并增加 `object[]` WinRT UI 投影及变更通知 |
| `WeatherViewModels.AotBindableProperties.cs` | 仅 NativeAOT 编译的三组精确生成绑定属性，覆盖 Weather 主 ViewModel、小时项和日项 |
| `WeatherWidgetContent.xaml` | 复用原布局，只增加真实控件命名和 hourly/daily UI 投影入口 |
| `WeatherWidgetContent.AotSurfaceSmoke.cs` | 等待并抓取 Expanded/Compact、Day/Week、文本、可见性、集合容器及 DataContext |
| `WidgetManager.AotWeatherSurfaceSmoke.cs` | 定位固定真实宿主，执行 Compact 实窗缩放，并在 `finally` 中恢复和核对原边界 |
| `App.AotWeatherSurfacePersistenceSmoke.cs` | 三阶段产品操作、逐字段期望及结构化 UI 证据 |
| `run-aot-managed-ui-smoke.ps1` | 三进程等值、PID/哈希、日志、正式数据指纹、自然退出与 owned 清理门禁 |
| `publish-aot-audit.ps1` | profile 45 / schema 42 的源码、夹具隔离、UI 投影、警告和 Rust 边界契约 |
| `AotStage5B4B2C2BContractTests.cs` | 13 条场景、夹具、生成绑定、集合投影、真实 surface、Compact、指标显隐、runner 和审计契约 |

## 4. 设计边界

### 4.1 夹具不能成为产品后门

`AotWeatherSurfaceFixture` 整体位于 `#if DESKBOX_NATIVE_AOT` 中，普通 JIT 二进制不包含它。AOT 中也必须同时满足以下三项才会返回夹具服务：

1. `DESKBOX_AOT_MANAGED_UI_SMOKE=WeatherSurfacePersistenceRestart`；
2. phase 精确为 `Mutate`、`VerifyRestore` 或 `Postflight`；
3. Widget ID 精确为 `aot-5b4b2c2b-weather`。

夹具收到非固定上海坐标或位置名会立即抛错。默认 AOT 产品路径、其他 Widget、其他 runner 场景及普通 JIT 都继续进入真实 `WeatherService`。

### 4.2 业务集合保持强类型，WinRT 边界单独投影

`HourlyForecast` 和 `DailyForecast` 继续是业务层的 typed collection。实际 AOT 运行所需的 `ItemsSource` 使用刷新时生成的 `object[]` 投影，避免 CsWinRT 对自定义泛型集合投影返回 `E_INVALIDARG`。DataTemplate 条目本身仍保留 `WeatherHourViewModel` / `WeatherDayViewModel` DataContext，并通过精确生成绑定属性提供器工作。

### 4.3 只要求当前可见分支生成容器

Day 和 Week 共用 Expanded 的最后一行，但非当前分支被折叠。WinUI 不保证折叠的 `ItemsControl` 生成容器，因此门禁要求当前可见分支必须有真实首项容器、DataContext 和文本投影，同时仍要求两个 ViewModel 集合都为非空且计数正确。该规则验证真实 UI 行为，不把惰性布局误报为功能错误。

### 4.4 Compact 必须来自真实窗口响应式切换

Compact 证据不是直接调用私有布局函数或伪造宽度。runner 先读取真实 Expanded surface 和物理窗口边界，根据 DPI 比例把窗口缩到 205 logical px，等待 `LayoutMode=Compact` 与真实控件文本稳定，然后无条件恢复原物理边界。恢复后再次等待 Expanded surface，并要求恢复边界与原始记录完全相等。

## 5. 实施与复盘中发现的问题

### 5.1 折叠分支不会提前生成 DataTemplate 容器

第一次真实 AOT 运行中，Day 分支正常生成 24 个小时项，但折叠的 Week 列表没有容器。原断言错误地要求两个不可同时可见的分支都生成首项。修正后仍要求两个集合非空，只对当前可见分支要求真实容器与模板文本；随后 Day 和 Week 分别在对应状态通过。

### 5.2 初版完成审计发现两个范围遗漏

第一次按 C2B 目标复盘时，已覆盖 Expanded、Day/Week、单位和皮肤，但尚未真正进入 Compact 布局，也只证明所有指标的显示态，没有证明隐藏态。继续在同一阶段补充了：

- 真实窗口缩放、Compact 控件文本和边界恢复；
- UV/气压设置从显示切为隐藏，再跨进程恢复显示；
- App evidence、PowerShell 跨进程等值和 AOT 审计三层对应门禁。

### 5.3 Compact 首次实跑发现命名取证点指向标签

补充后的第一次三进程运行正确进入 Compact，但湿度、风速和降水取到了 `Humidity`、`Wind`、`Precipitation` 标签，而不是相邻数值。产品 UI 本身正常，问题来自新增 `x:Name` 放在了错误的 TextBlock。将三个名称移动到真实数值控件后重新发布并完整重跑，数值分别为 `64%`、`18 km/h`/`11.2 mph` 和 `70%`。

这些问题都由真实 AOT 运行或完成后审计发现，没有通过放宽核心结果断言掩盖。

## 6. 结构化证据

最终成功证据位于：

```text
.artifacts/aot-managed-ui-smoke/win-x64/weather-surface-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-surface-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-surface-persistence-restart/verify-restore-result.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-surface-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-surface-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-surface-persistence-restart/DeskBox.log
```

关键实测值：

- 三个 PID 互不相同，3/3 自然退出；
- 三次运行的 EXE SHA-256 均与同次 AOT 审计摘要一致；
- Expanded：`420 x 520` logical px；Compact：`205 x 520` logical px；
- 基线：Day、Rich、`20°C`、`18 km/h`、UV/气压可见、小时首项真实投影；
- 变更：Week、Standard、`68°F`、`11.2 mph`、UV/气压隐藏、日首项 `Today / 75°F / 61°F` 真实投影；
- 当前/小时/日计数：1 / 24 / 7；湿度 `64%`、降水 `70%`、UV `5`、气压 `1012 hPa`；
- `FixtureLogCount=6`、`NetworkLogCount=0`、`RuntimeFailureLogCount=0`；
- 正式数据目录前后指纹相同；
- `PreviewRootCleaned=true`，结束后受审计 AOT 进程数为 0。

PID、HWND、EXE 哈希和正式数据指纹属于本次本机运行证据，不应被当作跨机器固定常量。

## 7. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B2C2B 定向契约 | 13/13 通过 |
| 全部 AOT 相关测试 | 348/348 通过 |
| x64 .NET 全量测试 | 2343/2343 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell / XAML 语法 | runner、审计、启动器解析通过；Weather XAML 可解析 |
| 审计 profile / schema | 45 / 42 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布目录 / 符号目录 | 84.1 MiB / 184.3 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| C2B 缺失模式 / 禁止范围 / 目标源码告警 | 0 / 0 / 0 |
| JSON source-generated 清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9，staging 与 publish SHA-256 一致 |
| Weather 三进程矩阵 | 通过，3/3 正常退出，6 条 fixture、0 条网络日志 |
| 共享 runner 回归 | `BasicReadOnly`、`GlancePersistenceRestart`、`WeatherSettingsPersistenceRestart` 全部通过 |

`-RequireCleanAnalysis` 仍会因仓库已有的 CS0108、CS0169、CS0414、CS8601、CS8602 和精确冻结的 WMC1510 返回非零；`unexpectedWarningCodes` 为 0，C2B 自身源码告警为 0。这组结果不能描述为“全仓库零警告”。

## 8. 完成后审计与剩余边界

对文档和已实现代码再次复盘后，C2B 定义内的确定性数据、生产路由隔离、真实 Weather host、Expanded/Compact、Day/Week、皮肤、单位、UV/气压显隐、非空集合投影、三进程重载、恢复、postflight、日志、正式数据保护和清理均有对应证据；没有发现仍阻断 C2B 完成的遗漏。

仍需明确以下边界：

1. 没有调用系统定位、IP 定位、城市在线搜索、MSN/Open-Meteo、手动刷新、超时、重试、缓存或 stale/fallback；
2. 没有验证 Mini 布局、所有七项指标的每种组合、极端长本地化文本、极端温度、不同 DPI/多显示器的视觉排版；
3. 自动化证明控件值、布局分支和可见性，不替代用户对颜色、间距、截断和动画的人工视觉验收；
4. Quick Capture 图片/剪贴板扩展、Todo 提醒/重复、附件 Undo/孤立文件回收仍是独立债务；
5. 文件拖放/复制移动/回收站/上下文菜单、Picker、Shell、快捷键、媒体 UI、安装升级、CRT、ARM64/Store 和 Rust `SearchCore` 尚未完成。

## 9. 下一阶段调整

下一阶段建议进入 **5B-4C1A：owned 本地文件 surface 与核心文件操作**，先不把物理拖放、回收站、Picker、Shell UI、快捷键、媒体和天气公网混在同一批。

建议的 C1A 范围：

1. 在 owned preview 根建立固定源目录和目标目录，包含非空文件、子文件夹、重名输入和可校验哈希；
2. 启用真实 File Widget，验证 HWND/XamlRoot、非空 Item 容器、文件/文件夹类型、排序、目录进入/返回和 watcher 刷新；
3. 经 `WidgetViewModel.ImportPathsAsync`、`RenameItemAsync` 和产品刷新路径验证 copy、move、rename 及冲突失败，逐步比较磁盘状态、ViewModel 和真实 UI；
4. 使用三个新 AOT 进程证明变更重载、恢复和 postflight，并继续保护正式数据目录及 owned 根；
5. 只操作 runner 创建且已验证位于 owned 根内的路径，任何删除先采用可恢复/明确清理边界；产品回收站、Shell 进度和外部拖放另设 C1B/C1C。

选择 C1A 先行的原因是它覆盖 DeskBox 最核心且剩余面最大的 File Widget，同时输入完全本地、可哈希、可回滚，不依赖网络、定位授权、媒体 session 或真实鼠标拖动。复杂度为中高，主要风险是 FileSystemWatcher 时序、WinRT StorageItem/图标投影、重名策略和磁盘/UI 双向一致性。

当前不建议把 C1A 改成 Rust。文件复制移动主要受磁盘 I/O 限制，现有实现按流/逐项处理，尚无大型托管常驻或复制内存热点证据；WinUI、watcher 和 Shell 生命周期也仍应留在 C#。若后续用大目录/大文件基准确认枚举、哈希或路径规划存在显著内存峰值，再把那一段收成粗粒度 Rust 边界，不预先改写整套文件功能。

建议后续顺序为：C1A 本地文件核心；C1B 回收站/Shell 进度/上下文菜单；C1C Picker 与真实拖放；C2 快捷键和输入钩子；C3 媒体 UI 与 Weather 网络/定位。安装升级、CRT、ARM64/Store 和 Rust `SearchCore` 继续保持独立门禁。
