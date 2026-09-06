# DeskBox Native AOT 阶段 4E-0 完成报告

- 日期：2026-08-21
- 范围：搜索桌面组件历史条目的 6 条 WMC1506
- 状态：代码、契约、x64 全量测试和 AOT 产物审计已完成；Debug 可见行为待人工确认

## 1. 阶段结论

4E-0 已将 `SearchWidgetContent.xaml` 中 6 处针对不可变 `SearchHistoryEntry` 的
`OneWay x:Bind` 精确改为 `OneTime`。WMC1506 从 6 降为 0，WMC1510 保持 1265，没有提前
展开下一批，也没有给 DTO 增加不必要的通知接口、bindable attribute 或 XAML 警告抑制。

本批没有修改搜索历史服务、点击/删除/清空逻辑、语言刷新、Rust 模块、COM 边界或 AOT
运行策略。AOT 主程序仍未启动。

## 2. 行为前提与实现

`SearchHistoryEntry.Query` 和 `DeleteLabel` 均为 `required init`。条目创建后不会原地修改；
`UpdateHistoryList()` 的现有行为是清空 `_recentQueries`，再根据当前历史和当前语言重新创建
每一个条目。因此以下 6 个显示值只需初始化一次：

- `Query`：主按钮 AutomationName、主按钮 Tag、显示文本、删除按钮 Tag，共 4 处；
- `DeleteLabel`：删除按钮 AutomationName 和 Tooltip，共 2 处。

语言变化会调用 `UpdateContent()`，继而调用 `UpdateHistoryList()`；历史变化、单项删除和清空
同样会重建集合。因此 `OneTime` 不会阻止查询文本或本地化删除文案刷新。

## 3. 审计门禁

AOT 审计升级为 profile 23 / schema 20，并增加以下门禁：

1. 两种旧 `Mode=OneWay` 模式必须为 0；
2. Query 的 OneTime 必须精确为 4，DeleteLabel 必须精确为 2；
3. DTO 的两个 `required init` 属性和刷新时的 clear/rebuild 结构必须保留；
4. `SearchWidgetContent.xaml/.cs` 必须保持目标文件零告警；
5. WMC1506 从全局允许警告集合移除，任何页面重新出现都会直接让审计失败；
6. WMC1510 继续允许且必须留给后续分批处理。

## 4. 验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧实现红线契约 | 4 失败 / 2 通过，符合预期 |
| 4E-0 契约 | 6/6 |
| 4E-0 + 4D-5 联合契约 | 12/12 |
| AOT 发布契约扩大验证 | 26/26 |
| 4E-0 + 4D-5 + AOT 发布契约最终复验 | 32/32 |
| DeskBox x64 全量测试 | 2073/2073 |
| `git diff --check` | 通过，仅有共享工作区既有换行提示 |
| x64 AOT 审计 | profile 23 / schema 20，通过 |

首次成功审计用时约 256.7 秒；同步阶段文档后的最终复审用时约 347.7 秒，两次源码指纹均
前后一致。最终复审产生 39 个发布文件，共 84,951,693 字节，以及 3 个分离 PDB，共
181,170,176 字节。Rust DLL 保持 ABI 2、能力掩码 255、九个必需导出，staging/publish
SHA-256 一致。

最终告警代码只剩 `CS0108`、`CS0169`、`CS0414`、`CS8601`、`CS8602` 和 `WMC1510`：

- WMC1506：0；
- WMC1510：1265；
- 4E-0 旧 OneWay 命中：0；
- OneTime 数量或不可变行为缺失：0；
- 4E-0 目标文件告警：0；
- 非预期告警：0；
- 完整 always-throw：0。

## 5. 审计期间发现的基础设施问题

第一次完整编译已成功，但结束快照遇到共享工作区新增的未跟踪文件 `备份.zip`。Git 默认将
中文路径输出为带引号的八进制转义文本，旧脚本将该显示文本直接传给 `Test-Path`，因非法路径
字符而失败。

修正只作用于审计输入：`git status` 和 `git ls-files` 增加 `core.quotepath=false`，让 Windows
PowerShell 获得真实中文路径。扩大契约验证该标志存在，并实际完成 51 个未跟踪文件的哈希。
`备份.zip` 没有被移动、删除、修改或加入发布产物；它只参与审计前后工作树指纹。

## 6. 完成后复盘

代码、生命周期和 AOT 摘要交叉核对后，没有发现需要扩大 4E-0 的遗漏：

1. 产品差异只有 6 个 binding mode，没有修改 DTO 或 code-behind；
2. Query 与 DeleteLabel 的所有显示/交互消费者均已覆盖，没有漏掉 Tag 或辅助功能文本；
3. 语言和历史刷新都会替换条目实例，OneTime 与现有生命周期一致；
4. WMC1506 全局归零，WMC1510 数量没有下降或增加；
5. 所有 4D 阶段门禁继续通过，Rust ABI 与发布路由没有变化；
6. 没有使用 `x:SuppressXamlTrimWarnings` 掩盖问题。

自动化没有代替真实 UI。Debug 启动后仍需人工确认：搜索组件能显示近期查询，点击可重新搜索，
删除按钮能移除条目，语言切换后删除按钮的 AutomationName/Tooltip 使用新语言。

## 7. 下一阶段建议

下一阶段建议为 **4E-1 PinStateIcon 编译绑定 pilot**，复杂度低：

- `Controls/PinStateIcon.xaml` 当前只有 2 条 WMC1510；
- 两条都是 `ElementName=Root` 读取同一 `UserControl.Foreground`，来源类型和生命周期明确；
- 优先改为强类型 `x:Bind Foreground, Mode=OneWay`，保留 Foreground 这个 DependencyProperty 的
  动态主题/继承更新；
- 保留现有 `IsPinned` DependencyProperty、填充/轮廓切换和所有调用方；
- 目标是 WMC1510 1265→1263，不同时处理 Quick Capture 父模板中的其他 Binding。

这一 pilot 可以验证“自定义 UserControl 自身 DependencyProperty 的 compiled binding”路径，
风险和回归面都比直接给大型 ViewModel 增加 bindable surface 更低。若编译器或运行验证表明
直接 OneWay x:Bind 不能保持 Foreground 更新，再在该控件内部增加窄范围属性同步，不使用全局
bindable attribute 或 suppression。

## 8. 后续状态

4E-1 已扩大为四个低风险叶子 XAML 的 7 条 compiled binding，并完成 profile 24 / schema 21
审计。WMC1510 从 1265 降至 1258，x64 全量测试为 2081/2081；生成代码确认 Foreground、
Markdown 自有属性和两个 TextBlock.Text 均注册 DependencyProperty 变化回调。下一批调整为
4E-2 `MusicTransportIcon` 与 `WidgetInlineEditor`，不回头修改 4E-0 的搜索历史生命周期。
