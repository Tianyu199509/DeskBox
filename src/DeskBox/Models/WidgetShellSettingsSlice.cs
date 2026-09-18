namespace DeskBox.Models;

/// <summary>
/// Widget chrome, material, animation, compact/capsule, density, and typography defaults applied to widget windows.
/// </summary>
public sealed class WidgetShellSettingsSlice
{
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
    /// When true, clicking one widget during batch raise keeps only that widget on top;
    /// others move to non-topmost. When false (default), all widgets stay visible together.
    /// </summary>
    public bool FocusClickedWidgetOnRaise { get; set; }

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
}
