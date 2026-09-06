# DeskBox Native AOT 阶段 5B-1 完成与复盘报告

- 报告日期：2026-08-21
- 阶段范围：x64 Native AOT 进程中的 shortcut AOT → Rust 真实边界冒烟
- 审计配置：profile 30 / summary schema 27
- 结论：5B-1 已完成，可以开放 5B-2A；本报告不代表完整 AOT 功能矩阵、安装升级或正式发布已经通过

## 1. 本阶段结果

5B-1 已证明受审计的 NativeAOT 主程序能够通过实际产品入口调用 Rust shortcut 模块，而不只是“发布目录中存在 DLL”。最终覆盖：

- 应用快捷方式创建、覆盖、读取和无 UI Resolve；
- 目标缺失时仍读取已存储元数据；
- 文件夹快捷方式创建、覆盖、工作目录与描述；
- 损坏 `.lnk` 的失败语义与缓存失效；
- 使用真实托盘 HWND 调用带 UI Resolve；
- 有效目标保持、缺失目标取消后保留、同卷移动后自动修复、确认后删除；
- AOT 运行时动态代码关闭、Rust 后端已选中、模块已加载、ABI/能力和非零模块句柄；
- AOT EXE 与 Rust DLL 的路径和 SHA-256 均与同次 profile 30 审计一致；
- 每个场景使用独立 preview 根，正式 `%LOCALAPPDATA%\DeskBox` 在场景前后保持相同元数据指纹。

普通 JIT 行为没有改变。冒烟入口只在 `DESKBOX_NATIVE_AOT` 编译中存在，并且必须同时显式设置 shortcut 场景与 AOT preview 数据根。

## 2. 实现边界

### 2.1 AOT 进程内的窄入口

`App.AotShortcutSmoke.cs` 新增显式 opt-in 环境变量 `DESKBOX_AOT_SHORTCUT_SMOKE`。入口只在 `OnLaunched completed successfully` 之后调度，并在执行前要求：

1. 当前构建是 NativeAOT；
2. `DeskBoxDataPathService.Current` 正在使用开发/预览根；
3. 显式 `DESKBOX_AOT_PREVIEW_DATA_ROOT` 与当前数据根完全相同；
4. 测试夹具只能位于 `<preview-root>\aot-shortcut-smoke\<scenario>`；
5. 只重建当前固定场景目录，不删除整个 preview 根。

Core 场景复用 `DragDropPermissionService` 和 `ShortcutHelper` 的产品写入、读取及 Resolve 入口。UI 场景复用 `_trayWindow` 的真实 HWND 和 `ResolveBrokenShortcutWithShellUi`，没有直接调用测试专用 FFI 或绕开产品分派。

结果以 source-generated `AotShortcutSmokeJsonContext` 写入 `result.json`，记录稳定步骤、进程、EXE、Rust 模块、模块句柄、ABI、能力、owner HWND 和最终快捷方式状态。

### 2.2 受控运行脚本

`run-aot-shortcut-smoke.ps1` 负责：

- 调用 profile 30 / schema 27 的严格 AOT 启动器；
- 为五个场景生成彼此独立的数据根；
- 等待结构化 `Running` / `AwaitingShellUi` / `Completed` / `Failed` 状态；
- 核对实际 EXE、Rust DLL 路径及哈希；
- 要求动态代码关闭、Rust 后端、Loaded、ABI 2、shortcut 能力位和非零模块句柄；
- 重新计算正式数据根指纹；
- 将每个成功场景写入 `.artifacts\aot-shortcut-smoke\win-x64\<scenario>\session.json`；
- 除非显式 `-KeepRunning`，无论成功、失败还是超时，都只按受审计 EXE 的完整路径清理 AOT 进程；
- 在调用结束后恢复 `DESKBOX_AOT_SHORTCUT_SMOKE` 环境变量。

## 3. 真实 AOT 场景矩阵

所有最终场景均使用同一批审计产物：

- `DeskBox.exe`：`9B381D16B13C9EB2BD8AD7DE16F06E9FDC528FE502C260FF0F83C8DAE6BDDDD3`
- `deskbox_native.dll`：`4367D50F05DA301F9F6C2BDD827D005247196A411C8A6602D9F3F5347DE0B72F`
- Rust：ABI 2、能力 255、实际加载状态 `Loaded`

| 场景 | AOT PID | 关键结果 | owner / 窗口证据 |
| --- | ---: | --- | --- |
| Core | 33556 | 应用与文件夹快捷方式创建/覆盖、读取、有效/缺失目标无 UI Resolve、损坏读取全部通过 | 不需要 UI |
| UiValid | 4904 | 有效 `.lnk` 保留原目标，结果 `ResolvedOrKept` | 托盘 owner `0x6409E6` |
| UiCancel | 34968 | 选择“否”后损坏 `.lnk` 与原存储目标保留 | 对话框 `0xAF0940` 的实际 owner 与记录值 `0x1021294` 相同 |
| UiRepair | 5868 | 同卷移动保持文件身份，Windows 分布式链接跟踪把目标更新到 `shell-ui-replacement.txt`，结果可再次读取且目标存在 | 托盘 owner `0x790E62`；不需要人工浏览按钮 |
| UiDelete | 43540 | 选择“是”后磁盘 `.lnk` 删除，结果 `ShortcutDeleted` | 对话框 `0xFC0EF8` 的实际 owner 与记录值 `0x8112CA` 相同 |

UiRepair 的含义需要明确：冻结的产品 flags 是 `SLR_UPDATE | SLR_NOSEARCH | SLR_OFFER_DELETE_WITHOUT_FILE`。`SLR_NOSEARCH` 关闭启发式搜索，但不关闭分布式链接跟踪。因此修复场景必须把原目标在同卷移动到新路径，不能“新建另一个文件后删除原目标”，也不存在需要自动点击的“浏览并修复”按钮。

## 4. 自动化与构建验证

| 项目 | 结果 |
| --- | --- |
| 5B-1 新契约红灯 | 8/8 按预期失败 |
| 5B-1 实施后契约 | 8/8 通过 |
| 5B-1 + JSON 固定清单 | 20/20 通过 |
| Rust 单元测试 | 52/52 通过 |
| x64 全量测试 | 2141/2141 通过 |
| PowerShell 语法 | Windows PowerShell 5.1 与 PowerShell 7 均通过 |
| `git diff --check` | 通过；仅报告仓库既有 LF/CRLF 提示 |
| 失败清理负向探测 | UiCancel 故意等待 10 秒超时；脚本返回失败后仓库内 AOT/Debug `DeskBox.exe` 数量为 0 |

全量测试最初为 2140/2141：JSON 固定清单发现新增 evidence context 尚未登记。清单现已从 16 个文件、49 处调用、14 个 context 所有者更新为 17 个文件、50 处调用、15 个 context 所有者。第 50 处只编入 NativeAOT、只写隔离冒烟证据；50/50 仍全部使用 source-generated `JsonTypeInfo`，默认反射没有重新开放。

## 5. 最终 x64 AOT 审计

| 项目 | 结果 |
| --- | --- |
| 审计耗时 | 229,238 ms |
| 源码在审计期间 | 稳定，前后指纹均为 `5785598FF387BE65E3CC160CA9FC0453B87F53F26363A4BD64DAD535A71F93A5` |
| 发布目录 | 39 个文件，85,072,325 bytes |
| 符号目录 | 3 个文件，181,620,736 bytes |
| 警告代码 | CS0108、CS0169、CS0414、CS8601、CS8602、WMC1510 |
| WMC1506 / WMC1510 | 0 / 1216 |
| IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 均为 0 |
| 完整 `always-throw` | 0 |
| 5B-1 缺失 runner / 缺失脚本 / 不安全模式 / 目标源告警 | 均为 0 |
| 非预期警告 / 非预期 `always-throw` | 均为 0 |
| Rust | ABI 2、能力 255、9 个必需导出，staging/publish 哈希一致 |

关键文件：

| 文件 | 大小 | SHA-256 |
| --- | ---: | --- |
| `DeskBox.exe` | 39,433,728 | `9B381D16B13C9EB2BD8AD7DE16F06E9FDC528FE502C260FF0F83C8DAE6BDDDD3` |
| `DeskBox.Updater.exe` | 2,020,352 | `2917056076606E571B994156685F4924C264352028FEB019DEEA4D8B3C259074` |
| `deskbox_native.dll` | 146,944 | `4367D50F05DA301F9F6C2BDD827D005247196A411C8A6602D9F3F5347DE0B72F` |

## 6. 正式数据与隔离边界

五个最终场景的正式根基线均为：

| 项目 | 场景前 | 场景后 |
| --- | ---: | ---: |
| 文件数 | 122 | 122 |
| 总字节数 | 303,080,019 | 303,080,019 |
| 元数据指纹 | `6778C2D06892C488F7160BBD896B32BDA1D0D2D05085BF0BDA3576E19DC5DDC4` | 相同 |

该值与 5A 报告中的快照不同，说明两阶段之间正式用户数据发生过阶段外变化；5B-1 的结论只依据每次运行开始时重新取得的基线及同次运行后的相同比较，不把旧快照误当作当前状态。

每个场景的数据根名称都带工作树哈希和场景名。正式数据、Quick Access、系统音量、安装状态和普通 JIT 配置均未被本批修改。

## 7. 完成后的复盘

本轮审计实际发现并修正三项遗漏：

1. UiRepair 最初错误地创建无关替代文件再删除原目标，无法触发 Windows 链接跟踪。改为同卷 `File.Move` 后，AOT 实际修复通过，并增加源码契约和审计门禁防止回归。
2. 冒烟脚本最初只在成功路径停止 AOT 进程，失败场景会遗留进程。清理现已进入外层 `finally`，10 秒超时负向探测确认进程被精确清理。
3. AOT evidence JSON 增加了第 50 处生产源码调用，旧固定清单按设计让全量测试失败。清单和 JSON 基线文档现已同步更新，没有排除新文件或重新启用反射。

复盘未发现 shortcut 产品调用链的第三套 Shell Link 实现、JIT 默认后端变化、正式数据旁路或 AOT 中回退 C# COM 的路径。普通 JIT 仍保留 C# oracle；AOT 编译只保留 Rust shortcut 路径。

## 8. 尚未完成的范围

5B-1 不代表以下项目已经通过：

- Explorer 托管环境启动和 Quick Access Rust 边界的实际 AOT 行为；
- 音乐默认设备、系统音量和 session 音量 getter/setter；
- 设置、搜索、Quick Capture、Todo、天气、文件组件等托管功能矩阵；
- 多语言、主题、DPI、多屏及全部拖放交互；
- 安装、原位升级、更新器、卸载、回滚、签名、WACK 和受支持 Windows 版本；
- Rust CRT 部署策略、JIT/AOT 性能、内存和体积比较；
- ARM64 AOT 与 Store 包。

这些属于后续阶段，不是 5B-1 的遗漏实现。

## 9. 下一阶段建议

下一批建议调整为 **5B-2A：Explorer 启动 + Quick Access 只读查询的 AOT → Rust 真实边界冒烟**，复杂度为中低，可以合并执行：

1. 在隔离 preview 根建立临时文件和目录；
2. 通过实际产品入口调用 Explorer 托管环境启动，核对目标被系统 Shell 接收；
3. 调用 Quick Access query，核对 AOT 进程实际加载同次审计 Rust DLL、ABI/能力和结构化结果；
4. 不执行 pin/unpin，不改变用户 Quick Access；
5. 继续复用哈希、正式数据指纹、精确进程清理和结构化证据门禁。

随后单独执行 **5B-2B：Quick Access 临时目录 pin/unpin**。该批复杂度为中等，因为会短暂改变用户 Shell 状态，必须在独立目录上执行，并把失败、超时和进程终止后的补偿性 unpin 纳入门禁，不能和只读验证混为同一证据。

完成 5B-2 后，再进入音乐 getter/setter；托管 UI 功能矩阵、安装升级/回滚和 CRT 决策继续后置。Rust `SearchCore` 仍是独立性能方向，不作为当前 AOT 功能验收的前置条件，也不恢复大规模 Binding 清理。
