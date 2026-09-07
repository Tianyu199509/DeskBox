# DeskBox.Contracts（宿主内部能力端口）

插件化 roadmap 阶段 3 的切点产物。**这些接口是宿主内部的依赖倒置缝
（host-internal ports），不是未来的插件公开能力 API。**

## 边界

- 它们存在的唯一目的：让宿主内代码（App 启动路径、QuickCapture、设置页……）
  依赖接口而不是互相直捣内部实现（`WidgetManager.FeatureWidgets.cs` 的三个
  穿透切点，见 roadmap §7 阶段 3）。
- 插件/扩展侧的能力契约是另一层：数据导向的 DTO 类型（Extension Model 硬
  约束，roadmap §5），不共享这些进程内接口。
- 这里的类型不进 `DeskBox.Abstractions`（那是 Host Abstractions 试点，
  保持纯度）。

## 接线顺序（v1.8 修订：先接线，后搬移）

1. `WidgetManager` 就地实现/委托这三个接口，App 与功能调用方全部改成只
   依赖接口——**功能行为必须零变化**。**（3b 已完成：App 两处 Todo 路径、
   QuickCapture 三处落盘、App 回调 switch 全部改走端口。）**
2. 依赖集稳定后（adapter 只需少量稳定宿主能力）再决定是否物理搬移实现。
   若搬移需要给 `WidgetManager` 暴露一批 internal getter/dictionary，就先
   不搬——那只是把 God Class 变成 God Class + friend class。

**验收标准**：不是 FeatureWidgets.cs 少了多少行，而是调用方（App /
QuickCapture / Todo）不再知道 `TodoWidgetContent`、`FileSurfaceContent`、
`WidgetManager` 内部字典、`App.Current` 服务——只依赖能力端口。实现侧
`IFileWidgetImportTarget` 的 CancellationToken 必须真兑现（流式
`CopyToAsync(token)`，不是开始前查一次）。

## 现有端口

| 端口 | 切点 | 语义要点 |
|---|---|---|
| `ITodoReminderPresenter` | Todo reminder 穿透 `IWidgetContent` | 无匹配 widget 时**创建** widget（保持现行为）；`TargetPresented` 是唯一成功信号 |
| `IFileWidgetImportTarget` | QuickCapture 直达 File widget 文件夹 | 是导入/sink 面，不是 drag/drop；目标=映射目录或托管目录；带 `CancellationToken` |
| `IFeatureStateEvents` | `App.Current.*` 回调 switch | 只是 enable-state 事件（非三条 lifecycle）；UI 线程 raise、订阅者异常隔离、退订自理 |
