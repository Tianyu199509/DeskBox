using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureSurfaceContent
{
    private void WireDetailEditorOptimizationEvents(MarkdownSourceEditor editor)
    {
        editor.CancelRequested += DetailMarkdownEditor_CancelRequested;
        editor.RichPasteRequested += DetailMarkdownEditor_RichPasteRequested;
    }

    private void UnwireDetailEditorOptimizationEvents(MarkdownSourceEditor editor)
    {
        editor.CancelRequested -= DetailMarkdownEditor_CancelRequested;
        editor.RichPasteRequested -= DetailMarkdownEditor_RichPasteRequested;
    }

    private async void DetailMarkdownEditor_CancelRequested(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isDetailEditing)
        {
            return;
        }

        if (_isCreatingDetail && !HasNewDetailContent())
        {
            ClearDetailForViewChange();
            return;
        }

        await SaveDetailAsync(completeEditing: true);
    }

    private async void DetailMarkdownEditor_RichPasteRequested(
        object? sender,
        MarkdownClipboardPasteEventArgs e)
    {
        if (_isDisposed || !_isDetailEditing)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                if (batch.Files.Count > 0)
                {
                    if (_isCreatingDetail || _detailItem is null)
                    {
                        foreach (DroppedFilePath file in batch.Files)
                        {
                            if (!_pendingDetailAttachments.Any(existing =>
                                    string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                            {
                                _pendingDetailAttachments.Add(file);
                            }
                        }

                        MarkDetailDirty();
                    }
                    else
                    {
                        _detailItem = await ViewModel.AddAttachmentsAsync(_detailItem, batch.Files) ?? _detailItem;
                    }

                    RefreshDetailAttachments();
                    e.Handled = true;
                    return;
                }
            }

            byte[]? pngBytes = await QuickCaptureService.TryExtractClipboardPngBytesAsync(e.DataView);
            if (pngBytes is { Length: > 0 })
            {
                if (_isCreatingDetail || _detailItem is null)
                {
                    DroppedFilePath? tempFile =
                        await QuickCaptureWidgetViewModel.SaveClipboardImageBytesToTempFileAsync(pngBytes);
                    if (tempFile is not null)
                    {
                        _pendingDetailAttachments.Add(tempFile);
                        MarkDetailDirty();
                        RefreshDetailAttachments();
                        e.Handled = true;
                    }
                }
                else
                {
                    QuickCaptureItemViewModel? updated =
                        await ViewModel.AddClipboardImageBytesAttachmentAsync(_detailItem, pngBytes);
                    if (updated is not null)
                    {
                        _detailItem = updated;
                        RefreshDetailAttachments();
                        RaiseFeedback(
                            T("QuickCapture.PastedScreenshot"),
                            WidgetFeedbackSeverity.Success,
                            "quick-paste-screenshot");
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Rich paste failed: {ex.Message}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-paste-error");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<bool> TryPasteIntoListAsync()
    {
        DataPackageView? dataView;
        try
        {
            dataView = Clipboard.GetContent();
        }
        catch
        {
            return false;
        }

        if (dataView is null)
        {
            return false;
        }

        try
        {
            if (DeskBoxDragData.HasDroppedFiles(dataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
                if (batch.Files.Count > 0)
                {
                    return await ImportNativeDroppedFilesAsync(batch.Files, targetItem: null);
                }
            }

            if (dataView.Contains(StandardDataFormats.Text))
            {
                string text = (await dataView.GetTextAsync())?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    QuickCaptureWriteResult res = await ViewModel.AddDetailedItemWithResultAsync(
                        null,
                        text,
                        QuickCaptureAppearancePreset.Default,
                        ViewModel.EditorContentFormat);
                    ReportBodyTruncation(res);
                    if (res.Saved)
                    {
                        RaiseFeedback(
                            T("QuickCapture.Saved"),
                            WidgetFeedbackSeverity.Success,
                            "quick-paste-text");
                        return true;
                    }
                }

                return false;
            }

            byte[]? pngBytes = await QuickCaptureService.TryExtractClipboardPngBytesAsync(dataView);
            if (pngBytes is { Length: > 0 })
            {
                QuickCaptureItemViewModel? created = await ViewModel.AddClipboardImageBytesAsync(pngBytes);
                if (created is not null)
                {
                    RaiseFeedback(
                        T("QuickCapture.PastedScreenshot"),
                        WidgetFeedbackSeverity.Success,
                        "quick-paste-screenshot");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] List paste failed: {ex.Message}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-list-paste-error");
        }

        return false;
    }

    private void ResponsiveContentGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_isDisposed || !Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            return;
        }

        int delta = e.GetCurrentPoint(ResponsiveContentGrid).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        double step = delta > 0 ? 1.0 : -1.0;
        var qcSlice = _settingsService.Settings.QuickCapture;
        double baseSize = _settingsService.Settings.WidgetShell.TextSize;
        bool overDetail = IsElementInsideAncestor(e.OriginalSource as DependencyObject, DetailPage);

        if (overDetail)
        {
            double current = SettingsService.NormalizeTextSize(
                qcSlice.QuickCaptureContentTextSize > 0 ? qcSlice.QuickCaptureContentTextSize : baseSize);
            double next = SettingsService.NormalizeTextSize(current + step);
            if (Math.Abs(next - current) > 0.01)
            {
                qcSlice.QuickCaptureContentTextSize = next;
                _settingsService.SaveDebounced();
            }
        }
        else
        {
            double current = SettingsService.NormalizeTextSize(
                qcSlice.QuickCaptureListTextSize > 0 ? qcSlice.QuickCaptureListTextSize : baseSize);
            double next = SettingsService.NormalizeTextSize(current + step);
            if (Math.Abs(next - current) > 0.01)
            {
                qcSlice.QuickCaptureListTextSize = next;
                _settingsService.SaveDebounced();
            }
        }

        e.Handled = true;
    }

    private static bool IsElementInsideAncestor(DependencyObject? element, DependencyObject? ancestor)
    {
        if (element is null || ancestor is null)
        {
            return false;
        }

        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
