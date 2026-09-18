using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    /// <summary>
    /// Focuses the input text box so the user can immediately type a new note.
    /// Used by search actions after showing the widget.
    /// </summary>
    internal void FocusInputForNewNote()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            InputTextBox.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>Opens the exact saved item requested by global search.</summary>
    internal async Task RevealItemAsync(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        ViewModel.CollapseSearch();
        ViewModel.SelectedView = QuickCaptureViewMode.Records;
        await ViewModel.RefreshItemsAsync();

        var item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        ItemsListView.SelectedItem = item;
        ItemsListView.ScrollIntoView(item);
        await OpenDetailAfterSavingAsync(item);
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanAddInput)
        {
            QuickCaptureWriteResult result = await ViewModel.AddInputAsync();
            ReportBodyTruncation(result);
            InputTextBox.Focus(FocusState.Programmatic);
            return;
        }

        await OpenNewDetailAsync();
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(!ViewModel.Config.IsPositionLocked);
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetSizeLocked(!ViewModel.Config.IsSizeLocked);
    }

    private async void ExpandInputButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenNewDetailAsync(InputTextBox.Text);
        InputTextBox.Text = string.Empty;
    }

    private async void AddNoteCardButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenNewDetailAsync();
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool controlPressed = Win32Helper.IsKeyPressed(
            Windows.System.VirtualKey.Control);
        bool saveShortcut = TextBoxEditorShortcutHelper.IsCtrlSaveShortcut(
            e.Key,
            controlPressed,
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift));
        if (e.Key != Windows.System.VirtualKey.Enter && !saveShortcut)
        {
            return;
        }

        e.Handled = true;
        if (saveShortcut || SettingsService.ShouldSubmitEditorOnEnter(
                _settingsService.Settings.QuickCaptureEditorEnterBehavior,
                controlPressed))
        {
            QuickCaptureWriteResult result = await ViewModel.AddInputAsync();
            ReportBodyTruncation(result);
            return;
        }

        TextBoxEditorShortcutHelper.InsertLineBreak(InputTextBox);
    }

    private async Task OpenNewDetailAsync(string? initialBody = null)
    {
        await FlushPendingDetailSaveAsync();
        _detailAutoSaveTimer?.Stop();
        _detailItem = null;
        _isCreatingDetail = true;
        _isDetailEditing = true;
        _detailEditRevision = string.IsNullOrEmpty(initialBody) ? 0 : 1;
        _detailSavedRevision = 0;
        _detailHasUnsavedChanges = _detailEditRevision != _detailSavedRevision;
        _detailContentFormat = ViewModel.EditorContentFormat;
        _detailIsPinned = ViewModel.IsPinnedView;
        _detailAppearance = QuickCaptureAppearancePreset.Default;
        _pendingDetailAttachments = [];
        DetailTitleTextBox.Text = string.Empty;
        _detailOriginalBody = initialBody ?? string.Empty;
        SetDetailEditorText(_detailOriginalBody);
        RefreshDetailAttachmentList();
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            DateTimeOffset.Now.ToString("yyyy/M/d HH:mm"));
        ShowDetailPage();
        if (_detailHasUnsavedChanges)
        {
            _detailAutoSaveTimer?.Start();
        }
    }

    private void OpenDetail(QuickCaptureItemViewModel item)
    {
        _detailAutoSaveTimer?.Stop();
        _detailItem = item;
        _isCreatingDetail = false;
        _isDetailEditing = !item.IsRecent &&
            (!_isDualPane || SettingsService.NormalizeQuickCaptureWideOpenMode(
                _settingsService.Settings.QuickCaptureWideOpenMode) ==
                SettingsService.QuickCaptureWideOpenEditing);
        _detailContentFormat = _isDetailEditing
            ? ViewModel.EditorContentFormat
            : item.ContentFormat;
        _detailHasUnsavedChanges = false;
        _detailEditRevision = 0;
        _detailSavedRevision = 0;
        _detailIsPinned = item.IsPinned;
        _detailAppearance = item.AppearancePreset;
        _pendingDetailAttachments = [];
        DetailTitleTextBox.Text = string.Empty;
        _detailOriginalBody = item.Type == QuickCaptureItemType.Image &&
                              string.Equals(item.Body, "Image", StringComparison.Ordinal)
            ? string.Empty
            : BuildBodyText(item);
        SetDetailEditorText(_detailOriginalBody);
        DetailTimestampText.Text = BuildDetailTimestampText(item);
        RefreshDetailAttachmentList();
        ShowDetailPage();
        if (_detailHasUnsavedChanges)
        {
            _detailAutoSaveTimer?.Start();
        }
    }

    private async Task OpenDetailAfterSavingAsync(QuickCaptureItemViewModel item)
    {
        if (_detailItem is not null &&
            string.Equals(_detailItem.Id, item.Id, StringComparison.Ordinal))
        {
            if (!_isDualPane && !_showDetailInSinglePane)
            {
                ShowDetailPage();
            }
            return;
        }

        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return;
        }

        OpenDetail(item);
    }

    private void ShowDetailPage()
    {
        ClearQuickCaptureCopySelection();
        CloseInlineEdit(restoreInputFocus: false);
        _showDetailInSinglePane = true;
        ApplyResponsiveDetailLayout();
        UpdateDetailSelectionVisuals();
        UpdateDetailPinVisual();
        ApplyDetailMaterialSurface();
        RefreshDetailPresentation();
        long generation = ++_detailTransitionGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _detailTransitionGeneration ||
                DetailPage.Visibility != Visibility.Visible)
            {
                return;
            }

            if (!_isDualPane)
            {
                DetailPageTransitionHelper.PlayEnter(DetailPage);
            }

            if (_isDetailEditing)
            {
                DetailMarkdownEditor.FocusEditor(moveCaretToEnd: _isCreatingDetail);
            }
        });
    }

    private static string BuildBodyText(QuickCaptureItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Body;
        }

        return string.IsNullOrWhiteSpace(item.Body)
            ? item.Title
            : $"{item.Title}{Environment.NewLine}{item.Body}";
    }

    private async void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAndCloseDetailAsync();
    }

    private async Task<bool> SaveAndCloseDetailAsync()
    {
        await FlushPendingDetailSaveAsync();
        return await SaveDetailAsync(closeAfterSave: true);
    }

    private async Task<bool> SaveDetailAsync(bool closeAfterSave)
    {
        if (_isClosingDetail)
        {
            return false;
        }

        await _detailSaveGate.WaitAsync();
        try
        {
            _isSavingDetail = true;
            if (_detailItem?.IsRecent == true)
            {
                if (closeAfterSave)
                {
                    await CloseDetailPageAsync(saveBeforeClose: false);
                }
                return true;
            }

            bool saved;
            do
            {
                saved = await SaveDetailCoreAsync(closeAfterSave: false);
                if (!saved)
                {
                    return false;
                }
            }
            while (_detailHasUnsavedChanges);

            if (closeAfterSave)
            {
                await CloseDetailPageAsync(saveBeforeClose: false);
            }
            return true;
        }
        finally
        {
            _isSavingDetail = false;
            _detailSaveGate.Release();
        }
    }

    private async Task<bool> SaveDetailCoreAsync(bool closeAfterSave)
    {
        long revisionAtStart = _detailEditRevision;
        string body = DetailMarkdownEditor.Text;
        if (_isCreatingDetail)
        {
            QuickCaptureItem? createdModel = null;
            if (_pendingDetailAttachments.Count > 0)
            {
                QuickCaptureItemViewModel? created = await ViewModel.AddItemWithAttachmentsAsync(
                    _pendingDetailAttachments);
                if (created is null)
                {
                    ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
                    return false;
                }

                QuickCaptureWriteResult attachmentUpdateResult =
                    await ViewModel.EditItemDetailsWithResultAsync(
                    created,
                    null,
                    body,
                    _detailAppearance,
                    _detailContentFormat);
                if (!attachmentUpdateResult.Saved)
                {
                    return false;
                }

                ReportBodyTruncation(attachmentUpdateResult);
                createdModel = attachmentUpdateResult.Item ?? created.ToModel();
                if (created.IsPinned != _detailIsPinned)
                {
                    await ViewModel.SetPinnedAsync(created.Id, _detailIsPinned);
                }

                await ViewModel.RefreshItemsAsync();
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                QuickCaptureWriteResult addResult =
                    await ViewModel.AddDetailedItemWithResultAsync(
                    null,
                    body,
                    _detailAppearance,
                    _detailContentFormat);
                ReportBodyTruncation(addResult);
                createdModel = addResult.Item;
                if (createdModel is not null && createdModel.IsPinned != _detailIsPinned)
                {
                    await ViewModel.SetPinnedAsync(createdModel.Id, _detailIsPinned);
                }
            }

            if (createdModel is not null)
            {
                await ViewModel.RefreshItemsAsync();
                _detailItem = ViewModel.Items.FirstOrDefault(item =>
                    string.Equals(item.Id, createdModel.Id, StringComparison.Ordinal));
                _isCreatingDetail = false;
            }

            _detailSavedRevision = Math.Max(_detailSavedRevision, revisionAtStart);
            _detailHasUnsavedChanges = _detailEditRevision > revisionAtStart;
            _detailOriginalBody = body;
            if (closeAfterSave)
            {
                await CloseDetailPageAsync(saveBeforeClose: false);
            }
            else
            {
                UpdateDetailSelectionVisuals();
                RefreshDetailPresentation();
            }

            return true;
        }

        if (_detailItem is not { } item)
        {
            if (closeAfterSave)
            {
                await CloseDetailPageAsync(saveBeforeClose: false);
            }
            return !_detailHasUnsavedChanges;
        }

        if (!_detailHasUnsavedChanges && _detailAppearance == item.AppearancePreset &&
            _detailIsPinned == item.IsPinned && _detailContentFormat == item.ContentFormat)
        {
            if (closeAfterSave)
            {
                await CloseDetailPageAsync(saveBeforeClose: false);
            }
            return true;
        }

        if (item.Type != QuickCaptureItemType.Image &&
            string.IsNullOrWhiteSpace(body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return false;
        }

        QuickCaptureWriteResult detailUpdateResult =
            await ViewModel.EditItemDetailsWithResultAsync(
            item,
            null,
            body,
            _detailAppearance,
            _detailContentFormat);
        if (!detailUpdateResult.Saved)
        {
            return false;
        }

        ReportBodyTruncation(detailUpdateResult);

        if (_detailIsPinned != item.IsPinned)
        {
            await ViewModel.SetPinnedAsync(item.Id, _detailIsPinned);
        }

        await ViewModel.RefreshItemsAsync();
        _detailItem = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, item.Id, StringComparison.Ordinal));
        _detailSavedRevision = Math.Max(_detailSavedRevision, revisionAtStart);
        _detailHasUnsavedChanges = _detailEditRevision > revisionAtStart;
        _detailOriginalBody = body;
        if (closeAfterSave)
        {
            await CloseDetailPageAsync(saveBeforeClose: false);
        }
        else
        {
            UpdateDetailSelectionVisuals();
            RefreshDetailPresentation();
        }

        return true;
    }

    private void ReportBodyTruncation(QuickCaptureWriteResult result)
    {
        if (result.WasTruncated)
        {
            ShowStatusToast(_localizationService.T("QuickCapture.BodyTruncated"));
        }
    }

    private async Task CloseDetailPageAsync(bool saveBeforeClose = true)
    {
        if (_isClosingDetail || DetailPage.Visibility != Visibility.Visible)
        {
            return;
        }

        if (saveBeforeClose && _detailHasUnsavedChanges &&
            !await SaveDetailAsync(closeAfterSave: false))
        {
            return;
        }

        _isClosingDetail = true;
        _detailAutoSaveTimer?.Stop();
        long generation = ++_detailTransitionGeneration;
        DetailPage.IsHitTestVisible = false;
        try
        {
            if (!_isDualPane)
            {
                await DetailPageTransitionHelper.PlayExitAsync(DetailPage);
            }
            if (generation != _detailTransitionGeneration)
            {
                return;
            }

            _detailItem = null;
            _isCreatingDetail = false;
            _isDetailEditing = false;
            _detailHasUnsavedChanges = false;
            _detailEditRevision = 0;
            _detailSavedRevision = 0;
            _showDetailInSinglePane = false;
            _detailIsPinned = false;
            _detailAppearance = QuickCaptureAppearancePreset.Default;
            _pendingDetailAttachments = [];
            DetailAttachmentsList.ItemsSource = null;
            DetailAttachmentScroller.Visibility = Visibility.Collapsed;
            ApplyResponsiveDetailLayout();
            UpdateDetailSelectionVisuals();
            RefreshItemMaterialSurfaces();
            if (_isDualPane)
            {
                ReconcileDetailSelection(autoSelectFirst: true);
            }
            RootGrid.Focus(FocusState.Programmatic);
        }
        finally
        {
            if (generation == _detailTransitionGeneration)
            {
                DetailPageTransitionHelper.Reset(DetailPage);
                DetailPage.IsHitTestVisible = true;
            }

            _isClosingDetail = false;
        }
    }

    private void ClearQuickCaptureListContainerSelection()
    {
        ItemsListView.SelectedItem = null;
        foreach (object visibleItem in ItemsListView.Items)
        {
            if (ItemsListView.ContainerFromItem(visibleItem) is ListViewItem container)
            {
                container.IsSelected = false;
            }
        }
    }

    private async void DetailPinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailItem?.IsRecent == true)
        {
            return;
        }

        bool wasPinned = _detailIsPinned;
        bool isPinned = !wasPinned;
        _detailIsPinned = isPinned;
        UpdateDetailPinVisual();

        if (_detailItem is not null && !await ViewModel.SetPinnedAsync(_detailItem.Id, isPinned))
        {
            _detailIsPinned = wasPinned;
            UpdateDetailPinVisual();
            return;
        }

        ShowStatusToast(_localizationService.T(isPinned
            ? "QuickCapture.PinnedSuccess"
            : "QuickCapture.UnpinnedSuccess"));
    }

    private void UpdateDetailPinVisual()
    {
        DetailPinIcon.IsPinned = _detailIsPinned;
        DetailPinButton.Background = _detailIsPinned
            ? GetBrushResourceOrFallback(
                "SubtleFillColorSecondaryBrush",
                DetailPinButton.ActualTheme == ElementTheme.Dark
                    ? ColorHelper.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00))
            : new SolidColorBrush(Colors.Transparent);
        string tooltip = _localizationService.T(_detailIsPinned ? "QuickCapture.Unpin" : "QuickCapture.Pin");
        ToolTipService.SetToolTip(DetailPinButton, tooltip);
        AutomationProperties.SetName(DetailPinButton, tooltip);
    }

    private void MaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDetailEditing || _detailItem?.IsRecent == true)
        {
            return;
        }

        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: false, out QuickCaptureAppearancePreset preset))
        {
            _detailAppearance = preset;
            MarkDetailDirty();
            _detailAutoSaveTimer?.Stop();
            _detailAutoSaveTimer?.Start();
            ApplyDetailMaterialSurface();
        }
    }

    private void ApplyDetailMaterialSurface()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        DetailMaterialSurface.Background = GetMaterialBrush(_detailAppearance, isDark);
        DetailMaterialSurface.BorderBrush = GetMaterialBorderBrush(_detailAppearance, isDark);
        foreach (Button button in GetMaterialButtons())
        {
            bool isSelected = string.Equals(button.Tag as string, _detailAppearance.ToString(), StringComparison.Ordinal);
            // A selected swatch ring is a selection state, so it uses the
            // neutral strong stroke rather than the accent.
            button.BorderBrush = isSelected
                ? new SolidColorBrush(NeutralInteractionBrush.Line(button))
                : new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(isSelected ? 1.5 : 1);
        }
    }

    private IEnumerable<Button> GetMaterialButtons()
    {
        yield return DefaultMaterialButton;
        yield return PaperMaterialButton;
        yield return YellowMaterialButton;
        yield return RoseMaterialButton;
        yield return MintMaterialButton;
        yield return BlueMaterialButton;
    }

    private async void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCreatingDetail || _detailItem is not { } item)
        {
            await CloseDetailPageAsync();
            return;
        }

        await DeleteItemWithUndoAsync(item);
        await CloseDetailPageAsync();
    }

    private string BuildDetailTimestampText(QuickCaptureItemViewModel item)
    {
        QuickCaptureItem model = item.ToModel();
        string created = _localizationService.Format(
            "QuickCapture.Detail.Created",
            model.CreatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm"));
        return created;
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ViewModel.CollapseSearch();
            RootGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Down) ||
            ItemsListView.Items.Count == 0)
        {
            return;
        }

        ItemsListView.SelectedIndex = 0;
        ItemsListView.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandSearch();
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CloseSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CollapseSearch();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void QuickCaptureViewSegmented_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySegmentedStyle();
    }

    private void QuickCaptureViewSegmented_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySegmentedLayout();
    }

    private void ApplySegmentedLayout()
    {
        if (IsCompactTransitionActive)
        {
            _segmentedLayoutRefreshDeferred = true;
            return;
        }

        if (ViewModel.TabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(QuickCaptureViewSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(QuickCaptureViewSegmented);
        }
    }

    private void ApplySegmentedStyle()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        WidgetSegmentedStyleHelper.Apply(QuickCaptureViewSegmented, ViewModel.TabStyle);
        ApplySegmentedLayout();
    }

    private async void QuickCaptureViewSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingQuickCaptureViewSelection)
        {
            return;
        }

        QuickCaptureViewMode requestedView = GetSelectedSegmentView();
        if (ViewModel.SelectedView == requestedView)
        {
            return;
        }

        _isSynchronizingQuickCaptureViewSelection = true;
        RefreshSelectedViewSegment();
        _isSynchronizingQuickCaptureViewSelection = false;
        if (!await CommitDetailBeforeViewChangeAsync())
        {
            return;
        }

        SelectView(requestedView);
    }

    private async Task<bool> CommitDetailBeforeViewChangeAsync()
    {
        if (!_isDetailEditing && !_detailHasUnsavedChanges)
        {
            return true;
        }

        await FlushPendingDetailSaveAsync();
        if (_detailHasUnsavedChanges)
        {
            return false;
        }

        if (_isCreatingDetail && _detailItem is null)
        {
            await CloseDetailPageAsync(saveBeforeClose: false);
        }
        else
        {
            _isDetailEditing = false;
            _detailOriginalBody = DetailMarkdownEditor.Text;
            RefreshDetailPresentation();
        }
        return true;
    }

    private void SelectView(QuickCaptureViewMode view)
    {
        if (ViewModel.SelectedView == view)
        {
            RefreshSelectedViewSegment();
            return;
        }

        ViewModel.SelectedView = view;
    }

    private async void EnableRecentCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (await QuickCaptureClipboardActivationHelper.EnableAsync(RootGrid.XamlRoot, _localizationService))
        {
            SelectView(QuickCaptureViewMode.Recent);
        }
    }
}
