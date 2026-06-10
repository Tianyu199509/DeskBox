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
    /// Accent color source. Valid values: <c>"System"</c>, <c>"Custom"</c>.
    /// </summary>
    public string AccentColorMode { get; set; } = "System";

    /// <summary>
    /// Custom accent color in hex format such as <c>#0078D4</c>.
    /// </summary>
    public string CustomAccentColor { get; set; } = "#0078D4";

    /// <summary>Whether DeskBox should launch automatically at Windows startup.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Default width applied to newly created widgets.</summary>
    public double DefaultWidgetWidth { get; set; } = 300;

    /// <summary>Default height applied to newly created widgets.</summary>
    public double DefaultWidgetHeight { get; set; } = 400;

    /// <summary>
    /// Background opacity for widget windows (0.0 - 1.0).
    /// </summary>
    public double WidgetOpacity { get; set; } = 0.64;

    /// <summary>Whether to double click to open files.</summary>
    public bool DoubleClickToOpen { get; set; } = true;

    /// <summary>
    /// Whether shortcut icons should hide the arrow overlay inside DeskBox.
    /// </summary>
    public bool HideShortcutArrowOverlay { get; set; }

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
    public double IconSize { get; set; } = 36;

    /// <summary>
    /// Label text size used by widgets in both icon and list views.
    /// </summary>
    public double TextSize { get; set; } = 11;

    /// <summary>
    /// Layout density for widget content. Valid values: <c>"Comfortable"</c>, <c>"Compact"</c>.
    /// </summary>
    public string LayoutDensity { get; set; } = "Comfortable";

    /// <summary>
    /// Continuous layout density scale used by widget content.
    /// Smaller values create a tighter layout.
    /// </summary>
    public double LayoutDensityScale { get; set; } = 1.0;

    /// <summary>
    /// Whether widgets should use the native Windows acrylic backdrop blur.
    /// Kept in settings for compatibility with older config files.
    /// </summary>
    public bool UseNativeBackdropBlur { get; set; } = true;

    /// <summary>All configured widgets.</summary>
    public List<WidgetConfig> Widgets { get; set; } = [];

    /// <summary>Widget ids that were deleted and should not be restored.</summary>
    public List<string> DeletedWidgetIds { get; set; } = [];
}
