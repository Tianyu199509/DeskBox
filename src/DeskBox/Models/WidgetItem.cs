using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Models;

/// <summary>
/// Runtime representation of a file, folder, or shortcut displayed inside a widget.
/// Observable so the UI can bind directly to property changes.
/// </summary>
public partial class WidgetItem : ObservableObject
{
    /// <summary>Display name (typically the filename without extension for shortcuts).</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Absolute path to the item on disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPath))]
    private string _path = string.Empty;

    public string FullPath => Path;

    /// <summary>Resolved target path when the item is a .lnk shortcut.</summary>
    [ObservableProperty]
    private string _targetPath = string.Empty;

    /// <summary>Thumbnail / icon image for display in the widget.</summary>
    [ObservableProperty]
    private BitmapImage? _icon;

    /// <summary>File size in bytes (0 for folders).</summary>
    [ObservableProperty]
    private long _fileSize;

    /// <summary>Last modification timestamp.</summary>
    [ObservableProperty]
    private DateTime _lastModified;

    /// <summary>Whether this item is a .lnk shortcut file.</summary>
    [ObservableProperty]
    private bool _isShortcut;

    /// <summary>Whether this item represents a directory.</summary>
    [ObservableProperty]
    private bool _isFolder;

    /// <summary>Display order within the parent widget.</summary>
    [ObservableProperty]
    private int _sortOrder;

    /// <summary>Whether the item is currently selected inside the widget.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Whether the item is currently marked as cut.</summary>
    [ObservableProperty]
    private bool _isCut;
}
