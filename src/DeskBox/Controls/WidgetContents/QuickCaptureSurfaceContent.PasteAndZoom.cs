using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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

        if (_isCreatingDetail)
        {
            await NavigateBackFromDetailAsync();
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

    private async void Surface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isDisposed || ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        bool ctrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool alt = Win32Helper.IsKeyPressed(VirtualKey.Menu);
        bool shift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        object? focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        bool isTextInputFocused = focused is TextBox or RichEditBox;

        if (alt && !ctrl && !shift && e.Key == VirtualKey.Left)
        {
            if (!_isDualPane && _showDetailInSinglePane)
            {
                e.Handled = true;
                await NavigateBackFromDetailAsync();
            }
            return;
        }

        if (ctrl && !alt && !shift && e.Key == VirtualKey.Z)
        {
            if (!isTextInputFocused && _lastUndoAction is { } undoAction)
            {
                e.Handled = true;
                await undoAction();
            }
            return;
        }

        if (ctrl && !alt && !shift && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            if (!_isDualPane && _showDetailInSinglePane)
            {
                await NavigateBackFromDetailAsync();
                if (_showDetailInSinglePane)
                {
                    return;
                }
            }

            ExpandAndFocusSearch();
            return;
        }

        if (ctrl && !alt && !shift && e.Key == VirtualKey.N)
        {
            e.Handled = true;
            if (ListPage.Visibility == Visibility.Visible &&
                ViewModel.InputAreaVisibility == Visibility.Visible)
            {
                OpenInlineComposer();
            }
            else
            {
                await AddFromTitleButtonAsync();
            }
            return;
        }

        if (!ctrl && !alt && !shift &&
            !isTextInputFocused &&
            e.Key == VirtualKey.Escape &&
            !_isDualPane &&
            _showDetailInSinglePane &&
            !_isDetailEditing)
        {
            e.Handled = true;
            await NavigateBackFromDetailAsync();
        }
    }

    private async void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        PointerPointProperties props = e.GetCurrentPoint(ResponsiveContentGrid).Properties;
        if (props.PointerUpdateKind == PointerUpdateKind.XButton1Pressed &&
            !_isDualPane &&
            _showDetailInSinglePane)
        {
            e.Handled = true;
            await NavigateBackFromDetailAsync();
        }
    }

    private bool HasOwnerKeyboardFocus() =>
        _isWindowActivated &&
        DeskBox.Views.WidgetWindowBase.IsOwnerWindowKeyboardFocused(this);

    private void UpdateOwnerFocusSelectionVisuals()
    {
        if (_isDisposed || ItemsList is null)
        {
            return;
        }

        bool hasKeyboardFocus = HasOwnerKeyboardFocus();
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsList.ContainerFromItem(item) is ListViewItem container)
            {
                if (FindQuickCaptureVisualChild<FrameworkElement>(container, "SelectionIndicator") is { } indicator)
                {
                    indicator.Opacity = hasKeyboardFocus ? 1.0 : 0.32;
                }

                if (container.ContentTemplateRoot is Border border)
                {
                    ApplyItemSelectionFocusVisual(border, hasKeyboardFocus);
                }
            }
        }
    }

    private static void ApplyItemSelectionFocusVisual(Border border, bool hasKeyboardFocus)
    {
        if (FindQuickCaptureVisualChild<Border>(border, "QuickCaptureItemSelectionWash") is { } wash)
        {
            wash.Opacity = hasKeyboardFocus ? 0.10 : 0.03;
        }

        if (FindQuickCaptureVisualChild<Border>(border, "QuickCaptureItemSelectionAccentBar") is { } accentBar)
        {
            accentBar.Opacity = hasKeyboardFocus ? 1.0 : 0.32;
        }
    }

    private void PlaySinglePaneNavigationTransition(bool enteringDetail)
    {
        if (_isDisposed ||
            _isDualPane ||
            !IsLoaded ||
            _isCompactCollapsed ||
            _isResponsiveLayoutTransitionActive)
        {
            return;
        }

        UIElement target = enteringDetail ? DetailPage : ListPage;
        UIElement other = enteringDetail ? ListPage : DetailPage;
        DetailPageTransitionHelper.Reset(other);
        if (!WindowsCompatibilityService.AreAnimationsEnabled)
        {
            DetailPageTransitionHelper.Reset(target);
            return;
        }

        DetailPageTransitionHelper.PlayNavigationEnter(target, enteringDetail ? 14f : -14f);
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(160);
        opacityAnimation.InsertKeyFrame(0f, 0.32f);
        opacityAnimation.InsertKeyFrame(1f, 1f, easing);

        var translationAnimation = compositor.CreateVector3KeyFrameAnimation();
        translationAnimation.Duration = TimeSpan.FromMilliseconds(160);
        translationAnimation.InsertKeyFrame(0f, new Vector3(enteringDetail ? 14f : -14f, 0f, 0f));
        translationAnimation.InsertKeyFrame(1f, Vector3.Zero, easing);

        target.Opacity = 1;
        target.Translation = Vector3.Zero;
        visual.Opacity = 1f;
        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Translation", translationAnimation);
    }

    private static void PlayExpandEntranceAnimation(UIElement? element)
    {
        if (element is null)
        {
            return;
        }

        if (!WindowsCompatibilityService.AreAnimationsEnabled)
        {
            DetailPageTransitionHelper.Reset(element);
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(140);
        opacityAnimation.InsertKeyFrame(0f, 0f);
        opacityAnimation.InsertKeyFrame(1f, 1f, easing);

        var translationAnimation = compositor.CreateVector3KeyFrameAnimation();
        translationAnimation.Duration = TimeSpan.FromMilliseconds(140);
        translationAnimation.InsertKeyFrame(0f, new Vector3(0f, -6f, 0f));
        translationAnimation.InsertKeyFrame(1f, Vector3.Zero, easing);

        element.Opacity = 1;
        element.Translation = Vector3.Zero;
        visual.Opacity = 1f;
        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Translation", translationAnimation);
    }

    private static T? FindFirstDescendantOfType<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindFirstDescendantOfType<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
