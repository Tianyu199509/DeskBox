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

    private void ItemsView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WidgetItem item)
        {
            return;
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
        if (_settingsService.Settings.DoubleClickToOpen &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item)
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    private void OpenItem(WidgetItem item)
    {
        ViewModel.OpenItem(item, _hWnd);
        ClearRemovedCutPaths();
        UpdateEmptyState();
    }

    private void ItemsView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element.DataContext is not WidgetItem item)
        {
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

        var deleteItem = CreateFileContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteSelectedItemsAsync();
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        var openItem = CreateFileContextCommand("Widget.Open", "\uE8E5");
        openItem.Click += (_, _) =>
        {
            flyout.Hide();
            OpenItem(item);
        };
        flyout.Items.Add(openItem);

        if (CanCopyImageText(item))
        {
            var copyImageTextItem = CreateFileContextCommand("Widget.CopyImageText", "\uE8C8");
            copyImageTextItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await CopyImageTextAsync(item);
            };
            flyout.Items.Add(copyImageTextItem);
        }

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

        var propItem = CreateFileContextCommand("Common.Properties", "\uE946");
        propItem.Click += (_, _) =>
        {
            flyout.Hide();
            ShellContextMenuHelper.ShowProperties(_hWnd, item.Path);
        };
        flyout.Items.Add(propItem);

        if (CanMoveItemsBackToDesktop())
        {
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
        if (e.Handled || _isMigrationBusy)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            HasAncestorOfType<TextBox>(source))
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
            ClearItemSelectionCore(clearCutState: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter && GetPrimarySelectedItem() is { } item)
        {
            OpenItem(item);
            e.Handled = true;
        }
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
