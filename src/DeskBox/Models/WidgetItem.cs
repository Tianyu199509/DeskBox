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
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Absolute path to the item on disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPath))]
    public partial string Path { get; set; } = string.Empty;

    public string FullPath => Path;

    /// <summary>Resolved target path when the item is a .lnk shortcut.</summary>
    [ObservableProperty]
    public partial string TargetPath { get; set; } = string.Empty;

    /// <summary>Thumbnail / icon image for display in the widget.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconVisibility))]
    [NotifyPropertyChangedFor(nameof(FallbackIconVisibility))]
    public partial BitmapImage? Icon { get; set; }

    /// <summary>File size in bytes (0 for folders).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    public partial long FileSize { get; set; }

    /// <summary>Number of visible direct children when this item represents a folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    public partial int FolderItemCount { get; set; }

    /// <summary>Whether the visible child count has been loaded for a folder item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    public partial bool IsFolderItemCountLoaded { get; set; } = true;

    /// <summary>Last modification timestamp.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    public partial DateTime LastModified { get; set; }

    /// <summary>Filesystem creation timestamp, used by automatic date stacks.</summary>
    [ObservableProperty]
    public partial DateTime CreatedAt { get; set; }

    /// <summary>Time this item was first added to or observed by its DeskBox widget.</summary>
    [ObservableProperty]
    public partial DateTimeOffset AddedAt { get; set; }

    /// <summary>Windows Property System kind such as document, picture, music, or video.</summary>
    [ObservableProperty]
    public partial string ShellKind { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsShellKindLoaded { get; set; }

    /// <summary>Whether this item is a .lnk shortcut file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackGlyph))]
    public partial bool IsShortcut { get; set; }

    /// <summary>Whether this item represents a directory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryInfo))]
    [NotifyPropertyChangedFor(nameof(FallbackGlyph))]
    public partial bool IsFolder { get; set; }

    /// <summary>Display order within the parent widget.</summary>
    [ObservableProperty]
    public partial int SortOrder { get; set; }

    /// <summary>Whether the item is currently selected inside the widget.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Whether the item is currently marked as cut.</summary>
    [ObservableProperty]
    public partial bool IsCut { get; set; }

    /// <summary>Temporary indentation marker while an automatic stack is expanded.</summary>
    [ObservableProperty]
    public partial bool IsStackChild { get; set; }

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
