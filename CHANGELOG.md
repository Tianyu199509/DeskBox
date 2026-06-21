# Changelog

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
