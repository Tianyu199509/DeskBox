# Changelog

## 1.4.6 - 2026-08-28

紧急修复了部分系统下，格子间拖动 `.lnk` 快捷方式会被误删除并进入回收站的严重问题。

### English

DeskBox 1.4.6 is a major feature, performance, and runtime update. The notes below cover everything added or changed since 1.4.3.

#### Important before updating

- DeskBox now uses Windows App SDK and Windows App Runtime 2.4 instead of 2.2. A PC that only has the 2.2 runtime will download and install 2.4 once during a Direct-installer update; this is expected and does not remove the older shared runtime used by other applications.
- The installer downloads Windows App Runtime 2.4 only when it is missing. A restart may be requested after runtime installation. For a fully offline update, install the matching x64 or ARM64 Windows App Runtime 2.4 first.
- GitHub Direct builds now use Native AOT and no longer download or require a separate .NET 10 runtime.
- Normal in-place updates keep the existing DeskBox settings, widget layouts, Todo, Quick Capture, and managed files. The updater also pins the current-user or all-users install scope so a silent update cannot switch scope unexpectedly.
- The minimum supported system remains Windows 10 21H2 (build 19044), and the Direct installer now enforces that requirement.

#### Performance and resource use

- Added Balanced, Resource saver, and Custom performance modes under Settings > General > Performance and resources.
- Custom mode can control hidden-widget cache cleanup, visible-idle cleanup, transient-window release, icon/thumbnail/image cache budget, and individual continuous animations such as text marquee, vinyl rotation, Glance image rotation, and capsule effects.
- Hidden and inactive widgets now release recreatable UI surfaces, decoded images, icons, and thumbnails according to the selected policy. Search and other temporary windows can also release their visual trees after remaining hidden.
- Reused process-wide WinRT settings objects, shared brushes, cached window factories, batched background work, and targeted stack updates reduce repeated allocations, redundant settings fan-out, and full list rebuilds.
- File widgets, Music, and other surfaces avoid unnecessary refreshes when their data is still current, reducing idle CPU and background work without delaying real changes.
- Window animation pacing adapts to the refresh rate of the current display, with additional frame-pacing and backdrop safeguards for Windows 10.

#### Multi-display layouts, movement, and reveal

- DeskBox now stores a separate widget layout for each known monitor topology. Reconnecting a previous display arrangement restores the positions, sizes, group surfaces, and capsule placement saved for that arrangement.
- Display hot-plug, work-area, and DPI changes are stabilized before restore, and layout writes are paused during the transition so temporary coordinates do not overwrite a known layout.
- A replacement or differently scaled monitor receives a proportional in-bounds layout instead of leaving widgets off-screen.
- Hold Ctrl while dragging a widget title to move all eligible widgets on the current display as one bounded group.
- Widget snapping now works while moving as well as resizing, supports a configurable gap, and keeps screen-edge placement inside the usable work area.
- Added a Quick Reveal layer for temporarily showing widgets above other windows without permanently changing their desktop-layer behavior.

#### Hotkeys and desktop activation

- Global activation now provides ready-made choices for F7 (default), double Ctrl, Alt+Space, Win+Space, and a standalone Win-key tap, while retaining custom shortcut recording.
- Reserved Windows combinations show their system-side effects before they are enabled; modifier chords and incomplete taps are rejected so they do not trigger DeskBox accidentally.
- Added an optional double-click on a blank desktop area to show or hide all widgets. Icon clicks and distant or slow clicks are excluded.
- Quick Reveal preserves the first activating click and dismisses only for the matching desktop action, reducing lost clicks and unexpected hides.

#### File stacking 2.0 and capsule interaction

- File stacking now separates the master switch from automatic grouping. Manual stacks remain available when automatic grouping is off, and automatic stacking is off by default for new users.
- A stack can open inline or in a separate popover. Popovers support Adaptive, 3×3, and 5×5 layouts, vertical overflow scrolling, and either the widget material or a neutral acrylic style.
- Popover placement follows the source stack and current work area, adapts to item count and file-widget layout, and stays within screen edges.
- Stack popovers now share the file grid's icon size, density, filename, selection, and Ctrl+mouse-wheel sizing behavior.
- Interacting with a popover, context menu, drag operation, title editor, or close confirmation keeps a hover-expand capsule or widget group open until the interaction is finished and the pointer has left.
- Switching between stacks no longer reveals the previous stack on the first frame. Reused popover windows bind and compose the new content while hidden before being shown.
- Fixed Native AOT popovers collapsing into one clipped column, repeated opening/closing retaining excess memory, duplicate item activation, and stale first-frame content.
- Capsule settings now use the expansion behavior as the main control. New widgets expand downward by default, the Sensitive hover preset uses 100 ms expand / 200 ms collapse delays, and the Relaxed animation preset consistently uses 360 ms.
- Fixed collapsed-capsule width changes reverting after pointer release on Windows 10, and kept group close confirmations operable while a group is collapsed or hover-expanded.

#### File widgets and Windows integration

- File and stack-popover selection now follows the Windows desktop shape more closely: the highlight fills the available row or column width with a narrow horizontal gap and adapts vertically to the icon and filename.
- File names can now be hidden in icon view in addition to the existing one-line and two-line choices. Each file widget can also override the global icon size, and widgets can be resized down to 50×50.
- Dragging files can follow the Windows default copy/move decision, including cross-volume behavior and modifier-key shortcut creation. Native drop images and target descriptions are used for Explorer, folders, stacks, and file widgets.
- Shell copy and move operations show per-item progress badges while keeping source, destination, and receiving folders protected from conflicting mutations.
- Added Create shortcut, Permanently delete with confirmation and partial-result reporting, and Run as administrator for supported executable targets. DeskBox itself remains at normal user privilege.
- The More menu opens near the originating mouse pointer, with a stable button fallback for keyboard or touch. More system operations uses a Windows 10-compatible native Shell path and reports invocation failures instead of silently succeeding.
- Folder and case-only renames are committed atomically, shortcut icons resolve through Shell PIDLs, and the duplicate full-path tooltip line was removed.

#### Search powered by Everything

- File search now reads Everything's existing index over local IPC and merges file and folder results with DeskBox notes, todos, and settings in the same search window.
- Settings can detect or launch Everything, choose its executable, show connection and permission status, opt into advanced Everything syntax, and filter low-value system/cache paths.
- DeskBox includes the IPC integration component but does not bundle or install the Everything application. File search requires Everything to be installed, running, and explicitly allowed in DeskBox.
- The legacy DeskBox-maintained file index, USN tracking, Windows Index integration, and native search core were removed. DeskBox-owned leftover index data is cleaned automatically, eliminating a duplicate background index.

#### Appearance, media, and everyday details

- Widget foregrounds can follow the app theme or use light, dark, or custom text and monochrome-control colors, with an optional text edge treatment and per-widget overrides.
- Glance adds independent background-image transparency, while Music can switch between available media sessions or follow the system-selected source.
- Todo manual reordering, notification actions, Quick Capture attachments, Glance switching, and feature-widget settings were hardened for Native AOT builds.
- Desktop organization can optionally include retained folders, large files, and items beyond the quick batch, and now explains access-denied, in-use, changed, unavailable, or failed transfers separately.
- Onboarding can recommend a suitable internal non-system drive for managed files. If the selected storage drive is temporarily disconnected, widgets remain intact and recover after the drive returns.
- Weather startup uses fresh cached forecasts immediately, preserves manual locations, bounds automatic retries, and keeps refresh work off the interaction path.

#### Startup, persistence, and packaging reliability

- Startup no longer forces Explorer to create a desktop host while Windows is restoring desktop icon positions. Widget restoration proceeds immediately, while desktop-layer attachment waits for Explorer's existing icon host to stabilize.
- Auto-start now uses the per-user Run entry and appears in Windows Startup apps. Legacy task registrations are migrated when safe, and disabling DeskBox from Windows is reflected by the in-app switch.
- Fixed a Microsoft Store persistence failure that could restore settings and widget data from an older state after reopening DeskBox. Atomic replacement now retries and uses a verified backup/write-through fallback when Windows temporarily blocks destination removal.
- Native AOT compatibility fixes restore settings dropdowns, file stacks, Glance, Music, Todo, Quick Capture, image attachments, support QR images, and multiple-widget switching in Direct builds.
- Installer filenames remain `DeskBox_Setup_<version>_<arch>.exe`, preserving the update contract used by 1.4.3. Direct installers continue to be produced for x64 and ARM64.

### 中文

DeskBox 1.4.6 是一次大型功能、性能与运行环境更新。以下内容为相对 1.4.3 的全部主要变化。

#### 更新前必读

- DeskBox 使用的 Windows App SDK 与 Windows App Runtime 已从 2.2 升级到 2.4。如果电脑只有 2.2，使用官网下载的直发安装包更新时会额外下载并安装一次 2.4；这是正常升级流程，也不会删除其他应用仍在使用的旧版共享运行时。
- 安装器只在缺少 2.4 时下载。安装运行时后，少数电脑可能需要重启。完全离线更新时，请先手动安装与电脑架构一致的 x64 或 ARM64 Windows App Runtime 2.4。
- GitHub 直发版改为 Native AOT 构建，不再需要也不再下载单独的 .NET 10 运行时。
- 正常覆盖更新会沿用现有 DeskBox 设置、格子布局、待办、随记和收纳文件。更新器会固定当前用户或所有用户安装范围，避免静默更新时意外切换安装位置。
- 最低支持系统仍为 Windows 10 21H2（build 19044），直发安装器现在会明确执行这项检查。

#### 性能与资源占用

- 设置 > 常规新增“性能与资源”，提供“均衡”“节省资源”和“自定义”三种性能模式。
- 自定义模式可分别控制格子隐藏后的缓存回收、可见但闲置时的缓存回收、临时窗口释放、图标/缩略图/解码图片缓存容量，以及文字跑马灯、唱片旋转、时光图片切换、胶囊光效与粒子等持续动画。
- 隐藏和非活动格子会按所选策略释放可重建的界面、解码图片、图标和缩略图；搜索等临时窗口长期隐藏后也可释放界面树。
- 复用进程级 WinRT 设置对象、共享画刷、缓存窗口工厂、批处理后台任务，并让叠放只更新实际变化的条目，减少重复分配、设置广播和整表重建。
- 文件格子、音乐等界面会跳过数据仍然有效时的重复刷新，降低闲置 CPU 和后台工作，同时仍会及时响应真实变化。
- 窗口动画会按当前显示器刷新率调整节拍，并为 Windows 10 增加帧节奏和背景材质保护。

#### 多显示器、布局移动与快捷唤起

- DeskBox 会为不同的显示器拓扑分别保存格子布局。重新接入使用过的屏幕组合后，会恢复该组合对应的位置、尺寸、格子组表面和胶囊位置。
- 显示器热插拔、工作区变化和 DPI 变化会先等待状态稳定再恢复；切换期间暂停写入布局，避免临时坐标覆盖已经保存的布局。
- 更换显示器或缩放比例变化时，会按可用工作区比例映射布局，并把格子限制在屏幕范围内，减少格子跑到屏幕外。
- 按住 Ctrl 拖动格子标题，可以把当前显示器上的可移动格子作为一个整体移动，并确保整体不越出工作区。
- 格子吸附同时支持移动和调整尺寸，可设置相邻格子间距，并保证贴近屏幕边缘时仍在可用工作区内。
- 新增“快捷唤起层”，可临时把格子显示在其他窗口上方，不会永久改变其桌面层级行为。

#### 快捷键与桌面唤起

- 全局唤起新增 F7（默认）、双击 Ctrl、Alt+Space、Win+Space、单独按 Win 等预设，同时保留自定义快捷键录制。
- 启用会占用 Windows 行为的组合前会显示明确提示；修饰键组合、未完成按键和系统掩码会被正确排除，减少误触发。
- 新增可选的“双击桌面空白区域显示或隐藏全部格子”，点击桌面图标、间距过远或超时的两次点击不会触发。
- 快捷唤起会保留第一次用于操作格子的点击，只响应匹配的桌面动作进行收起，减少点击丢失和意外隐藏。

#### 文件叠放 2.0 与胶囊交互

- 文件叠放拆分为叠放总开关与自动叠放开关。关闭自动归组后仍可使用手动叠放；新用户默认保留叠放功能，但不会自动把文件归组。
- 点击叠放可选择“当前布局内展开”或“弹出式展开”。弹窗提供自适应、3×3、5×5 布局，内容超出后纵向滚动，并可选择跟随格子材质或使用中性亚克力样式。
- 叠放弹窗会根据来源位置、当前屏幕工作区、文件数量和格子布局自适应尺寸与位置，并保持在屏幕边缘以内。
- 叠放弹窗与文件格子共用图标大小、密度、文件名、选中状态和 Ctrl+鼠标滚轮缩放逻辑。
- 操作叠放弹窗、右键菜单、拖放、标题编辑或关闭确认时，悬停展开的胶囊和格子组会保持展开；交互结束且鼠标移出后才会收起。
- 在不同叠放间切换时，不再在首帧闪现上一次打开的内容。复用窗口会在隐藏状态下完成新内容绑定和合成后再显示。
- 修复 Native AOT 下叠放弹窗退化为单列并裁切内容、反复开关保留过多内存、同一项目重复触发以及复用窗口首帧残留等问题。
- 胶囊设置改为以“展开方式”为主控制项。新格子默认向下展开，“灵敏”悬停预设改为展开 100 ms / 收起 200 ms，“舒缓”动画统一为 360 ms。
- 修复 Windows 10 上收起状态调整胶囊宽度后松手回弹，以及格子组收起或悬停展开时无法操作关闭确认的问题。

#### 文件格子与 Windows 操作

- 文件格子和叠放弹窗的选中区域更接近 Windows 桌面：横向铺满可用列宽并保留窄间距，纵向根据图标和文件名高度自适应。
- 图标视图的文件名除了单行、双行外，还可选择隐藏。每个文件格子可以单独覆盖全局图标大小，格子最小可调整到 50×50。
- 拖入文件可选择“跟随 Windows 默认”，正确处理跨磁盘复制以及修饰键创建快捷方式；资源管理器、文件夹、叠放和文件格子使用原生拖放图像与目标说明。
- Windows Shell 复制和移动会显示逐项进度标记，并在传输期间保护来源、目标和接收文件夹，减少互相冲突的操作。
- 新增“创建快捷方式”“永久删除”和“使用管理员身份打开”。永久删除带二次确认和部分失败结果；管理员权限只用于所选目标，DeskBox 本身继续以普通用户权限运行。
- “更多”菜单会优先出现在本次点击位置附近，键盘或触控操作则回退到按钮锚点。“更多系统操作”改用兼容 Windows 10 的原生 Shell 路径，并在调用失败时报告错误。
- 文件夹重命名和仅修改大小写的重命名改为原子提交，快捷方式图标通过 Shell PIDL 解析，完整路径悬浮提示的重复行也已移除。

#### 搜索改用 Everything

- 文件搜索通过本机 IPC 读取 Everything 已有索引，并把文件、文件夹结果与 DeskBox 的随记、待办和设置合并在同一搜索窗口。
- 设置页可以检测或启动 Everything、手动选择程序、查看连接与权限状态、选择是否允许高级 Everything 语法，并过滤低价值系统与缓存路径。
- DeskBox 随包提供 IPC 集成组件，但不会捆绑或安装 Everything 应用。使用文件搜索需要自行安装并运行 Everything，并在 DeskBox 中明确授权。
- 旧的 DeskBox 自建文件索引、USN 跟踪、Windows 索引集成和原生搜索核心已移除；DeskBox 自己留下的旧索引数据会自动清理，不再维护第二份后台索引。

#### 外观、媒体与日常细节

- 格子文字和单色控件可跟随应用主题，也可选择浅色、深色或自定义颜色，并支持文字边缘效果和单个格子独立覆盖。
- 时光新增独立的背景图片透明度；音乐可在可用媒体会话之间切换，也可跟随系统当前选择的播放源。
- 待办手动排序与通知操作、随记附件、时光切换和功能格子设置在 Native AOT 版本下完成兼容加固。
- 桌面整理可自行选择是否包含文件夹、大文件和超出快速批次的项目，并分别说明无权限、占用中、预览后变化、暂不可用或传输失败等保留原因。
- 新手引导可以推荐合适的内部非系统磁盘存放收纳文件。所选存储盘临时断开时，格子保持原样，磁盘重新连接后自动恢复。
- 天气启动时优先使用仍有效的本地缓存，保留手动位置，并限制自动重试频率，刷新过程不会阻塞主要交互。

#### 启动、数据保存与安装可靠性

- 开机启动时不再强制 Explorer 创建桌面宿主，避免与 Windows 恢复桌面图标位置发生竞争。格子本身会正常恢复，桌面层挂接则等待 Explorer 已有的图标宿主稳定后再完成。
- 开机自启改用用户级 Run 项，并显示在 Windows“启动应用”中；旧计划任务会在安全时迁移，在系统中关闭 DeskBox 后应用内开关也会同步。
- 修复 Microsoft Store 版本可能在重启后恢复旧设置和旧格子数据的问题。原子替换遇到 Windows 暂时阻止删除目标文件时，会重试并使用经过校验的备份与原位写入兜底。
- Native AOT 兼容修复覆盖设置下拉框、文件叠放、时光、音乐、待办、随记、图片附件、支持二维码和多个功能格子切换。
- 安装包继续使用 `DeskBox_Setup_<版本>_<架构>.exe` 命名，保持 1.4.3 使用的更新契约；直发安装器继续提供 x64 与 ARM64 架构。

## 1.4.3 - 2026-08-19

### English

#### Windows 10 motion and materials

- Windows 10 startup now treats a rejected tray Efficiency Mode request as an optional optimization failure, so `WidgetManager` initialization and desktop organization can continue.
- Widget resize, capsule transitions, and full-widget expand or collapse keep moving the real widget windows while reducing redundant UI-thread work and repeated window-position updates.
- Resize guidance is updated more efficiently and restores the illuminated edge feedback without placing a placeholder over the widget.
- Windows 10 receives a dedicated material compatibility path. Unsupported Mica and system-corner choices are identified as Windows 11-only, while acrylic, solid, and fully transparent modes retain their Windows 10 behavior.
- Rapidly interrupted animations now hand off to the latest operation more cleanly, reducing delayed or sticky responses during repeated interaction.

#### Traditional Chinese

- Traditional Chinese (`zh-TW`) is available throughout the app and in both x64 and ARM64 installers.
- Language persistence, update notes, weather descriptions, city search, lunar calendar content, Todo, Quick Capture, and desktop organization now distinguish Simplified and Traditional Chinese.
- Traditional Chinese terminology and punctuation were reviewed for Taiwan usage, with complete resource-key and formatting-placeholder parity across all supported languages.

#### Release packages

- x64 and ARM64 are available as recommended framework-dependent installers and as larger `_Full` offline installers containing private .NET and Windows App Runtime components.

### 中文

#### Windows 10 动画与材质

- Windows 10 拒绝托盘效率模式请求时，会将其视为可选优化失败，继续完成 `WidgetManager` 初始化和桌面整理。
- 格子边缘调整、胶囊切换以及完整格子的展开和收起继续驱动真实窗口，同时减少 UI 线程上的重复计算和窗口位置更新。
- 调整尺寸时的辅助提示改为更轻量的更新方式，并恢复边缘发光反馈，不会用预占位替代真实格子。
- Windows 10 使用独立的材质兼容路径。设置会明确提示云母与系统圆角仅适用于 Windows 11，同时保留亚克力、纯色和全透明模式在 Windows 10 上的对应行为。
- 连续快速操作时，新动画会更顺畅地接替未完成的旧动画，减少延迟和粘滞感。

#### 繁體中文

- 應用程式與 x64、ARM64 安裝程式新增繁體中文（`zh-TW`）。
- 語言儲存、更新說明、天氣描述、城市搜尋、農曆內容、待辦、隨記與桌面整理均能區分簡體和繁體中文。
- 繁中詞彙與標點已按臺灣使用習慣複核，所有支援語言的資源鍵和格式化預留位置保持一致。

#### 发布包

- x64 与 ARM64 同时提供推荐的框架依赖安装包，以及内置专用 .NET 与 Windows App Runtime 的 `_Full` 离线完整包。

## 1.4.2 - 2026-08-16

### English

#### Todo and Quick Capture

- Todo checkmarks stay with the intended task under custom sorting and filtering. Unchecking an item in Completed removes only that task without clearing the next row's checkmark.
- Single-pane Todo detail now uses a clear back arrow and removes the duplicate completion control. Header actions use consistent spacing and a compact frosted surface.
- Clipboard images in Quick Capture show thumbnails in both list and detail views. Copying an image record writes the image itself back to Windows instead of a local path.
- Todo and Quick Capture share equal-size attachment tiles. Images show thumbnails, other files show file icons, and the horizontal strip reveals a remove button on hover.
- Records and Pinned keep their empty states after repeated tab changes. Pinned has its own add card and creates pinned content directly.
- Meaningful Quick Capture drafts are saved before switching records, while an empty draft no longer blocks navigation or creates a blank entry.
- Newly created Todo and Quick Capture widgets include editable localized guides. Resetting either feature restores its guide so the main functions remain easy to discover.
- Markdown tables, code, links, task lists, emphasis, and pasted images render more consistently. Text and semantic accent colors remain readable in both light and dark themes.
- Quick Capture search now opens in the tab row instead of adding another line. The field's clear button removes the query, while Cancel or Esc closes search and restores the tabs.

#### File widgets, stacks, and previews

- Manual stacks remain available when automatic stacking is turned off. Changing the members or order of an automatic stack converts it to a manual stack so later rule updates do not overwrite the change.
- Files can be dragged into a stack from File Explorer, a browser, or another file widget. Members can be removed individually, a stack can be dissolved, and a stack with one remaining member dissolves automatically.
- Dragging an item out of a widget no longer changes its sorting mode. Reordering inside a stack stays local to that stack instead of rearranging the entire file widget.
- Stack expand and collapse transitions use dedicated motion, keyboard handling, focus recovery, and clearer ownership cues. Drop highlights and pulsing borders are cleared as soon as a drag ends.
- File-type and custom stacking rules now follow the selected mode independently, avoiding unexpected groups produced by two rule sets at once.
- Files and applications opened from a file widget now launch through the Explorer desktop shell. They receive the same user environment as desktop-launched items, improving compatibility with local development tools, services, and globally installed integrations.
- When QuickLook reaches the edge of the current file widget, arrow-key navigation can continue into an adjacent visible file widget without closing the preview.

#### Capsule mode, widget groups, and desktop layer

- Capsule expansion direction can be set to Auto, Down, or Up. Fixed directions keep the title edge anchored, including after widgets are hidden and restored with F7.
- Hover auto-expand reconnects after mode changes, hiding, and restoring. Hiding a widget also clears stale hover state and the temporary drag mask.
- Widgets assigned to the desktop layer stay there when their title or empty area is clicked. Dynamic-layer widgets keep their existing temporary foreground behavior.
- Expanding, collapsing, and resizing widgets release interrupted animation work more reliably, making repeated compact-mode operations feel smoother.
- Group wheel navigation no longer stalls during rapid input or jumps two members from one effective step. One switch produces one background highlight instead of repeated flashing.
- First-to-last circular navigation remains available, so continuous wheel movement can keep cycling through the group.
- Newer navigation requests replace unfinished older ones without rolling back an already visible member. Collapsed groups also show a compact position indicator.
- Widgets recover their desktop position more reliably after interaction, display changes, or Win+D, reducing unexpected overlap above other applications.

#### Music and interface polish

- Music transport controls use filled Windows-style icons. Play, pause, previous, and next share one responsive size, while repeat and volume remain compact auxiliary controls.
- Cover mode uses a taller frosted control strip with left-aligned song information and a tighter single-line layout. Record and control modes receive matching spacing and hover treatment.
- Todo, Quick Capture, onboarding, desktop organization, and release notes use clearer frosted action surfaces with more consistent margins and compact controls.
- The floating search window's drag handle uses an opaque light or dark color, avoiding the visible center overlap caused by translucent layers.
- Feature-widget settings use the same colorful icons as widget title bars, with card spacing aligned to the rest of Settings. Several unclear settings descriptions are shorter and more direct.
- The title-bar add button opens the same widget-creation menu from file and feature widgets. Todo and Quick Capture keep their content-specific new-item actions inside their workspaces.

#### Onboarding, search, and smaller details

- The opening logo animation is slower and fades smoothly into the first onboarding step without shrinking toward the corner. The product message stays visible long enough to read.
- Onboarding moves through the main actions more directly and keeps feature-widget choices synchronized with Settings.
- Background search refreshes retain unchanged rows, selection, and resolved icons, reducing visible rebuilding and flicker.
- Weekly high and low temperatures use the same visual scale as their day labels, and several empty, hover, and compact states now follow one consistent style.

### 中文

#### 待办与随记

- 自定义排序和筛选时，待办勾选始终对应实际任务。在“已完成”中取消勾选只会移出当前任务，不会让下一项丢失勾选状态。
- 待办单列详情使用明确的返回箭头，并移除重复的完成按钮。标题与右侧操作获得更合理的空间，顶部加入紧凑磨砂背景。
- 随记剪贴板图片会在列表和详情中显示缩略图。复制图片记录时，写回 Windows 剪贴板的是图片本身，不再是本地路径。
- 待办与随记共用等尺寸附件方块。图片显示缩略图，其他文件显示文件图标，可横向滚动，鼠标移入后出现删除按钮。
- “随记”和“固定”反复切换后仍会显示正确空状态。固定页加入新增卡片，从这里创建的内容会直接固定。
- 切换到其他随记前会先保存有效草稿。空白草稿不会阻止切换，也不会产生空记录。
- 首次创建待办或随记格子时，会加入可编辑、可删除的本地化功能说明。重置数据后也会恢复对应说明。
- Markdown 表格、代码、链接、任务列表、强调文字和粘贴图片显示更加稳定。浅色与深色主题下的正文和语义色都保持清晰。
- 随记搜索会在标签栏原位置展开，不再额外占用一行。输入框内的清除按钮只清空关键词，点击“取消”或按 Esc 才会退出搜索并恢复标签栏。

#### 文件格子、叠放与预览

- 关闭自动叠放后，手动创建的叠放仍会保留。调整自动叠放的成员或顺序后，该组会转为手动叠放，后续规则更新不会覆盖用户操作。
- 支持从资源管理器、浏览器和其他文件格子直接拖入叠放。成员可以单独移出，也可以解散整个叠放，只剩一个成员时会自动解散。
- 将文件拖出格子不会意外切换排序方式。叠放内部的排序只影响当前叠放，不会改变整个文件格子的顺序。
- 叠放使用专门的展开与收起动画，并补齐键盘操作、焦点恢复和归属提示。拖放结束后，高亮和呼吸边框会立即清理。
- 文件类型与自定义叠放规则会按当前选中的模式分别生效，不再同时叠加并产生意外分组。
- 从文件格子打开文件或应用时，改由资源管理器桌面 Shell 执行。启动后的程序会获得与桌面启动一致的用户环境，本地开发工具、服务和全局扩展能够更可靠地读取已有配置。
- QuickLook 浏览到当前文件格子边界后，可以继续使用方向键切换到相邻的可见文件格子，无需关闭预览。

#### 胶囊模式、格子组与桌面层级

- 胶囊展开方向新增“自动”“向下”和“向上”三种选择。固定方向会保持标题边缘作为锚点，F7 隐藏再显示后也不会发生位置漂移。
- 切换模式、隐藏或重新显示格子后，悬停自动展开会重新挂接。隐藏格子时也会清理旧的悬停状态和临时拖动遮罩。
- 固定在桌面层的格子，点击标题或空白区域后仍会留在桌面层。动态层级继续保留原有的临时前置交互。
- 优化格子展开、收起和尺寸变化时的动画衔接，连续操作时更少出现卡顿、中断或残留动画。
- 格子组快速滚动不再出现切不动或一次跳过两个成员的情况。一次有效滚轮输入只切换一个格子，并只播放一次背景提示。
- 保留首尾循环，持续向同一方向滚动可以一直循环切换格子组成员。
- 新的切换请求会接替尚未完成的旧请求，不会把已经显示的成员切回去。收起后的格子组还会显示简洁的位置指示。
- 操作格子、切换显示器或按下 Win+D 后，格子会更可靠地回到正确桌面层级，减少意外压在其他窗口上方的情况。

#### 音乐与界面细节

- 音乐播放控件改用接近 Windows 媒体控制的面性图标。播放、暂停、上一首和下一首使用统一响应式尺寸，循环和音量继续保持较小规格。
- 封面模式采用更高的磨砂控制条、左对齐歌曲信息和紧凑单行布局。唱片与控制模式同步调整间距和悬浮效果。
- 待办、随记、新手引导、桌面整理和更新日志的操作区加入更清楚的磨砂层，并统一边距与紧凑控件尺寸。
- 悬浮搜索窗口顶部拖动手势条改为适配浅色与深色主题的纯色，避免半透明图层重叠造成中间发灰。
- 功能格子设置改用标题栏同款彩色图标，卡片间距与其他设置页保持一致，多项难以理解的说明也已缩短。
- 文件格子和全部功能格子的标题栏添加按钮统一打开格子创建菜单。待办与随记继续通过内容区内的入口新建任务或笔记。

#### 新手引导、搜索与其他细节

- 开场 Logo 动画放慢，并通过渐隐平滑进入第一步，不再向左上角缩小。产品说明会停留足够时间，方便用户读完。
- 新手引导更直接地介绍主要操作，功能格子开关会立即生效，并与设置页保持同步。
- 搜索结果后台刷新时会保留未变化的结果、选择状态和图标，减少列表重建与闪烁。
- 天气周视图的高低温与日期标签使用统一视觉比例，多处空状态、悬浮状态和紧凑控件也完成统一。

## 1.4.1 - 2026-08-12

### English

#### File widget polish

- File-widget scrollbars now appear during pointer, wheel, and keyboard activity, then hide after three seconds without interaction.
- Grid and list layouts share the same scrollbar activity tracking and release their timer when the surface is unloaded or disposed.

#### Visibility reliability

- Hide All now cancels pending automatic and topmost safety restoration before the hide animation begins.
- Desktop-layer restoration is rejected for hidden, actively hiding, or closing widgets, preventing stale callbacks from reopening windows after the user hides them.
- Application, package, x64 installer, and ARM64 installer versions are aligned on 1.4.1 / 1.4.1.0.

### 中文

#### 文件格子细节

- 文件格子的滚动条会在鼠标移动、滚轮和键盘操作时出现，连续 3 秒无操作后自动隐藏。
- 图标与列表布局共用滚动条活动跟踪；界面卸载或释放时会停止并解除对应定时器。

#### 显隐可靠性

- “全部收起”会在隐藏动画开始前取消尚未执行的自动恢复和置顶安全恢复。
- 已隐藏、正在收起或关闭中的格子会拒绝桌面层恢复，避免旧回调在用户主动隐藏后重新显示窗口。
- 应用、应用包、x64 安装器和 ARM64 安装器版本统一为 1.4.1 / 1.4.1.0。

## 1.4.0 - 2026-08-11

### English

#### Todo and Quick Capture workspaces

- Todo and Quick Capture now share a responsive master/detail foundation. Narrow widgets use a focused single-pane flow; wide widgets can keep the list and editor visible together with a persisted, adjustable splitter.
- Standalone and grouped Quick Capture hosts now use the same surface, layout policy, tab visibility, preview-line limit, material refresh, selection restore, and edit behavior.
- Todo notes and Quick Capture records gain native Markdown editing and safe preview, including formatting commands, task lists, tables, source-preserving edits, configurable Enter behavior, and compatibility with existing plain-text records.
- Quick Capture attachments can be removed directly while editing in the shared host. Pending files and persisted attachments follow the same empty-record safeguards as the standalone editor.

#### Settings and live application

- Todo and Quick Capture settings now use the same compact hierarchy: the feature switch comes first, wide-layout behavior follows it, related controls are grouped in native expanders, and related display choices use concise multi-selection menus.
- Quick Capture wide-open mode, tab visibility, list preview lines, Markdown defaults, and Enter behavior now update both active hosts instead of waiting for a reconstruction or affecting only the legacy window.
- Weather view selection is no longer overwritten by the segmented control's template default during initialization. Explicit Day or Week choices persist even when they match the global default.
- Quick Capture materials refresh correctly after recycled list containers, theme changes, view switches, and unsaved paper-style changes. Clipboard-history rows cannot inherit a saved note's paper color.

#### Capsule, groups, and desktop layer

- Smart hover expansion is rearmed after a Todo or Quick Capture member is switched into an existing group window. Real pointer movement can repair a missing routed entry event, and a deliberate dwell over title controls can expand without stealing an active click.
- Segmented tabs are realized against the final expanded width before the capsule animation starts, preventing Todo and Quick Capture tabs from appearing after the body.
- Rapid group-member wheel input is coalesced per surface, while completed live presenter swaps are protected from stale cancellation callbacks.
- Temporary title and interaction raises are tracked with generation-safe leases. The complete batch returns to the desktop layer after interaction, and stale delayed callbacks cannot release a newer raise.
- Desktop-layer ownership is reasserted during idle normalization so moving or interacting with a widget does not leave it above applications or break its Win+D desktop behavior.

#### Memory and reliability

- Hidden widgets now perform a soft cleanup after 30 seconds: dead localization targets, file metadata, icon and thumbnail caches, an unused search shell, eligible managed objects, and a high working set are released without waiting for the deep-cleanup timer.
- When widgets remain visible but DeskBox has had no foreground, pointer, settings, search, onboarding, or widget interaction for 30 seconds, visible-idle maintenance releases caches and performs allocation-aware managed collection. Any new activity restarts the idle window.
- The heavy-cleanup marker is always consumed, preventing a stale flag from permanently disabling visible-idle maintenance and compact-expansion warmup.
- Version metadata is aligned across the application, package manifest, x64 installer, and ARM64 installer at 1.4.0 / 1.4.0.0.

### 中文

#### 待办与随记工作区

- 待办与随记统一使用响应式列表/详情基础结构。窄格子采用聚焦单页流程，宽格子可以同时展示列表和编辑区，并保存可调整的分隔条位置。
- 独立随记和组内随记改为使用同一共享宿主，宽屏布局、顶部标签、列表预览行数、纸张材质、选择恢复和编辑行为保持一致。
- 待办备注与随记加入原生 Markdown 编辑和安全预览，支持格式命令、任务列表、表格、保留源文的编辑方式和可配置回车行为；已有纯文本记录继续兼容。
- 共享随记宿主补齐附件删除。未保存文件和已持久化附件都可以在编辑状态直接移除，并继续遵守空记录保护。

#### 设置与即时生效

- 待办与随记设置统一为紧凑层级：顶部先展示功能总开关，其后是宽屏布局；同类配置收进原生风琴，相关显示项使用简洁的多选菜单。
- 随记宽屏打开方式、顶部标签、列表预览行数、Markdown 默认格式和回车行为会同步更新当前共享宿主，不再只影响旧窗口或必须重建后才生效。
- 修复天气分段控件在初始化时用模板默认值覆盖真实设置的问题。用户明确选择的日视图或周视图会可靠保存，包括与全局默认相同的选择。
- 修复随记列表复用、主题变化、标签切换和未保存纸张样式下的材质刷新；剪贴板记录不会再继承其他随记的纸张颜色。

#### 胶囊、格子组与桌面层级

- 待办或随记切换进已有格子组窗口后，会重新挂接悬停自动展开。真实鼠标移动可以修复缺失的进入事件，鼠标停在标题控制区也可经过稳妥延迟后展开，同时不会抢走正在发生的点击。
- 胶囊展开前会按最终宽度预先准备顶部分段标签，避免待办和随记的标签晚于正文出现。
- 格子组的连续滚轮请求会按窗口合并，已经完成的真实内容切换也不会被旧的取消回调撤销。
- 标题点击和交互触发的临时前置改用带代次的租约管理。整批格子会在交互结束后回到桌面层，旧延迟回调无法释放后来的新状态。
- 空闲层级校正会重新确认桌面宿主关系，避免移动或操作格子后长期压在其他应用上方，或导致 Win+D 保留桌面格子的行为失效。

#### 内存与可靠性

- 全部格子隐藏 30 秒后执行轻量回收，释放无效本地化订阅、文件元数据、图标与缩略图缓存、闲置搜索壳、符合条件的托管对象和过高工作集，无需等待深度回收定时器。
- 格子仍显示时，若 DeskBox 连续 30 秒没有前台、鼠标、设置、搜索、新手引导或格子交互，也会释放缓存并按新增分配量执行托管回收；任何新操作都会重新计算空闲时间。
- 修复重度清理标记未被消费后长期阻止可见空闲回收和胶囊预热的问题。
- 应用、应用包、x64 安装器和 ARM64 安装器版本统一为 1.4.0 / 1.4.0.0。

## 1.3.9 - 2026-08-09

### English

#### Capsule mode and startup

- Smart auto-hide now synchronizes from the physical pointer when the mode is entered, retries collapses deferred by menus or interaction, and limits concurrent bounds animations during bulk state changes.
- The currently expanded capsule holds an explicit peer-layer lease so another DeskBox widget cannot cover it. Stale collapse callbacks cannot release the newer expanded widget.
- A new application session restores every enabled standalone widget and the active member of each widget group. Shutdown no longer persists process teardown as a user-requested hidden state.
- Group-title wheel navigation wraps and retains its pending member until preparation, persistence, and content switching finish.

#### Selection, drag, and keyboard parity

- Dragging from a multi-selection now preserves the full batch in file widgets, search results, standalone Quick Capture, and grouped Quick Capture.
- Internal drag payloads retain mixed file and folder paths instead of dropping folders when files are present.
- Quick Capture tab drops apply every dragged item, and Delete handles all selected Quick Capture or Todo rows.

#### Large transfers and cancellation

- Transfer UI appears during preparation, uses indeterminate progress while byte totals are unknown, and ignores stale callbacks from an earlier operation.
- Pressing Cancel immediately changes the card to an explicit canceling state with a progress indicator. The transfer pipeline reports canceling and canceled phases in order.
- Cross-volume moves use cancellable streamed copies. Same-volume moves keep their atomic path, read-only source files can be removed safely, progress callbacks are throttled for large batches, and canceled batches roll back completed work where possible.

#### First-run experience

- The onboarding flow is reduced to one introduction, two optional real exercises, and a final choice. Forced path confirmation and repeated explanation panels are removed.
- Managed file drops are explained as Move by default. File and visibility exercises complete only after their actual operation succeeds.
- The 2.5-second DeskBox logo sequence remains, while each step now pairs concise text with native icons, state diagrams, and icon-backed progress feedback.
- Application, package, x64 installer, and ARM64 installer versions are aligned on 1.3.9 / 1.3.9.0.

### 中文

#### 胶囊模式与启动恢复

- 进入悬停自动展开时会按真实鼠标位置同步状态。菜单或交互暂时阻止收起后会继续重试，批量切换时也会限制同时执行的尺寸动画。
- 当前展开的胶囊会持有独立的同级窗口层级。旧的收起回调无法清除后来展开的格子，其他 DeskBox 格子也不会盖住当前内容。
- 新的应用会话会恢复所有已启用的独立格子，以及每个格子组当前使用的成员。退出进程不再把窗口关闭误记为用户主动隐藏。
- 格子组标题滚轮支持循环切换，并会保留待切换目标，直到准备、保存和内容切换全部结束。

#### 多选拖拽与键盘操作

- 从文件格子、搜索结果、独立随记和组内随记拖出多选内容时，会保留完整选择。
- 内部拖拽同时包含文件与文件夹时，所有路径都会进入同一批载荷。
- 随记拖到标签页时会处理全部选中项。在随记或待办中按 Delete 也会处理当前全部选择。

#### 大文件进度与取消

- 准备传输时就会显示进度界面。总字节数尚未确定时使用不确定进度，也不会接收上一轮任务遗留的回调。
- 点击取消后，界面立即显示取消中和进度指示。传输层会依次报告取消中与已取消。
- 跨盘移动改为可取消的流式复制，同盘移动继续使用原子操作。只读源文件可以安全删除，大批小文件会限制进度回调频率，取消整批任务时会尽量撤回已经完成的内容。

#### 新手引导

- 引导调整为一屏介绍、两次可跳过的真实练习和结束选择，去掉强制路径确认与重复说明。
- 收纳格子的默认拖入行为明确为移动。文件练习和显隐练习只有在真实操作成功后才完成。
- DeskBox Logo 动画保留为 2.5 秒，每一步加入原生图标、状态示意和带图标的进度反馈。
- 应用、应用包、x64 安装器和 ARM64 安装器版本统一为 1.3.9 / 1.3.9.0。

## 1.3.8 - 2026-08-08

### English

#### Highlights

- Added Hindi, Spanish, French, Arabic, Bengali, and Russian language options. System-language detection, package resources, weather, update messages, onboarding, and core file-widget flows now recognize all six locales.
- Standardized product wording around file widgets, folder mapping, automatic stacks, widget groups, tray creation, and Move to Recycle Bin.
- The first file widget opens on the right, explains managed storage and folder mapping, and points users to the system tray when they need another widget.
- Fixed pasting from the desktop into a file widget, copying or cutting multiple selected files to Explorer, and stale cut-state visuals after a completed move.

#### Reliability and updates

- Startup activation, command-line fallback, and interactive launches now share one policy. Startup stays silent and tray-first, while a deliberate launch remains interactive.
- The initial file widget is created only for a genuinely new profile. Removing every file widget is respected, and settings recovery does not recreate one unexpectedly.
- Dynamic widgets remain on the desktop when using Win+D, while still raising above other applications when explicitly invoked.
- Uninstall now offers a safe choice between keeping application data for a later reinstall and permanently removing settings, widget layouts, notes, tasks, caches, logs, update files, and recovery snapshots. Files in the configured managed-storage location are always preserved.
- Localized installer, dependency, upgrade, and uninstall messages now substitute paths and item counts correctly; all six new installer languages also pass the matching locale to DeskBox on first launch.
- ARM64 updates now use their own installer URL, SHA-256, and size from the stable manifest.
- Application, package, x64 installer, and ARM64 installer versions are aligned on 1.3.8 / 1.3.8.0.

#### Localization scope

- The new language packs prioritize the main file-widget, onboarding, weather, and update experiences. Less-used detailed settings temporarily fall back to English rather than mixing in unrelated Chinese text.
- The x64 and ARM64 installers now offer the same eleven selectable interface languages as DeskBox, including localized dependency download and uninstall messages.

### 中文

#### 更新亮点

- 新增印地语、西班牙语、法语、阿拉伯语、孟加拉语和俄语。系统语言识别、应用包资源、天气、更新提示、新手流程和核心文件格子流程均已适配。
- 统一文件格子、文件夹映射、自动叠放、格子组、托盘创建和移入回收站等产品表述。
- 首个文件格子默认从屏幕右侧打开，先解释收纳存储与文件夹映射的区别，并提示用户可从系统托盘新建格子。
- 修复从桌面粘贴到格子、向资源管理器复制或剪切多个选中文件，以及剪切完成后残留错误剪切样式的问题。

#### 稳定性与更新

- 开机启动、命令行兼容参数和用户主动打开共用一套启动判断。开机启动继续静默驻留托盘，主动打开保持交互式体验。
- 默认文件格子只会为真正的新用户创建一次；用户主动删除全部文件格子后不会被再次补回，设置恢复也不会意外创建新格子。
- 使用 Win+D 时动态格子仍保留在桌面；用户主动唤起时依然可以临时显示在其他应用上方。
- 卸载时可选择保留应用数据以便日后重新安装，或彻底删除设置、格子布局、随记、待办、缓存、日志、更新文件和恢复快照；配置的收纳路径内的真实文件始终保留。
- 修复安装、依赖下载、升级和卸载提示中的路径与数量占位符；新增的 6 种安装器语言也会在首次启动时向 DeskBox 传递对应语言。
- ARM64 更新会读取独立的安装包地址、SHA-256 和大小。
- 应用、应用包、x64 安装器和 ARM64 安装器版本统一为 1.3.8 / 1.3.8.0。

#### 多语言范围

- 新增语言优先覆盖文件格子、新手流程、天气和更新等主要体验；少量不常用的详细设置暂时回退英文，避免出现中文混杂。
- x64 和 ARM64 安装器现已提供与 DeskBox 相同的 11 种可选界面语言，依赖下载和卸载提示也会随语言切换。

## 1.3.7 - 2026-08-05

### English

#### Unified File Widgets and Groups

- **One shared file surface:** standalone file widgets and grouped file widgets now run through the same `FileSurfaceContent` and unified content host. The legacy standalone file interaction tree and XAML have been removed, eliminating a major source of behavior drift.
- **Group and standalone settings stay aligned:** list details, path display, compact list spacing, title styles, context menus, QuickLook, sorting, auto stacks, and cyclic Ctrl+Tab switching now follow the same implementation.
- **File operations are more complete:** newly created folders scroll into view and enter inline naming, new and existing folders can be renamed, files can be moved or copied into a folder item, and manual ordering is restored reliably after a cold start.
- **Native drag behavior is more predictable:** shortcut drag-out uses a Shell-compatible virtual-file path and reconciles a completed move, while reorder insertion indicators use a softer transition and no longer block dropping onto a folder.

#### Hotkeys, State, and Recovery

- **Repeated global toggles are serialized:** rapid tray or hotkey presses are processed through one state transaction, preventing widget windows from being stranded off-screen or the global hotkey from becoming unresponsive after several toggles.
- **Lifecycle recovery is broader:** display topology, DPI, sleep and resume, remote-session changes, and Explorer restarts share window-state recovery and diagnostics.
- **Settings survive shutdown more reliably:** pending file-order and configuration changes are flushed before exit, recoverable snapshots protect against damaged settings, and save failures are recorded instead of silently reverting to defaults.
- **Weather view preference persists:** the selected Day or Week forecast mode is restored after an application restart.

#### Search and Interface

- **Search returns useful work sooner:** providers publish staged incremental results and failures are isolated so one unavailable source does not block the remaining search pipeline.
- **Selection and tables are clearer:** Ctrl/Shift multi-selection, rubber-band selection with edge auto-scroll, result rows, sortable columns, and header hover states have been refined for predictable batch work.
- **Windows-native visual states:** file hover and selection use theme-adaptive neutral colors with tighter corners; destructive Close actions use one softer red treatment across widgets, and file context menus have a consistent order.
- **Settings and support polish:** About uses space more efficiently, the unnecessary repository button was removed, a feedback email card was added, and diagnostics can be exported as a privacy-filtered package.

#### Installation and Updates

- **Upgrades keep one DeskBox installation:** stable installer identity and existing-path detection reuse and lock the current install directory, preventing a normal upgrade from creating a second copy.
- **The update handoff is visible:** after DeskBox closes, the downloaded installer opens normally so the user can see progress without choosing the installation path again.
- **Download failures are actionable:** the updater offers retry and official-site fallback, while long release notes open in a dedicated view instead of being clipped above the progress bar.
- **Dual-architecture distribution:** framework-dependent x64 and ARM64 installers reuse compatible .NET 10 and Windows App Runtime 2.2 installations and download only a missing architecture-matched dependency.

### 中文

#### 文件格子与格子组统一

- **共用同一套文件内容**：独立文件格子与格子组内文件格子统一由 `FileSurfaceContent` 和内容宿主承载，旧独立文件交互树与 XAML 已删除，从架构上消除两套实现逐渐不一致的问题。
- **组内与独立设置保持一致**：列表详细信息、路径显示、紧凑间距、标题样式、右键菜单、QuickLook、排序、自动叠放和可循环 Ctrl+Tab 均走同一套实现。
- **文件操作更完整**：新建文件夹会自动滚动到可见位置并进入名称输入，新旧文件夹均可重命名，文件可以移动或复制到格子内的文件夹，冷启动后也能可靠恢复手动排序。
- **原生拖放更可控**：快捷方式拖出使用兼容 Shell 的虚拟文件路径，并在移动完成后同步源状态；排序插入提示过渡更柔和，也不会再挡住拖入文件夹的操作。

#### 快捷键、状态与恢复

- **连续唤起串行处理**：短时间连续点击托盘或按全局快捷键时，所有显隐操作进入同一个状态事务，避免格子丢失到屏幕外，或多次切换后全局热键暂时失效。
- **生命周期恢复覆盖更完整**：显示器拓扑、DPI、睡眠唤醒、远程会话和资源管理器重启共用窗口状态恢复与诊断。
- **退出保存更可靠**：退出前刷新待保存的文件顺序与配置；损坏设置可以从快照恢复；保存失败会记录和提示，不再静默恢复默认值。
- **天气视图会记忆**：天气格子选择的日视图或周视图会在应用重启后恢复。

#### 搜索与界面

- **搜索更快返回可用结果**：不同来源分阶段增量发布结果，并隔离单个来源异常，某个数据源不可用时不会阻塞其余搜索流程。
- **多选与表格更清晰**：补齐 Ctrl/Shift 多选、带边缘自动滚动的框选，并优化结果行、可排序列和表头悬停状态，批量操作更可预测。
- **更接近 Windows 原生视觉**：文件悬停与选中改为明暗自适应的中性色并缩小圆角；所有格子的“关闭”使用统一且更柔和的红色，文件右键菜单顺序保持一致。
- **设置与支持细节**：关于页减少无效空白，移除不需要的开源仓库按钮，新增反馈邮箱卡片，并可导出经过隐私过滤的一键诊断包。

#### 安装与更新

- **升级只保留一个 DeskBox**：稳定的安装器标识与已有路径检测会复用并锁定当前安装目录，普通升级不会再生成第二份应用。
- **更新交接过程可见**：DeskBox 关闭后正常打开已下载的安装器，用户可以看到安装进度，同时无需重新选择路径。
- **下载失败可处理**：更新器提供重试与官网回退；较长的版本日志改在独立界面打开，不再挤在进度条上方被截断。
- **双架构发布**：x64 和 ARM64 均为框架依赖安装包，复用兼容的 .NET 10 与 Windows App Runtime 2.2，只在缺少时下载对应架构依赖。

## 1.3.6 - 2026-08-04

### English

#### Widget Group Reliability and Interaction

- **Fast tab switching is recoverable:** Ctrl+Tab is now handled once per physical key gesture, with a short cooldown for rapid separate presses. Repeated key-down messages can no longer queue a switch storm or leave the keyboard path waiting for a manual tab click to recover.
- **Faster grouped file surfaces:** inactive group members retain a bounded, reusable content cache. Rapid tab changes no longer repeatedly dispose and recreate file grids, watchers, and icon work.
- **Correct group layering:** grouped widgets now keep the same temporary foreground state as standalone widgets at startup and across merge, detach, dissolve, and member-switch transitions.
- **QuickLook works inside groups:** Space reaches the selected file surface in a group and opens the same QuickLook preview behavior as a standalone file widget.

#### File Import and Visual Polish

- **Browser and WeChat drops are reliable:** virtual browser files preserve their resolved extension when copied into a standalone widget, and the native drop bridge lets WeChat and other non-WinUI sources import into grouped file widgets.
- **Initial icon hydration is consistent:** grouped file surfaces no longer cancel their first background refresh merely because their desktop window is shown without activation. Placeholder icons are temporary, square rounded cards with a softer appearance.
- **Capsule and content transitions are smoother:** compact expansion warm-up follows the actual incoming group content, while expensive surface work is deferred during geometry transitions.

#### Desktop Organization and Release

- **Organization targets are clearer:** the preview reports the actual existing widget destination rather than misleadingly showing a new target, and placement can keep working when the visible desktop is crowded or widgets are hidden.
- **Framework-dependent installers:** x64 and ARM64 installers remain small and check the matching .NET 10 Runtime and Windows App Runtime 2.2. Missing dependencies are downloaded only when needed.

### 中文

#### 格子组可靠性与交互

- **高频 Tab 切换可恢复**：Ctrl+Tab 现在按一次物理按键手势只处理一次，并对连续独立按键做短暂冷却。重复 KeyDown 不会再堆积切换事务，也不会出现必须手动点一次 Tab 才能恢复的问题。
- **格子组文件页切换更快**：非活动成员会保留有容量上限、可复用的内容缓存；频繁切换时不再反复销毁和重建文件网格、监听器与图标加载。
- **格子组层级正确**：启动以及合并、拆分、解散、成员切换期间，格子组会与独立格子保持一致的临时前景层级。
- **组内空格预览生效**：空格可以传递到格子组内选中的文件区域，调用与独立文件格子相同的 QuickLook 预览行为。

#### 文件导入与视觉细节

- **浏览器和微信拖入更稳定**：浏览器虚拟文件复制到独立格子时会保留解析出的扩展名；微信等非 WinUI 拖放源也可以导入格子组内的文件格子。
- **首次图标加载一致**：格子组的文件页不会因桌面窗口以非激活方式显示而取消首次后台刷新。占位图标改为短暂展示的柔和正方形圆角卡片。
- **胶囊与内容切换更流畅**：胶囊展开预热会跟随实际切入的格子组内容，尺寸过渡期间会延后较重的表面工作。

#### 桌面整理与发布

- **整理目标更清晰**：预览会展示实际命中的已有格子，而不会误显示为新建目标；可见桌面拥挤或格子隐藏时也会继续寻找合适位置完成整理。
- **框架依赖安装包**：x64 与 ARM64 安装包继续保持轻量，按架构检测 .NET 10 Runtime 和 Windows App Runtime 2.2，仅在缺少时联网下载。

## 1.3.5 - 2026-08-03

> Scope note: Desktop organization, automatic organization, and widget groups were not part of 1.3.4. They are new in 1.3.5; the 1.3.4 section below remains the historical release record.

### English

#### New in 1.3.5

- **Desktop organization (new):** A responsive card preview shows the files to move, the selected destination, the items that stay on the Desktop, and the final storage location before anything runs.
- **Desktop organization controls (new):** Each card has its own organize checkbox and target selector, with readable icon previews, a destination explanation, a non-moving-items panel, and responsive window sizing.
- **Automatic organization (new):** Growing downloads, temporary/archive work, extraction, and same-path replacements wait for a stable terminal state; 100 MB is the large-file threshold and incomplete baselines are never committed.
- **Widget groups (new):** File widgets can be merged, scrolled with the title wheel, detached by dragging the title, dissolved, and normalized to the group’s standard or compact presentation with consistent z-order.
- **File dragging and sorting:** Cross-screen reorder uses a breathing insertion line and commits once on drop; standalone and grouped surfaces share the same virtualization-safe insertion calculation.
- **Folder widget synchronization:** Structured enumeration distinguishes empty, partial, unavailable, and access-denied folders; offline snapshots stay visible, refreshes preserve manual order, and watcher generations isolate stale mappings.
- **Search and USN reliability:** Root manifests, partial-scan protection, subtree cleanup, watcher recovery, and incremental USN create/delete/rename/hard-link updates prevent valid results from disappearing during outages or journal gaps.
- **Path and merge safety:** Junction, symlink, SUBST, UNC-alias, nested-mapping, merge, dissolve, and save-failure paths receive real-identity checks and rollback protection.
- **Windows compatibility and recovery:** Backdrop fallback, reduced-motion/high-contrast handling, sleep/unlock/RDP recovery, Explorer restart checks, display recovery, and startup z-order normalization improve Windows 10 and Windows 11 behavior.
- **Release notes, diagnostics, and localization:** Settings opens the latest bilingual Markdown notes in a separate window; watcher/index health is visible; five language resources and reliability tests are synchronized.

### 中文

> 范围说明：1.3.4 不包含桌面整理、自动整理和格子组；这些都是 1.3.5 新增内容。下面的 1.3.4 区域仍保留为历史版本记录。

#### 1.3.5 新增与改进

- **桌面整理（新增）**：使用响应式卡片预览要移动的文件、目标位置、不会移动的项目和最终收纳位置，执行前先看清结果。
- **整理控制（新增）**：每个卡片单独控制是否整理和目标格子，提供图标预览、收纳位置说明、不会移动项目面板和响应式窗口尺寸。
- **自动整理（新增）**：下载增长、临时/压缩包处理、解压和同路径替换会等待稳定终态；大文件限制为 100 MB，不完整 baseline 不会提交。
- **格子组（新增）**：文件格子可以合并、标题滚轮切换、长按标题拖出、解散，并统一采用组的标准/紧凑样式和临时层级。
- **文件拖动与排序**：跨屏排序使用呼吸式插入线，松开时一次提交；独立格子和组内格子共用虚拟化安全的位置计算。
- **文件夹格子同步**：结构化枚举区分空目录、部分结果、不可用和无权限；离线保留快照，刷新保留手动排序，watcher 代次隔离旧映射。
- **搜索与 USN 可靠性**：根目录清单、部分扫描保护、子树清理、watcher 恢复和 USN 创建/删除/重命名/硬链接增量，避免掉线或 journal 断档时误删结果。
- **路径与合并安全**：补充 junction、符号链接、SUBST、UNC 别名、嵌套映射以及合并/解散保存失败的真实身份检查和回滚保护。
- **Windows 兼容与恢复**：补齐背景回退、减少动态/高对比度、休眠解锁/RDP、Explorer 重启、显示器恢复和启动层级统一。
- **更新日志、诊断与多语言**：设置可在独立窗口查看最新双语 Markdown 内容，watcher/索引健康状态更可见，五种语言资源和稳定性测试同步完善。

## 1.3.4 - 2026-07-29

### English

#### Resource Lifecycle and Memory

- **Feature widgets now release their UI**: Disabling Todo, Quick Capture, Music, Weather, or Search closes the corresponding window and releases its content, view model, subscriptions, timers, and feature-owned services while preserving saved data.
- **Settings closes instead of hiding**: Closing Settings now destroys the window and releases its WinUI visual tree. Reopening Settings creates a fresh window.
- **Transient timer ownership fixed**: One-shot capsule timers now detach their handlers when they fire or are cancelled, and music timers, storyboards, and event subscriptions stop with their owning view instead of remaining rooted for the process lifetime.
- **Guarded idle maintenance**: DeskBox can compact managed/native heaps and trim resident pages after full background inactivity. A separate threshold-based maintenance pass can also run while widgets remain visible but DeskBox is not being used; foreground, pointer, resize, search, indexing, and other active work suppress collection.
- **Bounded caches**: File icons, decoded bitmaps, and metadata caches now have count and estimated-memory limits, with diagnostics for verifying their size.

#### Search Responsiveness and Index Residency

- **Search follows its feature switch**: Heavy search services are not initialized at startup when Search is disabled. Turning Search off releases its popup, hotkey registration, custom/USN indexes, history/action services, file metadata service, and icon cache.
- **Popup shell warm-up**: When Search is enabled, DeskBox prepares the empty popup shell during a low-priority idle slice so a desktop-widget click does not have to construct the full WinUI window first.
- **Open-only widget action**: Repeated search-widget clicks now open or refocus the popup and can no longer toggle it closed while queued pointer events are still arriving.
- **Window-first loading**: The native popup is shown and focused before recommendations, result icons, or an idle-unloaded index do work. Index restoration starts when the popup is invoked, not after the user types.
- **Idle index unload**: After five minutes without Search, the large resident custom index is saved and released while lightweight file-system watchers remain active. Changes are collected in a small delta map and reconciled when the popup restores the index.
- **Lower-cost indexing and ranking**: Persisted indexes are streamed instead of building a second full JSON copy; directory strings are pooled; stale scans are cancellable; fresh indexes can be reused; and searches retain only the highest-ranked candidates.
- **Visible result fallbacks**: File and folder results without a decoded Shell icon now display an appropriate fallback glyph instead of an empty icon column.

#### Capsule Mode and Window Layering

- **Idle expansion warm-up**: Collapsed widgets pre-measure their expanded layout during an idle UI slice, reducing the first hover expansion hitch without visibly opening every capsule.
- **First-hover recovery**: Smart hover expansion reads the native cursor position after startup, tray restore, and wake, so a capsule no longer needs an activating click before hover works.
- **No hover-through between capsules**: Smart expansion verifies the native pointer root window before opening. Moving inside an expanded widget can no longer trigger an overlapping collapsed capsule underneath it or let that capsule steal the foreground layer.
- **Foreground-safe hover expansion**: If a capsule is still physically above the desktop after a temporary tray/F7 raise has ended, hover expansion reorders it only among DeskBox windows and keeps the current external application in front.
- **Immediate title-bar collapse**: In click-to-toggle mode, clicking the expanded title bar collapses the widget on the next UI turn without the previous fixed 420 ms delay. Other expansion modes are unchanged.

#### Weather Redesign

- **Responsive information hierarchy**: Mini, compact, and expanded layouts were rebuilt around current conditions, compact metrics, hourly forecast, sunrise/sunset, and a scrollable Week view.
- **Standard and Rich skins**: Standard follows the selected app theme and capsule surface; Rich uses condition-aware gradients and matching compact presentation. Rich is now the default for new users and after reset.
- **Contrast across conditions**: Rich-skin text is selected from the weather backdrop rather than blindly following app light/dark mode, while interaction indicators and daylight details use palettes tuned separately for Standard and Rich surfaces.
- **Cleaner native selection**: Day/Week and current-hour selection use a small system-style bottom indicator without changing label weight or color, and labels no longer clip at compact heights.
- **Compact sunrise arc**: The previous tall divider was replaced by a lower sunrise/sunset arc with a daylight progress marker.
- **Reduced continuous effects**: Decorative perpetual weather animations were removed. Only short, state-driven feedback such as the active refresh rotation remains.
- **Forecast and symbol fixes**: Restored the multi-day forecast in the largest single-day layout and replaced the boxed fog emoji with a compatible cloud symbol.
- **Late-day hourly continuity**: MSN forecasts now continue into following days when the current day has no remaining hourly rows.
- **Reliable city selection**: City search ignores spacing, punctuation, and diacritics, ranks exact text matches before proximity, validates coordinates, and queues a fresh request when the previous city's refresh is still running.

#### Music Stability and Efficiency

- **Stable track handoff**: Bursts of media-property events are settled and generation-checked before display, keeping the previous complete track visible until the next complete title and artist are available.
- **Transient empty-state protection**: Brief empty metadata returned while changing tracks is retried instead of clearing the capsule and rebuilding its text several times.
- **Cover work is deduplicated**: Covers reload only when the session/track signature changes, decode at a bounded size, and discard stale asynchronous results.
- **Inactive animation work stops**: Title marquee and record rotation stop while the widget is hidden or collapsed and are restarted only when the expanded surface is visible. The marquee no longer leaves a flashing trailing clone while paused.
- **Lower-cost progress updates**: Expanded, collapsed, hidden, and non-seekable states use different refresh behavior instead of running the full timeline path continuously.
- **Control sizing polish**: Play is slightly larger in Controls, Record vertical, and Record horizontal layouts; previous/next, playback mode, and volume use a smaller consistent secondary size.
- **Empty-cover corners**: Fixed the soft upper corners around the empty album-art surface in Controls mode.

#### File Icons and Launching

- **Sharper large icons**: Executables and shortcuts request higher-resolution Windows Shell icon sources, fixing visibly blurred enlarged icons for apps such as packaged or custom launchers.
- **Cleaner small icons**: High-quality downsampling reduces the jagged look that appeared after improving large icon extraction.
- **Stack icon scaling**: Icons inside automatic stacks now follow the configured file-icon size.
- **Bounded thumbnail memory**: Decoded icon and thumbnail data is evicted by both item count and estimated bytes.
- **Shortcut launch compatibility**: `.lnk` files use the Windows Shell execution path, avoiding direct-resolution failures and reducing shortcut-specific crashes or long stalls.

#### Widgets and Context Menus

- **Blank-area menus**: Todo and Quick Capture blank content areas now expose the same widget menu as their title bars.
- **File-widget menu completion**: The file content menu includes Title style and Expansion mode, positioned below Auto stack and above Sort by.
- **Conditional Paste**: Paste is omitted when the clipboard does not contain content that the file widget can accept, instead of showing a disabled command.
- **Close from file content**: File and mapped-folder content menus now include the same Close widget command and second-step confirmation used by the title-bar menu, positioned above Refresh.

#### Hotkeys and Product Cleanup

- **Removed low-level keyboard hooks**: Global and Search hotkeys now use standard `RegisterHotKey`/`WM_HOTKEY` handling, avoiding intercepted D-key input, repeats, and latency.
- **RDP modifier recovery**: Modifier key-up recovery prevents Alt/Ctrl/Shift from remaining logically pressed after a Remote Desktop hotkey.
- **Correct Search hotkey lifecycle**: The Search hotkey switch refreshes registration correctly and cannot open Search while the Search widget feature is disabled.
- **Simplified hotkey settings**: Global and Search hotkeys share the top-level Shortcuts & Interaction page in compact expandable cards.
- **Opt-in first-run behavior**: New installations and reset defaults keep capsule mode and the global Search hotkey off until the user enables them.
- **Search widget title default**: Newly created Search widgets use floating title chrome by default, matching Weather and Music.
- **Image widget removed**: The legacy image-gallery widget and its associated services, settings, models, and views were removed.

#### Distribution

- **Version 1.3.4**: Application, file, assembly, MSIX, and installer versions are aligned to 1.3.4.
- **x64 and ARM64 installers**: Both packages are framework-dependent. Setup checks the matching .NET 10 Runtime and Windows App Runtime 2.2 architecture and downloads only a missing dependency.

### 中文

#### 资源生命周期与内存

- **功能格子关闭后释放界面**：关闭待办、随记、音乐、天气或搜索功能时，会关闭对应窗口并释放内容、ViewModel、订阅、定时器和功能专属服务，同时保留已保存数据。
- **设置窗口真正关闭**：关闭设置页时不再只隐藏窗口，而是销毁窗口并释放 WinUI 视觉树；下次打开时重新创建。
- **临时定时器正确释放**：胶囊一次性定时器在触发或取消时会解绑事件；音乐定时器、Storyboard 与订阅跟随所属视图停止，不再被意外保留到进程结束。
- **受保护的空闲整理**：应用完全进入后台后可整理托管堆、原生堆与工作集；即使格子仍显示，只要 DeskBox 未被操作且达到资源阈值，也可执行另一组保守维护。前台、鼠标、缩放、搜索、索引等活动会阻止回收。
- **缓存增加上限**：文件图标、解码位图和元数据缓存同时受条目数与估算内存限制，并增加可观测诊断数据。

#### 搜索响应与索引常驻

- **搜索资源与功能开关联动**：搜索未开启时，启动阶段不初始化重型搜索服务；关闭搜索会释放弹窗、快捷键注册、自定义/USN 索引、历史与操作服务、文件元数据服务和图标缓存。
- **空闲预热弹窗外壳**：搜索开启后，DeskBox 会在低优先级空闲切片中准备空弹窗，点击桌面搜索格子时无需先构造完整 WinUI 窗口。
- **搜索格子只负责打开**：连续点击搜索格子只会打开或重新聚焦弹窗，不会因为排队到达的指针事件把刚打开的窗口再次关闭。
- **窗口优先显示**：先显示并聚焦原生弹窗，再加载推荐内容、结果图标和已卸载索引；索引恢复在弹窗唤起时开始，不再等到用户输入文字。
- **索引空闲卸载**：搜索连续五分钟未使用时，保存并释放常驻的大型自定义索引，但保留轻量文件监听；期间变化进入小型增量表，下次弹窗恢复索引时再合并。
- **降低索引与排序成本**：索引改为流式写入，避免构造第二份完整 JSON；复用目录字符串；过期扫描可取消；可复用新鲜索引；搜索只保留最高排名候选。
- **结果图标占位修复**：无法解码 Shell 图标的文件和文件夹会显示对应的后备图标，不再留下空白图标列。

#### 胶囊模式与窗口层级

- **空闲预热展开布局**：收起的格子会在 UI 空闲切片中预先测量展开布局，改善第一次悬停展开的卡顿，不会在桌面上逐个可见展开。
- **首次悬停恢复**：应用启动、托盘恢复或唤醒后通过原生光标位置同步悬停状态，胶囊无需先点击即可自动展开。
- **相邻胶囊不再穿透误触**：智能展开前会核对原生指针所属窗口；在已展开格子内操作时，不会触发其下方重叠的收起胶囊，也不会让下方胶囊抢占前台层级。
- **悬停展开不越过前台应用**：托盘/F7 临时唤起结束后，如果胶囊仍在桌面上方，悬停展开只调整 DeskBox 格子之间的顺序，不会盖住当前外部前台应用。
- **标题栏立即收起**：点击切换模式下，点击展开格子的标题栏会在下一个 UI 调度立即收起，移除原来的固定 420 ms 延迟；其他展开模式不变。

#### 天气视觉重构

- **重建响应式信息层级**：迷你、紧凑和展开布局围绕当前天气、紧凑指标、逐小时预报、日出日落与可滚动周视图重新组织。
- **标准与高级皮肤**：标准皮肤跟随应用主题和标准胶囊表面；高级皮肤使用随天气变化的渐变，并同步高级胶囊样式。新用户与恢复默认后使用高级皮肤。
- **不同天气下保持对比度**：高级皮肤文字根据天气背景亮度选择，不再简单套用应用明暗模式；交互指示与日照细节也分别适配标准/高级表面。
- **原生化选中状态**：日/周和当前时段仅使用系统风格的底部小横条，不改变文字粗细与颜色，并修复紧凑高度下文字裁切。
- **收紧日出日落圆弧**：用更低的日照进度圆弧替换原先偏高的分隔视觉，并增加日照位置标记。
- **降低持续动效开销**：移除持续运行的装饰性天气动效，仅保留刷新中旋转等短时、状态驱动反馈。
- **预报与符号修复**：恢复最大单日布局中的多日预报，并用兼容性更好的云符号替换会显示成白块的雾 Emoji。
- **跨日小时预报连续显示**：MSN 当天已没有小时数据时，会继续读取后续预报日，避免日视图下方留白。
- **城市选择更可靠**：城市搜索忽略空格、标点和重音符号，精确文本优先于距离排序；同时校验坐标，并在旧城市刷新仍运行时排队刷新新城市。

#### 音乐稳定性与效率

- **切歌信息稳定交接**：媒体属性事件集中到达时先等待稳定并核对代次，在下一首完整歌曲名与歌手名可用前保留上一首完整内容。
- **保护瞬时空数据**：切歌时播放器短暂返回空信息会先重试，不再立刻清空胶囊并让文字重复重排。
- **封面工作去重**：只有媒体会话/歌曲签名变化时才重新加载封面；限制解码尺寸，并丢弃已经过期的异步结果。
- **非活跃时停止动画**：音乐格子隐藏或收起后停止跑马灯和唱片旋转，只在展开表面可见时恢复；暂停后跑马灯尾部副本也不再闪烁。
- **降低进度刷新成本**：展开、收起、隐藏和不可拖动状态使用不同刷新策略，不再持续执行完整时间轴刷新。
- **控制按钮尺寸统一**：控制、唱片竖排、唱片横排中的播放键略微放大；上一首、下一首、播放模式和音量统一使用更小的辅助尺寸。
- **空封面圆角修复**：修复控制模式空封面上方左右圆角发虚。

#### 文件图标与启动

- **大图标更清晰**：可执行文件和快捷方式会请求更高分辨率的 Windows Shell 图标源，改善放大后明显模糊的问题。
- **小图标更平滑**：改进高质量缩小采样，减少大图标优化后小尺寸出现的锯齿。
- **叠放图标同步缩放**：自动叠放中的图标会跟随文件图标尺寸设置。
- **缩略图内存有界**：图标和缩略图解码数据会按条目数与估算字节数淘汰。
- **快捷方式兼容启动**：`.lnk` 使用 Windows Shell 路径打开，避免直接解析启动带来的失败，并降低快捷方式特有的崩溃或长时间等待。

#### 格子与右键菜单

- **空白区域菜单**：待办和随记的空白内容区域现在显示与标题栏一致的格子菜单。
- **补全文件格子菜单**：文件内容区域新增“标题样式”和“展开方式”，位于“自动叠放”下方、“排序方式”上方。
- **按需显示粘贴**：剪贴板没有文件格子可接受的内容时，直接隐藏“粘贴”，不再显示置灰项。
- **内容区域可关闭格子**：文件格子和映射文件夹格子的内容菜单在“刷新”上方加入“关闭格子”，并保留与标题栏一致的二级确认。

#### 热键与功能精简

- **移除低级键盘钩子**：全局与搜索热键改为标准 `RegisterHotKey`/`WM_HOTKEY`，避免 D 键被拦截、重复输入和延迟。
- **远程桌面修饰键恢复**：热键触发后补充修饰键释放，避免 RDP 环境下 Alt/Ctrl/Shift 保持按下状态。
- **搜索热键生命周期修复**：搜索热键开关会正确刷新注册，搜索功能关闭时也无法再唤起搜索弹窗。
- **简化热键设置**：全局和搜索热键统一放到一级“快捷与交互”页面，并使用紧凑的可展开卡片。
- **首次使用按需开启**：新用户安装和恢复默认后，胶囊模式与搜索全局快捷键默认关闭，由用户主动开启。
- **搜索格子标题默认值**：新建搜索格子默认使用与天气、音乐一致的悬浮标题样式。
- **移除图片格子**：删除旧图片画廊格子以及相关服务、设置、模型和界面。

#### 发布

- **版本统一为 1.3.4**：应用、文件、程序集、MSIX 和安装器版本号保持一致。
- **x64 与 ARM64 安装包**：两个包均不内置运行时；安装器会检测匹配架构的 .NET 10 Runtime 与 Windows App Runtime 2.2，只在缺少时下载。

## 1.3.3 - 2026-07-24

### English

#### Drag & Drop Enhancements

- **WeChat file drag-drop**: Drag files and images directly from WeChat chat windows into grid items. A background polling thread reliably detects mouse release during OLE drags, bypassing WinUI 3's unreliable Drop event for non-Chromium drag sources.
- **Browser URL drag-drop**: Dragging images or file links from browsers (Chrome, Edge, Firefox) now downloads the URL to a temporary file and imports it automatically.
- **Folder drop support**: Files dragged onto a folder item within a grid are transferred into that folder.
- **Virtual file fix**: NativeDropTarget now tries `TYMED_ISTREAM` and `TYMED_HGLOBAL` separately for `FileContents`, fixing silent failures on browser virtual file drops.

#### Stack Group Management

- **Rename stacks**: Right-click a stack group header to rename it with an inline text box. Custom names persist across restarts.
- **Reorder stacks**: Move stack groups up or down via the right-click context menu.
- **Disable/restore stacks**: "Don't stack this group" dissolves a stack so its members display as loose items. Disabled groups can be restored from the same menu.

#### Weather Data Source

- **MSN Weather (default)**: Added MSN Weather API as the default data source — the same source used by the Windows weather widget. Provides current conditions, hourly and daily forecasts, sunrise/sunset, and UV index.
- **Open-Meteo fallback**: If the primary source fails, the app automatically falls back to Open-Meteo. Users can also manually select the data source in Settings → Weather.
- **Weather code mapping**: Added reverse mapping from MSN weather description text to WMO codes, so MSN-sourced data reuses the existing emoji/glyph/animation system.

#### F7 Z-Order Reliability Fix

- **Silent restore fix**: `ClearTopMostPreservingForeground` now unconditionally calls `BringWindowToFront(foreground)` instead of gating on `wasTopMost`. Previously, raised widgets used temporary TopMost (TOPMOST→NOTOPMOST), so the gate was always false — the restore state changed but the visual didn't, causing "flicker without collapse" on the next F7 press.
- **Cross-process click detection**: Replaced unreliable low-bit `GetAsyncKeyState` with a 50ms high-bit mouse sampler that detects up→down edges globally. The 200ms restore monitor now consumes an `_outsideMousePressObserved` flag, reliably detecting clicks on foreign windows — including clicks on an already-activated window.
- **Drag poll interference fix**: Reduced the drag-drop background polling timeout from 30s to 5s to minimize interference with `GetAsyncKeyState` low-bit consumption.

#### Search Widget Refinements

- **Clear history button**: Added a clear button in the search widget footer to wipe recent queries and result cards while preserving pinned favorites.
- **Live sync**: The search widget now auto-refreshes when search history changes in the popup.
- **Removed auto-generated recommendations**: The widget body shows only items the user actually opened. Auto-generated Start Menu app shortcuts are no longer shown in the widget body.
- **Simplified placeholder**: Search placeholder changed to "Search files..." for clarity.

#### UI / UX Polish

- **Tray icon label fix**: The "Black" and "White" tray icon labels were swapped — now correctly labeled.
- **Collapsed preview chevron**: Hidden the chevron icon in collapsed stack preview mode for a cleaner look.
- **Search popup**: Removed the "Recommended apps" section header; minor padding adjustment.

#### Localization

- Added new strings for weather data source, stack management, and search clear button across all five supported languages (zh-CN, en-US, ja-JP, de-DE, pt-BR).
- Fixed tray icon color labels in all languages.

### 中文

#### 拖拽增强

- **微信文件拖拽**：可直接从微信聊天窗口拖拽文件和图片到格子内。后台轮询线程可靠检测 OLE 拖拽时的鼠标释放，绕过 WinUI 3 对非 Chromium 拖拽源 Drop 事件不可靠的问题。
- **浏览器链接拖拽**：从浏览器拖拽图片或文件链接时，自动下载 URL 到临时文件并导入。
- **文件夹投放支持**：拖拽文件到格子内的文件夹项目上时，文件会直接传输到该文件夹中。
- **虚拟文件修复**：NativeDropTarget 现在分别尝试 `TYMED_ISTREAM` 和 `TYMED_HGLOBAL` 获取 `FileContents`，修复浏览器虚拟文件拖拽静默失败的问题。

#### 叠放组管理

- **重命名叠放组**：右键叠放组标题可使用内联文本框重命名，自定义名称跨重启保存。
- **叠放组排序**：通过右键菜单上移/下移叠放组。
- **取消/恢复折叠**：可选择"不再折叠此分组"，将叠放组成员显示为散列项目。已取消的分组可从同一菜单恢复。

#### 天气数据源

- **MSN 天气（默认）**：新增 MSN 天气 API 作为默认数据源，与 Windows 天气小组件同源。提供实时天气、逐小时和逐日预报、日出日落和 UV 指数。
- **Open-Meteo 备用源**：主源请求失败时自动切换到 Open-Meteo。用户也可在设置 → 天气中手动选择数据源。
- **天气代码映射**：新增 MSN 天气描述文本到 WMO 代码的反向映射，使 MSN 数据源可复用现有的 emoji/动画系统。

#### F7 层级可靠性修复

- **静默回落修复**：`ClearTopMostPreservingForeground` 现在无条件调用 `BringWindowToFront(foreground)`，不再以 `wasTopMost` 门控。此前唤起的格子使用瞬态置顶，门控恒为 false —— 回落状态变了但画面没变，导致下次按 F7 时"闪烁不收起"。
- **跨进程点击检测**：用 50ms 高位 `GetAsyncKeyState` 采样器（全局物理状态 + up→down 边沿检测）替代不可靠的低位检测。200ms 回落监视器现在消费采样器设置的 `_outsideMousePressObserved` 标志，可靠检测对外部窗口的点击 —— 包括点击已激活窗口这种 Windows 不做 Z-order 变化的情况。
- **拖拽轮询干扰修复**：拖拽后台轮询超时从 30s 缩短到 5s，减少对 `GetAsyncKeyState` 低位消费的干扰。

#### 搜索格子改进

- **清空历史按钮**：搜索格子底部新增清空按钮，可清除最近搜索和结果卡片，保留收藏项。
- **实时同步**：搜索弹窗中历史变化时，搜索格子自动刷新。
- **移除自动推荐**：格子主体现在只显示用户实际打开过的项目。自动生成的开始菜单应用快捷方式不再显示在格子主体中。
- **简化占位符**：搜索占位符改为"搜索文件..."。

#### 界面/体验打磨

- **托盘图标标签修复**："黑色"和"白色"托盘图标标签此前是反的，现已修正。
- **折叠预览箭头**：折叠叠放组预览中隐藏了箭头图标，更简洁。
- **搜索弹窗**：移除"推荐应用"分区标题；微调内边距。

#### 本地化

- 在全部五种语言（zh-CN、en-US、ja-JP、de-DE、pt-BR）中新增天气数据源、叠放组管理和搜索清空按钮的翻译字符串。
- 修复全部语言中托盘图标颜色标签颠倒的问题。

## 1.3.2 - 2026-07-23

### English

#### Search System (New)

- **Full-text desktop search**: Added a complete search infrastructure with a dedicated search popup window, global hotkey activation, and instant result display. Supports searching files, folders, applications, and settings from a single entry point.
- **USN Journal indexing**: Added `UsnJournalIndexService` that reads the NTFS USN change journal for fast, low-overhead file discovery without walking the directory tree.
- **Windows Index integration**: Added `WindowsIndexSearchService` that queries the Windows Indexing Service for content-searchable results including document metadata.
- **Search result ranking**: Added `SearchResultRanker` with a weighted scoring algorithm considering name match position, file type priority, recency, and path depth.
- **Search history**: Added `SearchHistoryService` with recent-search persistence, quick re-execution, and history clearing.
- **Recommended apps panel**: The search popup shows a recommended-apps panel with keyboard navigation (arrow keys + Enter to launch, Space to preview via QuickLook).
- **Type sort and filter**: Search results can be sorted and filtered by type (files, folders, apps, settings).
- **Search widget content**: Added `SearchWidgetContent` and `SearchWidgetContentAdapter` so search can be embedded as a widget content type.
- **Search settings section**: Added a dedicated Search settings page with hotkey configuration, index scope, result count, and recommendation toggles.

#### Multi-Language Expansion

- **Three new languages**: Added Japanese (ja-JP), German (de-DE), and Brazilian Portuguese (pt-BR) localization with 1500+ translated strings each, covering all widgets, settings, onboarding, search, tray menus, dialogs, and status messages.
- **Localization architecture rewrite**: Replaced the legacy `.resw` resource system with a JSON-based `LocalizationService` that loads embedded `Strings/{locale}.json` files at runtime, enabling easier community translation contributions and hot-reload during development.

#### Onboarding Redesign

- **Five-step focused flow**: Rebuilt the first-run guide from the previous multi-panel layout into a focused five-step flow: Welcome, Feature Overview, Storage Setup, Hotkey Configuration, and Completion.
- **Refined layout and animations**: Each step uses purpose-built entrance animations, consistent spacing, and localized content in all five supported languages.

#### Adaptive Tray Animation System

- **Hardware-adaptive controller**: Added `AdaptiveTrayAnimationController` and `HardwareAdaptiveAnimationService` that detect GPU capability and adjust animation complexity accordingly — full composition animations on discrete GPUs, simplified fallbacks on integrated graphics.
- **Batch animation driver**: Added `WidgetTrayBatchAnimationDriver` for synchronized multi-widget tray show/hide, eliminating per-widget timing drift.
- **Smart animation adapter**: Added `SmartAnimationAdapter` that bridges the adaptive controller with existing widget animation paths.
- **Synchronized scheduling**: Replaced fire-and-forget `DispatcherQueue.TryEnqueue` calls with synchronized scheduling, reducing animation tearing on rapid tray toggles.

#### Weather Improvements

- **City database expansion**: Expanded the offline `cities.json` database from ~500 to 5000+ entries covering significantly more small and medium cities worldwide.
- **Improved city search**: `CitySearchService` now uses prefix + fuzzy matching with CJK-aware tokenization for better results in Chinese, Japanese, and other non-Latin scripts.
- **Location service refinement**: Improved `WindowsLocationHelper` with better timeout handling, fallback to last-known position, and clearer error reporting.
- **Weather layout improvements**: Refined `WeatherWidgetContent` layouts for all four size modes with better spacing, icon alignment, and forecast list scrolling.

#### Installer and Updater

- **ARM64 installer**: Added `DeskBox.arm64.iss` and `DeskBox.Dependencies.arm64.iss` for native ARM64 Windows builds.
- **Reliable process kill**: Installer now uses a robust process-termination sequence before overwrite, avoiding file-lock failures.
- **Force-update enforcement**: `AppUpdateService` now enforces mandatory updates when the server flags a version as critical.
- **Helper cleanup**: Stale update-helper directories under `%LocalAppData%\DeskBox\update-helper` are cleaned up automatically.
- **Official download URLs**: Switched all download links to the official GitHub Releases channel.
- **Migration support**: Added `DeskBox.Migration.iss` for handling data migration during major version upgrades.
- **English installer language**: Added `Languages/English.isl` for proper English installer UI on non-Chinese systems.
- **Installer language selection**: The installer now shows a language-selection dialog (Chinese, English, Japanese, German, Brazilian Portuguese) pre-selected to the system locale. The chosen language is written to `HKCU\Software\DeskBox\InstallLanguage`, and DeskBox uses it as the default app language on first run (a manual in-app change still wins).
- **Search popup polish**: The result-list header now aligns with the data rows; the sort header carries a subtle background and shares the menu-bar margins.
- **Weather capsule fix**: Removed a duplicate title icon so capsule mode shows only the weather emoji.
- **Capsule hover mask**: Hidden the semi-transparent right-edge hover mask in capsule mode (interaction unchanged).

#### Architecture and Maintainability

- **App.Tray extraction**: Extracted `App.Tray.cs` (704 lines) from `App.xaml.cs`, consolidating all tray icon, menu, and lifecycle logic.
- **ServiceRegistry**: Added `ServiceRegistry` for centralized service location, reducing constructor parameter explosion.
- **SettingsMigrationService**: Added `SettingsMigrationService` for versioned settings schema migrations.
- **FileMetaService**: Added `FileMetaService` (332 lines) for unified file metadata extraction (icon, type name, size formatting).
- **AppDiagnosticsService**: Added `AppDiagnosticsService` for runtime diagnostics including handle counts, memory pressure, and UI thread responsiveness.
- **User guide**: Added a 10-chapter user guide under `docs/user-guide/` covering getting started, file widgets, todo, quick capture, capsule mode, stacks & QuickLook, appearance, backup, advanced workflows, and troubleshooting.

#### Fixes

- **QuickLook compatibility (critical)**: Fixed a critical issue where DeskBox could crash QuickLook's single-threaded named-pipe server by connecting without sending data (pipe probe in `CanPreview` and raw `CreateFile` fallback). Availability checks now use process enumeration only; the pipe is touched exclusively when sending a Toggle message.
- **Wallpaper loss**: Prevented desktop wallpaper loss caused by repeated `WorkerW` window spawns during widget layer operations in `WidgetLayerService`.
- **Capsule mode defaults**: Aligned `AppSettings` initial values with `ApplyDefaultPreferences` so new installs and global reset produce identical capsule behavior.
- **Tray menu height**: Fixed tray right-click menu height display issue.
- **Dead shortcut cleanup**: Added detection and removal of invalid `.lnk` and `.url` shortcuts (e.g. uninstalled Steam games) that previously showed broken icons in file widgets.
- **Weather code mapping**: Expanded `WeatherCodeMapper` with additional WMO weather codes for more accurate condition descriptions.

### 中文

#### 搜索系统（全新）

- **全文桌面搜索**：新增完整的搜索基础设施，包括专用搜索弹窗、全局快捷键唤起和即时结果展示。支持从单一入口搜索文件、文件夹、应用和设置。
- **USN 日志索引**：新增 `UsnJournalIndexService`，通过读取 NTFS USN 变更日志实现快速、低开销的文件发现，无需遍历目录树。
- **Windows 索引集成**：新增 `WindowsIndexSearchService`，查询 Windows 索引服务获取可内容搜索的结果（包括文档元数据）。
- **搜索结果排序**：新增 `SearchResultRanker`，使用加权评分算法，综合考虑名称匹配位置、文件类型优先级、时间新近度和路径深度。
- **搜索历史**：新增 `SearchHistoryService`，支持最近搜索持久化、快速重新执行和历史清除。
- **推荐应用面板**：搜索弹窗展示推荐应用面板，支持键盘导航（方向键 + Enter 启动，Space 通过 QuickLook 预览）。
- **类型排序和筛选**：搜索结果可按类型（文件、文件夹、应用、设置）排序和筛选。
- **搜索格子内容**：新增 `SearchWidgetContent` 和 `SearchWidgetContentAdapter`，搜索可作为格子内容类型嵌入。
- **搜索设置分区**：新增独立的搜索设置页，支持快捷键配置、索引范围、结果数量和推荐开关。

#### 多语言扩展

- **三种新语言**：新增日语（ja-JP）、德语（de-DE）和巴西葡萄牙语（pt-BR）本地化，每种语言 1500+ 翻译条目，覆盖所有格子、设置、引导、搜索、托盘菜单、对话框和状态消息。
- **本地化架构重写**：将旧的 `.resw` 资源系统替换为基于 JSON 的 `LocalizationService`，运行时加载嵌入的 `Strings/{locale}.json` 文件，便于社区翻译贡献和开发时热重载。

#### 新用户引导重构

- **五步聚焦流程**：将首次启动引导从之前的多面板布局重建为聚焦的五步流程：欢迎、功能概览、收纳设置、快捷键配置和完成。
- **精炼布局与动画**：每步使用专门的入场动画、一致的间距和全部五种支持语言的本地化内容。

#### 自适应托盘动画系统

- **硬件自适应控制器**：新增 `AdaptiveTrayAnimationController` 和 `HardwareAdaptiveAnimationService`，检测 GPU 能力并相应调整动画复杂度——独立显卡使用完整组合动画，集成显卡使用简化回退。
- **批量动画驱动器**：新增 `WidgetTrayBatchAnimationDriver`，用于同步多格子托盘显示/隐藏，消除单格子计时漂移。
- **智能动画适配器**：新增 `SmartAnimationAdapter`，将自适应控制器与现有格子动画路径桥接。
- **同步调度**：将即发即弃的 `DispatcherQueue.TryEnqueue` 调用替换为同步调度，减少快速切换托盘时的动画撕裂。

#### 天气改进

- **城市数据库扩展**：离线 `cities.json` 数据库从约 500 条扩展到 5000+ 条，覆盖更多中小城市。
- **改进城市搜索**：`CitySearchService` 现在使用前缀 + 模糊匹配，并支持 CJK 感知的分词，在中文、日文等非拉丁文字下搜索效果更好。
- **定位服务优化**：改进 `WindowsLocationHelper` 的超时处理、回退到上次已知位置，以及更清晰的错误报告。
- **天气布局改进**：优化 `WeatherWidgetContent` 四种尺寸模式的布局，改善间距、图标对齐和预报列表滚动。

#### 安装器与更新器

- **ARM64 安装器**：新增 `DeskBox.arm64.iss` 和 `DeskBox.Dependencies.arm64.iss`，支持原生 ARM64 Windows 构建。
- **可靠进程关闭**：安装器现在使用健壮的进程终止序列，避免覆盖安装时的文件锁定失败。
- **强制更新机制**：`AppUpdateService` 现在在服务器标记版本为关键时强制执行更新。
- **缓存清理**：自动清理 `%LocalAppData%\DeskBox\update-helper` 下的残留更新缓存目录。
- **正式下载地址**：所有下载链接切换到正式 GitHub Releases 渠道。
- **迁移支持**：新增 `DeskBox.Migration.iss` 处理大版本升级时的数据迁移。
- **英文安装器语言**：新增 `Languages/English.isl`，非中文系统显示英文安装界面。
- **安装器语言选择**：安装器现在提供语言选择对话框（中文、英文、日语、德语、巴西葡萄牙语），默认按系统区域预选。所选语言写入 `HKCU\Software\DeskBox\InstallLanguage`，DeskBox 首次启动会默认使用该语言（手动在应用内切换仍优先）。
- **搜索弹窗打磨**：结果列表表头与数据行现已左对齐；排序表头增加半透明底，并与上方菜单栏左右对齐。
- **天气胶囊修复**：移除了重复的标题图标，胶囊模式下只显示天气 emoji。
- **胶囊悬停遮罩**：隐藏胶囊右侧边缘的半透明悬停遮罩（交互行为不变）。

#### 架构与可维护性

- **App.Tray 提取**：从 `App.xaml.cs` 提取 `App.Tray.cs`（704 行），统一托盘图标、菜单和生命周期逻辑。
- **ServiceRegistry**：新增 `ServiceRegistry` 用于集中服务定位，减少构造函数参数爆炸。
- **SettingsMigrationService**：新增 `SettingsMigrationService` 用于版本化设置架构迁移。
- **FileMetaService**：新增 `FileMetaService`（332 行），统一文件元数据提取（图标、类型名称、大小格式化）。
- **AppDiagnosticsService**：新增 `AppDiagnosticsService` 用于运行时诊断，包括句柄数、内存压力和 UI 线程响应性。
- **用户指南**：在 `docs/user-guide/` 下新增 10 章用户指南，涵盖快速开始、文件格子、待办、随记、胶囊模式、叠放与 QuickLook、外观、备份、高级工作流和故障排除。

#### 修复

- **QuickLook 兼容性（严重）**：修复 DeskBox 可能因空连接（连接后不发送数据）导致 QuickLook 单线程命名管道服务器崩溃的严重问题（`CanPreview` 中的管道探测和 raw `CreateFile` 回退）。可用性检查现在仅使用进程枚举；管道仅在发送 Toggle 消息时才连接。
- **壁纸丢失**：防止 `WidgetLayerService` 格子层级操作期间反复生成 `WorkerW` 窗口导致桌面壁纸丢失。
- **胶囊模式默认值**：将 `AppSettings` 初始值与 `ApplyDefaultPreferences` 对齐，确保新安装和全局重置产生一致的胶囊行为。
- **托盘菜单高度**：修复托盘右键菜单高度显示问题。
- **死快捷方式清理**：新增检测和移除无效 `.lnk` 和 `.url` 快捷方式（如已卸载的 Steam 游戏），此前这些快捷方式会在文件格子中显示损坏图标。
- **天气代码映射**：扩展 `WeatherCodeMapper` 支持更多 WMO 天气代码，提供更准确的天气状况描述。

## 1.3.1 - 2026-07-20

### English

- **High-DPI icon clarity**: Replaced 32×32 `SHGFI_LARGEICON` extraction with `SHGetImageList` Jumbo (256×256) and Extra Large (48×48) fallback, plus `SHDefExtractIcon` for indexed icons. File and shortcut icons now match desktop icon clarity on high-DPI displays.
- **Steam shortcut icons**: Resolved `.url` shortcut icons not displaying by parsing comma-separated icon indices (e.g. `steam.exe,0`), searching for Steam via registry, and falling back to common install locations and system PATH.
- **Animation performance**: Replaced CPU-driven opacity/scale animations with GPU Composition KeyFrame animations. Added `DWMWA_TRANSITIONS_FORCEDISABLED` to prevent DWM interference. Replaced `AppWindow.Move` with direct `SetWindowPos` P/Invoke for lower latency.
- **Black screen transition fix**: Removed backdrop suppression during tray show/hide animations so Mica/Acrylic material remains consistent throughout the animation cycle.
- **Music artwork corner radius**: Fixed asymmetric corner radius on music widget album art in both empty and populated states by dynamically computing `CornerRadius` from widget settings.
- **Duplicate startup entries**: Unified startup registration to a single registry `Run` key. Removed redundant startup shortcut creation from the installer and drag-drop permission service. Added automatic cleanup of legacy startup shortcuts.
- **Capsule mode defaults**: New installs and global reset now default to hover auto-expand, sensitive hover response, and relaxed expand/collapse animation for a smoother first-run experience.
- **Freeze diagnostics**: Added a UI thread watchdog that detects and logs unresponsive periods with handle counts and GC generation. Fixed a sync-over-async deadlock in `StoreStartupService.RequestEnableAsync`.
- **Manual sort improvement**: Any drag-and-drop action in a file widget now automatically switches to manual sort mode. Subsequent sort mode clicks revert to automatic sorting. Removed the explicit "Manual Sort" menu option.

### 中文

- **高 DPI 图标清晰度**：将 32×32 的 `SHGFI_LARGEICON` 提取替换为 `SHGetImageList` Jumbo（256×256）和 Extra Large（48×48）回退，以及 `SHDefExtractIcon` 索引图标提取。文件和快捷方式图标在高 DPI 显示器上现在与桌面图标一样清晰。
- **Steam 快捷方式图标**：修复 `.url` 快捷方式图标不显示的问题，解析逗号分隔的图标索引（如 `steam.exe,0`），通过注册表搜索 Steam 安装路径，并回退到常见安装位置和系统 PATH。
- **动画性能**：将 CPU 驱动的透明度/缩放动画替换为 GPU Composition KeyFrame 动画。添加 `DWMWA_TRANSITIONS_FORCEDISABLED` 防止 DWM 干扰。将 `AppWindow.Move` 替换为直接 `SetWindowPos` P/Invoke 调用，降低延迟。
- **黑屏过渡修复**：移除托盘显示/隐藏动画期间的材质抑制，使 Mica/Acrylic 材质在整个动画周期内保持一致。
- **音乐封面圆角**：修复音乐格子封面在空状态和有值时圆角上下不对称的问题，通过动态计算 `CornerRadius` 解决。
- **重复启动项**：统一开机启动注册为单一注册表 `Run` 键。移除安装器和拖拽权限服务中多余的启动快捷方式创建。新增旧启动快捷方式自动清理。
- **胶囊模式默认值**：新安装和全局重置现在默认使用悬停自动展开、灵敏悬停响应和舒缓的展开/收起动画，提供更流畅的首次使用体验。
- **卡死诊断**：新增 UI 线程看门狗，检测并记录无响应时段的句柄数和 GC 代数。修复 `StoreStartupService.RequestEnableAsync` 中的 sync-over-async 死锁。
- **手动排序改进**：文件格子中任意拖拽操作现在自动切换到手动排序模式。后续排序模式点击恢复为自动排序。移除了显式的"手动排序"菜单选项。

## 1.3.0 - 2026-07-18

### English

- **Capsule mode**: Widgets can now collapse into compact desktop surfaces and expand by click or title-area hover. Compact content supports smart highlights, summaries or icon-and-title only, with an optional privacy mode for Todo and Quick Capture text.
- **Stable compact geometry**: Added aligned and independent width modes, anchored expansion toward the available screen area, separate compact and expanded bounds, resize guides, and per-widget overrides. Dragging content over a compact widget temporarily expands it without permanently moving or resizing either state.
- **Combined capsule layouts**: Compact widgets can remain independently positioned or form a movable combined bar. The bar supports user-defined order, floating or edge placement, automatic direction, adjustable spacing, and reliable restoration of free-layout positions.
- **Automatic file stacks**: File widgets can group related items by type or date without moving the underlying files. Custom extension rules support names, priority ordering, live match previews, thresholds, internal sorting and a configurable unmatched-file policy.
- **Stack interaction and QuickLook compatibility**: Stacks use an in-widget spread/collapse interaction with selection and path-copy commands. When QuickLook is already running, pressing Space on a selected file forwards the preview request without adding settings, startup work or a hard dependency.
- **Todo and Quick Capture workflows**: Added multiple file attachments with link-or-copy storage, attachment-aware clipboard formatting, configurable tab visibility, drag-to-tab actions, new Todo views for Active, This week and This month, adjustable list preview lines, and configurable Enter/Ctrl+Enter save behavior.
- **Appearance and responsive content**: Added Mica Alt and Base acrylic, material intensity, neutral/accent/hidden border colors, compact/standard/relaxed/custom density presets, and forced Cover or Controls layouts for Music. Solid material now remains fully opaque.
- **Windows-style Settings redesign**: Reorganized crowded pages into focused detail pages, moved global search into the title area, improved search matching and result navigation, surfaced important choices directly on entry cards, and made per-page hierarchy and reset behavior more consistent.
- **Backup, restore and attachment health**: Added integrity-checked ZIP export/restore, automatic and pre-restore snapshots, staged restart-safe restore, resilient JSON recovery, and attachment health scans for missing linked files, missing managed files and orphaned managed attachments.
- **Window and animation reliability**: Refined show/hide transitions, detail-page transitions, title-bar collapse actions, hover hit regions, Z-order, multi-monitor bounds restoration, resize alignment and tray menu sizing. Rapid capsule and stack interactions now use guarded state transitions to reduce flicker and stuck intermediate states.
- **Installer upgrades**: The installer now closes a running DeskBox process reliably before replacing application files, avoiding the intermittent Retry / Ignore / Cancel prompt during overwrite installs.
- **Maintainability and tests**: Split the largest window, widget, settings, Todo, Quick Capture, Music and Weather classes into focused modules. Expanded regression coverage across attachments, backup safety, settings migration/search, compact bounds and privacy, stacks, animation and positioning.

### 中文

- **胶囊模式**：格子现在可以收起为紧凑桌面形态，并通过点击或标题区域悬停展开。收起后可显示关键信息、简要摘要或仅图标和标题，也可隐藏待办与随记正文等敏感内容。
- **稳定的收起与展开几何逻辑**：新增保持同宽和独立调整两种宽度关系，并根据屏幕可用区域从胶囊锚点向合适方向展开。胶囊与展开状态分别保存位置和尺寸，支持参考线与单格覆盖；内容拖入时会临时展开，操作结束后不会永久改变两种状态。
- **胶囊组合排列**：胶囊可独立摆放，也可组成能够整体移动的组合栏。组合栏支持自定义顺序、悬浮或贴边位置、自动排列方向、间距调整，并能稳定恢复原来的自由布局位置。
- **文件自动叠放**：文件格子可按文件类型或日期自动分组，不移动真实文件。自定义格式规则支持叠放名称、优先级、实时命中预览、形成数量、内部排序，以及未匹配文件保持散开或收入“其他”。
- **叠放交互与 QuickLook 兼容**：叠放在格子内部散开展开和收回，并支持全选内容与复制路径。若 QuickLook 已经运行，在文件上按空格即可转交预览请求，不增加设置项、启动扫描或强制依赖。
- **待办与随记工作流**：支持一条内容关联多个文件，可选择关联原路径或复制到 DeskBox；复制文本时会按中英文格式附带附件路径。新增标签页显示配置、拖到标签页直接改变状态、待办“进行中/本周/本月”视图、列表预览行数，以及 Enter 与 Ctrl+Enter 保存行为互换。
- **外观与自适应内容**：新增云母 Alt、标准亚克力、材质浓度、中性/主题色/无边框颜色，显示密度支持紧凑、标准、宽松和自定义。音乐可强制使用封面或控制布局，纯色材质固定保持完全不透明。
- **Windows 风格设置重构**：将拥挤页面拆为聚焦的三级页面，把全局搜索移入标题栏并改进匹配与结果跳转；三级入口卡片直接提供最重要的选项，页面层级、前置控制和重置语义更加一致。
- **备份、恢复与附件健康检查**：新增带完整性校验的 ZIP 导出/恢复、自动快照、恢复前快照、重启后安全应用恢复、JSON 损坏回退，以及缺失关联文件、缺失托管附件和孤立附件扫描。
- **窗口与动画稳定性**：优化全局显示/隐藏、详情页进出、标题栏收起操作、悬停命中区域、窗口层级、多显示器位置恢复、调整大小参考线和托盘菜单高度。快速操作胶囊与叠放时使用受控状态切换，减少闪烁和卡在中间状态的问题。
- **覆盖安装体验**：安装器现在会在替换应用文件前可靠关闭正在运行的 DeskBox，避免覆盖安装时偶发弹出“重试 / 忽略 / 取消”提示。
- **可维护性与测试**：拆分体积过大的窗口、格子、设置、待办、随记、音乐和天气类，并扩展附件、备份安全、设置迁移与搜索、胶囊边界与隐私、叠放、动画和窗口定位的回归测试。

## 1.2.9 - 2026-07-13

### English

- **Todo experience rebuilt**: Replaced the dense single-page workflow with a card-based task list and a dedicated full-widget detail view. The detail title is a resizable multiline field, metadata actions use a compact horizontal toolbar, and task cards display up to ten lines.
- **Todo scheduling and organization**: Added eight color markers and filters, due date, reminder, recurrence and attachment workflows, recurring-task history handling, and context-menu shortcuts. Reminder and recurrence stay disabled until a due date exists.
- **Todo selection and batch actions**: Added box selection and batch copy, delete, color, reminder, due-date and recurrence commands. Removed native ListView selection residue after opening or returning from details, including the add-task card focus state.
- **Quick Capture redesign**: Reworked notes into a spacious card list and body-only full-widget editor. Added paper/material styles, image add and replace actions, thumbnails, image-specific copy controls, pin state feedback, and optional creation-time display.
- **Quick Capture sorting and batch actions**: Added animated drag sorting for regular and pinned notes while preserving drag-out copy behavior. Added batch copy, delete, pin and paper commands, and fixed stale image previews after returning from details.
- **Responsive widgets**: Reduced the minimum widget size from 200 x 200 to 150 x 150. Refined Weather layouts for small and medium sizes. Music now switches to an artwork-first layout below 180 px, keeps controls and the title readable, centers artwork cropping, reuses title marquee behavior, and applies a slower nonblank artwork transition.
- **Music simplification**: Removed all spectrum/rhythm visuals, settings, ViewModel state and related code. Unified solid playback icons, button sizing and rounded-rectangle hover surfaces across minimal and medium layouts.
- **File and context-menu improvements**: Simplified the native-style vertical menu order, kept Open and Extract text from image, renamed the Explorer command to Open file location, removed Show more options, and placed Delete last. Added file multi-selection actions and ensured commands close their flyouts immediately.
- **Widget title editing**: Rename commands now enter a focused editor immediately. Double-clicking any widget title starts editing, while clicking the remaining title area commits and exits.
- **Dynamic Z-order fixes**: Reworked tray and hotkey activation so all widgets raise together without a one-second flash, repeated activation hides them consistently, and subsequent foreground changes are managed naturally by Windows. Clicking a title raises all widgets; clicking content raises the current widget.
- **Settings and reset behavior**: Global reset now preserves language, startup and feature-widget switches, resets Quick Capture creation-time visibility and resize snapping correctly, clears per-widget title-style overrides, and uses confirmation dialogs for destructive feature-widget resets. Feature-widget menu wording now represents enable/disable rather than deletion.
- **Default settings updated**: New installs and global reset use Comfortable density, Mica material, Thin border, Round corners, segmented Todo and Quick Capture tabs, and hidden Todo footer counts.
- **Performance and memory**: Reduced unrelated Todo refreshes caused by organizer history writes, suppressed redundant settings broadcasts for file operations, tightened cancellation-token and event cleanup, removed music spectrum allocation paths, and improved repeated language/material/theme change behavior.
- **Reliability fixes**: Fixed Quick Capture clearing removing the add-note entry, image-plus-text copy behavior, created-time visibility, image persistence between list and detail views, completed Todo checkmark contrast, overlapping Todo cards, feature-widget reset completeness, and several tray/hotkey layer edge cases.
- **Tests**: Expanded coverage for Todo recurrence, persistence and batch changes; Quick Capture image, ordering and reset behavior; settings defaults; organizer notification suppression; responsive Music layout; and Weather layout thresholds.

### 中文

- **待办体验重构**：将拥挤的单页操作改为卡片任务列表和格子内完整详情页。详情标题支持多行输入并可拖动调整高度，提醒等元数据改为紧凑横排工具栏，任务卡片最多展示 10 行。
- **待办时间与整理能力**：增加 8 种颜色标记和筛选、截止日期、提醒、重复、附件、重复任务历史及右键快捷操作。未设置截止日期时，提醒和重复保持禁用。
- **待办多选与批量操作**：支持拉框多选，以及批量复制、删除、颜色、提醒、截止日期和重复。修复进入详情再返回后的 ListView 选中残留，以及添加任务卡片误显示选中状态。
- **随记重新设计**：改为更舒展的记录列表和仅保留正文的格子内详情编辑。支持纸张材质、添加/替换图片、缩略图、图片独立复制、固定状态反馈和创建时间显示开关。
- **随记排序与批量操作**：普通与固定列表均支持带动效的拖动排序，同时保留拖出复制。新增批量复制、删除、固定和纸张操作，修复详情返回后图片列表不及时刷新的问题。
- **格子自适应布局**：最小尺寸由 200 x 200 降至 150 x 150。优化天气小尺寸和中等尺寸排版。音乐在小于 180 px 时切换为封面优先布局，保留歌名与播放控制，封面始终居中裁剪，复用跑马灯，并使用更慢且不展示空状态的封面过渡。
- **音乐功能精简**：完整移除频谱/节奏视觉、设置、ViewModel 状态和相关代码。统一最小与中等布局的实心播放图标、按钮尺寸及圆角矩形悬浮背景。
- **文件与右键菜单**：恢复流畅的竖排菜单并重新整理顺序，保留“打开”和“提取图片文字”，资源管理器命令改为“打开文件路径”，删除“显示更多选项”，删除命令固定在底部。文件格子增加多选操作，菜单命令执行后立即关闭。
- **格子标题编辑**：点击重命名后立即聚焦编辑；所有格子支持双击标题进入编辑；点击标题剩余空白区域提交并退出。
- **动态层级修复**：重新整理托盘与快捷键唤起逻辑，确保全部格子同时前置、不再闪烁一秒后置底，再次触发可一致隐藏，之后的前后台层级交还 Windows 管理。点击标题唤起全部格子，点击内容只唤起当前格子。
- **设置与重置行为**：全局重置保留语言、开机启动和功能格子开关，正确重置随记创建时间显示与调整大小吸附，清除格子标题栏独立覆盖值。功能格子完整重置增加二次确认，标题菜单文案改为开关而非删除。
- **默认设置调整**：新安装与全局重置默认使用舒适密度、云母材质、细边框、大圆角、待办/随记分段按钮，并关闭待办底部数量。
- **性能与内存**：减少文件整理历史写入引起的无关待办刷新，文件操作不再广播多余设置变更，完善取消令牌和事件释放，删除音乐频谱分配路径，并优化反复切换语言、材质和明暗模式时的生命周期。
- **稳定性修复**：修复清空随记后添加入口消失、图文复制失败、创建时间开关不生效、列表与详情图片不同步、待办完成勾选对比度、待办内容偶发重叠、功能格子重置不完整，以及多项托盘/快捷键层级边界问题。
- **测试**：扩展待办重复与批量变更、随记图片/排序/重置、设置默认值、整理服务通知抑制、音乐响应式布局和天气布局阈值测试。

## 1.2.8 - 2026-07-11

### English

- **Weather widget**: Added a full-featured weather widget with four layout modes (Mini, Compact, Standard, Detailed), city search with offline `cities.json` database and Windows Location API, auto-location, hourly and weekly forecasts, sunrise/sunset times, UV index, precipitation probability, humidity, wind speed, atmospheric pressure, rich skin backgrounds, and configurable refresh interval. Standard view activates at 250×215 and above. Horizontal scrollbar and mouse wheel support for the hourly temperature list. Week forecast fills remaining space and is scrollable even on small widgets. Loading animation with rotating refresh icon and content hiding during refresh.
- **Resize snap guide**: Added `ResizeGuideOverlayService` that detects edge alignment (8px threshold) with other widgets and work area boundaries during widget resize, showing accent-colored highlight bars on matched edges. Integrated into `WidgetWindow`, `QuickCaptureWidgetWindow`, and `ContentWidgetWindow`.
- **Quick Capture fixes**: Widget reset now properly clears all internal data via `QuickCaptureService.ClearAsync()`. Fixed blank display on first tab switch — data now appears immediately instead of requiring multiple switches. Added `ScheduleTransitionSafetyFallback` to `PlayItemsViewTransition` for more reliable content transitions.
- **Settings defaults unified**: Default appearance changed to Mica material, Medium border, Round corners — consistent across new installs, reset-to-defaults, and `AppSettings` initial values. Global reset (`ApplyDefaultPreferences`) now restores `CustomAccentColor` to `#0078D4` and `FocusClickedWidgetOnRaise` to `false`. Todo widget single-widget reset now restores `TodoReminderEnabled` and `TodoDefaultReminderOffsetMinutes`. Weather widget single-widget reset now clears `WeatherLatitude` and `WeatherLongitude` to prevent inconsistent location behavior.
- **Widget title font size**: Content widget titles now dynamically compute `TitleTextSize` and `TitleIconSize` from user settings (`TextSize`/`IconSize`) instead of using fixed values. Added `RefreshMetrics()` method to update title metrics when settings change.
- **Memory leak fix**: `TodoWidgetViewModel` now implements `IDisposable`, and `TodoWidgetContentAdapter` properly disposes the ViewModel on widget disposal, preventing memory leaks.
- **Delete widget button**: Added delete widget option to the "more" menu for all widget types, not just file widgets. Fixed `ContentWidgetWindow` more flyout placement.
- **Settings UI expansion**: Added standalone material type selector (Mica / Acrylic / Solid) and border style selector (None / Thin / Medium) to the Appearance settings page.
- **Widget layer improvement**: Added `SetWindowToDesktopLevel` method in `WidgetLayerService` that pushes windows to desktop level without using `HWND_BOTTOM`, preventing widgets from being hidden by Win+D while staying at desktop level.
- **Localization simplification**: Shortened drag-and-drop diagnostics text across both Chinese and English for clearer, more concise messaging.
- **FolderWatcherService optimization**: Replaced `CancellationTokenSource`-based debounce with `DispatcherQueueTimer`, reducing thread-pool task creation for file system change notifications.
- **Code refactoring — WidgetWindowBase**: Extracted a new `WidgetWindowBase.cs` (1027 lines) consolidating window setup, backdrop management, layer/Z-order control, drag/resize logic, and display-change restoration. `ContentWidgetWindow` and `QuickCaptureWidgetWindow` now inherit from this shared base, eliminating duplicated window management code.
- **Code refactoring — WidgetWindow**: Split `WidgetWindow.xaml.cs` (~5000 lines) into 6 partial classes: `Rename` (335 lines), `Menus` (677), `ItemSurface` (364), `Clipboard` (249), `Selection` (397), `DragDrop` (806). Main file reduced to 2828 lines.
- **Code refactoring — WidgetManager**: Split `WidgetManager.cs` (3532 lines) into 4 partial classes: `WidgetManager.TrayAnimation.cs` (353 lines, tray show/hide animation), `WidgetManager.ZOrder.cs` (376 lines, Z-order/layer management/mouse hook), `WidgetManager.Storage.cs` (595 lines, managed storage/folder mapping/orphan cleanup), `WidgetManager.FeatureWidgets.cs` (889 lines, Music/Weather/Todo/QuickCapture feature widgets). Core file reduced to 1383 lines.
- **Log rotation**: Added `TryRotateLogFileIfNeeded` with a 5MB size threshold — automatically backs up to `.bak` and cleans up old logs.
- **Atomic settings writes**: `SettingsService.SaveToFileOnlyAsync` now writes to a `.tmp` file first, then moves it to the final path, preventing configuration corruption from interrupted writes.
- **Onboarding redesign**: Simplified onboarding animation API and refined the first-run experience.

### 中文

- **天气格子**：新增完整功能天气格子，支持四种布局模式（迷你、紧凑、标准、详细），城市搜索（离线 `cities.json` 数据库 + Windows Location API）、自动定位、逐小时和每周预报、日出日落时间、紫外线指数、降水概率、湿度、风速、大气压、丰富皮肤背景和可配置刷新频率。标准视图在 250×215 及以上尺寸激活。逐小时温度列表支持横向滚动条和鼠标滚轮。周预报填满剩余空间，小尺寸下也可滚动。刷新时显示旋转加载动画并隐藏内容。
- **调整大小参考线**：新增 `ResizeGuideOverlayService`，在调整格子大小时检测边缘对齐（8px 阈值），与其他格子边缘和工作区边界对齐时显示强调色高亮条。已集成到 `WidgetWindow`、`QuickCaptureWidgetWindow` 和 `ContentWidgetWindow`。
- **随记修复**：格子重置现在通过 `QuickCaptureService.ClearAsync()` 正确清理所有内部数据。修复首次切换 Tab 时空白显示的问题——数据现在立即显示，无需多次切换。为 `PlayItemsViewTransition` 添加 `ScheduleTransitionSafetyFallback` 安全兜底，提升内容切换可靠性。
- **默认设置统一**：默认外观改为云母材质、中等边框、大圆角——新安装、恢复默认和 `AppSettings` 初始值三处保持一致。全局重置（`ApplyDefaultPreferences`）现在恢复 `CustomAccentColor` 为 `#0078D4`、`FocusClickedWidgetOnRaise` 为 `false`。待办格子单格子重置现在恢复 `TodoReminderEnabled` 和 `TodoDefaultReminderOffsetMinutes`。天气格子单格子重置现在清除 `WeatherLatitude` 和 `WeatherLongitude`，避免定位行为不一致。
- **格子标题字号**：功能格子标题现在根据用户设置（`TextSize`/`IconSize`）动态计算 `TitleTextSize` 和 `TitleIconSize`，不再使用固定值。新增 `RefreshMetrics()` 方法在设置变化时更新标题度量。
- **内存泄漏修复**：`TodoWidgetViewModel` 实现 `IDisposable`，`TodoWidgetContentAdapter` 在格子销毁时正确释放 ViewModel，防止内存泄漏。
- **删除格子按钮**：所有类型格子的"更多"菜单中均可删除格子，不再限于文件格子。修复 `ContentWidgetWindow` 更多菜单弹出位置。
- **设置页扩展**：外观设置新增独立的材质类型选择（云母 / 亚克力 / 纯色）和边框样式选择（无边框 / 细 / 中）。
- **格子层级改进**：`WidgetLayerService` 新增 `SetWindowToDesktopLevel` 方法，不使用 `HWND_BOTTOM` 即可将窗口推至桌面层级，防止 Win+D 隐藏格子。
- **本地化文案精简**：精简拖拽诊断相关文案（中英文），表述更简洁明了。
- **FolderWatcherService 优化**：将基于 `CancellationTokenSource` 的防抖替换为 `DispatcherQueueTimer`，减少文件系统变更通知时的线程池任务创建。
- **代码重构 — WidgetWindowBase**：提取新的 `WidgetWindowBase.cs`（1027 行），统一窗口设置、背景管理、层级控制、拖拽/调整大小逻辑和显示器变化恢复。`ContentWidgetWindow` 和 `QuickCaptureWidgetWindow` 现在继承此共享基类，消除重复的窗口管理代码。
- **代码重构 — WidgetWindow**：将 `WidgetWindow.xaml.cs`（约 5000 行）拆分为 6 个 partial 类：`Rename`（335 行）、`Menus`（677）、`ItemSurface`（364）、`Clipboard`（249）、`Selection`（397）、`DragDrop`（806）。主文件降至 2828 行。
- **代码重构 — WidgetManager**：将 `WidgetManager.cs`（3532 行）拆分为 4 个 partial 类：`WidgetManager.TrayAnimation.cs`（353 行，托盘显示/隐藏动画）、`WidgetManager.ZOrder.cs`（376 行，层级管理/鼠标钩子）、`WidgetManager.Storage.cs`（595 行，收纳管理/文件夹映射/孤立清理）、`WidgetManager.FeatureWidgets.cs`（889 行，音乐/天气/待办/随记功能格子）。核心文件降至 1383 行。
- **日志轮转**：新增 `TryRotateLogFileIfNeeded`，基于 5MB 大小阈值自动备份到 `.bak` 并清理旧日志。
- **原子化设置写入**：`SettingsService.SaveToFileOnlyAsync` 改为先写入 `.tmp` 临时文件再移动到最终路径，防止写入中断导致配置损坏。
- **新用户引导优化**：简化引导动画 API，优化首次启动体验。

## 1.2.7 - 2026-07-09

### English

- Replaced the fixed 16ms `DispatcherQueueTimer` in widget tray animation with `CompositionTarget.Rendering`, which is driven by the system VSync. Animations now run at the native display refresh rate — 60fps on 60Hz screens, 144fps on 144Hz screens — eliminating visible stutter on high-refresh-rate displays.
- Replaced the triple `Task.Delay` backdrop refresh pattern with a single staged `DispatcherQueueTimer` (80ms → 240ms → 580ms), reducing thread-pool scheduling overhead and keeping all refresh work on the UI thread.
- Removed an empty `PointerMoved` handler from widget item surfaces and added state guards to `PointerEntered`/`PointerExited` so hover surface updates are skipped while the window is closing, animating, or hidden.
- Stopped the music widget's progress, visualizer, and transition timers when the widget is deactivated, and restarted them on activation, eliminating unnecessary CPU usage when the widget is not visible.
- Added a 200ms debounce to `UISettings.ColorValuesChanged` in `ThemeService` so system accent color and theme changes no longer trigger redundant `RefreshAppearance` calls.
- Reduced the icon cache limit from 500 to 200 entries and set `DecodePixelWidth` before `SetSourceAsync` for both icons (48px) and image thumbnails (80px), lowering memory usage from decoded bitmaps.

### 中文

- 将格子托盘动画的固定 16ms `DispatcherQueueTimer` 替换为系统 VSync 驱动的 `CompositionTarget.Rendering`，动画帧率自动跟随显示器刷新率——60Hz 屏 60fps、144Hz 屏 144fps，消除高刷屏上的可见卡顿。
- 将毛玻璃背景的三次 `Task.Delay` 刷新替换为单个分阶段 `DispatcherQueueTimer`（80ms → 240ms → 580ms），减少线程池调度开销，所有刷新工作在 UI 线程定时器内完成。
- 移除格子项目表面的空 `PointerMoved` 事件处理器，并为 `PointerEntered`/`PointerExited` 增加状态守卫，窗口关闭、动画运行或隐藏时跳过无意义的悬停状态计算。
- 音乐格子失焦时停止进度、频谱和过渡定时器，激活时重新启动，消除不可见时的无效 CPU 占用。
- 为 `ThemeService` 的 `UISettings.ColorValuesChanged` 增加 200ms 防抖，系统强调色和主题变化不再触发多次冗余的 `RefreshAppearance` 调用。
- 图标缓存上限从 500 降至 200，并在 `SetSourceAsync` 之前设置 `DecodePixelWidth`（图标 48px、缩略图 80px），降低解码位图的内存占用。

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
