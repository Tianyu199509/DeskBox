using DeskBox.Helpers;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private async void DetailAddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
            InitializeWithWindow.Initialize(picker, owner == IntPtr.Zero ? foreground : owner);

            IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
            foreach (StorageFile file in files)
            {
                await ViewModel.AddAttachmentPathAsync(item.Id, file.Path);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Add attachment failed: {ex}");
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
            if (File.Exists(attachment.FilePath))
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
                await Launcher.LaunchFileAsync(file);
                return;
            }

            if (Directory.Exists(attachment.FilePath))
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(attachment.FilePath);
                await Launcher.LaunchFolderAsync(folder);
                return;
            }

            ShowUndoToast(
                ViewModel?.DetailFileMissingText ?? string.Empty,
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Open attachment failed: {ex}");
        }
    }

    private async void TodoAttachmentPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            await attachment.EnsureThumbnailAsync();
        }
    }

    private async void DetailRemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment } ||
            ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        await ViewModel.DeleteAttachmentAsync(item.Id, attachment.Id);
    }
}
