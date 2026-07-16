using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    private void DetailCopyButton_Click(object sender, RoutedEventArgs e)
    {
        string content = string.IsNullOrWhiteSpace(DetailBodyTextBox.Text)
            ? DetailTitleTextBox.Text.Trim()
            : DetailBodyTextBox.Text.Trim();
        IEnumerable<TodoAttachment> attachments = _detailItem?.Attachments
            .Select(attachment => attachment.Attachment) ??
            _pendingDetailAttachments.Select(file => new TodoAttachment
            {
                FilePath = file.Path,
                DisplayName = file.DisplayName,
                Type = AttachmentStorageService.GetAttachmentType(file.Path)
            });
        string text = QuickCaptureClipboardFormatter.FormatContent(
            content,
            attachments,
            _localizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
        App.Current.QuickCaptureService?.MarkClipboardTextWrittenByDeskBox(text);
        ShowCopyToast();
    }

    private async void DetailAddFileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hWnd);

        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        var droppedFiles = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            .Select(file => new DroppedFilePath(file.Path, file.Name, ForceManagedCopy: false))
            .ToList();
        await AddFilesToCurrentDetailAsync(droppedFiles);
    }

    private async Task AddFilesToCurrentDetailAsync(IReadOnlyList<DroppedFilePath> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (_isCreatingDetail || _detailItem is null)
        {
            foreach (DroppedFilePath file in files)
            {
                if (!_pendingDetailAttachments.Any(existing =>
                        string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    _pendingDetailAttachments.Add(file);
                }
            }
            RefreshDetailAttachmentList();
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.AddAttachmentsAsync(_detailItem, files);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachmentList();
        }
    }

    private async void DetailOpenAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return;
        }

        try
        {
            if (!File.Exists(attachment.FilePath))
            {
                ShowStatusToast(_localizationService.T("Todo.Detail.FileMissing"));
                return;
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to open attachment: {ex}");
        }
    }

    private async void DetailRemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return;
        }

        if (_isCreatingDetail || _detailItem is null)
        {
            _pendingDetailAttachments.RemoveAll(file =>
                string.Equals(file.Path, attachment.FilePath, StringComparison.OrdinalIgnoreCase));
            RefreshDetailAttachmentList();
            return;
        }

        if (_detailItem.Attachments.Count == 1 && string.IsNullOrWhiteSpace(DetailBodyTextBox.Text))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.DeleteAttachmentAsync(
            _detailItem,
            attachment.Id);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachmentList();
        }
    }

    private async void QuickCaptureAttachmentPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            await attachment.EnsureThumbnailAsync();
        }
    }

    private void RefreshDetailAttachmentList()
    {
        IReadOnlyList<TodoAttachmentViewModel> attachments = _detailItem?.Attachments ??
            _pendingDetailAttachments.Select(file => new TodoAttachmentViewModel(new TodoAttachment
            {
                FilePath = file.Path,
                DisplayName = file.DisplayName,
                Type = AttachmentStorageService.GetAttachmentType(file.Path),
                StorageMode = TodoAttachment.LinkedStorageMode
            })).ToList();
        DetailAttachmentsList.ItemsSource = attachments;
        DetailAttachmentScroller.Visibility = attachments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void QuickCaptureAttachmentCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment } ||
            !attachment.IsImage ||
            !File.Exists(attachment.FilePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            var dataPackage = new DataPackage();
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            DeskBoxClipboardWriteScope.MarkWrite(hasImage: true, paths: [attachment.FilePath]);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy attachment image: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }
}
