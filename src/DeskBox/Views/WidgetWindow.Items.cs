using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ViewModel_PropertyChanged(sender, e));
            return;
        }

        RefreshCompactPresentation();

        if (e.PropertyName is nameof(WidgetViewModel.IsLoading))
        {
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.TitleIconKind)
            or nameof(WidgetViewModel.IconGlyph)
            or nameof(WidgetViewModel.FollowsDefaultStoragePath))
        {
            ApplyTitleBarLayout();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.WidgetOpacity) or nameof(WidgetViewModel.MappedFolderPath))
        {
            ApplyBackdropPreference();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.ViewMode)
            or nameof(WidgetViewModel.IconViewVisibility)
            or nameof(WidgetViewModel.ListViewVisibility)
            or nameof(WidgetViewModel.ShowListItemDetails)
            or nameof(WidgetViewModel.ShowFileItemPathTooltips)
            or nameof(WidgetViewModel.IconTileWidth)
            or nameof(WidgetViewModel.IconTileHeight)
            or nameof(WidgetViewModel.IconTileMargin)
            or nameof(WidgetViewModel.IconTilePadding)
            or nameof(WidgetViewModel.IconContentSpacing)
            or nameof(WidgetViewModel.IconImageSize)
            or nameof(WidgetViewModel.IconLabelFontSize)
            or nameof(WidgetViewModel.IconLabelMaxWidth)
            or nameof(WidgetViewModel.ListItemMargin)
            or nameof(WidgetViewModel.ListItemPadding)
            or nameof(WidgetViewModel.ListIconSize)
            or nameof(WidgetViewModel.ListLabelFontSize))
        {
            ApplyTitleBarLayout();
            UpdateInteractiveSurfaces();
            QueueEmptyStateUpdate();
        }
    }

    private void ItemsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateInteractiveSurfaces();
    }

    public void RevealSavedItem(string itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => RevealSavedItem(itemPath));
            return;
        }

        var item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, itemPath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        SelectSingleItem(item);
        ShowStatusToast(_localizationService.T("Widget.SavedHere"));
    }

    private async void ItemsView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WidgetStackItem stack)
        {
            await ToggleStackFromInputAsync(stack);
            return;
        }

        if (e.ClickedItem is not WidgetItem item)
        {
            return;
        }

        if (!item.IsStackChild && GetExpandedStack() is { } expandedStack)
        {
            await SetStackExpandedWithAnimationAsync(expandedStack, expanded: false);
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        if (!_settingsService.Settings.DoubleClickToOpen)
        {
            OpenItem(item);
        }
    }

    private void ItemsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var diagSrc = e.OriginalSource as FrameworkElement;
        App.Log($"[DIAG] ItemsView_DoubleTapped srcType={e.OriginalSource?.GetType().Name} dcType={diagSrc?.DataContext?.GetType().Name ?? "null"} doubleClickToOpen={_settingsService.Settings.DoubleClickToOpen}");

        if (e.OriginalSource is FrameworkElement { DataContext: WidgetStackItem })
        {
            App.Log("[DIAG] ItemsView_DoubleTapped -> WidgetStackItem branch (no open)");
            e.Handled = true;
            return;
        }

        if (_settingsService.Settings.DoubleClickToOpen &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item)
        {
            OpenItem(item);
            e.Handled = true;
        }
        else
        {
            App.Log("[DIAG] ItemsView_DoubleTapped -> no matching WidgetItem DataContext (no open)");
        }
    }

    private void OpenItem(WidgetItem item)
    {
        App.Log($"[DIAG] OpenItem path='{item.Path}' target='{item.TargetPath}' isShortcut={item.IsShortcut} isFolder={item.IsFolder}");
        var result = ViewModel.OpenItem(item, _hWnd);
        if (result == FileService.OpenItemResult.Failed)
        {
            NotifyOpenFailed();
        }

        ClearRemovedCutPaths();
        UpdateEmptyState();
    }

    private static void NotifyOpenFailed()
    {
        var app = App.Current;
        if (app is null)
        {
            return;
        }

        var loc = app.LocalizationService;
        app.NativeNotificationService?.TryShow(
            loc.T("Widget.Open"),
            loc.T("Widget.OpenItemFailed"));
    }

    private void ItemsView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element.DataContext is not WidgetItem item)
        {
            return;
        }

        if (item is WidgetStackItem stack)
        {
            RemoveVirtualStackSelection();
            var stackFlyout = CreateStackFlyout(stack);
            ShowFlyoutWithElevation(stackFlyout, element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

        var listView = GetActiveItemsView();
        if (listView is not null)
        {
            ClearOtherWidgetSelections();
            if (!item.IsSelected)
            {
                listView.SelectedItems.Clear();
                listView.SelectedItems.Add(item);
                ApplySelectionState(listView);
            }
        }

        var selectedItems = GetSelectedItems();
        bool isMultiSelection = selectedItems.Count > 1;

        if (isMultiSelection)
        {
            var multiFlyout = CreateMultiSelectionFlyout();
            ShowFlyoutWithElevation(multiFlyout, element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

        var flyout = new MenuFlyout();

        var openItem = CreateFileContextCommand("Widget.Open", "\uE8E5");
        openItem.Click += (_, _) =>
        {
            flyout.Hide();
            OpenItem(item);
        };
        flyout.Items.Add(openItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var cutItem = CreateFileContextCommand("Common.Cut", "\uE8C6");
        cutItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopySelectionToClipboardAsync(cut: true);
        };
        flyout.Items.Add(cutItem);

        var copyItem = CreateFileContextCommand("Common.Copy", "\uE8C8");
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopySelectionToClipboardAsync(cut: false);
        };
        flyout.Items.Add(copyItem);

        var renameItem = CreateFileContextCommand("Common.Rename", "\uE8AC");
        renameItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await StartItemRenameAsync(item);
        };
        flyout.Items.Add(renameItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyPathItem = CreateFileContextCommand("Widget.CopyPath", "\uE8C8");
        copyPathItem.Click += (_, _) =>
        {
            flyout.Hide();
            CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPathItem);

        var showItem = CreateFileContextCommand("Widget.ShowInExplorer", "\uE838");
        showItem.Click += (_, _) =>
        {
            flyout.Hide();
            ViewModel.ShowInExplorerCommand.Execute(item);
        };
        flyout.Items.Add(showItem);

        if (CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var moveBackToDesktopItem = CreateFileContextCommand("Widget.MoveBackToDesktop", "\uE74A");
            moveBackToDesktopItem.Click += async (_, _) =>
            {
                flyout.Hide();
                try
                {
                    int movedCount = await ViewModel.MoveItemsBackToDesktopAsync([item], useShellProgress: true);
                    ShowStatusToast(movedCount > 0
                        ? _localizationService.Format("Widget.MovedBackToDesktop", movedCount)
                        : _localizationService.T("Widget.NoItemsMoved"));
                }
                catch (Exception ex)
                {
                    await ShowErrorDialogAsync(_localizationService.T("Widget.MoveBackToDesktopFailed"), ex.Message);
                }
            };
            flyout.Items.Add(moveBackToDesktopItem);
        }

        var deleteItem = CreateFileContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteSelectedItemsAsync();
        };
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
        e.Handled = true;
    }

    private MenuFlyoutItem CreateFileContextCommand(string localizationKey, string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = _localizationService.T(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    private async void ItemsView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await HandleItemsKeyDownAsync(e);
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await HandleItemsKeyDownAsync(e);
    }

    private async Task HandleItemsKeyDownAsync(KeyRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            HasAncestorOfType<TextBox>(source))
        {
            return;
        }

        // Space (QuickLook preview): handle even when e.Handled is already true.
        // The ListView may handle Space internally for selection toggle and set
        // e.Handled, which would prevent our preview from ever firing.
        if (e.Key == Windows.System.VirtualKey.Space &&
            ListViewNotCollapsedAndHasSelection())
        {
            e.Handled = true;
            if (GetPrimarySelectedItem() is { } spaceItem &&
                s_quickLookPreviewService.CanPreview(spaceItem.Path))
            {
                await s_quickLookPreviewService.TryToggleAsync(spaceItem.Path);
            }
            return;
        }

        if (e.Handled)
        {
            return;
        }

        if (TryHandleCompactActivation(e))
        {
            return;
        }

        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        bool shiftPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            ClearOtherWidgetSelections();
            listView.SelectAll();
            RemoveVirtualStackSelection();
            ApplySelectionState(listView);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectionToClipboardAsync(cut: false);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.X)
        {
            await CopySelectionToClipboardAsync(cut: true);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.V)
        {
            await PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Windows.System.VirtualKey.C)
        {
            CopySelectedPathsToClipboard();
            e.Handled = true;
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Windows.System.VirtualKey.N)
        {
            if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
            {
                await CreateFolderInMappedLocationAsync();
                e.Handled = true;
            }

            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.O)
        {
            if (GetPrimarySelectedItem() is { } openItem)
            {
                OpenItem(openItem);
                e.Handled = true;
            }

            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.R)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            ShowStatusToast(_localizationService.T("Widget.Refreshed"));
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F2)
        {
            if (GetPrimarySelectedItem() is { } renameItem)
            {
                await StartItemRenameAsync(renameItem);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            if (GetSelectedItems().Count > 0)
            {
                await DeleteSelectedItemsAsync();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            var expandedStack = ViewModel.VisibleItems
                .OfType<WidgetStackItem>()
                .FirstOrDefault(stack => stack.IsExpanded);
            if (expandedStack is not null)
            {
                await SetStackExpandedWithAnimationAsync(expandedStack, expanded: false);
                e.Handled = true;
            }
            else if (ViewModel.CollapseExpandedStack())
            {
                e.Handled = true;
            }
            else if (GetSelectedItems().Count > 0 || _cutClipboardPaths.Length > 0)
            {
                ClearItemSelectionCore(clearCutState: true);
                e.Handled = true;
            }
            else if (TryHandleCompactEscape())
            {
                e.Handled = true;
            }
            return;
        }

        if (listView.SelectedItems.OfType<WidgetStackItem>().FirstOrDefault() is { } selectedStack &&
            e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            await ToggleStackFromInputAsync(selectedStack);
            e.Handled = true;
        }
        else if (listView.SelectedItems.OfType<WidgetStackItem>().FirstOrDefault() is { } directionalStack &&
                 e.Key == Windows.System.VirtualKey.Right)
        {
            if (!directionalStack.IsExpanded)
            {
                await SetStackExpandedWithAnimationAsync(directionalStack, expanded: true);
            }

            e.Handled = true;
        }
        else if (listView.SelectedItems.OfType<WidgetStackItem>().FirstOrDefault() is { } collapseStack &&
                 e.Key == Windows.System.VirtualKey.Left)
        {
            if (collapseStack.IsExpanded)
            {
                await SetStackExpandedWithAnimationAsync(collapseStack, expanded: false);
            }

            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && GetPrimarySelectedItem() is { } item)
        {
            OpenItem(item);
            e.Handled = true;
        }
        // Space is handled at the top of HandleItemsKeyDownAsync (before the e.Handled check)
        // to prevent the ListView's internal selection toggle from swallowing the key.
    }

    /// <summary>
    /// Returns true when the widget is expanded and has a non-stack item selected
    /// (stack items use Space for expand/collapse toggle, not QuickLook preview).
    /// </summary>
    private bool ListViewNotCollapsedAndHasSelection()
    {
        if (IsWidgetCollapsed)
        {
            return false;
        }

        var lv = GetActiveItemsView();
        if (lv is null)
        {
            return false;
        }

        // If a stack is selected, Space toggles it — not a preview.
        if (lv.SelectedItems.OfType<WidgetStackItem>().Any())
        {
            return false;
        }

        return GetPrimarySelectedItem() is not null;
    }

    private static bool IsImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    private void ClearCutState()
    {
        _cutClipboardPaths = [];
        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private static string? TryGetPackageString(DataPackagePropertySetView properties, string key)
    {
        return properties.TryGetValue(key, out object? value) ? value as string : null;
    }

    private static IReadOnlyList<string> TryGetPackageStringArray(DataPackagePropertySetView properties, string key)
    {
        if (!properties.TryGetValue(key, out object? value) || value is null)
        {
            return [];
        }

        return value switch
        {
            string[] array => array,
            IReadOnlyList<string> readOnlyList => readOnlyList,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            _ => []
        };
    }

    private static bool HasFallbackFileFormats(DataPackageView dataView)
    {
        foreach (string format in dataView.AvailableFormats)
        {
            if (IsLikelyFileTransferFormat(format))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyFileTransferFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        if (format.StartsWith("Preferred DropEffect", StringComparison.Ordinal))
        {
            return false;
        }

        return format.Contains("StorageItems", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("StorageItem", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileName", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("Shell", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string[]> TryGetLegacyFormatPathsAsync(DataPackageView dataView)
    {
        var paths = new List<string>();
        foreach (string format in dataView.AvailableFormats)
        {
            if (!MayContainLegacyPathText(format))
            {
                continue;
            }

            try
            {
                object? data = await dataView.GetDataAsync(format);
                AppendCandidatePaths(paths, data);
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Legacy format read failed format='{format}': {ex.Message}");
            }
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MayContainLegacyPathText(string format)
    {
        return format.Contains("FileName", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("Shell IDList Array", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("ShellIDListArray", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileNameW", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileNameMap", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCandidatePaths(List<string> paths, object? data)
    {
        switch (data)
        {
            case null:
                return;
            case string text:
                AppendCandidatePathText(paths, text);
                return;
            case IEnumerable<string> strings:
                foreach (string value in strings)
                {
                    AppendCandidatePathText(paths, value);
                }

                return;
        }

        App.Log($"[DropDiagnostic] Legacy format returned unsupported type: {data.GetType().FullName}");
    }

    private static void AppendCandidatePathText(List<string> paths, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (string candidate in text.Split(
                     ["\0", "\r\n", "\n"],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalizeDroppedPath(candidate, out string normalizedPath))
            {
                paths.Add(normalizedPath);
            }
        }
    }
}
