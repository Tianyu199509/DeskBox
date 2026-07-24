﻿// Copyright (c) DeskBox. All rights reserved.

using System.Net.Http;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void ItemsView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Cancel = !TryPrepareItemDragPackage(
            e.Data,
            GetDragItems(e.Items.OfType<WidgetItem>().FirstOrDefault()));
    }

    private async void ItemsView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            await HandleItemDragCompletedAsync(args.DropResult);
        }
        catch (Exception ex)
        {
            App.Log($"[Widget] ItemsView_DragItemsCompleted failed: {ex}");
        }
    }

    private async Task HandleItemDragCompletedAsync(DataPackageOperation dropResult)
    {
        var draggedPaths = _activeDragSourcePaths;
        bool hasStorageItems = _activeDragHasStorageItems;
        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;

        if (draggedPaths.Length == 0)
        {
            return;
        }

        bool cursorOnDesktop = IsCursorOnDesktop();
        bool cursorOverWindow = IsCursorOverThisWindow();

        App.Log(
            $"[DragComplete] widget='{ViewModel.Name}' followsDefault={ViewModel.FollowsDefaultStoragePath} " +
            $"mapped='{ViewModel.MappedFolderPath}' dropResult={dropResult} hasStorageItems={hasStorageItems} " +
            $"cursorOnDesktop={cursorOnDesktop} cursorOverWindow={cursorOverWindow} paths={draggedPaths.Length}");

        if (ViewModel.FollowsDefaultStoragePath &&
            cursorOnDesktop &&
            !cursorOverWindow)
        {
            if (hasStorageItems || dropResult == DataPackageOperation.Move)
            {
                await RefreshAfterDragOutAsync(draggedPaths);
                return;
            }

            await MoveDraggedPathsBackToDesktopAsync(draggedPaths, useShellProgress: true);
            return;
        }

        // For mapped folder widgets, when items are dragged out to desktop
        // (or any location outside this widget), the shell handles the
        // move directly via OLE.  WinUI reports dropResult=None because
        // the drop happened outside any WinUI drop target.  We need to
        // refresh to remove the moved items.
        if (!ViewModel.FollowsDefaultStoragePath &&
            !string.IsNullOrEmpty(ViewModel.MappedFolderPath) &&
            !cursorOverWindow &&
            (hasStorageItems || dropResult == DataPackageOperation.Move))
        {
            await RefreshAfterDragOutAsync(draggedPaths);
            return;
        }

        if (dropResult != DataPackageOperation.Move)
        {
            return;
        }

        ClearRemovedCutPaths();
    }

    /// <summary>
    /// Handles the post-drag-out refresh for items dragged from this widget
    /// to an external target (Explorer, desktop, etc.).
    ///
    /// The Shell OLE move may take anywhere from milliseconds (small files)
    /// to many minutes (large files/folders).  We must NOT:
    /// - Do optimistic removal (we don't know if it's a copy or a move)
    /// - Call RefreshFromConfigAsync (it restarts the folder watcher,
    ///   clearing pending events and potentially missing the deletion)
    /// - Poll continuously at a fixed interval (wastes resources for long transfers)
    ///
    /// Instead we use exponential backoff: start at 300ms and double each
    /// iteration, capped at 5 minutes.  This gives:
    ///   300ms → 600ms → 1.2s → 2.4s → 4.8s → 9.6s → 19s → 38s → 76s → 152s → 300s
    /// Small files are caught within ~300-600ms; large files are caught
    /// whenever the transfer finishes.  Each check is a single File.Exists
    /// probe — essentially zero cost.
    /// </summary>
    private async Task RefreshAfterDragOutAsync(string[] draggedPaths)
    {
        ClearRemovedCutPaths();
        UpdateEmptyState();

        int delayMs = 300;
        while (delayMs <= 300_000)
        {
            await Task.Delay(delayMs);

            // Window may have been closed while we were waiting.
            if (IsClosing)
            {
                return;
            }

            bool anyExists = false;
            foreach (var path in draggedPaths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    anyExists = true;
                    break;
                }
            }

            if (!anyExists)
            {
                App.Log($"[DragComplete] Files gone after ~{delayMs}ms, refreshing.");
                await ViewModel.RefreshFolderContentsAsync();
                ClearRemovedCutPaths();
                UpdateEmptyState();
                return;
            }

            delayMs = (int)Math.Min(delayMs * 2, 300_000);
        }

        App.Log("[DragComplete] Files still exist after 10min, giving up.");
    }

    private IReadOnlyList<WidgetItem> GetDragItems(WidgetItem? fallbackItem)
    {
        var selectedItems = GetSelectedItems();
        if (fallbackItem is null)
        {
            return selectedItems;
        }

        if (selectedItems.Any(item => ReferenceEquals(item, fallbackItem)))
        {
            return selectedItems;
        }

        return [fallbackItem];
    }

    private bool TryPrepareItemDragPackage(DataPackage dataPackage, IReadOnlyList<WidgetItem> draggedItems)
    {
        if (draggedItems.Count == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        var sourcePaths = draggedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        dataPackage.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        var storageItems = App.Current.FileService.GetStorageItems(sourcePaths);
        if (storageItems.Count > 0)
        {
            dataPackage.SetStorageItems(storageItems, false);
        }
        else
        {
            App.Log($"[DragStart] No StorageItems for selected paths; using path fallback. paths={sourcePaths.Length}");
        }

        dataPackage.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
        dataPackage.Properties["DeskBoxSourcePaths"] = sourcePaths;
        dataPackage.Properties["DeskBoxInternalDragToken"] = DeskBoxInternalDragToken;
        dataPackage.Properties.Title = sourcePaths.Length == 1
            ? Path.GetFileName(sourcePaths[0])
            : _localizationService.Format("Widget.ItemCount", sourcePaths.Length);
        dataPackage.SetText(string.Join(Environment.NewLine, sourcePaths));
        _activeDragSourcePaths = sourcePaths;
        _activeDragHasStorageItems = storageItems.Count > 0;
        return true;
    }

    private async Task SyncMoveSourceAsync(string? sourceWidgetId, IReadOnlyList<string> sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(sourceWidgetId) || sourcePaths.Count == 0)
        {
            return;
        }

        if (string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            return;
        }

        if (App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.NotifyItemsMovedOutAsync(sourceWidgetId, sourcePaths);
        }
    }

    private static bool HasDeskBoxInternalDragData(DataPackagePropertySetView properties)
    {
        return string.Equals(
                   TryGetPackageString(properties, "DeskBoxInternalDragToken"),
                   DeskBoxInternalDragToken,
                   StringComparison.Ordinal) &&
               TryGetPackageStringArray(properties, "DeskBoxSourcePaths").Count > 0;
    }

    private static bool HasPathDropData(DataPackageView dataView)
    {
        return TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths").Count > 0 ||
               dataView.Contains(StandardDataFormats.StorageItems) ||
               dataView.Contains(StandardDataFormats.Text) ||
               HasFallbackFileFormats(dataView);
    }

    private static async Task<string[]> GetDropPathsAsync(DataPackageView dataView)
    {
        var sourcePaths = TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length > 0)
        {
            return sourcePaths;
        }

        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            HasFallbackFileFormats(dataView))
        {
            try
            {
                var items = await dataView.GetStorageItemsAsync();
                sourcePaths = items
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                App.Log(
                    $"[DropDiagnostic] GetStorageItemsAsync count={items.Count} pathCount={sourcePaths.Length} " +
                    $"formats={FormatDataPackageFormats(dataView.AvailableFormats)}");
                if (sourcePaths.Length > 0)
                {
                    return sourcePaths;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] GetStorageItemsAsync failed: {ex.Message}");
            }

            sourcePaths = await TryGetLegacyFormatPathsAsync(dataView);
            if (sourcePaths.Length > 0)
            {
                return sourcePaths;
            }
        }

        if (!dataView.Contains(StandardDataFormats.Text))
        {
            return [];
        }

        string text = await dataView.GetTextAsync();
        var pathList = new List<string>();
        foreach (var candidate in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalizeDroppedPath(candidate, out string normalizedPath))
            {
                pathList.Add(normalizedPath);
            }
            else if (TryNormalizeUrl(candidate, out string downloadedPath))
            {
                pathList.Add(downloadedPath);
            }
        }
        return pathList
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// If the candidate is an HTTP(S) URL, downloads it to a temp file
    /// and returns the path. Used for browser drag-drop of images/files
    /// that don't have local file paths.
    /// </summary>
    private static bool TryNormalizeUrl(string candidate, out string downloadedPath)
    {
        downloadedPath = string.Empty;
        string trimmed = candidate.Trim().Trim('"');

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        try
        {
            string fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "download";
            }
            fileName = FileService.SanitizeFileSystemName(fileName);

            string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox", "VirtualDrops", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string destPath = FileService.GetAvailablePath(Path.Combine(tempDir, fileName));

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            var bytes = httpClient.GetByteArrayAsync(uri).GetAwaiter().GetResult();
            File.WriteAllBytes(destPath, bytes);

            App.Log($"[DropDiagnostic] Downloaded url='{uri}' -> '{destPath}' ({bytes.Length} bytes)");
            downloadedPath = destPath;
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[DropDiagnostic] URL download failed for '{uri}': {ex.Message}");
            return false;
        }
    }

    private static bool TryNormalizeDroppedPath(string candidate, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        try
        {
            string fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                normalizedPath = fullPath;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void LogDropDiagnostic(
        string stage,
        DataPackageView dataView,
        DataPackageOperation acceptedOperation,
        bool movesIntoFolder)
    {
        string signature = $"{stage}|{dataView.RequestedOperation}|{acceptedOperation}|{movesIntoFolder}|{FormatDataPackageFormats(dataView.AvailableFormats)}";
        if (stage.StartsWith("Folder", StringComparison.Ordinal))
        {
            if (string.Equals(_lastFolderDragDiagnosticSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastFolderDragDiagnosticSignature = signature;
        }
        else
        {
            if (string.Equals(_lastRootDragDiagnosticSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastRootDragDiagnosticSignature = signature;
        }

        App.Log(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage={stage} " +
            $"mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} managed={ViewModel.FollowsDefaultStoragePath} " +
            $"requested={dataView.RequestedOperation} accepted={acceptedOperation} " +
            $"containsStorage={dataView.Contains(StandardDataFormats.StorageItems)} containsText={dataView.Contains(StandardDataFormats.Text)} " +
            $"fallback={HasFallbackFileFormats(dataView)} " +
            $"formats={FormatDataPackageFormats(dataView.AvailableFormats)}");
    }

    private static string FormatDataPackageFormats(IReadOnlyList<string> formats)
    {
        return formats.Count == 0
            ? "<none>"
            : string.Join(",", formats.OrderBy(format => format, StringComparer.Ordinal));
    }
}
