# DeskBox Native AOT 阶段 5B-4B2C2A 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT 天气本地设置、固定 Weather Widget 日/周视图元数据、三进程重载、基线恢复与 postflight
- 平台：x64 / `win-x64`
- 结论：5B-4B2C2A 已完成。本结论不包含真实 Weather surface、天气数据请求、自动定位、在线城市解析、数据源切换、Picker、安装升级、ARM64、Store 或正式发布验证

## 1. 本阶段结论

5B-4B2C2A 在既有 managed UI runner 中增加 `WeatherSettingsPersistenceRestart`，使用同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从固定上海基线开始，经与普通设置入口共用的本地产品策略写入成都、华氏、mph、Today、Standard、指标显隐和 15 分钟刷新设置，并将固定 Weather Widget 的视图覆盖从 Day 改为 Week；
2. `VerifyRestore` 在新进程中逐字段确认上述变更已恢复，再经同一产品路径恢复上海、摄氏、km/h、Week、Rich、显示项基线、60 分钟刷新和 Widget Day 覆盖；
3. `Postflight` 在第三个新进程中确认全局设置和 per-widget 元数据继续保持恢复后的基线。

最终矩阵证明：

- 三个 PID 互不相同，均由应用正常关闭路径自然退出；
- 三次启动使用同一受审计 EXE 及同一 SHA-256；
- 第一进程修改后的状态与第二进程启动时逐字段一致；
- 第二进程恢复后的状态与第三进程启动及结束状态逐字段一致；
- 全局默认视图与 per-widget 覆盖相互独立：基线为全局 Week / Widget Day，变更为全局 Today / Widget Week；
- Weather 配置存在且可保存元数据，但功能开关在夹具中关闭，因此未创建 Weather HWND、XamlRoot 或可见宿主；
- 运行日志中 `WeatherService` 和 `WeatherWidgetViewModel` 初始化记录均为 0；
- 正式数据目录前后指纹一致，证据归档后才删除 owned preview 根。

本阶段没有扩展 Rust 产品边界。天气设置只是少量字符串、数值、布尔值和一项 Widget metadata，不是托管常驻内存热点；改为 Rust 不会形成可量化的内存收益，反而增加 FFI 状态同步和普通设置页的调用复杂度。生产 Rust 模块继续保持 ABI 2、能力 255 和九个必需导出。

## 2. 开发前本地备份

由于本轮开始时工作区包含大量未提交和未跟踪文件，先完成独立于仓库的本地备份：

```text
D:\project\wingezi-local-backups\20260822-143347-pre-5b4b2c2a
```

备份包含：

- 可由 Git 验证的 `repository.bundle`；
- tracked 工作树二进制补丁 `working-tree.patch`；
- 当前源码快照 `workspace-source.zip`；
- 131 个未跟踪文件的独立归档 `untracked-files.zip`；
- Git 状态、差分统计、元数据、SHA-256 清单、恢复说明和验证记录。

验证不只检查文件存在：已从 bundle 建立临时 clone、应用二进制补丁，再生成补丁并确认 SHA-256 与备份补丁完全一致；源码与未跟踪归档也完成逐文件字节和 SHA-256 核对。备份前后工作区均为同一 HEAD、244 条状态记录（112 tracked、132 untracked）。仓库根已有约 18.9 GiB 的 `备份.zip` 没有再次嵌套复制，元数据中已明确记录该排除项。

## 3. 实现结构

| 文件 | 职责 |
| --- | --- |
| `WeatherSettingsPolicy.cs` | 提供手动城市/坐标校验、自动定位、单位、默认视图、皮肤、显示项和刷新间隔的纯本地共享产品策略 |
| `SettingsViewModel.WeatherOptions.cs` | 普通设置页复用共享策略；数据源、城市搜索、网络刷新等既有行为不纳入该策略 |
| `WidgetManager.AotWeatherSettingsPersistenceSmoke.cs` | 只处理固定 Weather 配置，复用 `WeatherWidgetViewModeSettings` 写入/读取 Day、Week 元数据，并证明没有 Weather host |
| `App.AotWeatherSettingsPersistenceSmoke.cs` | 三阶段产品操作、逐字段期望、host 抑制断言和结构化证据 |
| `App.AotManagedUiSmoke.cs` | 场景路由、phase 环境变量、固定 fixture、正常关闭和 source-generated evidence 输出 |
| `run-aot-managed-ui-smoke.ps1` | 基线预置、三进程执行、状态等值、PID/EXE 哈希、日志、正式数据和 owned 清理门禁 |
| `publish-aot-audit.ps1` | profile 44 / schema 41 的源码、策略、metadata、离线范围、警告和 runner 契约 |
| `WeatherSettingsPolicyTests.cs` | 本地产品策略的有效输入、非法坐标不写入、回退与刷新范围测试 |
| `AotStage5B4B2C2AContractTests.cs` | 11 条场景、策略、元数据、离线范围、Rust 边界和审计契约 |

## 4. 设计边界

### 4.1 真实 Weather surface 本轮必须保持未加载

`WeatherWidgetViewModel.InitializeAsync()` 会进入刷新和天气请求；即使预置手动城市及有效经纬度，也不能保证不访问网络。`WeatherService` 的缓存只存在于单个服务实例，不能作为三个全新进程之间的确定性输入。

因此 C2A 保留固定 Weather Widget 配置和真实 metadata 保存路径，但在 owned fixture 中关闭 Weather 功能。共享 Search 对照 Widget 仍正常恢复，严格要求总加载宿主数为 1、Weather 宿主数为 0。该边界避免把“设置持久化是否正确”和“天气网络是否可用”混成同一失败面。

### 4.2 全局默认视图和 Widget 覆盖必须分别取证

全局 `WeatherDefaultView` 与 `WidgetConfig.Metadata["Weather.ViewMode"]` 是两个不同层级。矩阵使用相反取值验证它们不会互相覆盖：

| 状态 | 全局默认 | 固定 Widget 覆盖 |
| --- | --- | --- |
| 基线 | Week | Day |
| 变更 | Today | Week |

证据同时记录 `HasViewModeOverride`、`UseWeekView` 和原始 `MetadataValue`，避免只根据计算后的布尔值推断元数据确实存在。

### 4.3 数据源不属于本地设置策略

本轮读取并冻结 `WeatherDataSource=MSN` 作为不变对照，但不提供数据源 setter，也不调用普通设置页的数据源切换路径。切换数据源可能触发刷新和不同网络实现，继续留在 OS/网络矩阵。

## 5. 实施中发现并修正的问题

### 5.1 历史 profile 契约有两处漏改

第一次 x64 全量测试为 2328/2330。失败的两个旧测试仍要求 `auditProfileVersion = 43`，而本阶段 profile 已前移到 44。只更新这两处当前版本断言后，完整测试为 2330/2330；旧阶段的行为、数量和边界断言均保留。

### 5.2 旧阶段审计对共享 runner 的新增场景过度耦合

第一次 AOT 发布已成功，但 5B-4B1 审计要求 runner 的完整 `ValidateSet` 必须等于 C1 时的旧字符串，新增 Weather 场景被误判为回归。该门改为分别要求旧阶段自己的 `DeepSettingsReadOnly` 和 `GlancePersistenceRestart` 仍存在，不再禁止未来追加场景。

第二次 AOT 发布同样成功，随后 5B-4B2A 把整个 PowerShell runner 中用于 C2A 日志取证的 `[WeatherService]` 文本误判为旧阶段进入天气业务。最终仍在旧阶段的三个 C# 源文件中禁止 `WeatherService`，但不把共享脚本的日志检查视为产品调用；C2A 同时保留独立的 WeatherService、网络、定位、Picker 和 Rust 禁止范围。

修正后重新执行完整发布，profile 44 / schema 41 审计通过。所有阶段的 Missing、Forbidden 和目标源码警告项复核后均为空。

## 6. 结构化证据

最终成功证据位于：

```text
.artifacts/aot-managed-ui-smoke/win-x64/weather-settings-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-settings-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-settings-persistence-restart/verify-restore-result.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-settings-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-settings-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/weather-settings-persistence-restart/DeskBox.log
```

关键实测值：

- mutate 前：`Shanghai AOT Baseline`、`31.2304/121.4737`、Celsius、kmh、MSN、Week、Rich、60 分钟、Widget Day；
- mutate 后：`Chengdu AOT Mutation`、`30.5728/104.0668`、Fahrenheit、mph、MSN、Today、Standard、15 分钟、Widget Week；
- verify 启动状态与 mutate 后逐字段相同，恢复后回到完整基线；
- postflight 启动和结束状态均与基线逐字段相同；
- 所有状态中的 Weather `isLoaded=false`、`windowHandle=0`、`hasXamlRoot=false`；
- `ProcessCount=3`、`NaturalExitCount=3`、`RuntimeFailureLogCount=0`、`WeatherInitializationLogCount=0`、`PreviewRootCleaned=true`；
- 正式数据目录前后指纹均为 `A5D134363521FE9AA55984AF820F929F854B00916790D5668D9B2224FEBFC212`。

## 7. 验证结果

| 验证 | 结果 |
| --- | --- |
| Weather policy + C2A + C1 定向测试 | 33/33 通过 |
| 全部 AOT 相关测试 | 335/335 通过 |
| x64 .NET 全量测试 | 2330/2330 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | `publish-aot-audit.ps1`、`run-aot-managed-ui-smoke.ps1`、`start-aot-preview.ps1` 全部通过解析 |
| 审计 profile / schema | 44 / 41 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布目录 / 符号目录 | 83.8 MiB / 183.2 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| C2A 缺失模式 / 禁止范围 / 目标源码告警 | 0 / 0 / 0 |
| evidence JSON 调用 | 共享 runner 中 1 次 source-generated 调用 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9，staging 与 publish SHA-256 一致 |
| Weather 三进程矩阵 | 通过，3/3 正常退出 |
| 共享 runner 回归 | `BasicReadOnly` 与 `GlancePersistenceRestart` 均通过 |

`-RequireCleanAnalysis` 仍会因仓库已有的 CS0108、CS0169、CS0414、CS8601、CS8602 和精确冻结的 WMC1510 返回非零；`unexpectedWarningCodes` 为 0，C2A 自身源码告警为 0。这组结果不能描述为“全仓库零警告”。

## 8. 复盘与未覆盖边界

本阶段定义的本地设置产品路径、坐标校验、全局设置保存、per-widget Day/Week metadata、三进程重载、基线恢复、host 抑制、日志、正式数据保护和清理均有对应证据，没有发现阻断 C2A 完成的遗漏。

仍需明确以下边界：

1. 本阶段没有创建 Weather ViewModel 或真实 surface，因此没有验证运行时 Binding、非空小时/周集合的 WinRT 投影、天气渐变、指标布局或单位换算后的真实文本。
2. 自动化没有调用城市搜索、在线城市解析、系统定位授权、MSN/Open-Meteo 数据源切换、手动刷新、超时、重试或降级。
3. 设置操作通过与普通设置页共用的产品策略执行，但没有人工点击设置页逐项观察当前 JIT UI；本轮没有视觉改动。
4. 本轮只验证固定合法坐标和 policy 单元测试中的非法坐标，不覆盖极端合法边界城市、时区或本地化城市名展示。
5. C2A 关闭 Weather 功能仅用于确保纯本地确定性，不代表产品关闭主功能后丢弃 Weather 配置；配置和 metadata 在三个进程中均被保留。

## 9. 下一阶段调整

下一阶段开放 **5B-4B2C2B：确定性天气 surface**，但应继续保持无公网、无定位：

1. 建立 NativeAOT-only、固定 fixture ID 且可由审计冻结的本地 `WeatherData` 注入边界；生产默认路径仍使用现有 WeatherService，普通 JIT 行为不变；
2. 让固定 Weather Widget 真正启用并加载 `WeatherWidgetViewModel`、`WeatherWidgetContent`、HWND 和 XamlRoot；
3. 使用非空 current/hourly/daily 数据，验证紧凑/展开布局、日/周切换、指标显隐、单位换算、真实集合 UI 投影和跨进程一致性；
4. 外层 runner 继续要求三个不同 PID、同一 EXE 哈希、正常退出、正式数据指纹不变、零公网/定位日志和 owned 根清理；
5. 保留 C2A 的全局设置与 metadata 矩阵作为回归，不把网络成功率作为 surface 正确性的前置条件。

C2B 复杂度为中高，主要风险是 AOT 运行时 Binding、WinRT 集合投影、异步初始化顺序和生产/夹具路由隔离。当前仍没有改用 Rust 的内存依据；WeatherData 规模小，真实风险集中在 WinUI 和异步生命周期。若实现中发现明显的大型托管数据常驻或复制证据，再按测量结果单独评估 Rust，不预先扩展 ABI。

真实定位、在线城市解析、MSN/Open-Meteo 切换、刷新、超时和降级在 C2B 完成后进入后续 OS/网络矩阵。C2B 不与这些外部状态合并。
