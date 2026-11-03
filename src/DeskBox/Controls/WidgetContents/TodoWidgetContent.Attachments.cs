using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.System;

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
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
            IReadOnlyList<string> files =
                await FileOpenPickerService.PickMultipleFilesAsync(
                    owner == IntPtr.Zero ? foreground : owner,
                    [],
                    PickerLocationId.Desktop);
            foreach (string file in files)
            {
                await ViewModel.AddAttachmentPathAsync(item.Id, file);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Add attachment failed: {ex}");
        }
    }

    private async void DetailAttachmentStrip_OpenRequested(
        object? sender,
        AttachmentTileEventArgs e)
    {
        await OpenTodoAttachmentAsync(e.Attachment);
    }

    private async Task OpenTodoAttachmentAsync(TodoAttachmentViewModel attachment)
    {

        try
        {
            if (File.Exists(attachment.FilePath))
            {
                bool launched = false;
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
                    launched = await Launcher.LaunchFileAsync(file);
                }
                catch (Exception launchEx)
                {
                    App.LogVerbose($"[Todo] Launcher.LaunchFileAsync declined '{attachment.FilePath}': {launchEx.Message}");
                }

                if (!launched)
                {
                    DeskBox.Platform.Win32Helper.OpenFileOrChooseApp(IntPtr.Zero, attachment.FilePath);
                }

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
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Open attachment failed: {ex}");
        }
    }

    private async void DetailAttachmentStrip_RemoveRequested(
        object? sender,
        AttachmentTileEventArgs e)
    {
        await DeleteDetailAttachmentAsync(e.Attachment);
    }

    private async Task<bool> DeleteDetailAttachmentAsync(
        TodoAttachmentViewModel attachment)
    {
        if (ViewModel?.SelectedDetailItem is not { } item)
        {
            return false;
        }

        // The view layer owns the App scope: hand the view model the
        // file-safety recycle kernel for the managed copy's deletion.
        FileService? fileService = (App.Current as App)?.FileService;
        return await ViewModel.DeleteAttachmentAsync(
            item.Id,
            attachment.Id,
            fileService is null ? null : path => fileService.DeleteEntryAsync(path, recycle: true));
    }
}
