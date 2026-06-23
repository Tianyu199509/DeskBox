using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Services;
using Microsoft.UI.Xaml;
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
    [NotifyPropertyChangedFor(nameof(IconVisibility))]
    [NotifyPropertyChangedFor(nameof(FallbackIconVisibility))]
    private BitmapImage? _icon;

    /// <summary>File size in bytes (0 for folders).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    private long _fileSize;

    /// <summary>Number of visible direct children when this item represents a folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    private int _folderItemCount;

    /// <summary>Whether the visible child count has been loaded for a folder item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    private bool _isFolderItemCountLoaded = true;

    /// <summary>Last modification timestamp.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    private DateTime _lastModified;

    /// <summary>Whether this item is a .lnk shortcut file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackGlyph))]
    private bool _isShortcut;

    /// <summary>Whether this item represents a directory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    [NotifyPropertyChangedFor(nameof(FallbackGlyph))]
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

    public string SecondaryInfo
    {
        get
        {
            if (IsFolder)
            {
                return IsFolderItemCountLoaded
                    ? LocalizeFormat("FileInfo.FolderItems", FolderItemCount)
                    : Localize("FileInfo.Folder");
            }

            string typeText = FormatFileSize(FileSize);
            return LastModified == default
                ? typeText
                : LocalizeFormat("FileInfo.FileModified", typeText, LastModified);
        }
    }

    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FallbackIconVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    public string FallbackGlyph => IsFolder
        ? "\uE8B7"
        : IsShortcut
            ? "\uE71B"
            : "\uE7C3";

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return Localize("FileInfo.File");
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} B"
            : $"{value:0.#} {units[unitIndex]}";
    }

    private static string Localize(string key)
    {
        return TryGetLocalizationService()?.T(key) ?? LocalizationService.DefaultText(key);
    }

    private static string LocalizeFormat(string key, params object[] args)
    {
        return TryGetLocalizationService()?.Format(key, args) ?? LocalizationService.DefaultFormat(key, args);
    }

    private static LocalizationService? TryGetLocalizationService()
    {
        try
        {
            return global::DeskBox.App.Current?.LocalizationService;
        }
        catch
        {
            return null;
        }
    }
}
