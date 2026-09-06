# DeskBox Native AOT 阶段 5B-4B2C1 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT Glance owned 本地图片、显示偏好、真实图片 surface、跨进程重载、基线恢复与 postflight
- 平台：x64 / `win-x64`
- 结论：5B-4B2C1 已完成。本结论不包含本地文件/文件夹 Picker、在线图片、网络、定位、天气数据请求、安装升级、ARM64、Store 或正式发布验证

## 1. 本阶段结论

5B-4B2C1 在既有 managed UI runner 中增加 `GlancePersistenceRestart`，使用同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从固定基线开始，经与普通设置入口共用的产品方法选择 owned PNG，设置时间、日期、年份、Editorial 布局、Strong 可读性、静态播放和照片操作层；
2. `VerifyRestore` 在新进程中重载 Glance store、ViewModel 和真实 WinUI surface，确认同一图片已被解码为活动 `ImageBrush`，再通过产品方法恢复空本地图片与 Centered 基线；
3. `Postflight` 在第三个新进程中确认 store、ViewModel 和真实 surface 均保持恢复后的基线。

最终矩阵证明：

- 三个 PID 互不相同，均由应用正常关闭路径自然退出；
- 三次启动使用同一受审计 EXE 及同一 SHA-256；
- 第一进程修改后的 store、ViewModel 和 surface 与第二进程启动时逐字段一致；
- 第二进程恢复后的状态与第三进程启动及结束状态逐字段一致；
- owned PNG 在运行前后 SHA-256 相同，没有被产品改写；
- 图片实际进入 `ImageBrush`，活动背景透明度为 1，Stretch 为 `UniformToFill`；
- Editorial/Centered 根、可读性遮罩和照片操作层的真实可见状态与偏好一致；
- 正式数据目录前后指纹一致，运行日志失败数为 0，证据归档后才删除 owned preview 根。

本阶段没有扩展 Rust 产品边界。Glance 业务层只处理少量路径和偏好，图片字节的解码、缩放与呈现由 WinUI 图像栈完成。把路径列表和 UI 状态改为 Rust 不会显著减少常驻托管内存，反而增加 FFI、异步 UI 生命周期和错误映射成本。生产 Rust 模块继续保持 ABI 2、能力 255 和九个必需导出。

## 2. 实现结构

| 文件 | 职责 |
| --- | --- |
| `GlanceWidgetSettingsPolicy.cs` | 提供本地文件规范化、清空来源与照片播放偏好的共享产品策略 |
| `GlanceWidgetSettingsSection.xaml.cs` | 普通设置页改为复用共享策略，Picker 与清空的用户行为保持不变 |
| `GlanceWidgetViewModel.cs` | 提供可等待的本地文件和播放偏好产品方法，继续复用既有布局与显示元素方法 |
| `GlanceWidgetViewModel.AotBindableProperties.cs` | 仅 NativeAOT 启用的一组窄生成属性提供器，覆盖真实 XAML 运行时 Binding 使用的 33 个属性 |
| `GlanceWidgetViewModel.AotPersistenceSmoke.cs` | 读取偏好、图片目录和 ViewModel 计算状态 |
| `GlanceWidgetContent.AotPersistenceSmoke.cs` | 等待真实 surface 稳定并读取实际 ImageBrush、布局根、可读性层与操作层 |
| `WidgetManager.AotGlancePersistenceSmoke.cs` | 只允许固定 Glance fixture，等待真实内容宿主和 `ContentReadyTask` |
| `App.AotGlancePersistenceSmoke.cs` | 三阶段产品操作、结构化取证、精确期望与正常关闭 |
| `App.AotManagedUiSmoke.cs` | 场景路由、phase 环境变量和 source-generated evidence 输出 |
| `run-aot-managed-ui-smoke.ps1` | owned PNG、预置基线、三进程执行、状态等值、哈希、日志、正式数据和清理门禁 |
| `publish-aot-audit.ps1` | profile 43 / schema 40 的源码、绑定、范围、告警和 runner 契约 |
| `AotStage5B4B2C1ContractTests.cs` | 16 条 C1 静态契约与历史门禁保留检查 |

## 3. 实施中发现并修正的问题

### 3.1 Glance 的运行时 Binding 在 AOT 中需要属性提供器

`GlanceWidgetContent.xaml` 仍使用运行时 `{Binding}`。真实 AOT surface 需要 `GlanceWidgetViewModel` 提供 `ICustomProperty` 元数据，因此只在 `DESKBOX_NATIVE_AOT` 下增加一个 `GeneratedBindableCustomProperty`，并将允许属性精确冻结为 33 个。

该桥不改变普通 JIT 类型表面，也不把所有 ViewModel 属性公开给 WinRT。第一次 AOT 编译还证明当前 CsWinRT 特性的构造函数必须同时提供读属性和写属性两个参数；补齐空写属性清单后发布成功。

### 3.2 阶段源码告警与全局 WMC1510 基线需要分层处理

Glance XAML 的运行时 Binding 会产生既有 `WMC1510`。本阶段已经用真实 AOT 运行证明生成属性桥可用，但编译器仍保留这些提示。审计因此采用两个独立门：

- 阶段源码告警只拒绝 C#/IL/其他新增问题；
- 全局 `WMC1510` 继续以精确值 1211 单独冻结，任何增减都要求显式复盘。

这样不会把既有 1211 条提示误报为 C1 新问题，也没有通过忽略所有告警来放宽基线。最终 C1 缺失模式、禁止范围、目标源码告警和非预期告警代码均为 0。

### 3.3 历史当前阶段断言需要随 profile 一起前移

第一次 x64 全量回归发现 23 个历史测试仍要求项目提示停留在 5B-4B2B2B2，另有两个测试冻结了未包含 Glance 的旧 runner 场景清单。修正后所有历史测试只把“当前 profile”前移到 C1，原阶段的行为与计数契约没有删除。

第二次 AOT 契约回归又发现项目提示在更新时丢失了历史 22 个叶子 compiled binding、14 个 typed DataTemplate binding、5 个 ViewModel bridge binding 和 8 个搜索结果行 binding 的精确文字。最终恢复这些数量，并保留后续窄生成属性桥的说明。324 条 AOT 相关测试随后全部通过。

## 4. 结构化证据

最终场景证据归档在：

```text
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/verify-restore-result.json
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/final-glance.json
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/glance-local.png
.artifacts/aot-managed-ui-smoke/win-x64/glance-persistence-restart/DeskBox.log
```

已观察到的关键值：

- mutate 后来源为 `LocalFiles`，图片数为 1，布局为 `Editorial`，可读性为 `Strong`；
- surface 的 `decodedImagePath` 与 owned PNG 相同，活动背景为 `ImageBrush`，图片与操作层可见；
- verify 进程启动时完整恢复上述状态；恢复后图片数为 0、布局为 `Centered`、可读性为 `Soft`；
- postflight 前后保持相同空图片基线；
- `ProcessCount=3`、`NaturalExitCount=3`、`RuntimeFailureLogCount=0`、`PreviewRootCleaned=true`；
- 正式数据目录前后指纹相等。

## 5. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B2C1 契约 | 16/16 通过 |
| 全部 AOT 相关测试 | 324/324 通过 |
| x64 .NET 全量测试 | 2313/2313 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | `publish-aot-audit.ps1`、`run-aot-managed-ui-smoke.ps1`、`start-aot-preview.ps1` 全部通过解析 |
| 审计 profile / schema | 43 / 40 |
| 发布文件 / 分离 PDB | 39 / 3 |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| C1 缺失模式 / 禁止范围 / 目标源码告警 | 0 / 0 / 0 |
| Glance generated-bindable / 属性 / evidence JSON 调用 | 1 / 33 / 1 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9，staging 与 publish SHA-256 一致 |
| Glance 三进程矩阵 | 通过，3/3 正常退出 |

`-RequireCleanAnalysis` 仍会因仓库已有的 CS0108、CS0169、CS0414、CS8601、CS8602 和精确冻结的 WMC1510 返回非零；`unexpectedWarningCodes` 为 0，C1 自身源码告警为 0。日常结构审计不应把这组已登记基线描述为“全仓库零警告”。

## 6. 复盘与未覆盖边界

本阶段定义的本地图片选择产品路径、per-widget store、ViewModel、真实图片解码 surface、显示/布局/播放偏好、跨进程重载、基线恢复和 postflight 均有对应证据，没有发现阻断 C1 完成的遗漏。

仍需明确以下边界：

1. 产品保存的是用户文件路径，不会把图片复制到 DeskBox owned 存储。用户移动、重命名或删除原图后，该图片会失效；本阶段没有改变这一产品语义。
2. 自动化使用 68 字节的有效 PNG，只证明最小本地图片解码。JPEG/WebP/GIF、EXIF 方向、超大图片、损坏图片和多图片轮播的性能与视觉仍未覆盖。
3. 本轮直接调用与普通设置入口共用的产品方法，不触发 FileOpenPicker、FolderPicker，也没有人工验证 Picker owner、取消、键盘、触控、动画或实际大图观感。
4. 在线图片、Bing 来源、缓存下载、网络失败、文件夹枚举和日历展示继续在本阶段之外。
5. 生成属性桥已经通过真实 AOT surface，但没有减少 WMC1510 的编译输出；后续只能按实际运行需要继续窄桥接，不能把所有 Glance 属性机械暴露。

## 7. 下一阶段调整

代码复盘确认原 5B-4B2C2 不能直接把“无网络设置持久化”和“真实天气 surface”合成一个矩阵：

- 天气城市、经纬度、温度单位、风速单位、皮肤、指标显隐和刷新间隔是全局 `AppSettings`；
- 每个 Weather Widget 只有日/周视图覆盖保存在 `WidgetConfig.Metadata["Weather.ViewMode"]`；
- 即使关闭自动定位并预置有效经纬度，`WeatherWidgetViewModel.InitializeAsync()` 仍立即调用 `GetWeatherAsync()`；
- `WeatherService` 当前缓存只存在于单个服务实例内，新的 AOT 进程没有可预置的持久化缓存，因此真实 surface 初始化必然可能访问网络。

建议把后续拆为两个顺序门：

1. **5B-4B2C2A 天气设置与 Widget 视图元数据持久化**：只验证全局手动城市/坐标、单位、皮肤、指标显隐、刷新间隔，以及固定 Weather Widget 的日/周覆盖；通过设置产品路径、保存、三个新进程重载与恢复取证，不初始化天气数据请求。复杂度中等，不使用 Rust。
2. **5B-4B2C2B 确定性天气 surface**：先设计一个范围严格、AOT 可审计的本地 `WeatherData` 夹具注入边界，再验证真实布局、非空小时/周集合投影、单位换算和跨进程恢复。该门不能偷偷依赖公网，也不能把生产 HTTP 语义改成测试语义。复杂度中高。

真实定位、在线城市解析、MSN/Open-Meteo 切换、刷新、超时与降级继续留到 OS/网络矩阵。天气状态规模很小，实际 HTTP 和 WinUI 集合才是主要边界；当前没有证据表明改用 Rust 能显著降低内存，因此 C2A/C2B 都不应扩展 Rust ABI。
