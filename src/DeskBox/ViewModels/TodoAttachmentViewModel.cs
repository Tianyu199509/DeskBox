using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.ViewModels;

public sealed class TodoAttachmentViewModel : ObservableObject
{
    private BitmapImage? _thumbnail;
    private bool _thumbnailLoadAttempted;

    public TodoAttachmentViewModel(TodoAttachment attachment)
    {
        Attachment = attachment;
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
        if (!IsImage || _thumbnailLoadAttempted)
        {
            return;
        }

        _thumbnailLoadAttempted = true;
        Thumbnail = await IconHelper.GetIconAsync(FilePath);
    }
}
