namespace DeskBox.Models;

/// <summary>
/// Root settings object that is serialized to/from the JSON config file.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Application theme. Valid values: <c>"System"</c>, <c>"Light"</c>, <c>"Dark"</c>.
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Tray icon style. Valid values: <c>"System"</c>, <c>"Colorful"</c>, <c>"Black"</c>, <c>"White"</c>.
    /// </summary>
    public string TrayIconStyle { get; set; } = "Colorful";

    /// <summary>
    /// Display language. Valid values: <c>"System"</c>, <c>"zh-CN"</c>, <c>"en-US"</c>.
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

    /// <summary>Whether the built-in Quick Capture widget is enabled.</summary>
    public bool QuickCaptureEnabled { get; set; }
    public bool TodoEnabled { get; set; }

    /// <summary>
    /// Enabled state for singleton feature widgets, keyed by <see cref="WidgetKind"/> name.
    /// Legacy boolean properties are still kept as compatibility mirrors.
    /// </summary>
    public Dictionary<string, bool> FeatureWidgetEnabledStates { get; set; } = [];

    /// <summary>Whether Quick Capture should record recent clipboard text and links.</summary>
    public bool QuickCaptureClipboardEnabled { get; set; } = true;

    /// <summary>Whether Quick Capture should record clipboard images.</summary>
    public bool QuickCaptureImageClipboardEnabled { get; set; } = true;

    /// <summary>Maximum number of recent clipboard text/link entries kept by Quick Capture.</summary>
    public int QuickCaptureRecentLimit { get; set; } = 30;

    /// <summary>Default Quick Capture view used when the widget opens. Valid values: <c>"Records"</c>, <c>"Pinned"</c>, <c>"Recent"</c>.</summary>
    public string QuickCaptureDefaultView { get; set; } = "Records";

    /// <summary>Where newly added Todo tasks are inserted. Valid values: <c>"Top"</c>, <c>"Bottom"</c>.</summary>
    public string TodoNewTaskPosition { get; set; } = "Top";

    /// <summary>Default Todo filter used when the widget opens. Valid values: <c>"All"</c>, <c>"Today"</c>, <c>"Important"</c>, <c>"Completed"</c>.</summary>
    public string TodoDefaultFilter { get; set; } = "All";

    /// <summary>Whether completed Todo tasks remain visible in non-completed views.</summary>
    public bool TodoShowCompletedTasks { get; set; } = true;

    /// <summary>Whether the Todo footer item count is visible.</summary>
    public bool TodoShowFooterStats { get; set; } = true;

    /// <summary>Whether the Todo footer clear-completed command is visible.</summary>
    public bool TodoShowClearCompletedButton { get; set; } = true;

    /// <summary>Whether Todo delete and clear-completed commands ask for confirmation first.</summary>
    public bool TodoConfirmBeforeDelete { get; set; }

    /// <summary>Whether the Music widget uses album artwork color as a soft backdrop.</summary>
    public bool MusicUseArtworkBackdrop { get; set; } = true;

    /// <summary>Whether the Music widget shows playback rhythm bars.</summary>
    public bool MusicShowRhythmBars { get; set; } = true;

    /// <summary>Music widget playback rhythm visual style. Valid values: <c>"SoftWave"</c>, <c>"GlassSpectrum"</c>, <c>"DotPulse"</c>, <c>"LineSpectrum"</c>.</summary>
    public string MusicRhythmStyle { get; set; } = "SoftWave";

    /// <summary>Whether the Music widget album cover reacts lightly to pointer hover.</summary>
    public bool MusicEnableCoverHoverMotion { get; set; } = true;

    /// <summary>Last file widget used as the target for saving Quick Capture content.</summary>
    public string LastQuickCaptureFileWidgetId { get; set; } = string.Empty;

    /// <summary>Whether the global hotkey is enabled.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Global hotkey modifier bit flags.</summary>
    public int GlobalHotkeyModifiers { get; set; } = (int)HotkeyModifierKeys.None;

    /// <summary>Global hotkey virtual key code.</summary>
    public int GlobalHotkeyKey { get; set; } = (int)Windows.System.VirtualKey.F7;

    /// <summary>Whether the first-run onboarding has been completed or skipped.</summary>
    public bool HasCompletedOnboarding { get; set; }

    /// <summary>Default width applied to newly created widgets.</summary>
    public double DefaultWidgetWidth { get; set; } = 280;

    /// <summary>Default height applied to newly created widgets.</summary>
    public double DefaultWidgetHeight { get; set; } = 400;

    /// <summary>
    /// Background opacity for widget windows (0.0 - 1.0).
    /// </summary>
    public double WidgetOpacity { get; set; } = 0.80;

    /// <summary>
    /// Native DWM corner style for widget windows.
    /// Valid values: <c>"Default"</c>, <c>"Square"</c>, <c>"Small"</c>, <c>"Round"</c>.
    /// </summary>
    public string WidgetCornerPreference { get; set; } = "Small";

    /// <summary>
    /// Animation effect used when desktop widgets show or hide.
    /// </summary>
    public string WidgetAnimationEffect { get; set; } = "Fade";

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
    /// Default chrome/title mode for display widgets such as Music, Weather, and System Monitor.
    /// Valid values: <c>"Standard"</c>, <c>"Compact"</c>, <c>"Overlay"</c>, <c>"Hidden"</c>.
    /// </summary>
    public string DisplayWidgetChromeMode { get; set; } = "Overlay";

    /// <summary>
    /// Default chrome/title mode for interactive widgets such as files, Quick Capture, Todo, and Tags.
    /// Valid values: <c>"Standard"</c>, <c>"Compact"</c>, <c>"Overlay"</c>, <c>"Hidden"</c>.
    /// </summary>
    public string InteractiveWidgetChromeMode { get; set; } = "Standard";

    /// <summary>Whether to double click to open files.</summary>
    public bool DoubleClickToOpen { get; set; } = true;

    /// <summary>
    /// Whether shortcut icons should hide the arrow overlay inside DeskBox.
    /// </summary>
    public bool HideShortcutArrowOverlay { get; set; } = true;

    /// <summary>
    /// Whether image files in file widgets should use the system file icon instead of image thumbnails.
    /// </summary>
    public bool ShowImageFilesAsIcons { get; set; }

    /// <summary>
    /// Whether to show action buttons on widget hover.
    /// </summary>
    public bool ShowHoverButtons { get; set; } = true;

    /// <summary>
    /// Whether list view should show secondary file details under item names.
    /// </summary>
    public bool ShowListItemDetails { get; set; }

    /// <summary>
    /// How files should be handled when dropped into a managed storage widget.
    /// Valid values: <c>"Move"</c>, <c>"Copy"</c>.
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
    /// Icon size used by widgets in icon view.
    /// </summary>
    public double IconSize { get; set; } = 30;

    /// <summary>
    /// Label text size used by widgets in both icon and list views.
    /// </summary>
    public double TextSize { get; set; } = 11.5;

    /// <summary>
    /// Layout density for widget content. Valid values: <c>"Comfortable"</c>, <c>"Compact"</c>.
    /// </summary>
    public string LayoutDensity { get; set; } = "Comfortable";

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
    /// When true, clicking one widget during batch raise keeps only that widget on top;
    /// others move to non-topmost. When false (default), all widgets stay visible together.
    /// </summary>
    public bool FocusClickedWidgetOnRaise { get; set; }

    /// <summary>Widget ids that were deleted and should not be restored.</summary>
    public List<string> DeletedWidgetIds { get; set; } = [];
}
