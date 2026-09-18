namespace DeskBox.Models;

/// <summary>
/// File widget behavior: open/drop semantics, stacks, managed storage, and file-name presentation.
/// </summary>
public sealed class FileWidgetSettingsSlice
{
    /// <summary>Whether to double click to open files.</summary>
    public bool DoubleClickToOpen { get; set; } = true;

    /// <summary>
    /// How child folders are opened from file widgets.
    /// Valid values: <c>"Explorer"</c>, <c>"Embedded"</c>.
    /// </summary>
    public string FileWidgetFolderOpenBehavior { get; set; } = "Explorer";

    /// <summary>
    /// Whether right-clicking a single file item in a file widget shows the
    /// native Windows context menu instead of the built-in DeskBox menu.
    /// </summary>
    public bool FileItemSystemContextMenuEnabled { get; set; }

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
    /// Whether DeskBox maintains a desktop shortcut that opens the current
    /// managed storage root. The shortcut is intentionally independent of the
    /// application executable so it remains useful after uninstalling.
    /// </summary>
    public bool ManagedStorageDesktopShortcutEnabled { get; set; }

    /// <summary>
    /// Absolute path of the desktop shortcut created by DeskBox. Tracking the
    /// exact path lets DeskBox avoid overwriting or deleting unrelated links.
    /// </summary>
    public string ManagedStorageDesktopShortcutPath { get; set; } = string.Empty;

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
}
