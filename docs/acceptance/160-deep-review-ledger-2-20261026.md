# 160 深审第二轮 — 未覆盖代码域账本（R15 起，2026-10-26）

- 承接：第一轮（`160-deep-review-ledger-20261025.md`，R13 窗口 14 笔提交）已覆盖 1.6.0 设置拆分全链（D1-D20）+ 1.5.9 功能域（E1-E14）。本轮=同一流程（r1 读码走查→r2 冗余/临时方案评估→r3 历史修复对抗复审→r4 修复→r5 真机验证）走**其余代码**。
- 红线不变：零推送零发版；自动化一律隔离根；测试禁 AnyCPU；每批门=dev-restart+全量测试+quickcheck。

## F 域清单与进度

| 域 | 内容 | 状态 |
|---|---|---|
| F1 | 启动/关机序列、单实例、激活路由、AppLifecycleRecoveryWatcher、lifeline | 🔄 |
| F2 | 桌面小组件核心：WidgetManager 全 partial（ZOrder/FeatureWidgets/Groups/Storage/MoveAll/NewWidgetPlacement/CapsuleArrangement/QuickLook）+WidgetShell | 🔄 |
| F3 | 文件格家族：FileSurfaceContent 全 partial（拖拽/选择/图标/键盘/视觉）+FileItemSurface+叠放族+FileService.ShellTransfer+NativeDropTarget | 🔄 |
| F4 | 整理与规则：OrganizerService+DesktopAutoOrganizationWatcher+OrganizationHistory+RestoreMachineLocalPathRebase+MoveAll undo | ⬜ |
| F5 | widget 内容：TodoWidgetContent 全部+QuickCapture runtime+MusicWidgetContent(SMTC)+Weather+GlanceWidgetStore | ⬜ |
| F6 | 搜索全家：SearchPopupWindow+SearchEngineService+EverythingSearchService+SearchHotkeyService+GlobalHotkeyService 核心+ResizeGuideOverlay/snap+DesktopDoubleClick | ⬜ |
| F7 | 备份引擎+更新器：DataBackupService+CloudBackupService+BackupRuntime 主侧+AppUpdateService 核心+DeskBox.Updater 进程+installer iss | ⬜ |
| F8 | 平台层：Win32Helper+ThemeService+UiDispatch+DragDropPermissionService+DisplayTopology+JumpList+StartupService+WidgetTitleIconAssetStore+LocalizationService 核心 | ⬜ |
| F9 | 托盘+主壳杂项：App.Tray+onboarding 接线+工作集修剪+lifeline 细节 | ⬜ |

## 待 Simon 拍板（累积）

（空）

## 债登记（累积）

（空）
