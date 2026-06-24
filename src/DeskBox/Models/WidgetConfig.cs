namespace DeskBox.Models;

/// <summary>
/// Represents the persisted configuration for a single desktop widget.
/// </summary>
public class WidgetConfig
{
    /// <summary>Unique identifier for this widget instance.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>User-facing display name.</summary>
    public string Name { get; set; } = "Deskbox";

    /// <summary>Screen X position in device-independent pixels.</summary>
    public double X { get; set; } = 100;

    /// <summary>Screen Y position in device-independent pixels.</summary>
    public double Y { get; set; } = 100;

    /// <summary>Widget width in device-independent pixels.</summary>
    public double Width { get; set; } = 300;

    /// <summary>Widget height in device-independent pixels.</summary>
    public double Height { get; set; } = 400;

    /// <summary>Widget content type.</summary>
    public WidgetKind WidgetKind { get; set; } = WidgetKind.File;

    /// <summary>Current view layout mode (Icon grid or List).</summary>
    public ViewMode ViewMode { get; set; } = ViewMode.Icon;

    /// <summary>Whether the widget window is currently shown.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Whether the widget is disabled from desktop restore and tray listing.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Whether moving this widget is locked.</summary>
    public bool IsPositionLocked { get; set; }

    /// <summary>Whether resizing this widget is locked.</summary>
    public bool IsSizeLocked { get; set; }

    /// <summary>
    /// Optional filesystem folder to mirror. When set, the widget auto-populates
    /// from folder contents. When <c>null</c>, items are managed manually.
    /// </summary>
    public string? MappedFolderPath { get; set; }

    /// <summary>
    /// Whether this widget follows the global default managed storage root path.
    /// </summary>
    public bool FollowsDefaultStoragePath { get; set; }

    /// <summary>
    /// Stable subfolder name used when the widget follows the default managed storage path.
    /// </summary>
    public string? ManagedFolderName { get; set; }

    /// <summary>Ordered list of items displayed in this widget.</summary>
    public List<WidgetItemConfig> Items { get; set; } = [];
}

/// <summary>
/// Persisted reference to a single item (file or shortcut) inside a widget.
/// </summary>
public class WidgetItemConfig
{
    /// <summary>Absolute path to the file, folder, or shortcut.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Display order within the parent widget.</summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// Determines how items are laid out inside a widget.
/// </summary>
public enum ViewMode
{
    /// <summary>Items displayed as an icon grid.</summary>
    Icon,

    /// <summary>Items displayed as a vertical list with details.</summary>
    List
}

/// <summary>
/// Defines the functional mode of a widget.
/// </summary>
public enum WidgetKind
{
    /// <summary>File-oriented widget used for references or folder-backed storage.</summary>
    File,

    /// <summary>Built-in lightweight text/link capture widget.</summary>
    QuickCapture,

    /// <summary>Legacy value kept only for migrating old settings files.</summary>
    Productivity
}
