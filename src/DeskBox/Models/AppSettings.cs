using System.Text.Json.Serialization;

namespace DeskBox.Models;

/// <summary>
/// Root settings object that is serialized to/from the JSON config file.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Settings schema version for migration purposes.
    /// New or legacy settings without this field begin at version 1 and are
    /// advanced by <see cref="DeskBox.Services.SettingsMigrationPipeline"/>.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Application theme. Valid values: <c>"System"</c>, <c>"Light"</c>, <c>"Dark"</c>.
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Tray icon style. Valid values: <c>"System"</c>, <c>"Colorful"</c>, <c>"Black"</c>, <c>"White"</c>.
    /// </summary>
    public string TrayIconStyle { get; set; } = "Colorful";

/// <summary>
/// Display language. Valid values: <c>"System"</c>, <c>"zh-CN"</c>, <c>"zh-TW"</c>, <c>"en-US"</c>, <c>"ja-JP"</c>, <c>"de-DE"</c>, <c>"pt-BR"</c>, <c>"hi-IN"</c>, <c>"es-ES"</c>, <c>"fr-FR"</c>, <c>"ar-SA"</c>, <c>"bn-BD"</c>, <c>"ru-RU"</c>.
/// </summary>
    public string Language { get; set; } = "System";

    /// <summary>
    /// Accent color source. Valid values: <c>"System"</c>, <c>"Custom"</c>.
    /// </summary>
    public string AccentColorMode { get; set; } = "System";

    /// <summary>
    /// Custom accent color in hex format such as <c>#0078D4</c>.
    /// </summary>
    public string CustomAccentColor { get; set; } = "#0078D4";

    /// <summary>Whether DeskBox should launch automatically at Windows startup.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Performance state. Selectable presets are <c>Balanced</c> and <c>ResourceSaver</c>; <c>Custom</c> records manual detail changes.</summary>
    public string PerformanceMode { get; set; } = "Balanced";

    /// <summary>Finite delay before releasing idle caches after every widget is fully hidden.</summary>
    public int HiddenCacheCleanupDelaySeconds { get; set; } = 30;

    /// <summary>Finite delay before shrinking recreatable caches while visible widgets are inactive.</summary>
    public int VisibleIdleCacheCleanupDelaySeconds { get; set; } = 10 * 60;

    /// <summary>Finite delay before closing a hidden transient window such as Search.</summary>
    public int TransientWindowReleaseDelaySeconds { get; set; } = 10 * 60;

    /// <summary>Budget for recreatable icon, thumbnail, and decoded-image caches. Valid values: <c>Small</c>, <c>Balanced</c>, <c>Large</c>.</summary>
    public string PerformanceCacheBudget { get; set; } = "Balanced";

    /// <summary>Legacy compatibility mirror for the former all-or-nothing decorative-effects switch.</summary>
    public bool EnableContinuousDecorativeAnimations { get; set; } = true;

    /// <summary>Whether continuously scrolling title text may run.</summary>
    public bool EnableTextMarqueeAnimations { get; set; } = true;

    /// <summary>Whether music vinyl artwork may rotate while playback is active.</summary>
    public bool EnableVinylRotationAnimations { get; set; } = true;

    /// <summary>Whether Glance widgets may automatically advance their image rotation.</summary>
    public bool EnableGlanceImageAutoRotation { get; set; } = true;

    /// <summary>Whether capsule glow, particle, breathing, and indeterminate ambient effects may run.</summary>
    public bool EnableCompactAmbientAnimations { get; set; } = true;

    /// <summary>Whether DeskBox should check for updates in the background.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Last time DeskBox successfully attempted an update check.</summary>
    public DateTimeOffset? LastUpdateCheckAt { get; set; }

    /// <summary>Whether the built-in Quick Capture widget is enabled.</summary>
    public bool QuickCaptureEnabled { get; set; }
    public bool TodoEnabled { get; set; }

    /// <summary>
    /// Enabled state for singleton feature widgets, keyed by <see cref="WidgetKind"/> name.
    /// Legacy boolean properties are still kept as compatibility mirrors.
    /// </summary>
    public Dictionary<string, bool> FeatureWidgetEnabledStates { get; set; } = [];

    /// <summary>Whether Quick Capture should record recent clipboard text and links.</summary>
    public bool QuickCaptureClipboardEnabled { get; set; }

    /// <summary>Whether Quick Capture should record clipboard images.</summary>
    public bool QuickCaptureImageClipboardEnabled { get; set; }

    /// <summary>Maximum number of recent clipboard text/link entries kept by Quick Capture.</summary>
    public int QuickCaptureRecentLimit { get; set; } = 30;

    /// <summary>Whether Quick Capture cards show their creation time.</summary>
    public bool QuickCaptureShowCreatedTime { get; set; } = true;

    /// <summary>Maximum number of text lines shown for each Quick Capture item in the list.</summary>
    public int QuickCaptureItemPreviewLineCount { get; set; } = 3;

    /// <summary>Enter-key behavior used by Quick Capture multiline editors.</summary>
    public string QuickCaptureEditorEnterBehavior { get; set; } = "CtrlEnterSaves";

    /// <summary>
    /// Preferred Quick Capture editor format. The property name is retained for compatibility with
    /// existing settings files. Valid values: <c>Markdown</c>, <c>PlainText</c>.
    /// </summary>
    public string QuickCaptureDefaultFormat { get; set; } = "Markdown";

    /// <summary>Responsive layout preference. Valid values: <c>Auto</c>, <c>SinglePane</c>, <c>DualPane</c>.</summary>
    public string QuickCaptureWideLayout { get; set; } = "Auto";

    /// <summary>Initial mode for saved notes in a wide detail pane. Valid values: <c>Reading</c>, <c>Editing</c>.</summary>
    public string QuickCaptureWideOpenMode { get; set; } = "Reading";

    /// <summary>Whether remote HTTP(S) images may be loaded by the Markdown reader.</summary>
    public bool QuickCaptureAllowRemoteImages { get; set; }

    /// <summary>Default storage behavior for Todo and Quick Capture attachments. Valid values: <c>Link</c>, <c>Copy</c>.</summary>
    public string AttachmentStorageMode { get; set; } = "Link";

    /// <summary>Default Quick Capture view used when the widget opens. Valid values: <c>"Records"</c>, <c>"Pinned"</c>, <c>"Recent"</c>.</summary>
    public string QuickCaptureDefaultView { get; set; } = "Records";

    /// <summary>Quick Capture tab style. Valid values: <c>"Pivot"</c>, <c>"Button"</c>.</summary>
    public string QuickCaptureTabStyle { get; set; } = "Button";

    public bool QuickCaptureShowTabBar { get; set; } = true;
    public bool QuickCaptureShowRecordsTab { get; set; } = true;
    public bool QuickCaptureShowPinnedTab { get; set; } = true;
    public bool QuickCaptureShowRecentTab { get; set; } = true;

    /// <summary>Where newly added Todo tasks are inserted. Valid values: <c>"Top"</c>, <c>"Bottom"</c>.</summary>
    public string TodoNewTaskPosition { get; set; } = "Top";

    /// <summary>Todo tab style. Valid values: <c>"Pivot"</c>, <c>"Button"</c>.</summary>
    public string TodoTabStyle { get; set; } = "Button";

    public bool TodoShowTabBar { get; set; } = true;
    public bool TodoShowAllTab { get; set; } = true;
    public bool TodoShowActiveTab { get; set; }
    public bool TodoShowTodayTab { get; set; } = true;
    public bool TodoShowThisWeekTab { get; set; }
    public bool TodoShowThisMonthTab { get; set; }
    public bool TodoShowImportantTab { get; set; } = true;
    public bool TodoShowCompletedTab { get; set; } = true;

    /// <summary>Default Todo filter used when the widget opens.</summary>
    public string TodoDefaultFilter { get; set; } = "All";

    /// <summary>Whether completed Todo tasks remain visible in non-completed views.</summary>
    public bool TodoShowCompletedTasks { get; set; }

    /// <summary>Maximum number of text lines shown for each Todo item in the list.</summary>
    public int TodoItemPreviewLineCount { get; set; } = 2;

    /// <summary>Enter-key behavior used by Todo multiline editors.</summary>
    public string TodoEditorEnterBehavior { get; set; } = "CtrlEnterSaves";

    /// <summary>Whether the Todo footer item count is visible.</summary>
    public bool TodoShowFooterStats { get; set; }

    /// <summary>Whether the Todo footer clear-completed command is visible.</summary>
    public bool TodoShowClearCompletedButton { get; set; } = true;

    /// <summary>Whether Todo due-date reminders are shown while DeskBox is running.</summary>
    public bool TodoReminderEnabled { get; set; } = true;

    /// <summary>Default reminder lead time in minutes before a Todo due date.</summary>
    public int TodoDefaultReminderOffsetMinutes { get; set; } = 5;

    public bool TodoUseWideDetailPane { get; set; } = true;

    /// <summary>
    /// Todo master-detail layout preference. Valid values: <c>Auto</c>,
    /// <c>SinglePane</c>, and <c>DualPane</c>. An empty value is treated as a
    /// legacy setting and migrated from <see cref="TodoUseWideDetailPane"/>.
    /// </summary>
    public string TodoLayoutMode { get; set; } = string.Empty;

    public bool TodoAutoSelectFirstInWideLayout { get; set; } = true;

    /// <summary>Whether the Music widget uses album artwork color as a soft backdrop.</summary>
    public bool MusicUseArtworkBackdrop { get; set; } = true;

    /// <summary>Whether the Music widget album cover reacts lightly to pointer hover.</summary>
    public bool MusicEnableCoverHoverMotion { get; set; } = true;

    /// <summary>
    /// Music widget layout mode. Valid values: <c>"Auto"</c>, <c>"Cover"</c>, <c>"Controls"</c>.
    /// </summary>
    public string MusicDisplayMode { get; set; } = "Auto";

    /// <summary>Last file widget used as the target for saving Quick Capture content.</summary>
    public string LastQuickCaptureFileWidgetId { get; set; } = string.Empty;

    /// <summary>Whether the global hotkey is enabled.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>The activation shape used by the global DeskBox shortcut.</summary>
    public HotkeyActivationKind GlobalHotkeyActivationKind { get; set; } =
        HotkeyActivationKind.Chord;

    /// <summary>Global hotkey modifier bit flags.</summary>
    public int GlobalHotkeyModifiers { get; set; } = (int)HotkeyModifierKeys.None;

    /// <summary>Global hotkey virtual key code.</summary>
    public int GlobalHotkeyKey { get; set; } = (int)Windows.System.VirtualKey.F7;

    /// <summary>Whether double-clicking blank desktop space toggles all widgets.</summary>
    public bool DesktopDoubleClickEnabled { get; set; }

    /// <summary>Whether the first-run onboarding has been completed or skipped.</summary>
    public bool HasCompletedOnboarding { get; set; }

    /// <summary>The last onboarding step reached before completion.</summary>
    public int OnboardingStepIndex { get; set; }

    /// <summary>The onboarding experience version most recently completed or skipped.</summary>
    public int CompletedOnboardingVersion { get; set; }

    /// <summary>
    /// Whether the one-time default file-widget setup has already been resolved.
    /// This remains true after the user deletes or disables every file widget.
    /// </summary>
    public bool HasResolvedInitialFileWidgetSetup { get; set; }

    /// <summary>Default width applied to newly created widgets.</summary>
    public double DefaultWidgetWidth { get; set; } = 280;

    /// <summary>Default height applied to newly created widgets.</summary>
    public double DefaultWidgetHeight { get; set; } = 400;

    /// <summary>
    /// Background opacity for widget windows (0.0 - 1.0).
    /// </summary>
    public double WidgetOpacity { get; set; } = 0.80;

    /// <summary>
    /// Material type for widget window backdrops.
    /// Valid values: <c>"Mica"</c>, <c>"MicaAlt"</c>, <c>"Acrylic"</c>,
    /// <c>"AcrylicBase"</c>, <c>"Solid"</c>.
    /// </summary>
    public string WidgetMaterialType { get; set; } = "Mica";

    /// <summary>Independent tint strength for native widget backdrop materials.</summary>
    public double WidgetMaterialIntensity { get; set; } = 0.65;

    /// <summary>
    /// Foreground palette used by widget text and monochrome controls.
    /// Valid values: <c>"FollowTheme"</c>, <c>"Light"</c>,
    /// <c>"Dark"</c>, <c>"Custom"</c>.
    /// </summary>
    public string WidgetForegroundMode { get; set; } = "FollowTheme";

    /// <summary>Custom widget foreground color in <c>#RRGGBB</c> form.</summary>
    public string WidgetForegroundColor { get; set; } = "#F5F5F5";

    /// <summary>
    /// Widget text edge mode: <c>"Off"</c>, <c>"Soft"</c>, <c>"Strong"</c>.
    /// </summary>
    public string WidgetTextEdgeMode { get; set; } = "Off";

    /// <summary>
    /// Border color mode for widget windows.
    /// Valid values: <c>"Neutral"</c>, <c>"Accent"</c>, <c>"None"</c>.
    /// </summary>
    public string WidgetBorderColorMode { get; set; } = "Neutral";

    /// <summary>
    /// Border style for widget windows.
    /// Valid values: <c>"Thin"</c>, <c>"Medium"</c>, <c>"Thick"</c>.
    /// </summary>
    public string WidgetBorderStyle { get; set; } = "Thin";

    /// <summary>
    /// Native DWM corner style for widget windows.
    /// Valid values: <c>"Square"</c>, <c>"Small"</c>, <c>"Round"</c>.
    /// </summary>
    public string WidgetCornerPreference { get; set; } = "Round";

    /// <summary>
    /// Animation effect used when desktop widgets show or hide.
    /// </summary>
    public string WidgetAnimationEffect { get; set; } = "SlideFade";

    /// <summary>
    /// Animation speed preset used when desktop widgets show or hide.
    /// </summary>
    public string WidgetAnimationSpeed { get; set; } = "Standard";

    /// <summary>
    /// Slide direction for Slide animation effect.
    /// Valid values: <c>"None"</c>, <c>"Left"</c>, <c>"Right"</c>, <c>"Up"</c>, <c>"Down"</c>.
    /// </summary>
    public string WidgetAnimationSlideDirection { get; set; } = "Right";

    /// <summary>
    /// Easing intensity for animations.
    /// Valid values: <c>"None"</c>, <c>"Light"</c>, <c>"Standard"</c>, <c>"Strong"</c>.
    /// </summary>
    public string WidgetAnimationEasingIntensity { get; set; } = "Standard";

    /// <summary>
    /// Window layer behavior for desktop widgets.
    /// Valid values: <c>"Dynamic"</c>, <c>"DesktopPinned"</c>,
    /// <c>"QuickReveal"</c>.
    /// </summary>
    public string WidgetLayerMode { get; set; } = "Dynamic";

    /// <summary>
    /// Whether dynamically layered widgets stay visible when Windows shows the desktop.
    /// Desktop-pinned mode always keeps widgets on the desktop.
    /// </summary>
    public bool KeepWidgetsVisibleOnShowDesktop { get; set; } = true;

    /// <summary>
    /// Default chrome/title mode for display widgets such as Music, Weather, and System Monitor.
    /// Valid values: <c>"Standard"</c>, <c>"Compact"</c>, <c>"Overlay"</c>, <c>"Hidden"</c>.
    /// </summary>
    public string DisplayWidgetChromeMode { get; set; } = "Overlay";

    /// <summary>
    /// Default chrome/title mode for interactive widgets such as files, Quick Capture, Todo, and Tags.
    /// Valid values: <c>"Standard"</c>, <c>"Compact"</c>, <c>"Overlay"</c>, <c>"Hidden"</c>.
    /// </summary>
    public string InteractiveWidgetChromeMode { get; set; } = "Standard";

    /// <summary>
    /// How widgets enter and leave their compact state.
    /// Valid values: <c>"Expanded"</c>, <c>"Click"</c>, <c>"Smart"</c>.
    /// </summary>
    public string WidgetCollapseBehavior { get; set; } = "Expanded";

    /// <summary>
    /// Legacy compact-mode gate retained only while reading pre-version-2
    /// profiles. Current code uses <see cref="WidgetCollapseBehavior"/> as the
    /// single source of truth and clears this value after migration.
    /// </summary>
    [JsonPropertyName("widgetCapsuleModeEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyWidgetCapsuleModeEnabled { get; set; }

    /// <summary>
    /// How compact and expanded widget widths relate to each other.
    /// Valid values: <c>"Aligned"</c>, <c>"Independent"</c>.
    /// </summary>
    public string WidgetCompactWidthMode { get; set; } = "Aligned";

    /// <summary>
    /// Vertical direction used when a compact widget expands.
    /// Valid values: <c>"Auto"</c>, <c>"Down"</c>, <c>"Up"</c>.
    /// </summary>
    public string WidgetCompactExpansionDirection { get; set; } = "Down";

    /// <summary>
    /// How compact widgets are arranged on the desktop.
    /// Valid values: <c>"Free"</c>, <c>"Bar"</c>.
    /// </summary>
    public string WidgetCapsuleArrangementMode { get; set; } = "Free";

    /// <summary>Logical pixel spacing between adjacent widgets in a capsule bar.</summary>
    public double WidgetCapsuleBarSpacing { get; set; } = 8;

    /// <summary>
    /// Where the capsule bar is anchored.
    /// Valid values: <c>"Floating"</c>, <c>"Top"</c>, <c>"Bottom"</c>,
    /// <c>"Left"</c>, <c>"Right"</c>.
    /// </summary>
    public string WidgetCapsuleBarPlacement { get; set; } = "Floating";

    /// <summary>
    /// Primary flow direction for the capsule bar.
    /// Valid values: <c>"Auto"</c>, <c>"Horizontal"</c>, <c>"Vertical"</c>.
    /// </summary>
    public string WidgetCapsuleBarDirection { get; set; } = "Auto";

    /// <summary>Stable user order used when compact widgets form a capsule bar.</summary>
    public List<string> WidgetCapsuleBarOrder { get; set; } = [];

    /// <summary>Free-layout placements preserved while a capsule bar is active.</summary>
    public Dictionary<string, WidgetCompactPlacement> WidgetCapsuleFreePlacements { get; set; } = [];

    /// <summary>
    /// Legacy combined compact style retained for settings migration.
    /// </summary>
    public string WidgetCollapsedStyle { get; set; } = "Smart";

    /// <summary>
    /// Information density used by compact widgets.
    /// Valid values: <c>"Smart"</c>, <c>"Minimal"</c>, <c>"Summary"</c>.
    /// </summary>
    public string WidgetCompactContentMode { get; set; } = "Smart";

    /// <summary>Whether compact Todo and Quick Capture widgets hide their content previews.</summary>
    public bool WidgetCompactHideSensitiveContent { get; set; }

    /// <summary>Schema version for compact content settings migrated from the legacy combined style.</summary>
    public int WidgetCompactSettingsVersion { get; set; }

    /// <summary>
    /// Motion style used when compact widgets expand or collapse.
    /// Valid values: <c>"Smooth"</c>, <c>"Slow"</c>, <c>"Snappy"</c>,
    /// <c>"Custom"</c>, <c>"None"</c>.
    /// </summary>
    public string WidgetCompactAnimationEffect { get; set; } = "Slow";

    /// <summary>Compact transition duration in milliseconds.</summary>
    public int WidgetCompactAnimationDurationMs { get; set; } = 360;

    /// <summary>Pointer hover delay before a smart compact widget expands.</summary>
    public int WidgetCompactExpandDelayMs { get; set; } = 100;

    /// <summary>Pointer leave delay before a smart compact widget collapses.</summary>
    public int WidgetCompactCollapseDelayMs { get; set; } = 200;

    /// <summary>
    /// Corner treatment for media inside compact widgets.
    /// Valid values: <c>"FollowWidget"</c>, <c>"Square"</c>, <c>"Small"</c>, <c>"Round"</c>.
    /// </summary>
    public string WidgetCompactMediaCornerMode { get; set; } = "FollowWidget";

    /// <summary>
    /// Title icon presentation for widget title bars.
    /// Valid values: <c>"FilledMono"</c>, <c>"LineMono"</c>, <c>"Color"</c>, <c>"Hidden"</c>, <c>"TextLabel"</c>.
    /// </summary>
    public string WidgetTitleIconMode { get; set; } = "Color";

    /// <summary>Whether to double click to open files.</summary>
    public bool DoubleClickToOpen { get; set; } = true;

    /// <summary>
    /// How child folders are opened from file widgets.
    /// Valid values: <c>"Explorer"</c>, <c>"Embedded"</c>.
    /// </summary>
    public string FileWidgetFolderOpenBehavior { get; set; } = "Explorer";

    /// <summary>
    /// Whether shortcut icons should hide the arrow overlay inside DeskBox.
    /// </summary>
    public bool HideShortcutArrowOverlay { get; set; } = true;

    /// <summary>
    /// Whether image and video files in file widgets should use the system file icon
    /// instead of media thumbnails.
    /// </summary>
    public bool ShowImageFilesAsIcons { get; set; }

    /// <summary>
    /// Whether to show action buttons on widget hover.
    /// </summary>
    public bool ShowHoverButtons { get; set; } = true;

    /// <summary>
    /// Comma-separated widget title hover actions. Valid values: <c>"LockPosition"</c>,
    /// <c>"LockSize"</c>, <c>"Add"</c>, <c>"More"</c>, <c>"Delete"</c>.
    /// </summary>
    public string WidgetHoverButtonActions { get; set; } = "Add,More";

    /// <summary>
    /// Whether snap-to-edge alignment guides are enabled while moving or
    /// resizing widgets.
    /// </summary>
    public bool ResizeSnapEnabled { get; set; } = true;

    /// <summary>
    /// Desired gap, in effective pixels, between adjacent snapped widgets.
    /// </summary>
    public double WidgetSnapSpacing { get; set; } = 5;

    /// <summary>
    /// Whether list view should show secondary file details under item names.
    /// </summary>
    public bool ShowListItemDetails { get; set; }

    /// <summary>
    /// Whether file widgets show full path tooltips when hovering items.
    /// </summary>
    public bool ShowFileItemPathTooltips { get; set; } = true;

    /// <summary>Whether file widgets automatically group related items into stacks.</summary>
    public bool FileStacksEnabled { get; set; } = true;

    /// <summary>
    /// Whether loose files are grouped into stacks automatically. Manual
    /// stacks are always available while <see cref="FileStacksEnabled"/> is
    /// on; this switch only controls automatic grouping.
    /// </summary>
    public bool FileStackAutoStacking { get; set; }

    /// <summary>Grouping rule used by automatic file stacks.</summary>
    public string FileStackGroupBy { get; set; } = "Kind";

    /// <summary>Minimum number of related files required to create an automatic stack.</summary>
    public int FileStackThreshold { get; set; } = 3;

    /// <summary>Ordering rule for members inside an automatic stack.</summary>
    public string FileStackOrderBy { get; set; } = "Widget";

    /// <summary>
    /// How clicking a stack reveals its members. Valid values are
    /// <c>"Inline"</c> and <c>"Popover"</c>.
    /// </summary>
    public string FileStackOpenMode { get; set; } = "Inline";

    /// <summary>
    /// Grid shape of the stack popover: <c>"Adaptive"</c>, <c>"Grid3"</c>
    /// (3×3), or <c>"Grid5"</c> (5×5). Fixed grids scroll vertically once
    /// the visible cells are exceeded. Defaults to the 3×3 grid.
    /// </summary>
    public string FileStackPopoverLayout { get; set; } = "Grid3";

    /// <summary>
    /// Visual style of the stack popover: <c>"FollowMaterial"</c> reuses the
    /// widget material system, <c>"Neutral"</c> keeps the original acrylic
    /// tint in both light and dark themes.
    /// </summary>
    public string FileStackPopoverStyle { get; set; } = "Neutral";

    /// <summary>User-defined extension groups, evaluated in list order.</summary>
    public List<FileStackCustomRule> FileStackCustomRules { get; set; } = [];

    /// <summary>How files not matched by a custom rule are displayed.</summary>
    public string FileStackUnmatchedBehavior { get; set; } = "KeepLoose";

    /// <summary>
    /// How files should be handled when dropped into a managed storage widget.
    /// Valid values: <c>"Move"</c>, <c>"Copy"</c>, <c>"FollowWindows"</c>.
    /// </summary>
    public string ManagedDropAction { get; set; } = "Move";

    /// <summary>
    /// Root folder used by widgets that follow the default managed storage path.
    /// </summary>
    public string DefaultManagedStorageRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Recent organization history used for undo and quick review.
    /// </summary>
    public List<OrganizationHistoryEntry> RecentOrganizationHistory { get; set; } = [];

    /// <summary>
    /// Stable per-widget routing rules used by desktop one-click and automatic
    /// organization. Rules target widget IDs and therefore survive renames and
    /// group membership changes.
    /// </summary>
    public List<DesktopOrganizationRule> DesktopOrganizationRules { get; set; } = [];

    /// <summary>
    /// Whether newly created, stable desktop files may be routed automatically.
    /// This is deliberately opt-in.
    /// </summary>
    public bool DesktopAutoOrganizationEnabled { get; set; }

    /// <summary>
    /// Baseline created when automatic organization is enabled. Existing
    /// desktop content is never processed merely by enabling the feature.
    /// </summary>
    public DateTimeOffset? DesktopAutoOrganizationBaselineUtc { get; set; }

    /// <summary>
    /// Icon size used by widgets in icon view.
    /// </summary>
    public double IconSize { get; set; } = 30;

    /// <summary>
    /// Label text size used by widgets in both icon and list views.
    /// </summary>
    public double TextSize { get; set; } = 11.5;

    /// <summary>
    /// Layout density preset for widget content.
    /// Valid values: <c>"Compact"</c>, <c>"Standard"</c>, <c>"Relaxed"</c>, <c>"Custom"</c>.
    /// </summary>
    public string LayoutDensity { get; set; } = "Standard";

    /// <summary>
    /// Continuous layout density scale used by widget content.
    /// Smaller values create a tighter layout.
    /// </summary>
    public double LayoutDensityScale { get; set; } = 0.56;

    /// <summary>
    /// Horizontal spacing scale used by widget content.
    /// Smaller values place items closer together horizontally.
    /// </summary>
    public double HorizontalSpacingScale { get; set; } = 0.40;

    /// <summary>
    /// Vertical spacing scale used by widget content.
    /// Smaller values place items closer together vertically.
    /// </summary>
    public double VerticalSpacingScale { get; set; } = 0.60;

    /// <summary>
    /// File name width scale used by icon-view labels.
    /// Smaller values keep labels narrower.
    /// </summary>
    public double FileNameWidthScale { get; set; } = 0.36;

    /// <summary>
    /// Maximum number of lines used by file names in icon view.
    /// </summary>
    public int FileNameLineCount { get; set; } = 2;

    /// <summary>
    /// Whether widget item labels should include file extensions.
    /// </summary>
    public bool ShowFileExtensions { get; set; }

    /// <summary>
    /// Whether .lnk shortcuts should keep their extension hidden even when file extensions are shown.
    /// </summary>
    public bool HideShortcutExtensionWhenShowingFileExtensions { get; set; } = true;

    /// <summary>All configured widgets.</summary>
    public List<WidgetConfig> Widgets { get; set; } = [];

    /// <summary>
    /// Groups that present several normal widget configs through one desktop
    /// surface. Widget data stays in <see cref="Widgets"/>; this collection owns
    /// group membership, active member, and shared window state.
    /// </summary>
    public List<WidgetGroupConfig> WidgetGroups { get; set; } = [];

    /// <summary>
    /// Bounded layout snapshots keyed by the connected-display topology. This
    /// prevents a temporary DPI/topology transition from overwriting the layout
    /// that belongs to another monitor arrangement.
    /// </summary>
    public Dictionary<string, WidgetTopologyLayoutProfile> WidgetTopologyLayouts { get; set; } = [];

    /// <summary>The topology profile currently projected into Widgets/WidgetGroups.</summary>
    public string? ActiveWidgetTopologyKey { get; set; }

    /// <summary>
    /// Legacy compatibility flag. Widget grouping is now always available;
    /// normalization keeps this value true for older settings files.
    /// </summary>
    public bool WidgetGroupsEnabled { get; set; } = true;

    /// <summary>
    /// Legacy navigation style retained only so pre-title-switcher settings can
    /// be migrated without losing user intent.
    /// </summary>
    public string WidgetGroupDefaultNavigationStyle { get; set; } = "Stack";

    /// <summary>
    /// Default identity layout used by the title-bar member selector.
    /// </summary>
    public string WidgetGroupDefaultTitleDisplayMode { get; set; } =
        WidgetGroupTitleDisplayModes.IconAndText;

    /// <summary>
    /// Enables sequential member switching while the pointer is over the
    /// title-bar member selector.
    /// </summary>
    public bool WidgetGroupWheelSwitchEnabled { get; set; } = true;

    /// <summary>
    /// Enables delayed pointer-hover activation for flat group title tabs.
    /// Individual groups may override this default.
    /// </summary>
    public bool WidgetGroupHoverSwitchEnabled { get; set; }

    /// <summary>
    /// When true, clicking one widget during batch raise keeps only that widget on top;
    /// others move to non-topmost. When false (default), all widgets stay visible together.
    /// </summary>
    public bool FocusClickedWidgetOnRaise { get; set; }

    /// <summary>Widget ids that were deleted and should not be restored.</summary>
    public List<string> DeletedWidgetIds { get; set; } = [];

    // ─── Weather Widget Settings ───────────────────────────────────

    /// <summary>
    /// Whether the weather widget uses auto location (IP-based).
    /// </summary>
    public bool WeatherAutoLocation { get; set; } = true;

    /// <summary>
    /// Saved city name for manual location.
    /// </summary>
    public string WeatherCityName { get; set; } = string.Empty;

    /// <summary>
    /// Saved latitude for manual location.
    /// </summary>
    public double WeatherLatitude { get; set; }

    /// <summary>
    /// Saved longitude for manual location.
    /// </summary>
    public double WeatherLongitude { get; set; }

    /// <summary>
    /// Temperature unit. Valid values: <c>"Celsius"</c>, <c>"Fahrenheit"</c>.
    /// </summary>
    public string WeatherTemperatureUnit { get; set; } = "Celsius";

    /// <summary>
    /// Wind speed unit. Valid values: <c>"kmh"</c>, <c>"ms"</c>, <c>"mph"</c>.
    /// </summary>
    public string WeatherWindSpeedUnit { get; set; } = "kmh";

    /// <summary>
    /// Weather data source. Valid values: <c>"MSN"</c>, <c>"OpenMeteo"</c>.
    /// </summary>
    public string WeatherDataSource { get; set; } = "MSN";

    /// <summary>
    /// Default view mode for the weather widget. Valid values: <c>"Today"</c>, <c>"Week"</c>.
    /// </summary>
    public string WeatherDefaultView { get; set; } = "Today";

    /// <summary>
    /// Weather widget skin/theme. Valid values: <c>"Standard"</c>, <c>"Rich"</c>.
    /// </summary>
    public string WeatherSkin { get; set; } = "Rich";

    /// <summary>
    /// Whether to show the 7-day forecast in the widget.
    /// </summary>
    public bool WeatherShowForecast { get; set; } = true;

    /// <summary>
    /// Whether to show sunrise/sunset times.
    /// </summary>
    public bool WeatherShowSunrise { get; set; } = true;

    /// <summary>
    /// Whether to show UV index.
    /// </summary>
    public bool WeatherShowUvIndex { get; set; } = true;

    /// <summary>
    /// Whether to show precipitation probability.
    /// </summary>
    public bool WeatherShowPrecipitation { get; set; } = true;

    /// <summary>
    /// Whether to show humidity.
    /// </summary>
    public bool WeatherShowHumidity { get; set; } = true;

    /// <summary>
    /// Whether to show wind speed.
    /// </summary>
    public bool WeatherShowWind { get; set; } = true;

    /// <summary>
    /// Whether to show atmospheric pressure.
    /// </summary>
    public bool WeatherShowPressure { get; set; }

    /// <summary>
    /// Refresh interval in minutes. Valid values: 15, 30, 60, 180.
    /// </summary>
    public int WeatherRefreshIntervalMinutes { get; set; } = 60;

    // ─── Search Settings ───────────────────────────────────────────────

    /// <summary>Whether the search global hotkey is enabled.</summary>
    public bool SearchHotkeyEnabled { get; set; }

    /// <summary>Search hotkey modifier bit flags.</summary>
    public int SearchHotkeyModifiers { get; set; } = (int)HotkeyModifierKeys.Alt;

    /// <summary>
    /// Search hotkey virtual key code. Default: D (0x44).
    /// Note: Alt+Space is reserved by Windows for the window system menu and
    /// cannot be registered via RegisterHotKey, so Alt+D is used instead.
    /// </summary>
    public int SearchHotkeyKey { get; set; } = 0x44;

    /// <summary>
    /// Search popup display mode. Valid values: "Spotlight", "Home", "Palette".
    /// </summary>
    public string SearchDisplayMode { get; set; } = "Spotlight";

    /// <summary>Whether to include DeskBox internal content (todos, notes, widget files) in search.</summary>
    public bool SearchIncludeDeskBoxContent { get; set; } = true;

    /// <summary>
    /// Whether the user has explicitly allowed DeskBox to query the locally running
    /// Everything instance through its read-only IPC API.
    /// </summary>
    public bool SearchEverythingEnabled { get; set; }

    /// <summary>
    /// An optional, user-selected Everything.exe path. An empty value keeps automatic
    /// detection enabled and is also the default for normal installed copies.
    /// </summary>
    public string SearchEverythingExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether search text is passed through as Everything syntax. Disabled by default
    /// so ordinary DeskBox searches are treated as literal filename phrases.
    /// </summary>
    public bool SearchEverythingAdvancedSyntaxEnabled { get; set; }

    /// <summary>Whether to show recommendations when the search popup opens.</summary>
    public bool SearchShowRecommendations { get; set; } = true;

    /// <summary>
    /// Whether the search popup records and shows search history (recent queries
    /// and pinned favorites). When false, searches are not saved and the empty
    /// state hint is shown instead of prior records.
    /// </summary>
    public bool SearchSaveHistory { get; set; } = true;

    /// <summary>Legacy result cap retained for settings compatibility; paged search ignores it.</summary>
    public int SearchMaxResults { get; set; } = 100;

    /// <summary>Default result tab for a new query: all, app, file, or deskbox.</summary>
    public string SearchDefaultTab { get; set; } = "all";

    /// <summary>
    /// Icon entrance animation style for the recommended-apps grid.
    /// 0 = Staggered fade + scale (Win11), 1 = Staggered fade + rise (Spotlight),
    /// 2 = Wave cascade, 3 = Soft bounce.
    /// </summary>
    public int SearchAppIconAnimation { get; set; } = 0;

    /// <summary>
    /// Custom search popup window bounds (physical pixels). When all four are set the
    /// popup opens at this position/size instead of the default centered, mode-based
    /// placement. Null means "use default". Double-clicking the popup title area clears
    /// these back to null.
    /// </summary>
    public int? SearchPopupCustomX { get; set; }
    public int? SearchPopupCustomY { get; set; }
    public int? SearchPopupCustomWidth { get; set; }
    public int? SearchPopupCustomHeight { get; set; }
}
