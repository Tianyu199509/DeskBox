# Changelog

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
