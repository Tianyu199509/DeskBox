# Changelog

## 1.2.6 - 2026-07-08

### English

- Improved Todo reminder reliability with per-task reminder offsets, snooze state persistence after app restart, and safer reminder grace handling for overdue tasks.
- Added native notification actions for Todo reminders, including completing a task directly from the notification and choosing snooze options from the reminder surface.
- Refined recurring Todo behavior so completed recurrence history can be folded under the active recurring task instead of filling the main list with repeated completed rows.
- Improved Todo and Quick Capture multi-select workflows, including rectangle selection from blank list space, formatted copy, drag-out text packages, and Escape-to-clear behavior.
- Improved Quick Capture tab switching responsiveness by delaying heavier list refresh work, reducing UI-thread churn, stabilizing empty-state layout, and using a softer content transition.
- Updated Quick Capture clipboard defaults so automatic clipboard capture starts disabled by default, and disabling Quick Capture also disables automatic clipboard and image capture.
- Expanded automated coverage for Todo reminders, recurring tasks, Quick Capture clipboard settings, multi-select behavior, and settings defaults.

### 中文

- 优化待办提醒可靠性：支持每条任务独立提醒偏移，应用重启后可以恢复稍后提醒状态，并改进逾期待办的提醒宽限逻辑。
- 系统通知增加待办操作能力，可直接在通知中标记完成，也可以从提醒界面选择稍后提醒。
- 优化重复待办体验：完成后的重复历史可以折叠在当前重复任务下方，避免主列表被大量已完成记录撑满。
- 补齐待办和随记的多选流程，包括从空白区域框选、格式化复制、拖拽导出文本，以及按 Esc 取消选择。
- 优化随记 Tab 切换性能：延迟较重的列表刷新，减少 UI 线程压力，稳定空状态布局，并使用更自然的内容过渡动画。
- 调整随记剪贴板默认策略：自动复制默认关闭，关闭随记功能时会联动关闭自动复制和图片复制。
- 扩展自动化测试，覆盖待办提醒、重复任务、随记剪贴板设置、多选行为和设置默认值。

## 1.2.5 - 2026-07-08

### English

- Improved the Todo widget with due times that keep hour/minute/second precision, native Windows reminder notifications, overdue suffix labels, completed-item sorting, click-to-copy, multi-select copying, and formatted clipboard output.
- Added Todo and Quick Capture drag/drop conversion so text can be moved between the two feature widgets more naturally.
- Added configurable Todo and Quick Capture tab styles, allowing each widget to use either the indicator-style tab bar or the segmented-button style.
- Added configurable top-right widget hover actions under Appearance -> Window appearance details. Users can choose 1 to 3 actions from lock position, lock size, add, more, and delete.
- Refined widget action icons with Fluent icon shapes, better lock-state icons, compact-title sizing, and clearer rendering. Resetting title styles now clears per-widget overrides so widgets follow the global title style again.
- Improved Music widget details, including a horizontal system-volume flyout, light-theme styling fixes, click-away closing, wider slider behavior, and album-ambience corner alignment with the widget corner setting.
- Added an optional file path tooltip switch under File widgets -> File display.
- Improved Direct and Microsoft Store update-channel behavior and wording, including Store-aware manual checks and clearer Direct fallback guidance.
- Expanded tests for Todo reminders, clipboard formatting, settings defaults, update behavior, and title-style reset behavior.

### 中文

- 优化待办格子：截止时间保留时分秒精度，支持 Windows 原生提醒通知、逾期标识、已完成排序、单击复制、多选复制，以及更适合粘贴到聊天和 Markdown 的复制格式。
- 支持待办和随记之间拖拽转换文本，随记文本可以拖到待办生成任务，待办也可以拖到随记保存为记录。
- 待办和随记新增可配置的顶部切换样式，可以分别选择指示条样式或分段按钮样式。
- 在「外观 -> 窗口外观细节」新增右上角悬浮按钮内容配置，可从锁定位置、锁定尺寸、新增、更多、删除中选择 1 到 3 个操作。
- 继续优化格子操作图标：替换为 Fluent 图标形态，锁定状态有独立图标，紧凑标题下图标更小更清晰；重置标题样式时会清除单个格子的覆盖值，让格子重新跟随全局标题样式。
- 优化音乐格子细节：系统音量浮窗改为横向，补齐浅色模式样式，支持点击空白区域关闭，滑杆更宽，封面氛围背景圆角会跟随格子圆角设置。
- 在「文件格子 -> 文件显示」中新增文件路径提示开关，可关闭鼠标移入文件时的完整路径提示。
- 优化 Direct 和 Microsoft Store 两个更新渠道的文案与行为，商店版使用商店更新语义，Direct 版保留更清楚的备用下载引导。
- 扩展自动化测试，覆盖待办提醒、复制格式、设置默认值、更新逻辑和标题样式重置行为。

## 1.2.4 - 2026-07-06

### English

- Fixed the in-app update installation handoff after an update has been downloaded.
- Runs `DeskBox.Updater.exe` from a detached local update-helper directory before starting the installer, so the installer can safely overwrite the DeskBox install directory.
- Updated installer packaging so old versions can update without the running updater locking `DeskBox.Updater.*`.

### 中文

- 修复应用内更新下载完成后，点击安装、确认弹窗后 DeskBox 退出但安装器没有继续执行的问题。
- 安装更新前会先把 `DeskBox.Updater.exe` 复制到本地更新缓存目录，再从缓存目录启动，避免更新助手锁住 DeskBox 安装目录。
- 调整安装包规则，旧版本通过应用内更新安装新版时，不再覆盖正在运行的 `DeskBox.Updater.*` 文件。

## 1.2.3 - 2026-07-06

### English

- Added a configurable desktop widget layer mode under Settings -> General. Users can keep the existing Dynamic behavior or switch to Desktop pinned mode.
- Improved Desktop pinned behavior so visible widgets are reattached to the desktop layer after Show Desktop / Win+D and display-topology refreshes.
- Updated Settings navigation icons to Fluent color icons and refined several Settings layouts for more consistent spacing and card hierarchy.
- Removed redundant ToggleSwitch on/off text while preserving the native WinUI switch visual style.
- Removed extra decorative icons from the widget title icon setting and update-status rows.

### 中文

- 在「设置 -> 常规」新增桌面格子层级模式，用户可以保留现有动态层级，也可以切换到桌面固定层。
- 优化桌面固定层行为，让已显示的格子在“显示桌面”/ Win+D 和显示环境刷新后继续回到桌面层。
- 设置左侧导航图标换成 Fluent 彩色图标，并继续调整设置页卡片间距和层级。
- 删除 ToggleSwitch 多余的开关文字，同时保留原生 WinUI 开关样式。
- 删除“格子标题图标”和“更新状态”行里多余的装饰图标。

## 1.2.2 - 2026-07-06

### English

- Upgraded the Direct/Inno build baseline to .NET 10 and Windows App SDK 2.2.
- Updated installer runtime dependency detection to .NET 10 Runtime x64 and Windows App Runtime 2.2 x64.

### 中文

- 将 Direct/Inno 版本的底层构建基线升级到 .NET 10 和 Windows App SDK 2.2。
- 安装器运行时依赖检测升级为 .NET 10 Runtime x64 和 Windows App Runtime 2.2 x64。

## 1.2.1 - 2026-07-05

### English

- Improved monitor-aware widget positioning for display topology changes, DPI changes, external display plug/unplug, and 1080p-to-4K display swaps. Visible widgets now restore against the current monitor work area, while hidden widgets are rechecked before being shown.
- Added configurable desktop widget title icons with color, filled mono, line mono, hidden, and localized text-label modes. Color icons are now the default for both new installs and restored default preferences.
- Added title-style selection to the file widget blank-area context menu, matching the existing title-bar menu behavior.
- Refined Settings organization by moving the widget title icon preference under Appearance -> Window appearance details.
- Aligned new-user defaults, reset defaults, and invalid-value fallbacks for animation and title-icon preferences. The default widget animation is now consistently `SlideFade`.
- Moved Quick Capture default-view normalization into the Quick Capture settings normalization path.
- Expanded automated coverage for settings defaults, widget title icon defaults, Quick Capture default-view normalization, and monitor-aware widget positioning.

### 中文

- 优化多屏和 DPI 场景下的格子定位：显示器拓扑变化、缩放变化、外接屏插拔、1080p 更换 4K 显示器时，格子会基于当前屏幕工作区重新恢复；隐藏格子在重新显示前也会重新校验位置。
- 新增桌面格子标题图标配置，支持彩色、面性单色、线性单色、隐藏和多语言文字标签模式。新安装和恢复默认设置都默认使用彩色图标。
- 文件格子空白区域右键菜单新增标题样式选择，和标题栏菜单使用同一套行为。
- 设置页继续整理：将格子标题图标偏好收进「外观 -> 窗口外观细节」。
- 对齐新用户默认值、恢复默认值和无效值兜底逻辑：动效和标题图标默认值保持一致，默认格子动效统一为 `SlideFade`。
- 将随记默认视图归一化逻辑挪回随记设置归一化路径，避免后续维护混淆。
- 扩展自动化测试覆盖：设置默认值、格子标题图标默认值、随记默认视图归一化，以及多屏感知的格子定位。

## 1.2.0 - 2026-07-02

### English

- Changed the project license from MIT to GPL-3.0-only for future source code and releases. Previously published MIT-licensed DeskBox versions remain under the MIT License.
- Completed the first large widget architecture refactor after 1.1.10: widgets now share a `WidgetShell`, content host, content factory, registry, session manager, window factory, and diagnostic path instead of keeping each widget type as a separate window implementation.
- Introduced the feature-widget foundation used by Todo, Quick Capture, Music, and future content widgets, including content providers, persisted widget kinds, lifecycle handling, positioning, z-order/session behavior, and settings integration.
- Added the Todo widget as a first-class desktop widget with local storage, task completion, filtering, inline editing, full-screen editing, custom due times, and coverage for store/view-model/content-adapter behavior.
- Added the Music widget with Windows media session integration, playback controls, playback mode switching, system volume control, responsive waveform styles, compact-card layout, long-title marquee behavior, and optional album-color ambience.
- Reworked Quick Capture on top of the newer content/widget infrastructure, with more consistent input and editing surfaces, safer recent-content refresh behavior, cached thumbnails for recent image previews, and reduced duplicate preview loading.
- Unified widget chrome and editing details across file and feature widgets: title bar metrics, title styles, inline editors, full-screen editor surfaces, hover/pressed states, empty states, tooltips, action buttons, segmented controls, and light/dark icon behavior.
- Reorganized Settings around the new architecture: feature-widget controls, appearance groups, file-widget display options, interaction/global hotkey controls, music rhythm options, Quick Capture preferences, and clearer localized labels.
- Improved managed-storage and widget lifecycle maintenance, including safer cleanup/restore paths, default managed storage handling, session persistence, and diagnostics around widget windows.
- Expanded automated coverage for the refactor with tests for content factories, widget registry/session/positioning, content window factory, Todo storage/view models, chrome mode resolution, feature-widget settings, storage cleanup, and Quick Capture thumbnail behavior.
- Updated release metadata, installer versioning, documentation, and dependency notes for the 1.2.0 build. The installer continues to check for .NET 8 Runtime x64 and Windows App Runtime 2.1.3 x64.

### 中文

- 项目授权协议从 MIT 调整为 GPL-3.0-only，适用于后续源码和版本；此前已经按 MIT 发布的 DeskBox 旧版本仍保持 MIT 授权。
- 完成 1.1.10 之后第一轮大规模格子架构重构：文件格子和功能格子开始共享 `WidgetShell`、内容宿主、内容工厂、注册表、会话管理、窗口工厂和诊断路径，不再让每类格子都维护一套孤立窗口实现。
- 建立功能格子基础设施，用于承载待办、随记、音乐以及后续内容格子：包括内容 Provider、格子类型持久化、生命周期处理、位置管理、层级/会话行为和设置页集成。
- 新增待办格子作为一等桌面格子：支持本地存储、完成状态、筛选、行内编辑、全屏编辑、自定义结束时间，并补充存储、ViewModel 和内容适配层测试。
- 新增音乐格子：接入 Windows 媒体会话，支持播放控制、播放模式切换、系统音量控制、自适应频谱、紧凑卡片布局、长歌名循环滚动和可选封面氛围取色。
- 将随记迁移到新的内容/格子基础上，统一输入和编辑体验，优化最近内容刷新，给最近图片生成缩略图缓存，并减少重复预览加载。
- 统一文件格子和功能格子的外壳与编辑细节：标题栏尺寸、标题样式、行内编辑、全屏编辑层、悬停/按下状态、空状态、tooltip、操作按钮、分段控件以及浅色/深色图标行为。
- 按新架构重新整理设置页：功能格子开关、外观分组、文件格子显示、交互/全局快捷键、音乐频谱、随记偏好和本地化标签都做了重新归类和清理。
- 强化收纳目录与格子生命周期维护：包括更安全的清理/恢复路径、默认收纳路径处理、格子会话持久化，以及窗口诊断能力。
- 扩展自动化测试覆盖：新增内容工厂、格子注册/会话/定位、内容窗口工厂、待办存储和 ViewModel、标题栏模式解析、功能格子设置、收纳清理和随记缩略图相关测试。
- 更新 1.2.0 发布元数据、安装器版本、文档和依赖说明。安装器继续检测 .NET 8 Runtime x64 和 Windows App Runtime 2.1.3 x64，缺少时可引导安装。

## 1.1.10 - 2026-06-29

### English

- Fixed Quick Capture recent clipboard monitoring after restart: clipboard event listening now initializes on the UI thread and `Refresh()` safely marshals back to the UI dispatcher when needed.
- Fixed Quick Capture list scrolling when recent content grows beyond the widget height by constraining the list area to the remaining widget space.
- Added the phase-1 widget architecture refactoring plan to document the next stable refactor path before starting the architecture work.

### 中文

- 修复随记最近复制内容在重启后偶发不自动记录的问题：剪贴板事件监听现在在 UI 线程初始化，`Refresh()` 在需要时会安全切回 UI 调度线程。
- 修复随记最近内容过多时列表无法上下滚动的问题：列表区域现在会被限制在格子剩余空间内，内部滚动可以正常工作。
- 新增第一阶段格子架构重构路线文档，作为后续正式重构前的稳定基线。

## 1.1.9 - 2026-06-29

### English

- Fixed clipboard monitoring not persisting after restart: `QuickCaptureEnabled` now defaults to `true`.
- Fixed widget z-order: widgets now follow natural Windows z-order instead of being pushed to bottom. Added 2s safety auto-restore timer.
- Fixed QuickCapture z-order consistency with WidgetWindow: both now use `ClearTopMostOnly`, `BringAllVisibleWidgetsToFront`, and 300ms deactivation guard.
- Improved QuickCapture UI: Sticky Notes style input, search moved to tab bar, expand button for full-screen editing, input only visible on Records tab.
- Fixed toggle switch text localization: deferred to `SettingsRoot.Loaded` for proper control resolution.
- Fixed tray menu font: explicit `FontFamily` fallback when `DefaultMenuFlyoutItemStyle` not found.
- Fixed widget animation: removed scale effect from SlideFade and ScaleSlide effects for pure slide-in.
- Fixed global hotkey (F7) not working after packaged install.
- Fixed delete widget crash: added `_isClosing` guard to `ApplyBackdropPreference`.
- Fixed hotkey toggle: `ShouldHideWidgetsForTrayToggle` now hides when widgets are visible.

### 中文

- 修复剪贴板监控重启后不生效：`QuickCaptureEnabled` 默认值改为 `true`。
- 修复格子 z-order：格子现在跟随 Windows 自然层级，不再被推到底层。新增 2 秒安全自动恢复定时器。
- 修复随记 z-order 与文件格子的一致性：两者都使用 `ClearTopMostOnly`、`BringAllVisibleWidgetsToFront` 和 300ms 失焦守卫。
- 优化随记 UI：便签风格输入框，搜索移到 Tab 栏，展开按钮全屏编辑，输入框仅在记录 Tab 显示。
- 修复开关文字本地化：延迟到 `SettingsRoot.Loaded` 后设置，确保控件已渲染。
- 修复托盘菜单字体：当 `DefaultMenuFlyoutItemStyle` 找不到时，显式设置 `FontFamily` 兜底。
- 修复格子动画：移除 SlideFade 和 ScaleSlide 效果的缩放，纯滑入。
- 修复全局快捷键（F7）打包后不工作。
- 修复删除格子崩溃：`ApplyBackdropPreference` 加 `_isClosing` 守卫。
- 修复快捷键切换：`ShouldHideWidgetsForTrayToggle` 在格子可见时返回隐藏。

## 1.1.8 - 2026-06-29

### English

- Fixed app not launching and tray menu losing WinUI styling on other computers: caused by `EnableMsixTooling` being disabled. Restored to `true` for proper WinUI resource resolution.
- Fixed global hotkey (F7) not working: `OnLaunched` was called multiple times, creating a new `GlobalHotkeyService` that overwrote the already-attached instance. Now reuses existing instance and adds late-attach fallback.
- Fixed toggle switch style consistency: all toggles now use `SettingToggleSwitchStyle`.
- Fixed settings window staying on top when opened from tray right-click.

### 中文

- 修复其他电脑上应用无法启动、托盘菜单丢失样式的问题：`EnableMsixTooling` 被关闭导致 WinUI 资源解析失败，恢复为 `true`。
- 修复全局快捷键（F7）不工作：`OnLaunched` 被多次调用，新创建的 `GlobalHotkeyService` 覆盖了已 attach 的实例。现在复用已有实例并添加延迟 attach 兜底。
- 修复开关样式一致性：所有开关统一使用 `SettingToggleSwitchStyle`。
- 修复从托盘右键打开设置页面后一直置顶的问题。

## 1.1.6 - 2026-06-29

### English

- Fixed settings window staying on top when opened from tray right-click menu. Removed always-on-top logic and let Windows handle z-order naturally.
- Improved widget z-order reliability: reduced safety auto-restore timer from 5s to 2s.

### 中文

- 修复从托盘右键打开设置页面后，设置页面一直置顶挡住其他窗口的问题。移除强制置顶逻辑，让 Windows 自然处理窗口层级。
- 优化格子 z-order 可靠性：安全自动恢复定时器从 5 秒缩短到 2 秒。

## 1.1.5 - 2026-06-29

### English

- Fixed clipboard monitoring not persisting after restart: changed QuickCaptureEnabled default to true so clipboard monitoring starts automatically on first install.
- Fixed widget z-order issue where widgets could get stuck on top after interaction: added 5-second safety timer that auto-restores widgets to desktop layer if they remain topmost without user interaction.
- Improved toggle switch style consistency across all settings: removed visual padding and aligned all toggles to the right edge.
- Simplified corner radius options: removed "System default" option, keeping only Small radius, Round, and Square corners. Default remains Small radius.

### 中文

- 修复剪贴板监控重启后不生效的问题：将 QuickCaptureEnabled 默认值改为 true，首次安装后自动启用剪贴板监控。
- 修复格子交互后可能卡在顶部的 z-order 问题：新增 5 秒安全定时器，如果格子在无用户交互的情况下保持顶部状态，自动恢复到桌面层。
- 统一设置界面所有开关样式：移除视觉内边距，所有开关右对齐。
- 精简圆角选项：移除"系统默认"选项，保留小圆角、圆角、直角三个选项。默认值仍为小圆角。

## 1.1.4 - 2026-06-28

### English

- Fixed widget z-order issue where widgets would stay above fullscreen browser after batch raise. Widgets now properly hide behind other windows when clicking outside.
- Optimized startup initialization: theme refresh and clipboard service now initialize in parallel, reducing launch time by 2-3 seconds.
- Added error handling to critical async event handlers (file drop, drag completion) to prevent unhandled exceptions from crashing the app.
- Added `SafeFireAndForget` helper method for safe async execution in event handlers.

### 中文

- 修复批量唤起格子后点击外部窗口时，部分格子仍留在浏览器上方的层级问题。
- 优化启动初始化：主题刷新和剪贴板服务并行初始化，启动速度提升 2-3 秒。
- 为关键异步事件处理器（文件拖放、拖拽完成）添加错误处理，防止未处理异常导致崩溃。
- 新增 `SafeFireAndForget` 辅助方法，用于事件处理器中的安全异步执行。

## 1.1.3 - 2026-06-27

### English

- Optimized widget animation performance: replaced per-frame Win32 P/Invoke opacity calls with GPU-accelerated Visual.Opacity, cached Composition Visual, and enabled Windows-native cubic bezier easing curves.
- Simplified animation settings: removed redundant direction-specific effects, added a single "Slide direction" dropdown and "Easing intensity" control with None/Light/Standard/Strong options. Direction dropdown is disabled for effects that have no slide component.
- Fixed animation effect inconsistency between file widgets and Quick Capture widgets: both now support the same set of effects with identical parameters.
- Added image thumbnail previews for image files in widgets instead of generic file type icons.
- Fixed single-click file open not working when "Double-click to open" is disabled.
- Fixed right-click triggering single-click open instead of showing the context menu.
- Fixed widget click events being consumed by box selection logic, preventing ItemClick from firing.
- Removed "Focus clicked widget only" setting due to unreliable z-order behavior across different widget types. All widgets now always show and hide together.
- Improved default settings: animation effect defaults to Fade, speed defaults to Standard.

### 中文

- 优化格子动画性能：将每帧的 Win32 P/Invoke 透明度调用替换为 GPU 加速的 Visual.Opacity，缓存 Composition Visual，启用 Windows 原生贝塞尔缓动曲线。
- 精简动画设置：移除重复的方向特定效果，新增统一的"滑动方向"下拉框和"缓动强度"控制（无/轻微/标准/强烈）。方向下拉框在无滑动成分的效果下自动禁用。
- 修复文件格子和随记格子动画效果不一致的问题，两种格子现在支持完全相同的效果和参数。
- 新增图片文件缩略图预览，替代原来的通用文件类型图标。
- 修复关闭"双击打开"后单击无法打开文件的问题。
- 修复右键点击文件时误触发单击打开而非弹出菜单的问题。
- 修复格子框选逻辑吞掉 ItemClick 事件导致点击失效的问题。
- 移除"唤起后仅保留点击的格子"设置，因不同格子类型间 z-order 行为不一致。所有格子现在统一显示和隐藏。
- 优化默认设置：动画效果默认为淡入淡出，速度默认为标准。

## 1.1.2 - 2026-06-26

### English

- Optimized Quick Capture tab switching performance: added equality guards to item view model updates, replaced O(n²) collection diffing with dictionary-based O(n) lookup, and cached tab/item action button brushes to eliminate per-switch allocations.
- Added a new setting "Focus clicked widget only" for batch widget raise behavior. When enabled, clicking one widget hides all others; when disabled (default), all widgets stay visible together.
- Fixed Z-order inconsistency during batch raise from tray: widgets no longer fall behind fullscreen applications when clicking one widget, and the previously-clicked widget no longer stays on top unexpectedly.
- Unified Z-order behavior between file widgets and Quick Capture widgets to prevent asymmetric deactivation handling.

### 中文

- 优化随记格子 Tab 切换性能：为 item ViewModel 更新添加等值守卫，将 O(n²) 的集合同步替换为基于字典的 O(n) 查找，并缓存 Tab 和操作按钮的 Brush 以消除每次切换的对象分配。
- 新增"唤起后仅保留点击的格子"设置。开启后点击一个格子，其他格子自动隐藏；关闭时（默认）所有格子保持可见。
- 修复批量唤起格子时的层级不一致问题：点击格子时其他格子不再跑到全屏应用后面，之前点过的格子也不会意外留在前台。
- 统一文件格子和随记格子的层级处理逻辑，避免两者行为不一致。

## 1.1.1 - 2026-06-26

### English

- Fixed internal dragging for shortcut files (`.lnk`) in managed widgets. DeskBox now keeps its own path-based drag metadata even when Windows cannot convert a shortcut into a `StorageItem`.

### 中文

- 修复收纳格子内快捷方式（`.lnk`）无法长按拖动的问题。即使 Windows 无法把快捷方式转换为 `StorageItem`，DeskBox 也会使用自身的路径数据继续完成格子内拖拽。

## 1.1.0 - 2026-06-26

### English

- Added drag-and-drop diagnostics in Settings with one-click repair for DeskBox compatibility flags, startup entries, and shortcuts. If Windows 10/11 cannot drag files into widgets, run this repair first.
- Improved Explorer drag/drop compatibility for managed and mapped widgets, including native shell message allowance, legacy shell format fallback, and more useful drop diagnostics.
- Fixed widget sorting stability with natural name ordering, deterministic tie-breakers, and correct insertion when new files are added while a sort mode is active.
- Improved Quick Capture text editing: saved text now opens the inline editor on double-click, while the context menu can edit text in Notepad and sync changes back.
- Changed the default tray icon style to colorful for new installs and restored defaults.
- Improved first-run onboarding so it is marked complete after the first install launch and no longer reappears just because widgets are empty.
- Improved installer and uninstall behavior: current-user install remains the default, the install folder can be changed, startup can be selected during setup, and uninstall can optionally keep or remove local DeskBox app data.

### 中文

- 新增设置内的拖拽异常诊断和一键修复，可清理 DeskBox 的兼容性标记、启动项和快捷方式。如果 Win10/Win11 遇到文件拖不进格子的问题，请先运行此修复。
- 优化资源管理器拖拽兼容，收纳格子和映射格子支持更多原生 shell 拖拽消息和旧格式兜底，并输出更完整的拖拽诊断日志。
- 修复格子内排序稳定性，使用更接近 Windows 的自然名称排序，补充稳定兜底，并确保新加入文件按当前排序方式插入。
- 优化随记文本编辑：已保存文本双击进入随记内编辑；右键可选择“在记事本中编辑”，保存关闭后会同步回随记。
- 新安装和恢复默认设置时，托盘图标默认改为彩色。
- 优化新用户引导，首次安装启动后即标记为已完成，不会因为格子为空而每次启动重复弹出；仍可在设置中手动打开。
- 优化安装和卸载体验：继续默认按当前用户安装，支持选择安装目录，安装时可选择开机自启，卸载时可选择保留或删除本地 DeskBox 应用数据。

## 1.0.9 - 2026-06-25

### English

- Reworked Settings into a cleaner Windows-style structure with fewer top-level categories, native ComboBox/NumberBox controls, toggle on/off labels, drill-in rows, and a clearer Quick Capture settings entry.
- Added widget item sorting by name, size, item type, and date modified, with per-widget persistence and repeat-click ascending/descending behavior.
- Improved widget menus by separating title-bar widget management from content-area file actions, including view switching, sorting, paste, refresh, and mapped-folder actions.
- Improved Quick Capture tabs, title buttons, hover actions, copy feedback, and shared show/restore behavior with regular widgets.
- Improved drag/drop compatibility, empty-widget drop handling, managed-vs-mapped drag captions, z-order restoration, icon hydration retries, and Chinese IME support during file/folder rename.
- Changed the installer to current-user installation by default and added automatic migration from older Program Files administrator installs to reduce Explorer drag/drop permission conflicts.
- Added clearer guidance for Explorer drag/drop failures: DeskBox should not be run as administrator, because Windows can block file drops from non-elevated Explorer windows into elevated DeskBox windows.
- Refined first-run onboarding with shorter Windows-style copy and simpler setup choices.

### 中文

- 重构设置页面结构，减少顶层分类，并统一使用原生 ComboBox、NumberBox、带“开 / 关”文字的开关、钻入式设置项和更清晰的随记设置入口。
- 新增格子内排序方式：名称、大小、项目类型、修改日期，并支持按格子保存排序状态和重复点击切换升序 / 降序。
- 优化格子菜单，将标题栏的格子管理操作和内容区的文件操作拆分得更清晰，包括视图切换、排序、粘贴、刷新和映射文件夹操作。
- 优化随记 Tab、标题栏按钮、悬浮按钮、复制反馈，以及与普通格子一致的显示 / 恢复层级行为。
- 增强拖拽兼容、空格子拖放、收纳 / 映射拖拽提示、层级恢复、图标加载重试和文件 / 文件夹重命名时的中文输入法支持。
- 补充拖拽异常排查说明：DeskBox 日常使用不应以管理员权限运行，否则 Windows 可能会阻止普通权限资源管理器向 DeskBox 拖入文件。
- 精简新用户引导文案和设置选项，更贴近 Windows 风格。

## 1.0.8 - 2026-06-24

### English

- Improved Windows 11 23H2 drag/drop compatibility by launching DeskBox after install as the original user instead of inheriting the installer elevation level.
- Improved Explorer drag/drop handling for file widgets by accepting link-style requested operations when the widget can safely resolve them into the configured managed action.
- Improved drag hover captions so managed storage widgets show "managed widget" and mapped-folder widgets show "mapped folder" as distinct targets.
- Improved Quick Capture copy feedback by replacing per-row copy bubbles with a stable bottom-centered toast.
- Improved Quick Capture clipboard writes with short automatic retries when Windows temporarily locks the clipboard, reducing first-click copy failures.
- Tightened Quick Capture title button sizing and hover action styling so More, Delete, and item actions match the regular widget controls more closely.
- Fixed Quick Capture click-to-copy feedback, copy failure messaging, and several post-1.0.7 polish issues around mapped widgets and drag prompts.

### 中文

- 优化 Windows 11 23H2 拖拽兼容性，安装完成后启动 DeskBox 时不再继承安装器管理员层级，而是回到原始用户权限。
- 优化资源管理器拖拽处理，文件格子可兼容部分 link-style 拖拽操作，并按设置中的收纳动作安全处理。
- 优化拖拽悬浮提示，收纳格子显示“收纳组件”，映射文件夹显示“映射文件夹”，目标更清楚。
- 优化随记复制反馈，移除每行内部气泡，统一改为底部居中的稳定 toast。
- 优化随记剪贴板写入，在 Windows 剪贴板被短暂占用时自动短间隔重试，减少第一次单击复制失败。
- 调整随记右上角按钮和记录悬浮按钮尺寸/样式，让更多、删除和记录操作更接近普通格子控件。
- 修复 1.0.7 后发现的随记单击复制反馈、复制失败提示、映射格子拖拽提示等细节问题。

## 1.0.7 - 2026-06-23

### English

- Improved tray and global-hotkey behavior so file widgets and Quick Capture are raised, hidden, and restored as one group.
- Added a light WidgetManager restore path that keeps DeskBox widgets together after menu interactions and restores the group only after the user moves back to another app.
- Improved full-screen app behavior: F7 can raise widgets again when they are visible but covered, and a keyboard-hook fallback prevents apps such as Axure from consuming the configured hotkey first.
- Improved widget show/hide animation with linear timing, shorter default duration, and group-aware off-screen slide distances so adjacent widgets move out consistently.
- Improved Quick Capture layout, hover actions, tab switching, copy/open behavior, image previews, and inline editing for long text.
- Added system-open behavior for Quick Capture items: single click copies with feedback, double click opens text, links, or images in the user's default app.
- Added orphan managed-storage folder management so removed widget folders can be restored, opened, moved back to Desktop, or deleted from Settings.
- Improved managed and mapped widget safety with duplicate-name guards, folder recovery handling, mapped-folder rename sync, icon refresh stability, and file-name display fixes.
- Improved performance and responsiveness around directory refresh, clipboard capture, tab switching, list rendering, and temporary topmost confirmation.

### 中文

- 优化托盘和全局快捷键行为，让文件格子和随记按同一组逻辑统一置顶、隐藏和恢复。
- 新增轻量级 WidgetManager 层级恢复入口，菜单交互后不再由单个窗口自行置底，而是由管理器统一判断整组层级。
- 优化全屏应用场景：格子可见但被外部应用遮挡时，F7 会重新置顶；同时增加快捷键钩子兜底，避免 Axure 等应用先消费快捷键。
- 优化格子显示/隐藏动画，改为线性节奏、更短默认时长，并按整组相对屏幕位置计算滑出距离，减少遮挡和割裂感。
- 优化随记布局、悬浮按钮、Tab 切换、复制/打开行为、图片预览和长文本内联编辑体验。
- 随记支持单击复制并提示成功，双击按系统默认应用打开文本、链接或图片，不再用内部编辑弹框作为双击入口。
- 新增孤立收纳文件夹管理页，已移除格子留下的收纳目录可在设置中恢复、打开、移回桌面或删除。
- 增强收纳格子和映射格子的稳定性，包括重名保护、异常文件夹恢复、映射文件夹改名同步、图标刷新稳定性和文件名显示修复。
- 优化目录刷新、剪贴板记录、Tab 切换、列表渲染和临时置顶确认等性能与响应细节。

## 1.0.6 - 2026-06-21

### English

- Added Quick Capture as an optional feature widget for local text, link, screenshot, and recent clipboard capture workflows.
- Added Quick Capture Records, Pinned, and Recent views with hover actions, compact search, drag-out support, image thumbnails, and save-to-file-widget actions.
- Added upload-friendly storage access: managed storage can be pinned to Quick Access, opened from the tray, and mirrored with folder shortcuts for file pickers.
- Improved drag/drop and clipboard behavior so file drags stay file-first, path copying is explicit, and DeskBox's own clipboard writes are ignored by Recent capture.
- Improved file widgets with custom Explorer icon refresh, filename extension display controls, shortcut-arrow settings placement, and clearer migration progress/result feedback.
- Improved tray/global-hotkey layering so widgets stay temporarily raised until the user clicks another app, and Settings can join the temporary topmost layer when opened during that state.
- Improved Quick Capture polish with scoped-search messaging, target-widget refresh/highlight after saving, compact edit dialogs, tighter tab/action layout, and theme-aligned styling.
- Improved first-run onboarding scaling for high-DPI setups and fixed several small layout, acrylic, and refresh edge cases.

### 中文

- 新增随记功能格子，用于本地保存文本、链接、截图和最近复制内容，功能可在设置中关闭。
- 随记支持记录、固定、最近三个视图，并加入悬停操作、紧凑搜索、拖出内容、图片缩略图和保存到文件格子。
- 增强上传友好入口：收纳路径可固定到快速访问，可从托盘打开，并为文件选择器保留格子文件夹快捷方式。
- 优化拖拽和剪贴板行为：文件拖拽优先保持文件格式，复制路径改为显式操作，并忽略 DeskBox 自己写入剪贴板造成的最近记录污染。
- 优化文件格子：支持资源管理器自定义图标刷新、文件后缀显示控制、快捷方式箭头设置归位，并补充迁移进度和结果反馈。
- 优化托盘和全局快捷键层级：格子临时置顶后，只有点击其他应用才恢复；此状态下打开设置页也会临时置顶。
- 优化随记细节：增加当前视图搜索提示，保存到文件格子后刷新并高亮目标文件，编辑弹窗适配小窗口，tab 和操作按钮布局更紧凑，并跟随 DeskBox 主题色。
- 优化新手引导在高 DPI 缩放下的布局，并修复若干毛玻璃、刷新和界面边界问题。

## 1.0.5 - 2026-06-18

### English

- Rebuilt first-run onboarding with a DeskBox logo intro, a five-step guide, looping right-side feature scenes, and Chinese, English, light-mode, and dark-mode support.
- Added an optional global hotkey that triggers the same show, hide, and temporary-raise flow as the tray left-click action.
- Improved Settings and tray access with managed-storage opening, Quick Access pinning, download-link actions, and maintenance controls.
- Improved storage and mapping workflows, including default storage migration, mapped shortcut sync, orphan managed-folder cleanup, and steadier drag/drop behavior.
- Removed remaining stale blur-toggle plumbing and release animation/window references more promptly after Settings or onboarding closes.

### 中文

- 重构新用户引导：加入前置 DeskBox logo 动效、五步引导、右侧循环演示场景，并适配中文、英文、浅色和深色模式。
- 新增全局快捷键，可在设置中启用，用键盘触发与托盘左键一致的显示、隐藏和临时置顶流程。
- 优化设置和托盘入口，补充打开默认收纳目录、固定到快速访问、下载链接和维护操作。
- 优化文件收纳与映射流程，包括默认收纳路径迁移、映射快捷方式同步、孤立收纳目录清理和拖拽稳定性。
- 清理旧的模糊开关残留，并在设置窗口、新用户引导关闭后更及时释放动画和窗口引用。

## 1.0.4 - 2026-06-16

### English

- Improved tray left-click behavior so raised widgets stay on top while the pointer moves, then return to desktop level only after the user clicks another non-DeskBox window.
- Added follow-up topmost confirmation when raising multiple widgets from the tray so every visible widget is brought forward consistently.
- Improved tray right-click menu positioning by anchoring the WinUI menu from the actual tray icon rectangle and keeping it out of the tray icon hit area.
- Added automatic backdrop refresh retries after widget show, tray reveal, theme, and appearance changes to recover acrylic surfaces that occasionally render as flat gray.
- Reworked Settings into a left-side navigation layout with dedicated General, Appearance, Widget layout, Animation, Storage, Interaction, Maintenance, and About sections.

### 中文

- 优化托盘左键逻辑：格子临时置顶后，移动鼠标不会触发层级恢复，只有点击其他非 DeskBox 窗口才会回到桌面层级。
- 增加多格子托盘置顶后的二次确认，确保可见格子能更稳定地被一起唤起。
- 优化托盘右键菜单定位，菜单会基于真实托盘图标位置弹出，并避开托盘图标点击区域。
- 增加毛玻璃背景自动刷新重试，在显示格子、托盘唤起、主题和外观变化后恢复偶发的灰底问题。
- 重构设置窗口为左侧导航布局，将常规、外观、格子布局、动画、文件与收纳、操作、重置与维护和关于分区展示。

## 1.0.3 - 2026-06-16

### English

- Added configurable widget show/hide animation effects and speed presets in Settings, with Chinese and English labels.
- Reworked tray animation execution to use smoother frame pacing, real window movement for slide effects, and composition-driven opacity/scale transitions.
- Reduced widget animation flicker by avoiding duplicate item transitions during mapped-folder reveal and by restoring the final visual state consistently.
- Improved tray left-click behavior so visible desktop-layer widgets hidden behind other apps are raised temporarily instead of being hidden immediately.
- Improved tray-launched Settings behavior so the Settings window opens temporarily on top and clears topmost state after focus leaves.
- Improved temporary foreground behavior for newly created widgets, widget dragging, and folder picker ownership.
- Added performance logging support and coverage for the performance logger.

### 中文

- 在设置中新增可配置的格子显示、隐藏动画效果和速度预设，并提供中文、英文标签。
- 重构托盘动画执行方式，使用更平滑的帧节奏、真实窗口移动和基于组合层的透明度、缩放过渡。
- 减少映射文件夹唤起时的格子动画闪烁，并更稳定地恢复最终视觉状态。
- 优化托盘左键逻辑：被其他应用遮挡的桌面层级格子会先被临时置顶，而不是立即隐藏。
- 优化托盘打开设置窗口时的临时置顶行为，设置窗口失焦后会清除置顶状态。
- 改进新建格子、拖动格子和文件夹选择器的前台窗口体验。
- 增加性能日志支持，并补充性能日志测试覆盖。

## 1.0.2 - 2026-06-16

### English

- Added Chinese and English localization across widgets, settings, tray menus, onboarding, dialogs, notes, empty states, and status messages.
- Added a language selector in Settings and refreshed localized text dynamically when the user changes languages.
- Reworked onboarding to expose important setup choices directly in the flow, including managed-drop behavior, the default storage path, folder mapping, and startup launch.
- Improved onboarding visuals, right-side step animations, and repeated scene playback so each step better matches the feature being introduced.
- Fixed startup-launch behavior so DeskBox starts silently to the tray after reboot instead of opening Settings.
- Improved tray behavior so right-click "Show all widgets" temporarily raises widgets just like left-clicking the tray icon.
- Improved widget show/hide animation with a unified right-to-left motion, removed per-widget cascade timing, and reduced mapped-widget flicker.
- Improved mapped-folder reveal behavior by suppressing duplicate item transitions during window animation.
- Improved light-mode styling in onboarding and settings, including text contrast, surface colors, and the active-step indicator shape.
- Improved widget selection behavior so selecting an item in one widget clears selections in other widgets.
- Improved drag-selection responsiveness and reduced repeated visual work during rectangle selection.
- Improved shortcut handling so broken `.lnk` files use the native Windows resolve/delete prompt when opened.
- Improved file operations around cut/copy, mapped folders, desktop drag-out refresh, and shell clipboard data.
- Updated README to Chinese by default, added an English README switch, and refreshed release documentation.

### 中文

- 增加中文和英文本地化，覆盖格子、设置、托盘菜单、新用户引导、对话框、提示、空状态和状态消息。
- 在设置中增加语言选择器，切换语言后动态刷新本地化文本。
- 重构新用户引导，在流程中直接暴露拖入处理方式、默认收纳路径、文件夹映射和开机自启等关键设置。
- 优化新用户引导视觉、右侧步骤动效和重复播放，让每一步更贴合对应功能。
- 修复开机自启行为，重启后 DeskBox 会静默启动到托盘，而不是打开设置窗口。
- 优化托盘行为，右键“显示全部格子”会像左键点击托盘图标一样临时置顶格子。
- 优化格子显示、隐藏动画，统一为从右向左的动作，移除每个格子的级联延迟，并减少映射格子闪烁。
- 优化映射文件夹唤起行为，在窗口动画期间抑制重复的项目过渡。
- 优化浅色模式下的新用户引导和设置样式，包括文字对比度、界面颜色和当前步骤指示器形状。
- 优化格子选中行为，在一个格子中选择项目时会清除其他格子的选择。
- 提升框选响应速度，并减少矩形选择过程中的重复视觉工作。
- 优化快捷方式处理，打开损坏的 `.lnk` 文件时使用 Windows 原生解析或删除提示。
- 优化剪切、复制、映射文件夹、拖出到桌面刷新和 Shell 剪贴板相关文件操作。
- 将 README 改为中文默认入口，增加英文 README 切换，并刷新发布文档。

## 1.0.1 - 2026-06-12

### English

- Added a Windows-native onboarding guide, with an entry in Settings for replaying it.
- Improved tray reveal behavior, widget show/hide animations, and temporary foreground behavior.
- Improved default settings, reset-to-defaults, live appearance preview, and display density controls.
- Improved widget file interactions including drag and drop, cut, rename, delete confirmation, and keyboard shortcuts.
- Fixed installer dependency detection for .NET 8 Runtime x64 and Windows App Runtime 2.1.3 x64.
- Improved installer shortcut icons and overwrite-install behavior while preserving user settings and managed files.

### 中文

- 增加 Windows 原生风格的新用户引导，并在设置中提供重新打开入口。
- 优化托盘唤起行为、格子显示隐藏动画和临时前台行为。
- 优化默认设置、恢复默认值、外观实时预览和显示密度控制。
- 优化格子文件交互，包括拖拽、剪切、重命名、删除确认和键盘快捷键。
- 修复安装器对 .NET 8 Runtime x64 和 Windows App Runtime 2.1.3 x64 的依赖检测。
- 优化安装器快捷方式图标和覆盖安装行为，并保留用户设置与收纳文件。

## 1.0.0 - 2026-06-11

### English

- Initial public test release.

### 中文

- 首个公开测试版本。
