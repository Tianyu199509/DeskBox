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
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? MoreButton;
        ShowFlyoutWithElevation(CreateMoreFlyout(), target);
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        if (QuickCaptureShell.IsCollapsed)
        {
            ShowFlyoutWithElevation(CreateMoreFlyout(), QuickCaptureShell, e.GetPosition(QuickCaptureShell));
        }
        else
        {
            ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, e.GetPosition(TitleBarGrid));
        }
        e.Handled = true;
    }

    private void QuickCaptureShell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (QuickCaptureShell.ChromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden) ||
            !ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateMoreFlyout(), QuickCaptureShell, e.GetPosition(QuickCaptureShell));
        e.Handled = true;
    }

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateToggleMenuItem(
            _localizationService.T("Widget.LockPosition"),
            "\uE72E",
            ViewModel.Config.IsPositionLocked,
            SetPositionLocked));
        flyout.Items.Add(CreateToggleMenuItem(
            _localizationService.T("Widget.LockSize"),
            "\uE9CE",
            ViewModel.Config.IsSizeLocked,
            SetSizeLocked));
        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(WidgetChromeMenuBuilder.Create(
            ViewModel.Config,
            _chromeDescriptor,
            _localizationService,
            SetChromeModeOverride));
        flyout.Items.Add(WidgetCollapseMenuBuilder.Create(
            ViewModel.Config,
            _localizationService,
            SetCollapseBehaviorOverride,
            ResetCompactWidthOverride));
        flyout.Items.Add(new MenuFlyoutSeparator());

        var renameItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        bool startRenameWhenClosed = false;
        renameItem.Click += (_, _) => startRenameWhenClosed = true;
        flyout.Closed += (_, _) =>
        {
            if (startRenameWhenClosed)
            {
                DispatcherQueue.TryEnqueue(StartTitleRename);
            }
        };
        flyout.Items.Add(renameItem);

        var settingsItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("QuickCapture.OpenSettings"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += (_, _) => App.Current.ShowSettings("QuickCaptureSettings");
        flyout.Items.Add(settingsItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var clearItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("QuickCapture.ClearData"),
            Icon = new FontIcon
            {
                Glyph = "\uE894",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        clearItem.Click += (_, _) =>
        {
            ShowConfirmMenu(
                MoreButton,
                _localizationService.T("QuickCapture.ClearDataTitle"),
                _localizationService.T("QuickCapture.ClearData"),
                async () => await ClearDataAsync());
        };
        flyout.Items.Add(clearItem);

        // Turning off a feature widget preserves its content, configuration, and position.
        flyout.Items.Add(new MenuFlyoutSeparator());
        var disableWidget = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.FeatureWidget.Disable"),
            Icon = new FontIcon { Glyph = "\uE7E8" }
        };
        disableWidget.Click += async (_, _) =>
        {
            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.QuickCapture, enabled: false, reveal: false);
            }
        };
        flyout.Items.Add(disableWidget);

        return flyout;
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        WidgetChromeModeNames.SetOverrideMode(ViewModel.Config, mode);
        _settingsService.UpdateWidget(ViewModel.Config);
        ApplyTitleBarLayout();
    }

    private void SetPositionLocked(bool value)
    {
        ViewModel.SetPositionLocked(value);
        ApplyLockActionIconState();
    }

    private void SetSizeLocked(bool value)
    {
        ViewModel.SetSizeLocked(value);
        ApplyLockActionIconState();
    }

    private MenuFlyout CreateItemFlyout(QuickCaptureItemViewModel item, FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        var copyItem = CreateQuickCaptureContextCommand("Common.Copy", "\uE8C8");
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopyItemWithFeedbackAsync(item);
        };
        flyout.Items.Add(copyItem);

        if (item.IsRecent)
        {
            var saveItem = CreateQuickCaptureContextCommand("QuickCapture.SaveToRecords", "\uE74E");
            saveItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ViewModel.SaveRecentItemAsync(item);
            };
            flyout.Items.Add(saveItem);

            var pinRecentItem = CreateQuickCaptureContextCommand("QuickCapture.PinToRecords", "\uE718");
            pinRecentItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ViewModel.PinRecentItemAsync(item);
            };
            flyout.Items.Add(pinRecentItem);

            var deleteRecentItem = CreateQuickCaptureContextCommand("Common.Delete", "\uE74D");
            deleteRecentItem.Click += (_, _) =>
            {
                flyout.Hide();
                DispatcherQueue.TryEnqueue(() => ShowQuickCaptureDeleteConfirmFlyout(item, anchor));
            };
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deleteRecentItem);
            return flyout;
        }

        var editItem = CreateQuickCaptureContextCommand("QuickCapture.Edit", "\uE70F");
        editItem.Click += (_, _) =>
        {
            flyout.Hide();
            OpenDetail(item);
        };
        flyout.Items.Add(editItem);

        var pinItem = new MenuFlyoutItem
        {
            Text = item.IsPinned ? _localizationService.T("QuickCapture.Unpin") : _localizationService.T("QuickCapture.Pin"),
            Icon = new FontIcon { Glyph = item.IsPinned ? "\uE840" : "\uE718" }
        };
        pinItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ViewModel.TogglePinnedAsync(item);
        };
        flyout.Items.Add(pinItem);

        var deleteItem = CreateQuickCaptureContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += (_, _) =>
        {
            flyout.Hide();
            DispatcherQueue.TryEnqueue(() => ShowQuickCaptureDeleteConfirmFlyout(item, anchor));
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (item.Type != QuickCaptureItemType.Image)
        {
            var notepadItem = CreateQuickCaptureContextCommand("QuickCapture.EditInNotepad", "\uE70F");
            notepadItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await OpenTextInNotepadAsync(item);
            };
            flyout.Items.Add(notepadItem);
        }

        flyout.Items.Add(CreateAppearanceFlyout(item, flyout));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private MenuFlyout CreateMultiItemFlyout(
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems,
        FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        string[] selectedIds = selectedItems.Select(item => item.Id).ToArray();
        bool isRecent = selectedItems.All(item => item.IsRecent);

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("QuickCapture.CopySelected", selectedItems.Count),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopySelectedQuickCaptureItemsAsync(selectedItems);
        };
        flyout.Items.Add(copyItem);

        if (!isRecent)
        {
            bool shouldPin = !selectedItems.All(item => item.IsPinned);
            var pinItem = new MenuFlyoutItem
            {
                Text = _localizationService.T(shouldPin ? "QuickCapture.Pin" : "QuickCapture.Unpin"),
                Icon = new FontIcon { Glyph = shouldPin ? "\uE718" : "\uE840" }
            };
            pinItem.Click += async (_, _) =>
            {
                flyout.Hide();
                ClearQuickCaptureCopySelection();
                await ViewModel.SetPinnedAsync(selectedIds, shouldPin);
                if (shouldPin)
                {
                    ShowStatusToast(_localizationService.T("QuickCapture.PinnedSuccess"));
                }
            };
            flyout.Items.Add(pinItem);
        }

        var deleteItem = CreateQuickCaptureContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += (_, _) =>
        {
            flyout.Hide();
            DispatcherQueue.TryEnqueue(() => ShowQuickCaptureDeleteSelectedConfirmFlyout(
                selectedIds,
                isRecent,
                anchor));
        };
        if (!isRecent)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateBatchAppearanceFlyout(selectedItems, selectedIds, flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private MenuFlyoutItem CreateQuickCaptureContextCommand(string localizationKey, string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = _localizationService.T(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    private MenuFlyoutSubItem CreateAppearanceFlyout(QuickCaptureItemViewModel item, MenuFlyout owner)
    {
        var appearanceMenu = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };

        foreach (var (preset, textKey) in new[]
        {
            (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
            (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
            (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
            (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
            (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
            (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
        })
        {
            var menuItem = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.T(textKey),
                IsChecked = item.AppearancePreset == preset
            };
            menuItem.Click += async (_, _) =>
            {
                owner.Hide();
                await ViewModel.SetAppearanceAsync(item, preset);
                DispatcherQueue.TryEnqueue(RefreshItemMaterialSurfaces);
            };
            appearanceMenu.Items.Add(menuItem);
        }

        return appearanceMenu;
    }

    private MenuFlyoutSubItem CreateBatchAppearanceFlyout(
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        MenuFlyout owner)
    {
        var appearanceMenu = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };
        foreach (var (preset, textKey) in new[]
        {
            (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
            (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
            (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
            (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
            (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
            (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
        })
        {
            var menuItem = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.T(textKey),
                IsChecked = selectedItems.All(item => item.AppearancePreset == preset)
            };
            menuItem.Click += async (_, _) =>
            {
                owner.Hide();
                ClearQuickCaptureCopySelection();
                await ViewModel.SetAppearanceAsync(selectedIds, preset);
                DispatcherQueue.TryEnqueue(RefreshItemMaterialSurfaces);
            };
            appearanceMenu.Items.Add(menuItem);
        }

        return appearanceMenu;
    }

    private MenuFlyoutSubItem CreateSaveToFileWidgetSubItem(QuickCaptureItemViewModel item)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.SaveToFileWidget"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };

        var targets = App.Current.WidgetManager?.GetQuickCaptureFileWidgetTargets() ?? [];
        if (targets.Count == 0)
        {
            subItem.Items.Add(new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.NoFileWidgetTargets"),
                IsEnabled = false
            });
            return subItem;
        }

        foreach (var target in targets)
        {
            var targetItem = new MenuFlyoutItem
            {
                Text = target.Name,
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            targetItem.Click += async (_, _) =>
            {
                await SaveQuickCaptureItemToFileWidgetAsync(item, target.WidgetId);
            };
            subItem.Items.Add(targetItem);
        }

        return subItem;
    }

    private MenuFlyoutItem? CreateSaveToLastFileWidgetItem(QuickCaptureItemViewModel item)
    {
        var target = App.Current.WidgetManager?.GetLastQuickCaptureFileWidgetTarget();
        if (target is null)
        {
            return null;
        }

        var menuItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("QuickCapture.SaveToLastFileWidget", target.Name),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        menuItem.Click += async (_, _) => await SaveQuickCaptureItemToFileWidgetAsync(item, target.WidgetId);
        return menuItem;
    }

    private async Task SaveQuickCaptureItemToFileWidgetAsync(QuickCaptureItemViewModel item, string targetWidgetId)
    {
        if (App.Current.WidgetManager is null)
        {
            return;
        }

        string? savedPath = await App.Current.WidgetManager.SaveQuickCaptureItemToFileWidgetAsync(
            item.ToModel(),
            targetWidgetId,
            _localizationService.T("QuickCapture.ImageExportFileNamePrefix"));
        ShowStatusToast(string.IsNullOrWhiteSpace(savedPath)
            ? _localizationService.T("QuickCapture.SaveToFileWidgetFailed")
            : _localizationService.T("QuickCapture.SavedToFileWidget"));
    }
}
