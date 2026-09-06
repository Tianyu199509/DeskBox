# DeskBox Native AOT 阶段 5B-4B1 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT 设置搜索、24 个深层设置路由、breadcrumb 与设置页 managed collection 投影
- 平台：x64 / `win-x64`
- 结论：5B-4B1 已完成；可以进入拆分后的 5B-4B2A，但本报告不代表设置写入、Widget 内容持久化、OS 交互、安装升级、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-4B1 在 5B-4A 的 `BasicReadOnly` 场景之外增加 `DeepSettingsReadOnly`，并使用最终受审计的 NativeAOT 产物在独立 preview 数据根实际运行。该矩阵证明：

1. 非空设置搜索能够产生真实建议，并通过产品激活路径进入 `BackupRestoreSettings`；
2. 24 个此前未覆盖的设置路由都完成导航、布局、可见内容、选中主导航项和 XamlRoot 核对；
3. 23 个嵌套路由都生成两项 breadcrumb，`CapsuleBehaviorSettings` 能通过 breadcrumb 返回 `CapsuleMode`；
4. 预置的 1 条文件叠放自定义规则能投影到真实列表容器；
5. 非空备份快照清单能投影到真实列表容器；
6. 设置窗口及六个设置子区直接使用的 `SettingsViewModel` Binding 已建立精确的 NativeAOT 生成绑定清单，当前为 282/282；
7. 真实运行中发现的 `SettingsOption`、文件 Widget 选项和天气城市建议投影问题均已做窄修复；
8. AOT 进程停止后，外层脚本再次检查运行日志，未发现未处理异常或备份清单投影失败；
9. 正式 `%LOCALAPPDATA%\DeskBox` 在矩阵前后保持相同指纹，测试数据只存在于本阶段 owned preview 根。

本阶段没有改写 Rust 产品边界。普通 JIT 仍默认使用原 C# shortcut、音乐音量、Explorer 启动和 Quick Access 实现；NativeAOT 编译期继续使用已经冻结的 Rust 粗粒度边界。

## 2. 范围与安全边界

外层入口仍为：

```text
scripts/run-aot-managed-ui-smoke.ps1 -Scenario DeepSettingsReadOnly
```

场景使用：

```text
DESKBOX_AOT_MANAGED_UI_SMOKE=DeepSettingsReadOnly
.artifacts/aot-managed-ui-smoke/win-x64/preview-root
.deskbox-aot-managed-ui-owned.json
```

安全边界如下：

- DataRoot 必须位于专属 artifact 根下，且必须带脚本创建的所有权标记；
- 只停止可执行文件完整路径等于受审计 AOT 产物的进程；
- 固定预置 File 与 Search 两个 Widget，以及 ID 为 `aot-5b4b1-design` 的单条文件叠放规则；
- 不调用 `SettingsService.Save`，不创建或删除 Widget，不写 Quick Capture、Todo 或 Glance store；
- 不触发文件、Shell、快捷键、Picker、媒体 setter 或第三方应用操作；
- 正式数据目录在运行前后执行确定性指纹、文件数与字节数比较；
- AOT 进程停止后才读取完整日志，并把 `Unhandled exception:` 与 `[DataBackup] Snapshot inventory failed:` 作为硬失败；
- `BasicReadOnly` 保留为独立回归场景，没有被深层设置场景替换或弱化。

## 3. 真实路由矩阵

本阶段按固定顺序覆盖以下 24 个路由：

| 主域 | 路由 |
| --- | --- |
| 外观与文件 | `AppearanceDetail`、`FileDisplaySettings`、`ManagedStorage`、`FileStackSettings`、`DesktopOrganizationSettings` |
| 胶囊与分组 | `CapsuleMode`、`WidgetGroups`、`CapsuleBehaviorSettings`、`CapsuleArrangementSettings`、`CapsuleAnimationSettings`、`CapsuleOverridesSettings` |
| 功能组件 | `QuickCaptureSettings`、`TodoSettings`、`MusicSettings`、`WeatherSettings`、`GlanceSettings`、`SearchSettings` |
| 外观细分 | `AppearanceMaterialSettings`、`AppearanceDensitySettings`、`AppearanceWindowSettings`、`AppearanceAnimationSettings` |
| 维护 | `BackupRestoreSettings`、`DataHealthSettings`、`CompatibilityDiagnosticsSettings` |

每个路由都要求：

- 当前 section 与目标一致；
- 目标内容可见，窗口具有有效尺寸和 XamlRoot；
- 主导航选中项与路由登记的 `NavTag` 一致；
- 顶级路由不出现 breadcrumb；
- 嵌套路由显示父级与当前页两项 breadcrumb，并显示返回入口。

设置搜索不是静态扫描。runner 使用本地化的备份标题产生非空建议，通过现有 `ActivateSettingsSearchResult` 路径激活精确页面，然后再执行完整路由矩阵。

## 4. NativeAOT 实际问题与修复

### 4.1 设置 ViewModel 的运行时 Binding 元数据

设置窗口仍有较大数量的运行时 `{Binding}`。为 5B-4B1 机械改写全部 XAML 会同时改变大量 TwoWay、命令和动态 DataContext 行为，因此本阶段采用 NativeAOT-only 精确生成清单：

```text
SettingsViewModel.AotBindableProperties.cs
```

清单只包含设置窗口和六个设置子区中直接绑定、且确实属于 `SettingsViewModel` 的 public property。契约通过反射和 XAML 解析动态比较两侧集合，当前结果为 282 个需要项、282 个生成项、0 缺失、0 多余。命令不进入该清单；可安全类型化的四个命令改用 `x:Bind`，其中本轮处理了 `ResetCapsuleWidthOverridesCommand` 的显式 ViewModel bridge。

该方案把 WMC1510 从 1216 降到 1211，但目标不是继续机械压低计数，而是保证真实 AOT 路径所需属性均有元数据，并由运行矩阵证明。

### 4.2 DataTemplate 条目类型

真实 AOT 运行首先暴露 `SettingsOption.DisplayName` 缺少可绑定元数据。随后对本阶段七类设置 DataTemplate 条目做完整清点，并为实际需要运行时 Binding 的类型增加生成绑定元数据：

- `SettingsSearchResult`
- `SettingsBreadcrumbItem`
- `BackupSnapshotListItem`
- `FileStackCustomRuleEditor`
- `SettingsOption`
- `CapsuleOverrideSettingsItem`
- `WidgetGroupSettingsItem` 与 `WidgetGroupMemberSettingsItem`
- `WeatherCitySearchResult`

这些修改只提供 WinRT 绑定元数据，没有改变条目业务字段或持久化格式。

### 4.3 managed collection 到 WinRT ItemsSource

真实 AOT 运行继续发现两类集合即使元素可绑定，集合本身仍不能可靠直接投影给 WinRT `ItemsSource`：

1. 文件叠放模式与文件夹打开行为的 `IReadOnlyList<SettingsOption>` compiled binding 在 NativeAOT 下返回 `E_INVALIDARG`；
2. 天气页的 `ObservableCollection<WeatherCitySearchResult>` 直接 Binding 在该路径仍无法完成投影。

修复保留原有 typed collection 供产品逻辑使用，仅为 UI 增加 `object[]` 投影：

- `AvailableFileStackModeOptionItems`
- `AvailableFileWidgetFolderOpenBehaviorOptionItems`
- `WeatherCitySuggestionItems`

原集合变化时显式通知对应投影属性。设置搜索、breadcrumb 和备份清单也在赋给 `ItemsSource` 前转换为 `object[]`。这是一条窄的 WinRT 边界适配，不是把业务集合全部改成弱类型。

### 4.4 异步页面诊断

深层页面包含异步装载与延迟布局。runner 为每个路由增加 begin/completed 日志、有限等待和 `UpdateLayout()`，从而把剩余失败定位到具体页面；没有用无界等待掩盖失败。文件叠放规则和备份清单还要求首个容器具有非空 XamlRoot 与正高度，避免只验证集合数量而没有验证真实模板实例化。

## 5. 实现结构

| 文件 | 职责 |
| --- | --- |
| `App.AotManagedUiSmoke.cs` | `DeepSettingsReadOnly` 调度、结构化证据、24 路由总门禁与正常退出 |
| `SettingsWindow.AotDeepSmoke.cs` | 非空搜索、路由、breadcrumb、文件规则与备份清单的真实 UI 诊断 |
| `SettingsWindow.Navigation.cs` | 搜索建议和 breadcrumb 使用 AOT-safe `object[]` 投影 |
| `SettingsWindow.Maintenance.cs` | 备份清单使用 AOT-safe `object[]` 投影 |
| `SettingsViewModel.AotBindableProperties.cs` | 仅 NativeAOT 编译的 282 项精确生成绑定清单 |
| `SettingsOption.cs`、`WeatherData.cs` 与设置 ViewModel 分部 | DataTemplate 类型元数据及局部集合投影 |
| `CapsuleModeSettingsSection.xaml(.cs)`、`FileWidgetSettingsSection.xaml` | 可类型化命令与选项 ItemsSource 的显式 ViewModel bridge |
| `run-aot-managed-ui-smoke.ps1` | owned 根、固定夹具、结构化证据、日志硬门禁、精确进程清理与正式数据指纹 |
| `publish-aot-audit.ps1` | profile 37 / schema 34 的源码、绑定、集合、警告、Rust 与产物门禁 |
| `AotStage5B4B1ContractTests.cs` | 本阶段 12 条契约及 282/282 动态清单比较 |

本阶段复用 `AotManagedUiSmokeJsonContext` 的单次 source-generated 序列化，没有增加新的 JSON 调用。固定清单仍为 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者。

## 6. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B1 契约 | 12/12 通过 |
| 全部 AOT 阶段契约 | 196/196 通过 |
| x64 .NET 全量测试 | 2216/2216 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | 全部脚本通过解析 |
| JSON 固定清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| 审计 profile / schema | 37 / 34 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布 / PDB 体积 | 82.6 MiB / 178.4 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 审计期间源码稳定 | `true` |
| `DeepSettingsReadOnly` | 24 个页面、非空搜索、breadcrumb 返回、1 条文件规则、非空备份清单，运行日志失败数 0 |
| `BasicReadOnly` 回归 | 2 个表面、6 个主设置分区、12 套语言、6 次筛选、8 次排序，运行日志失败数 0 |

上述 AOT UI 项是实际运行受审计 `DeskBox.exe` 的自动化证据，不只是源码扫描。它仍不能替代用户对视觉、焦点、键鼠手感、动画和目标系统差异的人工验收。

## 7. 复盘与遗漏检查

本轮完成后再次核对规划、代码、脚本、日志与现有测试，处理或确认了以下事项：

1. 初版生成绑定清单遗漏 `SelectedWidgetCapsuleBarPlacement`；动态 282/282 比较已使同类遗漏以后直接失败。
2. `GeneratedBindableCustomProperty` 当前构造函数同时要求 property name 与 indexer type 两组参数；NativeAOT-only 声明已使用准确签名。
3. 只给 DataTemplate 条目加元数据不足以保证 `ItemsSource` 可用；实际失败的三类 typed collection 已保留业务类型并增加 UI 边界投影。
4. 天气页最初只调整集合类型仍失败；最终通过独立 `object[]` 投影解决，typed collection 继续供选择逻辑使用。
5. 备份页原先可能只在后台日志记录失败；外层脚本现把该日志升级为场景硬失败。
6. `BasicReadOnly` 与 `DeepSettingsReadOnly` 都在最终矩阵保留，避免新场景覆盖基础搜索筛选/排序回归。
7. 所有新增类型和投影已进入 profile 37 源码门禁；目标源 AOT 告警、非预期告警和禁止的 mutation 模式均为 0。
8. 没有修改 Rust ABI、能力、导出或产品持久化格式。

当前没有发现阻断 5B-4B1 完成的代码遗漏。

## 8. 尚未证明的边界

- 通过设置 UI 修改值、显式 flush、正常退出和新进程重载；
- Widget 增删、锁定、位置/尺寸/可见性等拓扑变更的重启恢复；
- Quick Capture 与 Todo 的真实内容编辑、附件、保存和重启恢复；
- Glance 数据、图片路径与轮播状态的持久化恢复；
- 天气手动城市设置的重启恢复，以及定位/网络刷新行为；
- 文件拖放、复制移动、跨卷、回收站、上下文菜单、快捷键、Picker、Shell 和媒体 UI 交互；
- 安装、覆盖升级、自动更新、卸载、回滚与 CRT 部署决策；
- ARM64、Store、WACK、签名和真实目标设备矩阵。

## 9. 下一阶段调整

原规划把所有持久化变更合并为 5B-4B2。代码复盘显示这会同时跨越通用设置文件、Widget 生命周期和三个独立内容 store，还会把天气的定位/网络变量混入同一次诊断。现调整为三个顺序批次：

1. **5B-4B2A 设置与 Widget 拓扑持久化/重启恢复**：下一轮开放。只在 owned preview 根中通过真实设置/ViewModel 路径修改一组可逆的 bool、enum、数值和字符串设置，并修改固定 Widget 的锁定、可见性、位置/尺寸或标题；要求写前快照、`FlushPendingSaveAsync` 成功、正常退出、新 AOT 进程重载、结构化逐字段核对和最终 owned 根清理。复杂度中高。
2. **5B-4B2B Quick Capture 与 Todo 内容 store**：使用固定 Widget ID 写入最小文本、分组/完成状态和受控附件夹具，分别验证 UI flush、store 文件、重启恢复和删除清理。两者都有编辑防抖和附件生命周期，复杂度高，不与 2A 合并。
3. **5B-4B2C Glance 与天气**：Glance 使用 owned 图片夹具验证数据与轮播状态；天气先验证手动城市和展示设置持久化，把真实定位和网络刷新明确留给后续 OS/网络矩阵。复杂度中高。

推荐下一步只实施 5B-4B2A。它先证明所有后续内容组件共同依赖的 `SettingsService`、debounce/flush、Widget 配置和跨进程重载链路。2A 通过后，2B/2C 的失败就可以聚焦于各自 store 或 UI，不再混入通用设置保存问题。
