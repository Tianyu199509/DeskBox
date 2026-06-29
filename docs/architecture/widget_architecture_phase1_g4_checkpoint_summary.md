# DeskBox Widget Architecture Phase 1 G4 Checkpoint Summary

更新日期：2026-06-29  
分支：`codex/widget-architecture-phase1`  
基线：`main` / `v1.1.10` / `581ebb3`  
当前检查点：G4

## 1. 本阶段结论

当前阶段可以作为第一阶段架构铺底的低风险检查点。

已经完成：

- 统一格子外壳 `WidgetShell`。
- 预留未来 `WidgetKind`。
- 新增 `WidgetConfig.Metadata`。
- 新增 `WidgetRegistry`。
- 新增 `IWidgetContent` 和 `ExistingWidgetContent`。
- 新增 `WidgetSessionManager`，只记录状态，不接管窗口行为。
- 文件格子和随记都已经接入 `WidgetShell` 外壳。
- 文件格子内容区已标记未来 `FileWidgetContent` 边界，但没有迁移。
- Host 侧已具备统一只读身份 `WidgetWindowIdentity`。
- `WidgetManager` 部分日志已开始使用统一 Host 身份。

仍然保持：

- 多窗口架构。
- 原有 F7 / 托盘 / topmost / 桌面层恢复行为。
- 原有文件拖拽、中文重命名、ESC 取消重命名、排序、安装器逻辑。

## 2. 明确未做

- 未做全屏 `DeskBoxLayerWindow`。
- 未做格子合并 / Tab。
- 未新增天气、Todo、标签、音乐、系统监控正式入口。
- 未迁移文件格子内容区 XAML。
- 未抽 `BaseWidgetWindow`。
- 未抽 `WidgetWindowLayerController`。
- 未抽 `WidgetWindowAnimationController`。
- 未让 `WidgetSessionManager` 接管层级。

## 3. 验证结果

自动验证：

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

结果：

- build：0 warnings / 0 errors
- tests：106 / 106 passed

手测通过：

- 托盘/F7 行为正常。
- 随记显示正常。
- 文件格子显示正常。
- 格子拖拽未发现异常。

## 4. 下一步建议

建议先停在 G4，不继续推进高风险窗口生命周期迁移。

下一轮如继续重构，优先级建议：

1. 纯参数/只读整理，例如 appearance 参数计算。
2. 空壳功能格子试点，验证 `WidgetKind + WidgetRegistry + WidgetShell + IWidgetContent` 链路。
3. 再评估 `FileWidgetContent`，但必须单独列拖拽、IME、F7、右键菜单验证矩阵。

暂不建议：

- `WidgetWindowLayerController`
- `WidgetWindowAnimationController`
- `BaseWidgetWindow`
- 文件格子内容区迁移
