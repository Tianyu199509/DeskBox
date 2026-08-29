using System.Globalization;
using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.Storage;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    private void InitializeResponsiveDetail()
    {
        if (ViewModel.Config.Metadata.TryGetValue(MasterPaneWidthMetadataKey, out string? persisted) &&
            double.TryParse(persisted, NumberStyles.Float, CultureInfo.InvariantCulture, out double width))
        {
            _persistedMasterPaneWidth = _masterDetailLayoutPolicy.NormalizePersistedMasterWidth(width);
        }

        DetailMarkdownEditor.TextResolver = _localizationService.T;
        DetailMarkdownEditor.EditorTextChanged += DetailMarkdownEditor_EditorTextChanged;
        DetailMarkdownEditor.TextTruncated += DetailMarkdownEditor_TextTruncated;
        DetailMarkdownView.AttachmentResolver = ResolveDetailAttachmentPath;
        DetailMarkdownView.AttachmentOpenRequested += DetailMarkdownView_AttachmentOpenRequested;

        _detailAutoSaveTimer = DispatcherQueue.CreateTimer();
        _detailAutoSaveTimer.Interval = TimeSpan.FromMilliseconds(DetailAutoSaveDelayMs);
        _detailAutoSaveTimer.IsRepeating = false;
        _detailAutoSaveTimer.Tick += DetailAutoSaveTimer_Tick;
    }

    private void ReleaseDetailAutoSaveTimer()
    {
        if (_detailAutoSaveTimer is null)
        {
            return;
        }

        _detailAutoSaveTimer.Stop();
        _detailAutoSaveTimer.Tick -= DetailAutoSaveTimer_Tick;
        DetailMarkdownEditor.EditorTextChanged -= DetailMarkdownEditor_EditorTextChanged;
        DetailMarkdownEditor.TextTruncated -= DetailMarkdownEditor_TextTruncated;
        DetailMarkdownView.AttachmentOpenRequested -= DetailMarkdownView_AttachmentOpenRequested;
        _detailAutoSaveTimer = null;
    }

    private void DetailMarkdownEditor_TextTruncated(object? sender, EventArgs e)
    {
        ShowStatusToast(_localizationService.T("QuickCapture.BodyTruncated"));
    }

    private void ResponsiveContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInteractiveResizeActive)
        {
            // Live column sizing still tracks the HWND through normal XAML
            // layout; only the single<->dual pane policy switch waits for the
            // resize to finish.
            _needsResponsiveDetailRelayoutAfterResize = true;
            return;
        }

        ApplyResponsiveDetailLayout();
    }

    private void ApplyResponsiveDetailLayout()
    {
        if (ResponsiveContentGrid is null || MasterColumn is null)
        {
            return;
        }

        double availableWidth = ResponsiveContentGrid.ActualWidth;
        string layoutPreference = SettingsService.NormalizeQuickCaptureWideLayout(
            _settingsService.Settings.QuickCaptureWideLayout);
        bool forceSinglePane = layoutPreference == SettingsService.QuickCaptureWideLayoutSinglePane;
        bool forceDualPane = layoutPreference == SettingsService.QuickCaptureWideLayoutDualPane;
        MasterDetailLayoutSnapshot layout = _masterDetailLayoutPolicy.Resolve(
            availableWidth,
            _isDualPane,
            _persistedMasterPaneWidth,
            forceSinglePane,
            forceDualPane);
        bool enteredDualPane = !_isDualPane && layout.IsDualPane;
        _isDualPane = layout.IsDualPane;

        if (_isDualPane)
        {
            double minimumDualWidth = _masterDetailLayoutPolicy.Options.MinimumMasterWidth +
                                      _masterDetailLayoutPolicy.Options.SplitterWidth +
                                      _masterDetailLayoutPolicy.Options.MinimumDetailWidth;
            bool relaxMinimumWidths = forceDualPane && availableWidth < minimumDualWidth;
            SetDualPaneColumns(layout.MasterWidth, layout.SplitterWidth, relaxMinimumWidths);
            ListPage.Visibility = Visibility.Visible;
            PaneSplitter.Visibility = Visibility.Visible;
            DetailPage.Visibility = Visibility.Visible;
            DetailPage.IsHitTestVisible = true;
            DetailBackButton.Visibility = Visibility.Collapsed;

            if (enteredDualPane)
            {
                ReconcileDetailSelection(autoSelectFirst: true);
            }
        }
        else
        {
            PaneSplitter.Visibility = Visibility.Collapsed;
            SplitterColumn.Width = new GridLength(0);
            SplitterColumn.MinWidth = 0;
            SplitterColumn.MaxWidth = 0;
            MasterColumn.MinWidth = 0;
            MasterColumn.MaxWidth = double.PositiveInfinity;
            DetailColumn.MinWidth = 0;

            if (_showDetailInSinglePane)
            {
                MasterColumn.Width = new GridLength(0);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                ListPage.Visibility = Visibility.Collapsed;
                DetailPage.Visibility = Visibility.Visible;
                DetailPage.IsHitTestVisible = true;
                DetailBackButton.Visibility = Visibility.Visible;
            }
            else
            {
                MasterColumn.Width = new GridLength(1, GridUnitType.Star);
                DetailColumn.Width = new GridLength(0);
                ListPage.Visibility = Visibility.Visible;
                DetailPage.Visibility = Visibility.Collapsed;
                DetailBackButton.Visibility = Visibility.Visible;
            }
        }

        RefreshDetailPresentation();
    }

    private void SetDualPaneColumns(
        double masterWidth,
        double splitterWidth,
        bool relaxMinimumWidths)
    {
        MasterDetailLayoutOptions options = _masterDetailLayoutPolicy.Options;
        MasterColumn.MinWidth = relaxMinimumWidths ? 0 : options.MinimumMasterWidth;
        MasterColumn.MaxWidth = relaxMinimumWidths ? double.PositiveInfinity : options.MaximumMasterWidth;
        MasterColumn.Width = new GridLength(masterWidth);
        SplitterColumn.MinWidth = splitterWidth;
        SplitterColumn.MaxWidth = splitterWidth;
        SplitterColumn.Width = new GridLength(splitterWidth);
        DetailColumn.MinWidth = relaxMinimumWidths ? 0 : options.MinimumDetailWidth;
        DetailColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void PaneSplitter_ManipulationCompleted(
        object sender,
        Microsoft.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e) =>
        PersistCurrentMasterPaneWidth();

    private void PaneSplitter_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            PersistCurrentMasterPaneWidth();
        }
    }

    private void PaneSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _persistedMasterPaneWidth = _masterDetailLayoutPolicy.Options.DefaultMasterWidth;
        ApplyResponsiveDetailLayout();
        PersistCurrentMasterPaneWidth();
        e.Handled = true;
    }

    private void PersistCurrentMasterPaneWidth()
    {
        if (!_isDualPane)
        {
            return;
        }

        double normalized = _masterDetailLayoutPolicy.NormalizePersistedMasterWidth(
            MasterColumn.ActualWidth);
        _persistedMasterPaneWidth = normalized;
        ViewModel.Config.Metadata[MasterPaneWidthMetadataKey] = normalized.ToString(
            "0.###",
            CultureInfo.InvariantCulture);
        _settingsService.SaveDebounced();
    }

    private void ReconcileDetailSelection(bool autoSelectFirst)
    {
        if (_detailItem is not null)
        {
            QuickCaptureItemViewModel? refreshed = ViewModel.Items.FirstOrDefault(item =>
                string.Equals(item.Id, _detailItem.Id, StringComparison.Ordinal));
            if (refreshed is not null)
            {
                _detailItem = refreshed;
            }
            else if (!_isDetailEditing && !_detailHasUnsavedChanges)
            {
                _detailItem = null;
            }
        }

        if (_isDualPane && !_isCreatingDetail && _detailItem is null &&
            autoSelectFirst && ViewModel.Items.FirstOrDefault() is { } first)
        {
            OpenDetail(first);
            return;
        }

        UpdateDetailSelectionVisuals();
        RefreshDetailPresentation();
    }

    private void UpdateDetailSelectionVisuals()
    {
        string? selectedId = _detailItem?.Id;
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            item.IsDetailSelected = selectedId is not null &&
                string.Equals(item.Id, selectedId, StringComparison.Ordinal);
        }

        if (_detailItem is not null)
        {
            ItemsListView.SelectedItem = _detailItem;
        }
    }

    private void RefreshDetailPresentation()
    {
        if (DetailEmptyState is null)
        {
            return;
        }

        bool hasDetail = _isCreatingDetail || _detailItem is not null;
        bool isReadOnly = _detailItem?.IsRecent == true;
        DetailEmptyState.Visibility = _isDualPane && !hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailHeader.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        DetailContent.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        DetailReadOnlyText.Visibility = isReadOnly ? Visibility.Visible : Visibility.Collapsed;
        DetailEditButton.Visibility = hasDetail && !_isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDoneButton.Visibility = hasDetail && _isDetailEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailPinButton.Visibility = isReadOnly ? Visibility.Collapsed : Visibility.Visible;
        DetailAddFileButton.Visibility = _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMaterialPalette.Visibility = _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMarkdownEditor.Visibility = _isDetailEditing && !isReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailMarkdownView.Visibility = hasDetail && (!_isDetailEditing || isReadOnly)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hasDetail)
        {
            DetailMarkdownView.Markdown = DetailMarkdownEditor.Text;
            DetailMarkdownView.ContentFormat = _detailContentFormat;
            DetailMarkdownView.AllowRemoteImages = _settingsService.Settings.QuickCaptureAllowRemoteImages;
            DetailMarkdownView.AreTaskListsInteractive = !isReadOnly &&
                _detailContentFormat == TextContentFormat.Markdown;
            DetailMarkdownEditor.ShowFormattingToolbar =
                _detailContentFormat == TextContentFormat.Markdown;
            DetailMarkdownEditor.IsReadOnly = isReadOnly;
        }
    }

    private void DetailMarkdownEditor_EditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressDetailEditorChanges || !_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        MarkDetailDirty();
        _detailAutoSaveTimer?.Stop();
        _detailAutoSaveTimer?.Start();
    }

    private void MarkDetailDirty()
    {
        _detailEditRevision++;
        _detailHasUnsavedChanges = _detailEditRevision != _detailSavedRevision;
    }

    private async void DetailAutoSaveTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (_detailHasUnsavedChanges)
        {
            await SaveDetailAsync(closeAfterSave: false);
        }
    }

    private async Task FlushPendingDetailSaveAsync()
    {
        _detailAutoSaveTimer?.Stop();
        if (_detailHasUnsavedChanges || _isSavingDetail)
        {
            await SaveDetailAsync(closeAfterSave: false);
        }
    }

    private async void DetailMarkdownEditor_CommitRequested(object? sender, EventArgs e) =>
        await CompleteDetailEditingAsync();

    private async void DetailMarkdownEditor_CancelRequested(object? sender, EventArgs e)
    {
        _detailAutoSaveTimer?.Stop();
        SetDetailEditorText(_detailOriginalBody);
        _detailEditRevision = _detailSavedRevision;
        _detailHasUnsavedChanges = false;
        if (_isCreatingDetail || !_isDualPane)
        {
            await CloseDetailPageAsync(saveBeforeClose: false);
            return;
        }

        _detailContentFormat = _detailItem?.ContentFormat ?? ViewModel.EditorContentFormat;
        _isDetailEditing = false;
        RefreshDetailPresentation();
    }

    private void DetailEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem?.IsRecent == true)
        {
            return;
        }

        EnterDetailEditMode();
    }

    private async void DetailDoneButton_Click(object sender, RoutedEventArgs e) =>
        await CompleteDetailEditingAsync();

    private async Task CompleteDetailEditingAsync()
    {
        await FlushPendingDetailSaveAsync();
        if (!await SaveDetailAsync(closeAfterSave: !_isDualPane))
        {
            return;
        }

        ShowStatusToast(_localizationService.T("QuickCapture.Saved"));

        if (_isDualPane)
        {
            _isDetailEditing = false;
            _detailOriginalBody = DetailMarkdownEditor.Text;
            ReconcileDetailSelection(autoSelectFirst: true);
        }
    }

    private void EnterDetailEditMode()
    {
        if (_detailItem?.IsRecent == true)
        {
            return;
        }

        _detailContentFormat = ViewModel.EditorContentFormat;
        _isDetailEditing = true;
        _detailOriginalBody = DetailMarkdownEditor.Text;
        RefreshDetailPresentation();
        DispatcherQueue.TryEnqueue(() => DetailMarkdownEditor.FocusEditor(moveCaretToEnd: false));
    }

    private async void DetailMarkdownView_TaskToggleRequested(
        object? sender,
        MarkdownTaskToggleRequestedEventArgs e)
    {
        if (_detailItem?.IsRecent == true ||
            _detailContentFormat != TextContentFormat.Markdown ||
            !_markdownDocumentService.TryToggleTask(
                DetailMarkdownEditor.Text,
                e.TaskIndex,
                out string updated))
        {
            return;
        }

        SetDetailEditorText(updated);
        MarkDetailDirty();
        DetailMarkdownView.Markdown = updated;
        await SaveDetailAsync(closeAfterSave: false);
    }

    private async void DetailMarkdownView_AttachmentOpenRequested(
        object? sender,
        MarkdownAttachmentRequestedEventArgs e)
    {
        string? path = ResolveDetailAttachmentPath(e.AttachmentId);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            await Launcher.LaunchFileAsync(file);
        }
    }

    private string? ResolveDetailAttachmentPath(string attachmentId) =>
        _detailItem?.Attachments.FirstOrDefault(attachment =>
            string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal))?.FilePath;

    private void SetDetailEditorText(string value)
    {
        _suppressDetailEditorChanges = true;
        try
        {
            DetailMarkdownEditor.Text = value ?? string.Empty;
        }
        finally
        {
            _suppressDetailEditorChanges = false;
        }
    }
}
