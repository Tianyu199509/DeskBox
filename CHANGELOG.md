# Changelog

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
