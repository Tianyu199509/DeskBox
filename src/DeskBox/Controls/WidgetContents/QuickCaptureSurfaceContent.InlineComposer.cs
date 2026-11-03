using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureSurfaceContent
{
    private readonly List<DroppedFilePath> _pendingInlineAttachments = [];
    private DispatcherQueueTimer? _relativeTimestampTimer;
    private bool _isInlineComposerOpen;
    private bool _isCommittingInlineComposer;

    private void InitializeOptimizationHooks(DispatcherQueue dispatcherQueue)
    {
        _relativeTimestampTimer = dispatcherQueue.CreateTimer();
        _relativeTimestampTimer.Interval = TimeSpan.FromMinutes(1);
        _relativeTimestampTimer.IsRepeating = true;
        _relativeTimestampTimer.Tick += RelativeTimestampTimer_Tick;
        _localizationService.LanguageChanged += OnOptimizationLanguageChanged;

        UpdateOptimizationLocalizedChrome();
        ApplyMaterialPaletteSwatches();
    }

    private void StartRelativeTimestampTimer()
    {
        if (!_isDisposed)
        {
            _relativeTimestampTimer?.Start();
        }
    }

    private void StopRelativeTimestampTimer()
    {
        _relativeTimestampTimer?.Stop();
    }

    private void DisposeOptimizationResources()
    {
        _localizationService.LanguageChanged -= OnOptimizationLanguageChanged;
        if (_relativeTimestampTimer is not null)
        {
            _relativeTimestampTimer.Stop();
            _relativeTimestampTimer.Tick -= RelativeTimestampTimer_Tick;
            _relativeTimestampTimer = null;
        }

        _pendingInlineAttachments.Clear();
        InlineComposerAttachmentStrip?.ReleaseVisualHandlers();
    }

    private void OnOptimizationLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateOptimizationLocalizedChrome();
            RebuildFilterChipBar();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isDisposed)
                {
                    UpdateOptimizationLocalizedChrome();
                    RebuildFilterChipBar();
                }
            });
        }
    }

    private void RelativeTimestampTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed || !IsLoaded || !_isWindowVisible)
        {
            return;
        }

        ViewModel.RefreshVisibleRelativeTimestamps();
    }

    private void UpdateOptimizationLocalizedChrome()
    {
        if (InlineComposerTextBox is not null)
        {
            InlineComposerTextBox.PlaceholderText = T("QuickCapture.InputPlaceholder");
            InlineComposerTextBox.FontSize = ViewModel.ContentTextSize;
        }

        if (InlineComposerHintText is not null)
        {
            InlineComposerHintText.Text = T("QuickCapture.QuickJotHint");
        }

        if (InlineComposerAttachmentStrip is not null)
        {
            InlineComposerAttachmentStrip.RemoveAutomationName = T("QuickCapture.Detail.RemoveAttachment");
        }

        SetElementToolTipAndName(InlineComposerAttachButton, T("QuickCapture.AddFile"));
        SetElementToolTipAndName(InlineComposerExpandButton, T("QuickCapture.ExpandInput"));
        SetElementToolTipAndName(InlineComposerCancelButton, T("Common.Cancel"));
        SetElementToolTipAndName(InlineComposerSaveButton, T("Common.Save"));
        SetElementToolTipAndName(DetailPopoutButton, T("QuickCapture.Popout"));
        SetElementToolTipAndName(DetailMoreButton, T("Common.More"));
    }

    private static void SetElementToolTipAndName(UIElement? element, string text)
    {
        if (element is null)
        {
            return;
        }

        AutomationProperties.SetName(element, text);
        ToolTipService.SetToolTip(element, text);
    }

    private void ApplyMaterialPaletteSwatches()
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        if (PaperMaterialSwatch is not null)
        {
            PaperMaterialSwatch.Background = QuickCaptureMaterialBrushCache.GetSwatchBrush(
                QuickCaptureAppearancePreset.Paper,
                isDark);
        }

        if (YellowMaterialSwatch is not null)
        {
            YellowMaterialSwatch.Background = QuickCaptureMaterialBrushCache.GetSwatchBrush(
                QuickCaptureAppearancePreset.StickyYellow,
                isDark);
        }

        if (RoseMaterialSwatch is not null)
        {
            RoseMaterialSwatch.Background = QuickCaptureMaterialBrushCache.GetSwatchBrush(
                QuickCaptureAppearancePreset.Rose,
                isDark);
        }

        if (MintMaterialSwatch is not null)
        {
            MintMaterialSwatch.Background = QuickCaptureMaterialBrushCache.GetSwatchBrush(
                QuickCaptureAppearancePreset.Mint,
                isDark);
        }

        if (BlueMaterialSwatch is not null)
        {
            BlueMaterialSwatch.Background = QuickCaptureMaterialBrushCache.GetSwatchBrush(
                QuickCaptureAppearancePreset.MistBlue,
                isDark);
        }
    }

    private void OpenInlineComposer()
    {
        if (InlineComposerCard is null || AddNoteButtonHost is null)
        {
            return;
        }

        _isInlineComposerOpen = true;
        UpdateInlineComposerPresentation();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed && _isInlineComposerOpen)
            {
                InlineComposerTextBox.Focus(FocusState.Programmatic);
                InlineComposerTextBox.SelectionStart = InlineComposerTextBox.Text.Length;
            }
        });
    }

    private void CloseInlineComposer(bool clearDraft)
    {
        _isInlineComposerOpen = false;
        if (clearDraft)
        {
            if (InlineComposerTextBox is not null)
            {
                InlineComposerTextBox.Text = string.Empty;
            }

            _pendingInlineAttachments.Clear();
            RefreshInlineComposerAttachments();
        }

        UpdateInlineComposerPresentation();
    }

    private void UpdateInlineComposerPresentation()
    {
        if (InlineComposerCard is null || AddNoteButtonHost is null)
        {
            return;
        }

        bool inputAllowed = ViewModel.InputAreaVisibility == Visibility.Visible;
        bool showInline = _isInlineComposerOpen && inputAllowed;
        AddNoteButtonHost.Visibility = showInline ? Visibility.Collapsed : Visibility.Visible;
        InlineComposerCard.Visibility = showInline ? Visibility.Visible : Visibility.Collapsed;
        if (InlineComposerTextBox is not null)
        {
            InlineComposerTextBox.FontSize = ViewModel.ContentTextSize;
        }
    }

    private void RefreshInlineComposerAttachments()
    {
        if (InlineComposerAttachmentStrip is null)
        {
            return;
        }

        TodoAttachmentViewModel[] attachments = _pendingInlineAttachments
            .Select(file => new TodoAttachmentViewModel(new TodoAttachment
            {
                FilePath = file.Path,
                DisplayName = file.DisplayName,
                Type = AttachmentStorageService.GetAttachmentType(file.Path)
            }))
            .ToArray();
        InlineComposerAttachmentStrip.ItemsSource = attachments.Cast<object>().ToArray();
        InlineComposerAttachmentStrip.CanRemove = true;
        InlineComposerAttachmentStrip.Visibility = attachments.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task CommitInlineComposerAsync()
    {
        if (_isCommittingInlineComposer || InlineComposerTextBox is null)
        {
            return;
        }

        string body = InlineComposerTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body) && _pendingInlineAttachments.Count == 0)
        {
            return;
        }

        _isCommittingInlineComposer = true;
        try
        {
            DroppedFilePath[] attachments = _pendingInlineAttachments.ToArray();
            if (attachments.Length > 0)
            {
                QuickCaptureItemViewModel? createdWithFiles =
                    await ViewModel.AddItemWithAttachmentsAsync(attachments);
                if (createdWithFiles is not null && !string.IsNullOrWhiteSpace(body))
                {
                    QuickCaptureWriteResult res = await ViewModel.EditItemDetailsWithResultAsync(
                        createdWithFiles,
                        null,
                        body,
                        QuickCaptureAppearancePreset.Default,
                        ViewModel.EditorContentFormat);
                    ReportBodyTruncation(res);
                }
            }
            else
            {
                QuickCaptureWriteResult res = await ViewModel.AddDetailedItemWithResultAsync(
                    null,
                    body,
                    QuickCaptureAppearancePreset.Default,
                    ViewModel.EditorContentFormat);
                ReportBodyTruncation(res);
            }

            InlineComposerTextBox.Text = string.Empty;
            _pendingInlineAttachments.Clear();
            RefreshInlineComposerAttachments();
            RaiseFeedback(
                T("QuickCapture.Saved"),
                WidgetFeedbackSeverity.Success,
                "quick-inline-saved");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isDisposed && _isInlineComposerOpen)
                {
                    InlineComposerTextBox.Focus(FocusState.Programmatic);
                }
            });
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Inline composer commit failed: {ex.Message}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-inline-save-error");
        }
        finally
        {
            _isCommittingInlineComposer = false;
        }
    }

    private async Task PromoteInlineComposerToDetailAsync()
    {
        string draftBody = InlineComposerTextBox?.Text ?? string.Empty;
        DroppedFilePath[] draftAttachments = _pendingInlineAttachments.ToArray();
        CloseInlineComposer(clearDraft: true);

        await OpenNewDetailAsync();
        if (!_isCreatingDetail)
        {
            return;
        }

        if (!string.IsNullOrEmpty(draftBody))
        {
            SetDetailEditorText(draftBody);
            MarkDetailDirty();
        }

        if (draftAttachments.Length > 0)
        {
            _pendingDetailAttachments.AddRange(draftAttachments);
            MarkDetailDirty();
            RefreshDetailAttachments();
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed && _isDetailEditing)
            {
                EnsureDetailMarkdownEditorRealized().FocusEditor(moveCaretToEnd: true);
            }
        });
    }

    private async void InlineComposerTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ignore IME composition confirmation key events (VK_PROCESSKEY = 229).
        if ((int)e.OriginalKey == 229 || (int)e.Key == 229)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            bool ctrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
            bool shift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
            string enterBehavior = SettingsService.NormalizeEditorEnterBehavior(ViewModel.EditorEnterBehavior);

            if (!shift && SettingsService.ShouldSubmitEditorOnEnter(enterBehavior, ctrl))
            {
                e.Handled = true;
                await CommitInlineComposerAsync();
                return;
            }

            if (enterBehavior == SettingsService.EditorEnterBehaviorEnterSaves && ctrl && !shift)
            {
                e.Handled = true;
                InsertInlineComposerNewLineAtSelection();
                return;
            }

            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            bool isEmpty = string.IsNullOrWhiteSpace(InlineComposerTextBox.Text) &&
                           _pendingInlineAttachments.Count == 0;
            CloseInlineComposer(clearDraft: isEmpty);
        }
    }

    private void InsertInlineComposerNewLineAtSelection()
    {
        if (InlineComposerTextBox is null)
        {
            return;
        }

        string text = InlineComposerTextBox.Text ?? string.Empty;
        int start = Math.Clamp(InlineComposerTextBox.SelectionStart, 0, text.Length);
        int length = Math.Clamp(InlineComposerTextBox.SelectionLength, 0, text.Length - start);
        InlineComposerTextBox.Text = string.Concat(
            text.AsSpan(0, start),
            "\r",
            text.AsSpan(start + length));
        InlineComposerTextBox.SelectionStart = start + 1;
        InlineComposerTextBox.SelectionLength = 0;
    }

    private async void InlineComposerTextBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView? dataView;
        try
        {
            dataView = Clipboard.GetContent();
        }
        catch
        {
            return;
        }

        if (dataView is null)
        {
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(dataView))
        {
            e.Handled = true;
            try
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
                foreach (DroppedFilePath file in batch.Files)
                {
                    if (!_pendingInlineAttachments.Any(existing =>
                            string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        _pendingInlineAttachments.Add(file);
                    }
                }

                RefreshInlineComposerAttachments();
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCaptureSurface] Inline paste files failed: {ex.Message}");
            }

            return;
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            if (ViewModel.EditorContentFormat == TextContentFormat.Markdown &&
                InlineComposerTextBox.SelectionLength > 0)
            {
                try
                {
                    string clipText = await dataView.GetTextAsync();
                    if (MarkdownSourceEditor.TryFormatSmartUrlPaste(
                            InlineComposerTextBox.SelectedText,
                            clipText,
                            out string formattedLink))
                    {
                        e.Handled = true;
                        int start = InlineComposerTextBox.SelectionStart;
                        InlineComposerTextBox.SelectedText = formattedLink;
                        InlineComposerTextBox.SelectionStart = start + formattedLink.Length;
                        InlineComposerTextBox.SelectionLength = 0;
                    }
                }
                catch
                {
                }
            }

            return;
        }

        byte[]? pngBytes = await QuickCaptureService.TryExtractClipboardPngBytesAsync(dataView);
        if (pngBytes is { Length: > 0 })
        {
            e.Handled = true;
            DroppedFilePath? tempFile =
                await QuickCaptureWidgetViewModel.SaveClipboardImageBytesToTempFileAsync(pngBytes);
            if (tempFile is not null)
            {
                _pendingInlineAttachments.Add(tempFile);
                RefreshInlineComposerAttachments();
            }
        }
    }

    private async void InlineComposerAttachButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IntPtr foreground = Win32Helper.GetForegroundWindow();
            IntPtr owner = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
            IReadOnlyList<string> files = await FileOpenPickerService.PickMultipleFilesAsync(
                owner == IntPtr.Zero ? foreground : owner,
                [],
                PickerLocationId.Desktop);
            foreach (string path in files.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)))
            {
                if (!_pendingInlineAttachments.Any(existing =>
                        string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    _pendingInlineAttachments.Add(new DroppedFilePath(
                        path,
                        Path.GetFileName(path),
                        ForceManagedCopy: false));
                }
            }

            RefreshInlineComposerAttachments();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Inline attach failed: {ex.Message}");
        }
    }

    private async void InlineComposerExpandButton_Click(object sender, RoutedEventArgs e)
    {
        await PromoteInlineComposerToDetailAsync();
    }

    private void InlineComposerCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseInlineComposer(clearDraft: true);
    }

    private async void InlineComposerSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await CommitInlineComposerAsync();
    }

    private void InlineComposerAttachmentStrip_RemoveRequested(
        object? sender,
        AttachmentTileEventArgs e)
    {
        _pendingInlineAttachments.RemoveAll(file =>
            string.Equals(file.Path, e.Attachment.FilePath, StringComparison.OrdinalIgnoreCase));
        RefreshInlineComposerAttachments();
    }

    private void RebuildFilterChipBar()
    {
        if (FilterChipScrollViewer is null || FilterChipPanel is null)
        {
            return;
        }

        IReadOnlyList<QuickCaptureFilterChipDescriptor> chips = ViewModel.FilterChips;
        FilterChipPanel.Children.Clear();
        if (chips.Count == 0)
        {
            FilterChipScrollViewer.Visibility = Visibility.Collapsed;
            return;
        }

        Color lineColor = NeutralInteractionBrush.Line(this);
        Color fillColor = NeutralInteractionBrush.Fill(this);
        Brush defaultCardBrush = ResolveBrush(
            "CardBackgroundFillColorDefaultBrush",
            Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Brush primaryText = ResolveBrush("TextFillColorPrimaryBrush", Colors.White);
        Brush secondaryText = ResolveBrush("TextFillColorSecondaryBrush", Colors.Gray);
        double chipFontSize = Math.Max(11, ViewModel.SecondaryTextSize);

        foreach (QuickCaptureFilterChipDescriptor chip in chips)
        {
            var button = new Button
            {
                MinHeight = 24,
                MinWidth = 0,
                Height = 24,
                Padding = new Thickness(7, 1, 7, 1),
                CornerRadius = new CornerRadius(6),
                Background = chip.IsSelected ? new SolidColorBrush(fillColor) : defaultCardBrush,
                BorderBrush = chip.IsSelected
                    ? new SolidColorBrush(lineColor)
                    : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                Content = new TextBlock
                {
                    Text = $"{chip.Label} {chip.Count}",
                    FontSize = chipFontSize,
                    FontWeight = chip.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = chip.IsSelected ? primaryText : secondaryText,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            QuickCaptureFilterChipDescriptor captured = chip;
            button.Click += (_, _) =>
            {
                if (captured.IsArchiveChip)
                {
                    ViewModel.ToggleShowArchived();
                }
                else
                {
                    ViewModel.ToggleTagFilter(captured.Key);
                }
            };

            FilterChipPanel.Children.Add(button);
        }

        FilterChipScrollViewer.Visibility = Visibility.Visible;
    }

    private void ApplyQuickCaptureItemCardPresentation(
        Border border,
        QuickCaptureItemViewModel? item)
    {
        TextBlock? titleBlock = FindQuickCaptureVisualChild<TextBlock>(border, "ItemTitleTextBlock");
        TextBlock? summaryBlock = FindQuickCaptureVisualChild<TextBlock>(border, "ItemSummaryTextBlock");
        FontIcon? pinBadge = FindQuickCaptureVisualChild<FontIcon>(border, "ItemPinnedBadgeIcon");
        TextBlock? timestampBlock = FindQuickCaptureVisualChild<TextBlock>(border, "ItemTimestampTextBlock");
        TextBlock? checklistBlock = FindQuickCaptureVisualChild<TextBlock>(border, "ItemChecklistBadgeTextBlock");
        TextBlock? attachmentBlock = FindQuickCaptureVisualChild<TextBlock>(border, "ItemAttachmentBadgeTextBlock");
        TextBlock? tagsBlock = FindQuickCaptureVisualChild<TextBlock>(border, "ItemTagsTextBlock");

        if (item is null)
        {
            if (summaryBlock is not null)
            {
                summaryBlock.Visibility = Visibility.Collapsed;
                summaryBlock.TextHighlighters.Clear();
            }

            titleBlock?.TextHighlighters.Clear();
            if (pinBadge is not null)
            {
                pinBadge.Visibility = Visibility.Collapsed;
            }

            if (checklistBlock is not null)
            {
                checklistBlock.Visibility = Visibility.Collapsed;
            }

            if (attachmentBlock is not null)
            {
                attachmentBlock.Visibility = Visibility.Collapsed;
            }

            if (tagsBlock is not null)
            {
                tagsBlock.Visibility = Visibility.Collapsed;
            }

            return;
        }

        int previewLines = Math.Max(1, ViewModel.ItemPreviewLineCount);
        bool showTwoTier = item.HasDisplaySummary && previewLines > 1;
        if (titleBlock is not null)
        {
            titleBlock.Text = showTwoTier ? item.DisplayTitleLine : item.DisplayText;
            titleBlock.MaxLines = showTwoTier ? 1 : previewLines;
            titleBlock.FontWeight = showTwoTier ? FontWeights.SemiBold : FontWeights.Normal;
            ApplySearchTextHighlight(border, titleBlock, item.SearchKeyword);
        }

        if (summaryBlock is not null)
        {
            if (showTwoTier)
            {
                summaryBlock.Text = item.DisplaySummaryText;
                summaryBlock.FontSize = Math.Max(11, item.TextSize - 1);
                summaryBlock.MaxLines = Math.Max(1, previewLines - 1);
                summaryBlock.Visibility = Visibility.Visible;
                ApplySearchTextHighlight(border, summaryBlock, item.SearchKeyword);
            }
            else
            {
                summaryBlock.Text = string.Empty;
                summaryBlock.Visibility = Visibility.Collapsed;
                summaryBlock.TextHighlighters.Clear();
            }
        }

        if (pinBadge is not null)
        {
            pinBadge.Visibility = item.PinnedBadgeVisibility;
        }

        if (timestampBlock is not null)
        {
            ToolTipService.SetToolTip(timestampBlock, item.TimestampTooltipText);
        }

        double metaSize = item.SecondaryTextSize;
        if (checklistBlock is not null)
        {
            if (item.HasChecklist)
            {
                checklistBlock.Text = $"☑ {item.ChecklistBadgeText}";
                checklistBlock.FontSize = metaSize;
                checklistBlock.Visibility = Visibility.Visible;
            }
            else
            {
                checklistBlock.Visibility = Visibility.Collapsed;
            }
        }

        if (attachmentBlock is not null)
        {
            if (item.Attachments.Count > 0)
            {
                attachmentBlock.Text = $"📎 {item.AttachmentSummaryText}";
                attachmentBlock.FontSize = metaSize;
                attachmentBlock.Visibility = Visibility.Visible;
            }
            else
            {
                attachmentBlock.Visibility = Visibility.Collapsed;
            }
        }

        if (tagsBlock is not null)
        {
            if (item.HasTags)
            {
                tagsBlock.Text = item.TagsSummaryText;
                tagsBlock.FontSize = metaSize;
                tagsBlock.Visibility = Visibility.Visible;
            }
            else
            {
                tagsBlock.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplySearchTextHighlight(
        FrameworkElement scope,
        TextBlock textBlock,
        string? rawKeyword)
    {
        textBlock.TextHighlighters.Clear();
        if (string.IsNullOrWhiteSpace(rawKeyword) || string.IsNullOrEmpty(textBlock.Text))
        {
            return;
        }

        string keyword = rawKeyword.Trim();
        if (keyword.StartsWith('#') && keyword.Length > 1)
        {
            keyword = keyword[1..];
        }

        if (keyword.Length == 0)
        {
            return;
        }

        string text = textBlock.Text;
        int searchFrom = 0;
        TextHighlighter? highlighter = null;
        while (searchFrom < text.Length)
        {
            int matchIndex = text.IndexOf(keyword, searchFrom, StringComparison.CurrentCultureIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            highlighter ??= new TextHighlighter
            {
                Background = new SolidColorBrush(NeutralInteractionBrush.Fill(scope)),
                Foreground = ResolveBrush("TextFillColorPrimaryBrush", Colors.White)
            };
            highlighter.Ranges.Add(new TextRange
            {
                StartIndex = matchIndex,
                Length = keyword.Length
            });
            searchFrom = matchIndex + keyword.Length;
        }

        if (highlighter is not null)
        {
            textBlock.TextHighlighters.Add(highlighter);
        }
    }
}
