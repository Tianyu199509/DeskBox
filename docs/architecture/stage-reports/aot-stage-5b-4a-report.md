# DeskBox Native AOT 阶段 5B-4A 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT 基础托管 UI 只读矩阵，包括托盘、Widget 恢复、语言资源、设置主分区与搜索筛选/排序
- 平台：x64 / `win-x64`
- 结论：5B-4A 已完成，可以进入拆分后的 5B-4B1；本报告不代表持久化组件变更、文件与系统交互、安装升级、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-4A 使用最终受审计的 NativeAOT 产物，在隔离 preview 数据根完成了一轮真实 WinUI 自动化矩阵，实际证明：

1. 运行时 `RuntimeFeature.IsDynamicCodeSupported=false`，托盘图标窗口和 owner 窗口句柄均有效；
2. 预置的 File 与 Search 两个 Widget 按固定 ID 从设置中恢复，两个表面均已加载且可见，没有通过 runner 创建新 Widget；
3. 12 套已发布语言字典均能装载，每套包含 2350 个资源，并具有设置标题与“打开设置”搜索动作所需键；
4. 同一个设置窗口依次打开 General、Appearance、FeatureWidgets、Interaction、Maintenance、About 六个主分区，每一步都有有效 HWND、可见 AppWindow、XamlRoot、尺寸、标题、当前分区与选中分区证据；
5. 搜索窗口使用本地化且必定存在的“打开设置”动作产生结果，依次切换 All、FilesAndFolders、Apps、Images、Documents、DeskBox 六种筛选；
6. 名称、大小、日期、类型四个排序入口各调用两次，八次升降序转换均被记录，结束后恢复到 `All / Relevance / ascending`；
7. AOT 进程正常退出，正式 `%LOCALAPPDATA%\DeskBox` 运行前后指纹一致。

这轮没有改写 Rust 产品边界。此前 shortcut、Explorer/Quick Access 和音乐音量在 AOT 下继续使用已经冻结的 Rust ABI；普通 JIT 的默认后端策略不变。

## 2. 范围与安全边界

新外层入口为：

```text
scripts/run-aot-managed-ui-smoke.ps1
```

它固定使用：

```text
DESKBOX_AOT_MANAGED_UI_SMOKE=BasicReadOnly
.artifacts/aot-managed-ui-smoke/win-x64/preview-root
.deskbox-aot-managed-ui-owned.json
```

安全边界包括：

- DataRoot 必须位于本阶段专属 artifact 根之下，且必须具有本脚本创建的所有权标记；
- 只预置 `aot-5b4a-file` 与 `aot-5b4a-search` 两个固定 Widget；
- runner 拒绝非 NativeAOT、非 preview 根和不精确的预置配置；
- 只停止路径精确等于受审计 `DeskBox.exe` 的进程，不按进程名清理其他安装或开发实例；
- 正式数据目录在运行前后计算确定性指纹，任何变化都使矩阵失败；
- 本阶段不打开搜索结果、不写搜索历史、不变更设置、不创建或删除 Widget，也不触发文件、Shell 或媒体写操作；
- 七个 AOT smoke 脚本都会保存、清空并恢复全部七个 opt-in，避免父环境残留让多个 runner 同时执行。

## 3. 实现结构

本阶段把测试入口与产品行为分开：

| 文件 | 职责 |
| --- | --- |
| `App.AotManagedUiSmoke.cs` | NativeAOT-only 总控、超时、验证、结构化结果和正常退出 |
| `SettingsWindow.AotSmoke.cs` | 通过产品设置入口导航六个主分区并采集窗口/导航诊断 |
| `SearchPopupWindow.AotSmoke.cs` | 等待真实结果，调用六种筛选和四个排序 handler，并采集最终状态 |
| `LocalizationService.cs` | 只读装载 12 套资源字典并返回数量/必需键诊断 |
| `run-aot-managed-ui-smoke.ps1` | owned preview 根、固定设置、受审计启动、证据校验、进程清理和正式数据指纹 |
| `start-aot-preview.ps1` | 继续校验 profile/schema、EXE/Rust 哈希、数据根隔离与精确进程路径 |

`App.xaml.cs` 只在所有既有原生边界 smoke 调度之后调用 `StartAotManagedUiSmokeIfRequested()`。没有设置 opt-in 时，普通启动路径不进入本 runner。

结果位于：

```text
.artifacts/aot-managed-ui-smoke/win-x64/preview-root/aot-managed-ui-smoke/basic-read-only/result.json
.artifacts/aot-managed-ui-smoke/win-x64/session.json
```

生产源码只新增一处 `JsonSerializer.Serialize`，显式绑定 `AotManagedUiSmokeJsonContext`。JSON 固定清单因此由 22 个文件、57 次调用、20 个 context 所有者更新为 23 / 58 / 21；反射重载仍为 0。

## 4. 首次真实 AOT 失败与窄修复

第一轮受审计 AOT 运行在构造设置窗口时失败：

```text
System.ArgumentException: Value does not fall within the expected range
ABI.Microsoft.UI.Xaml.Controls.IItemsControlMethods.set_ItemsSource
SettingsWindow.UpdateSettingsSearchSuggestions
```

根因是空设置搜索把 `Array.Empty<SettingsSearchResult>()` 赋给 WinRT `ItemsSource`。`SettingsSearchResult` 是设置窗口内部的私有 managed record；普通 JIT 接受这条路径，但 NativeAOT 的 WinRT 投影返回 `E_INVALIDARG`。

修复只改变空查询的表达方式：把 `ItemsSource` 设为 `null`，同时关闭建议列表。空查询原本就没有可显示项，所以界面行为不变，也没有为 5B-4A 提前重构非空搜索建议模型。契约同时冻结新行为并拒绝旧的私有空数组赋值。

重新发布后，第二轮真实矩阵完整通过。这个结果也说明下一阶段应先核对非空设置建议和其他 managed collection 投影，再引入大批持久化变更。

## 5. 真实 AOT 矩阵结果

| 项目 | 结果 |
| --- | --- |
| 场景 | `BasicReadOnly` |
| 动态代码 | `false` |
| 托盘句柄 | icon 与 owner 均非 0 |
| 预置 / 已加载 / 可见 Widget | `2 / 2 / 2` |
| 可见类型 | `File`、`Search` |
| 语言字典 | 12/12；每套 2350 个资源，必需键齐全 |
| 设置主分区 | 6/6 |
| 搜索当前结果 | 3；包含打开设置动作 |
| 筛选转换 | 6/6 |
| 排序转换 | 8/8；四列各两次 |
| 最终搜索状态 | `All / Relevance / ascending` |
| AOT 进程遗留 | 无 |
| 正式数据前后指纹 | `AEBFD3FBE8A037F9BEBCF01EA6CD1987C04EBB81D035400E14E7DF697C4C80DB`，一致 |

该矩阵操作的是真实 WinUI 窗口与真实产品 handler，不是只扫描源码的静态测试。不过它仍属于自动化证据，不等于用户已经人工确认视觉层级、焦点、键鼠手感和动画。

## 6. AOT 与回归门禁

最终门禁基线为 profile 36 / schema 33：

| 验证 | 结果 |
| --- | --- |
| 5B-4A 新契约红灯 | 实现前 0/12，按预期失败 |
| 5B-4A 契约与 JSON 清单 | 13/13 通过 |
| JSON 固定清单 | 23 个文件、58/58 处 source-generated 调用、21 个 context 所有者 |
| PowerShell 语法 | 全部脚本通过解析 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| Rust workspace 测试 | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| x64 全量测试 | 2204/2204 通过 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布 / PDB 体积 | 82.0 MiB / 176.4 MiB |
| WMC1506 / WMC1510 | 0 / 1216 |
| 完整 `always-throw` | 0 |
| 5B-4A 缺失 runner / launch / settings / search / locale / script 模式 | 全部 0 |
| 不安全设置投影 / runner / script / opt-in 隔离模式 | 全部 0 |
| 5B-4A 目标源警告 / 非预期警告 | 0 / 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 审计期间源码稳定 | `true` |
| 真实 AOT 基础托管 UI 矩阵 | 通过 |

现有警告代码仍为 `CS0108`、`CS0169`、`CS0414`、`CS8601`、`CS8602` 和 `WMC1510`，均属于审计已登记基线。本阶段源文件没有新增目标警告。

## 7. 复盘与遗漏检查

实施后重新核对代码、脚本、结构化证据和旧阶段契约，发现并处理了以下遗漏：

1. 第一版真实运行暴露空私有 managed 数组的 WinRT `ItemsSource` 投影失败；已做行为等价的最小修复并纳入审计。
2. 新 runner 最初只在自身脚本中隔离 opt-in；现七个 smoke 脚本全部互相保存、清空、恢复七个 opt-in。
3. 新 evidence 写入最初未进入 JSON 精确清单；现冻结为第 23 个文件、第 58 处调用和第 21 个 context 所有者。
4. 旧阶段项目契约要求保留已完成边界的文字标识；5B-4A 项目说明现同时保留 shortcut、Explorer/Quick Access 和两类音乐音量阶段标识，避免新阶段覆盖历史门禁。
5. 搜索排序不是只核对最终值：证据要求四个产品 handler 各调用两次，并记录全部八次中间转换。
6. 设置证据不是只核对枚举值：每个分区同时要求真实窗口句柄、可见 AppWindow、XamlRoot、尺寸、当前/选中分区与可见内容。
7. runner 没有打开搜索结果、创建 Widget 或写产品 store；正式数据指纹和精确进程清理均通过。

当前没有发现阻断 5B-4A 完成的遗漏。

## 8. 未覆盖边界

以下事项仍明确未由 5B-4A 证明：

- 非空设置搜索建议、嵌套设置页和 breadcrumb 的 NativeAOT 投影；
- Widget 增删、锁定、编辑与设置保存后的重启恢复；
- Quick Capture、Todo、Glance、天气的持久化读写矩阵；
- 文件拖放、复制移动、跨卷、回收站、上下文菜单、快捷键、Picker、Shell 和媒体交互；
- 安装、覆盖升级、回滚、CRT 决策、ARM64、Store、WACK 与签名；
- 用户对实际视觉、焦点、输入与动画的人工验收。

## 9. 下一阶段建议

下一阶段仍为 5B-4B，但根据本轮真实问题拆成两个顺序批次：

1. **5B-4B1 设置深层路径与 managed collection 投影**：覆盖非空设置搜索建议、结果选择、嵌套设置页、breadcrumb，以及剩余明确赋给 WinRT `ItemsSource` 的内部 managed 集合。先以只读或隔离方式验证，只修复实际失败边界。复杂度中等。
2. **5B-4B2 组件持久化与重启恢复**：在 owned preview 根中覆盖 Widget 增删/锁定、Quick Capture、Todo、Glance、天气及相关设置保存，要求每项有写前快照、重启后验证和最终清理。复杂度高，应继续按组件拆小矩阵。

先做 5B-4B1 更合适。原因是本轮已经证明 AOT 风险首先出现在托管集合到 WinRT 控件的投影；若直接叠加持久化写入，失败时会同时混入 UI 投影、状态写入和恢复三个变量。5B-4B1 收口后再进入写入矩阵，定位和回滚边界都会更清楚。
