using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.ViewModels;

public sealed class TodoAttachmentViewModel : ObservableObject
{
    internal const string PreviewCacheScope = "attachment-preview";
    internal const string TodoPreviewCacheScope = "attachment-preview:todo";
    internal const string QuickCapturePreviewCacheScope = "attachment-preview:quickcapture";
    internal const int PreviewDecodePixelWidth = 128;

    internal static string GetTodoPreviewCacheScope(string? widgetId) =>
        string.IsNullOrWhiteSpace(widgetId)
            ? TodoPreviewCacheScope
            : $"{TodoPreviewCacheScope}:{widgetId}";

    private readonly string _cacheScope;
    private BitmapImage? _thumbnail;
    private bool _thumbnailLoadAttempted;

    public TodoAttachmentViewModel(TodoAttachment attachment, string cacheScope = PreviewCacheScope)
    {
        Attachment = attachment;
        _cacheScope = string.IsNullOrWhiteSpace(cacheScope) ? PreviewCacheScope : cacheScope;
    }

    public TodoAttachment Attachment { get; }

    public string StorageMode => Attachment.StorageMode;

    public bool IsManagedCopy => Attachment.IsManagedCopy;

    public string Id => Attachment.Id;

    public string FilePath => Attachment.FilePath;

    public string DisplayName => Attachment.DisplayName;

    public string Type => Attachment.Type;

    public bool Exists => File.Exists(FilePath) || Directory.Exists(FilePath);

    public bool IsImage => string.Equals(Type, "image", StringComparison.OrdinalIgnoreCase);

    public Visibility ImageCopyVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(ThumbnailVisibility));
                OnPropertyChanged(nameof(FileIconVisibility));
            }
        }
    }

    public Visibility ThumbnailVisibility => Thumbnail is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility FileIconVisibility => Thumbnail is null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string Glyph => Type switch
    {
        "image" => "\uEB9F",
        "pdf" => "\uEA90",
        "folder" => "\uE8B7",
        _ => "\uE8A5"
    };

    public async Task EnsureThumbnailAsync()
    {
        if (!IsImage || Thumbnail is not null || _thumbnailLoadAttempted || !File.Exists(FilePath))
        {
            return;
        }

        _thumbnailLoadAttempted = true;
        try
        {
            string previewPath = ResolvePreviewSourcePath(FilePath);
            Thumbnail = await IconHelper.GetIconAsync(
                previewPath,
                decodePixelWidth: PreviewDecodePixelWidth,
                cacheScope: _cacheScope);
        }
        finally
        {
            // A freshly captured image can briefly be unavailable while its
            // managed copy is being committed. Keep the placeholder retryable
            // when virtualization presents the item again.
            _thumbnailLoadAttempted = Thumbnail is not null;
        }
    }

    public void ReleaseThumbnail()
    {
        Thumbnail = null;
        _thumbnailLoadAttempted = false;
    }

    internal static string ResolvePreviewSourcePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return filePath;
        }

        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            string stem = Path.GetFileNameWithoutExtension(filePath);
            if (!string.IsNullOrWhiteSpace(directory) &&
                !string.IsNullOrWhiteSpace(stem) &&
                string.Equals(Path.GetFileName(directory), "images", StringComparison.OrdinalIgnoreCase))
            {
                string? parentDirectory = Path.GetDirectoryName(directory);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    string siblingThumbnail = Path.Combine(parentDirectory, "thumbnails", $"{stem}.png");
                    if (File.Exists(siblingThumbnail))
                    {
                        return siblingThumbnail;
                    }
                }
            }
        }
        catch
        {
        }

        return filePath;
    }
}