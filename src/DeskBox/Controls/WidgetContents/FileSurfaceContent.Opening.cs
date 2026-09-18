using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private const long OpenedSelectionSuppressionMs = 3000;

    private readonly FileOpenRequestGate _openRequestGate = new();
    private long _openStateGeneration;

    private string OpeningStatusText =>
        string.Concat(T("Widget.Open"), "…");

    private async Task OpenFileItemAsync(WidgetItem item)
    {
        // Keep the gate identity stable even if the item is renamed while a
        // Shell request is still in flight.
        string requestPath = item.Path;
        if (!TryBeginOpenItem(requestPath))
        {
            PerformanceLogger.Mark(
                "FileSurface.OpenItem.DuplicateSuppressed",
                $"widget={WidgetId} kind={GetOpenItemKind(item)}");
            return;
        }

        long generation = _openStateGeneration;
        long stackPopoverGeneration = _stackPopoverShowGeneration;
        string kind = GetOpenItemKind(item);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using IDisposable timing = PerformanceLogger.Measure(
            "FileSurface.OpenItem",
            $"widget={WidgetId} kind={kind}");

        bool dispatched = false;
        try
        {
            SetOpeningVisual(item, isOpening: true);
            PerformanceLogger.Mark(
                "FileSurface.OpenItem.Requested",
                $"widget={WidgetId} kind={kind}");

            // Give the item badge and the pressed state a dispatcher turn to
            // render before any provider or Shell call begins. A toast alone
            // is not sufficient because a synchronous call can otherwise
            // block the same UI queue before the toast is painted.
            await Task.Yield();

            FileService.OpenItemResult result = await ViewModel.OpenItemAsync(
                item,
                _hostWindowHandle,
                _lifetimeCancellation.Token);
            dispatched = result == FileService.OpenItemResult.OpenedOrHandled;

            if (_isDisposed || generation != _openStateGeneration)
            {
                return;
            }

            PerformanceLogger.Mark(
                "FileSurface.OpenItem.Completed",
                $"widget={WidgetId} kind={kind} result={result} " +
                $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}");

            if (result == FileService.OpenItemResult.Failed)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemFailed"),
                    WidgetFeedbackSeverity.Error,
                    "file-open-failed"));
            }
            else if (result == FileService.OpenItemResult.Busy)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemBusy"),
                    WidgetFeedbackSeverity.Warning,
                    "file-open-busy"));
            }
            else if (result == FileService.OpenItemResult.ShortcutTargetMissing)
            {
                // The stored target is gone, so the link went to Windows instead
                // of being launched. Saying "Windows has accepted the open
                // request" here would be a false success.
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemShortcutTargetMissing"),
                    WidgetFeedbackSeverity.Warning,
                    "file-open-shortcut-target-missing"));
            }
            else if (result == FileService.OpenItemResult.RequiresOpenWithPicker)
            {
                // No shell association: show the system application picker
                // here, because every Shell dispatch path reports a dismissed
                // picker as a silent success. The launcher reports a real
                // launch (true) versus a user dismissal (false).
                bool launched = false;
                bool pickerFailed = false;
                try
                {
                    Windows.Storage.StorageFile file = await Windows.Storage
                        .StorageFile
                        .GetFileFromPathAsync(item.Path);
                    launched = await Windows.System.Launcher.LaunchFileAsync(
                        file,
                        new Windows.System.LauncherOptions
                        {
                            DisplayApplicationPicker = true
                        });
                }
                catch (Exception ex)
                {
                    pickerFailed = true;
                    App.Log(
                        "[FileSurface] Open With picker failed " +
                        $"widget={WidgetId} kind={kind}: {ex}");
                }

                if (_isDisposed || generation != _openStateGeneration)
                {
                    return;
                }

                App.Log(
                    $"[FileSurface] Open With picker launched={launched} " +
                    $"path='{item.Path}'");
                if (pickerFailed)
                {
                    ShowFeedback(new WidgetFeedbackRequest(
                        T("Widget.OpenItemFailed"),
                        WidgetFeedbackSeverity.Error,
                        "file-open-failed"));
                }

                // A dismissed picker stays silent: the dialog APIs report a
                // user dismissal as success, so it is not distinguishable
                // from a real launch here.
                ClearOpenedItemSelectionAfterDispatch(
                    item,
                    stackPopoverGeneration,
                    generation);
            }
            else if (result == FileService.OpenItemResult.OpenedOrHandled)
            {
                // A successful dispatch is silent: the target window (or a
                // system dialog) is its own feedback, matching Explorer. The
                // item's opening badge covers the in-flight window.
                ClearOpenedItemSelectionAfterDispatch(
                    item,
                    stackPopoverGeneration,
                    generation);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation here is normally content disposal. Do not show an
            // error after the widget has already been torn down.
            if (!_isDisposed && generation == _openStateGeneration)
            {
                PerformanceLogger.Mark(
                    "FileSurface.OpenItem.Cancelled",
                    $"widget={WidgetId} kind={kind}");
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileSurface] Open item failed widget={WidgetId} " +
                $"kind={kind}: {ex}");
            if (!_isDisposed && generation == _openStateGeneration)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.OpenItemFailed"),
                    WidgetFeedbackSeverity.Error,
                    "file-open-failed"));
            }
        }
        finally
        {
            EndOpenItem(requestPath, dispatched);
            SetOpeningVisual(item, isOpening: false);
        }
    }

    private void ClearOpenedItemSelectionAfterDispatch(
        WidgetItem item,
        long stackPopoverGeneration,
        long generation)
    {
        ClearOpenedItemSelection(item, stackPopoverGeneration);
        // The second click of a double-click can commit its native selection
        // only after the dispatch already finished, and a desktop-layer widget
        // never receives a deactivation to clear it. Registering the path also
        // suppresses that late commit at the source, which a scheduled clear
        // alone cannot outrun on fast dispatch paths.
        RegisterOpenedItemSelectionSuppression(item);
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_isDisposed || generation != _openStateGeneration)
                {
                    return;
                }

                if (ItemsGrid.SelectedItems.Contains(item) ||
                    ItemsList.SelectedItems.Contains(item))
                {
                    App.Log(
                        "[FileSurface] Late native selection after " +
                        $"open cleared path='{item.Path}'");
                }

                ClearOpenedItemSelection(item, stackPopoverGeneration);
            });
    }

    private void ClearOpenedItemSelection(
        WidgetItem item,
        long stackPopoverGeneration)
    {
        // Only deselect the dispatched item: another file may have been
        // selected while Shell was busy. Do not clear a newly opened popover.
        ItemsGrid.SelectedItems.Remove(item);
        ItemsList.SelectedItems.Remove(item);
        ClearOpenedItemPointerFeedback(ItemsGrid, item);
        ClearOpenedItemPointerFeedback(ItemsList, item);
        if (stackPopoverGeneration == _stackPopoverShowGeneration)
        {
            _stackPopoverItemsView?.SelectedItems.Remove(item);
            if (_stackPopoverItemsView is { } popover)
            {
                ClearOpenedItemPointerFeedback(popover, item);
            }
        }

        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();
    }

    private void ClearOpenedItemPointerFeedback(ListViewBase view, WidgetItem item)
    {
        // Resolve through the owning view, not the shared surface registry:
        // an old completion must not reset a later popover or recycled item.
        if (view.ContainerFromItem(item) is not SelectorItem container ||
            FindDescendantByTag(container, "InteractiveSurface") is not Border border ||
            FileItemSurface.FindOwner(border) is not { } surface ||
            !ReferenceEquals(surface.DataContext, item))
        {
            return;
        }

        surface.ClearPointerFeedbackAfterOpen();
        // Commit the final unselected state even when pointer feedback was
        // already Normal and therefore raises no VisualStateChanged event.
        ApplyItemSurfaceVisual(border, surface.VisualState);
    }

    private bool TryBeginOpenItem(string path)
    {
        uint doubleClickTimeMs = Win32Helper.GetDoubleClickTime();
        if (doubleClickTimeMs == 0)
        {
            doubleClickTimeMs = 500;
        }

        return _openRequestGate.TryBegin(
            path,
            Environment.TickCount64,
            doubleClickTimeMs);
    }

    private void EndOpenItem(string path, bool dispatched)
    {
        _openRequestGate.Complete(path, dispatched);
    }

    private void SetOpeningVisual(WidgetItem item, bool isOpening)
    {
        if (_isDisposed)
        {
            return;
        }

        string status = isOpening ? OpeningStatusText : string.Empty;
        foreach (Border border in _itemSurfaces.ToArray())
        {
            FileItemSurface? surface = FileItemSurface.FindOwner(border);
            if (surface?.DataContext is WidgetItem surfaceItem &&
                ReferenceEquals(surfaceItem, item))
            {
                surface.SetOpeningState(isOpening, status);
            }
        }
    }

    private void ApplyOpeningStateToSurface(FileItemSurface surface)
    {
        if (surface.DataContext is not WidgetItem item)
        {
            return;
        }

        bool isOpening = _openRequestGate.IsActive(item.Path);
        surface.SetOpeningState(
            isOpening,
            isOpening ? OpeningStatusText : string.Empty);
    }

    private void ItemSurface_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (!_isDisposed && sender is FileItemSurface surface)
        {
            ApplyOpeningStateToSurface(surface);
        }
    }

    private void ResetOpenItemStateForReuse()
    {
        _openStateGeneration++;
        // An in-flight worker may still complete after this surface is reused.
        // Keep its active path until that completion so a recycled container
        // cannot start a second Shell request for the same item. Only the
        // short duplicate-click history is reset for the new presentation.
        _openRequestGate.ClearRecent();
        foreach (Border border in _itemSurfaces.ToArray())
        {
            if (FileItemSurface.FindOwner(border) is { } surface)
            {
                ApplyOpeningStateToSurface(surface);
            }
        }
    }

    private void ClearOpenItemStateForDispose()
    {
        _openStateGeneration++;
        _openRequestGate.Clear();
    }

    private static string GetOpenItemKind(WidgetItem item)
    {
        if (item.IsShortcut || ShortcutHelper.IsShortcutPath(item.Path))
        {
            return "shortcut";
        }

        return item.Path.StartsWith("\\\\", StringComparison.Ordinal)
            ? "unc"
            : "filesystem";
    }
}
