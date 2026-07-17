using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
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
    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanAddInput)
        {
            await ViewModel.AddInputAsync();
            InputTextBox.Focus(FocusState.Programmatic);
            return;
        }

        OpenNewDetail();
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(!ViewModel.Config.IsPositionLocked);
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetSizeLocked(!ViewModel.Config.IsSizeLocked);
    }

    private void ExpandInputButton_Click(object sender, RoutedEventArgs e)
    {
        OpenNewDetail(InputTextBox.Text);
        InputTextBox.Text = string.Empty;
    }

    private void AddNoteCardButton_Click(object sender, RoutedEventArgs e)
    {
        OpenNewDetail();
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter ||
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) &
             Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.AddInputAsync();
    }

    private async void DetailBodyTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (ShouldSubmitQuickCaptureEditor(e))
        {
            await SaveAndCloseDetailAsync();
        }
        else
        {
            TextBoxEditorShortcutHelper.InsertLineBreak(DetailBodyTextBox);
        }
    }

    private bool ShouldSubmitQuickCaptureEditor(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return false;
        }

        bool controlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        return SettingsService.ShouldSubmitEditorOnEnter(
            _settingsService.Settings.QuickCaptureEditorEnterBehavior,
            controlPressed);
    }

    private void OpenNewDetail(string? initialBody = null)
    {
        _detailItem = null;
        _isCreatingDetail = true;
        _detailIsPinned = false;
        _detailAppearance = QuickCaptureAppearancePreset.Default;
        _pendingDetailAttachments = [];
        DetailTitleTextBox.Text = string.Empty;
        DetailBodyTextBox.Text = initialBody ?? string.Empty;
        RefreshDetailAttachmentList();
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            DateTimeOffset.Now.ToString("yyyy/M/d HH:mm"));
        ShowDetailPage();
    }

    private void OpenDetail(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent)
        {
            return;
        }

        _detailItem = item;
        _isCreatingDetail = false;
        _detailIsPinned = item.IsPinned;
        _detailAppearance = item.AppearancePreset;
        _pendingDetailAttachments = [];
        DetailTitleTextBox.Text = string.Empty;
        DetailBodyTextBox.Text = item.Type == QuickCaptureItemType.Image &&
                                 string.Equals(item.Body, "Image", StringComparison.Ordinal)
            ? string.Empty
            : BuildBodyText(item);
        DetailTimestampText.Text = BuildDetailTimestampText(item);
        RefreshDetailAttachmentList();
        ShowDetailPage();
    }

    private void ShowDetailPage()
    {
        ClearQuickCaptureCopySelection();
        ClearQuickCaptureListContainerSelection();
        CloseInlineEdit(restoreInputFocus: false);
        ListPage.Visibility = Visibility.Collapsed;
        DetailPage.Visibility = Visibility.Visible;
        DetailPage.IsHitTestVisible = true;
        UpdateDetailPinVisual();
        ApplyDetailMaterialSurface();
        long generation = ++_detailTransitionGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _detailTransitionGeneration ||
                DetailPage.Visibility != Visibility.Visible)
            {
                return;
            }

            DetailPageTransitionHelper.PlayEnter(DetailPage);
            DetailBodyTextBox.Focus(FocusState.Programmatic);
            DetailBodyTextBox.Select(DetailBodyTextBox.Text.Length, 0);
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
        if (_isSavingDetail || _isClosingDetail)
        {
            return false;
        }

        _isSavingDetail = true;
        try
        {
            return await SaveAndCloseDetailCoreAsync();
        }
        finally
        {
            _isSavingDetail = false;
        }
    }

    private async Task<bool> SaveAndCloseDetailCoreAsync()
    {
        string body = DetailBodyTextBox.Text;
        if (_isCreatingDetail)
        {
            if (_pendingDetailAttachments.Count > 0)
            {
                QuickCaptureItemViewModel? created = await ViewModel.AddItemWithAttachmentsAsync(
                    _pendingDetailAttachments);
                if (created is null)
                {
                    ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
                    return false;
                }

                await ViewModel.EditItemDetailsAsync(created, null, body, _detailAppearance);
                if (_detailIsPinned)
                {
                    await ViewModel.SetPinnedAsync(created.Id, true);
                }

                await ViewModel.RefreshItemsAsync();
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                QuickCaptureItem? created = await ViewModel.AddDetailedItemAsync(null, body, _detailAppearance);
                if (created is not null && _detailIsPinned)
                {
                    await ViewModel.SetPinnedAsync(created.Id, true);
                }
            }

            await CloseDetailPageAsync();
            return true;
        }

        if (_detailItem is not { } item)
        {
            await CloseDetailPageAsync();
            return true;
        }

        if (item.Type != QuickCaptureItemType.Image &&
            string.IsNullOrWhiteSpace(body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return false;
        }

        bool saved = await ViewModel.EditItemDetailsAsync(item, null, body, _detailAppearance);
        if (!saved)
        {
            return false;
        }

        if (_detailIsPinned != item.IsPinned)
        {
            await ViewModel.SetPinnedAsync(item.Id, _detailIsPinned);
        }

        await ViewModel.RefreshItemsAsync();

        await CloseDetailPageAsync();
        return true;
    }

    private async Task CloseDetailPageAsync()
    {
        if (_isClosingDetail || DetailPage.Visibility != Visibility.Visible)
        {
            return;
        }

        _isClosingDetail = true;
        long generation = ++_detailTransitionGeneration;
        DetailPage.IsHitTestVisible = false;
        try
        {
            await DetailPageTransitionHelper.PlayExitAsync(DetailPage);
            if (generation != _detailTransitionGeneration)
            {
                return;
            }

            _detailItem = null;
            _isCreatingDetail = false;
            _detailIsPinned = false;
            _detailAppearance = QuickCaptureAppearancePreset.Default;
            _pendingDetailAttachments = [];
            DetailAttachmentsList.ItemsSource = null;
            DetailAttachmentScroller.Visibility = Visibility.Collapsed;
            DetailPage.Visibility = Visibility.Collapsed;
            ListPage.Visibility = Visibility.Visible;
            ClearQuickCaptureListContainerSelection();
            RefreshItemMaterialSurfaces();
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

        if (isPinned)
        {
            ShowStatusToast(_localizationService.T("QuickCapture.PinnedSuccess"));
        }
    }

    private void UpdateDetailPinVisual()
    {
        DetailPinIcon.Glyph = _detailIsPinned ? "\uE840" : "\uE718";
        DetailUnpinSlash.Visibility = _detailIsPinned ? Visibility.Visible : Visibility.Collapsed;
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
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: false, out QuickCaptureAppearancePreset preset))
        {
            _detailAppearance = preset;
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
            button.BorderBrush = isSelected
                ? GetBrushResourceOrFallback(
                    "AccentFillColorDefaultBrush",
                    isDark ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0xFF, 0x00, 0x5F, 0xB8))
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
        if (_isCreatingDetail || _detailItem is not { } item || sender is not FrameworkElement anchor)
        {
            await CloseDetailPageAsync();
            return;
        }

        ShowConfirmMenu(
            anchor,
            _localizationService.T("QuickCapture.DeleteConfirm.Title"),
            _localizationService.T("Common.Delete"),
            async () =>
            {
                await DeleteItemWithUndoAsync(item);
                await CloseDetailPageAsync();
            });
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

    private void QuickCaptureViewSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectView(GetSelectedSegmentView());
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
