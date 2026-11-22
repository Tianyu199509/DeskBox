using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Presentation and interaction guards for paths that are currently owned by
/// a Windows copy/move operation. The registry itself is UI-agnostic; this
/// partial keeps the policy local to the file surface.
/// </summary>
public sealed partial class FileSurfaceContent
{
    private void TransferSessions_StateChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshTransferVisuals();
            return;
        }

        DispatcherQueue.TryEnqueue(RefreshTransferVisuals);
    }

    private void RefreshTransferVisuals()
    {
        if (_isDisposed)
        {
            return;
        }

        foreach (Border border in _itemSurfaces.ToArray())
        {
            if (border.XamlRoot is null)
            {
                _itemSurfaces.Remove(border);
                continue;
            }

            FileItemSurface? surface = FileItemSurface.FindOwner(border);
            if (surface is null)
            {
                continue;
            }

            WidgetItem? item = surface.DataContext as WidgetItem ??
                border.DataContext as WidgetItem;
            FileTransferPathState state = GetTransferState(item);
            surface.SetTransferState(
                state,
                GetTransferStatusText(state));
        }
    }

    private FileTransferPathState GetTransferState(WidgetItem? item)
    {
        return item is null || string.IsNullOrWhiteSpace(item.Path)
            ? FileTransferPathState.None
            : _fileService.TransferSessions.GetState(item.Path);
    }

    private string GetTransferStatusText(FileTransferPathState state)
    {
        if (!state.IsActive)
        {
            return string.Empty;
        }

        if (state.IsSource)
        {
            return string.Concat(
                T(state.IsMove ? "Common.Move" : "Common.Copy"),
                "…");
        }

        return string.Concat(T("Widget.Import.Title"), "…");
    }

    private bool TryBlockTransferOpen(WidgetItem item)
    {
        FileTransferPathState state = GetTransferState(item);
        if (!state.BlocksOpen)
        {
            return false;
        }

        ShowTransferBlockedFeedback(state);
        return true;
    }

    private bool TryBlockTransferMutation(IEnumerable<WidgetItem> items)
    {
        foreach (WidgetItem item in items)
        {
            FileTransferPathState state = GetTransferState(item);
            if (state.BlocksMutation)
            {
                ShowTransferBlockedFeedback(state);
                return true;
            }
        }

        return false;
    }

    private bool TryBlockTransferMutation(WidgetItem? item)
    {
        return item is not null && TryBlockTransferMutation([item]);
    }

    private bool TryBlockTransferClipboard(
        IEnumerable<WidgetItem> items,
        bool cut)
    {
        foreach (WidgetItem item in items)
        {
            FileTransferPathState state = GetTransferState(item);
            bool blocked = cut
                ? state.BlocksMutation
                : state.BlocksOpen;
            if (blocked)
            {
                ShowTransferBlockedFeedback(state);
                return true;
            }
        }

        return false;
    }

    private bool TryGetSelectedTransferState(
        out FileTransferPathState state)
    {
        foreach (WidgetItem item in GetSelectedItems())
        {
            state = GetTransferState(item);
            if (state.IsActive)
            {
                return true;
            }
        }

        state = FileTransferPathState.None;
        return false;
    }

    private bool TryBlockTransferMutation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        FileTransferPathState state =
            _fileService.TransferSessions.GetState(path);
        if (!state.BlocksMutation)
        {
            return false;
        }

        ShowTransferBlockedFeedback(state);
        return true;
    }

    private bool HasActiveTransferSource(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                _fileService.TransferSessions.IsPathActive(path))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsTransferPathActive(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            _fileService.TransferSessions.IsPathActive(path);
    }

    private bool HasTransferConflict(
        IEnumerable<string> sourcePaths,
        string? destinationPath)
    {
        return HasActiveTransferSource(sourcePaths) ||
            IsTransferPathActive(destinationPath);
    }

    private void ShowTransferBlockedFeedback(FileTransferPathState state)
    {
        string status = GetTransferStatusText(state);
        ShowFeedback(new WidgetFeedbackRequest(
            string.IsNullOrWhiteSpace(status)
                ? T("Widget.Import.Description")
                : status,
            WidgetFeedbackSeverity.Warning,
            "file-transfer-busy"));
    }

    private bool TryBlockStoreVirtualizedDestination(string? destinationPath)
    {
        if (!Services.DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(destinationPath))
        {
            return false;
        }

        ShowStoreVirtualizedDestinationBlockedFeedback();
        return true;
    }

    private bool TryBlockStoreVirtualizedDestination(IEnumerable<WidgetItem> items)
    {
        foreach (WidgetItem item in items)
        {
            if (Services.DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(item.Path))
            {
                ShowStoreVirtualizedDestinationBlockedFeedback();
                return true;
            }
        }

        return false;
    }

    private void ShowStoreVirtualizedDestinationBlockedFeedback()
    {
        ShowFeedback(new WidgetFeedbackRequest(
            T("Widget.DropBlocked.StoreVirtualizedAppData"),
            WidgetFeedbackSeverity.Warning,
            "store-virtualized-appdata-drop-blocked"));
    }

    private void ShowMaterializationSkippedWarning(int skippedCount)
    {
        if (skippedCount <= 0)
        {
            return;
        }

        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                "Widget.ImportPartialMaterializationSkipped",
                skippedCount),
            WidgetFeedbackSeverity.Warning,
            "file-import-partial-materialization-skipped"));
    }
}
