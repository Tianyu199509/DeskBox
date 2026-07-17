// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

/// <summary>
/// Partial class containing context menu, flyout, toast, and error dialog logic.
/// </summary>
public sealed partial class WidgetWindow
{
    private MenuFlyout? _itemDeleteConfirmFlyout;
    private Flyout? _messageFlyout;
    private MenuFlyout? _deleteWidgetFlyout;
    private FrameworkElement? _lastMoreFlyoutTarget;
    private Windows.Foundation.Point? _lastMoreFlyoutPosition;
    private bool _isDeleteWidgetFlyoutOpen;
    private bool _isInlineFlyoutOpen;

    // ── Status toast ───────────────────────────────────────────

    private void ShowStatusToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusToastText.Text = message;
        StatusToast.Visibility = Visibility.Visible;
        StatusToast.Opacity = 1;

        _statusToastTimer ??= DispatcherQueue.CreateTimer();
        _statusToastTimer.Stop();
        _statusToastTimer.Interval = TimeSpan.FromSeconds(2.2);
        _statusToastTimer.Tick -= StatusToastTimer_Tick;
        _statusToastTimer.Tick += StatusToastTimer_Tick;
        _statusToastTimer.Start();
    }

    private void StatusToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        StatusToast.Opacity = 0;
        StatusToast.Visibility = Visibility.Collapsed;
    }

    // ── Error dialog ───────────────────────────────────────────

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        string displayMessage = FormatUserFacingError(message);
        var completion = new TaskCompletionSource();

        _messageFlyout?.Hide();

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Red),
            TextWrapping = TextWrapping.Wrap
        };

        var messageText = new TextBlock
        {
            Text = displayMessage,
            MaxWidth = 280,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        };

        var closeButton = new Button
        {
            Content = _localizationService.T("Common.Ok"),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var panel = new StackPanel
        {
            Width = 300,
            Spacing = 12,
            Padding = new Thickness(4)
        };
        panel.Children.Add(titleText);
        panel.Children.Add(messageText);
        panel.Children.Add(closeButton);

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false
        };

        _messageFlyout = flyout;
        closeButton.Click += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_messageFlyout, flyout))
            {
                _messageFlyout = null;
            }

            _isInlineFlyoutOpen = false;
            completion.TrySetResult();
            ReleaseInteractionLayer("file-message-closed");
        };

        _isInlineFlyoutOpen = true;
        BeginInteractionLayer("file-message-opened");
        flyout.ShowAt(MoreButton);
        await completion.Task;
    }

    private string FormatUserFacingError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return _localizationService.T("Widget.Error.OperationIncomplete");
        }

        if (message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已取消", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Widget.Error.OperationCanceled");
        }

        if (message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("没有权限", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.Format("Widget.Error.AccessDenied", message);
        }

        if (message.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("另一个进程", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("正由另一进程使用", StringComparison.OrdinalIgnoreCase))
        {
            string? path = TryExtractQuotedPath(message);
            return string.IsNullOrWhiteSpace(path)
                ? _localizationService.T("Widget.Error.FileInUse")
                : _localizationService.Format("Widget.Error.FileInUseWithPath", path);
        }

        if (message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已存在", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.Format("Widget.Error.DuplicateName", message);
        }

        return message;
    }

    private static string? TryExtractQuotedPath(string message)
    {
        int start = message.IndexOf('\'');
        if (start < 0)
        {
            return null;
        }

        int end = message.IndexOf('\'', start + 1);
        if (end <= start + 1)
        {
            return null;
        }

        return message[(start + 1)..end];
    }

    // ── Flyout elevation helper ────────────────────────────────

    private void ShowFlyoutWithElevation(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        BeginInteractionLayer("file-flyout-opened");
        flyout.Closed += (_, _) => ReleaseInteractionLayer("file-flyout-closed");

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    // ── Native context menu ────────────────────────────────────

    // ── Multi-selection context menu items ─────────────────────

    private MenuFlyout CreateMultiSelectionFlyout()
    {
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

        var deleteItem = CreateFileContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteSelectedItemsAsync();
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyPathItem = CreateFileContextCommand("Widget.CopyPath", "\uE8C8");
        copyPathItem.Click += (_, _) =>
        {
            flyout.Hide();
            CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPathItem);

        if (CanMoveItemsBackToDesktop())
        {
            var moveBackToDesktopItem = CreateFileContextCommand("Widget.MoveBackToDesktop", "\uE74A");
            moveBackToDesktopItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await MoveSelectedItemsBackToDesktopAsync();
            };
            flyout.Items.Add(moveBackToDesktopItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    // ── Content area flyout ────────────────────────────────────

    private MenuFlyout CreateContentAreaFlyout()
    {
        var flyout = new MenuFlyout();

        AddCurrentWidgetContentActions(flyout);

        var pasteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Paste"),
            Icon = new FontIcon { Glyph = "\uE77F" },
            IsEnabled = CanPasteFromClipboard()
        };
        pasteItem.Click += async (_, _) =>
        {
            try
            {
                await PasteFromClipboardAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(_localizationService.T("Widget.PasteFailed"), ex.Message);
            }
        };
        flyout.Items.Add(pasteItem);

        if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            var newFolderItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Common.NewFolder"),
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            newFolderItem.Click += async (_, _) => await CreateFolderInMappedLocationAsync();
            flyout.Items.Add(newFolderItem);

            var openFolderItem = new MenuFlyoutItem
            {
                Text = ViewModel.FollowsDefaultStoragePath
                    ? _localizationService.T("Widget.OpenStorageFolder")
                    : _localizationService.T("Widget.OpenCurrentFolder"),
                Icon = new FontIcon { Glyph = "\uE838" }
            };
            openFolderItem.Click += (_, _) => Win32Helper.OpenFile(ViewModel.MappedFolderPath);
            flyout.Items.Add(openFolderItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var iconView = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.IconView"),
            IsChecked = ViewModel.IsIconMode
        };
        iconView.Click += SetIconView_Click;
        flyout.Items.Add(iconView);

        var listView = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.ListView"),
            IsChecked = ViewModel.IsListMode
        };
        listView.Click += SetListView_Click;
        flyout.Items.Add(listView);

        flyout.Items.Add(CreateStackSettingsMenu());

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

        var sortSubItem = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("Widget.SortBy"),
            Icon = new FontIcon { Glyph = "\uE8CB" }
        };

        var sortName = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.Name") };
        var sortSize = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.Size") };
        var sortType = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.Type") };
        var sortDate = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.DateModified") };

        var sortItems = new[] { sortName, sortSize, sortType, sortDate };
        var sortModes = new[] { WidgetSortMode.Name, WidgetSortMode.Size, WidgetSortMode.Type, WidgetSortMode.DateModified };
        var currentSortIndex = Array.IndexOf(sortModes, ViewModel.Config.SortMode);

        for (int i = 0; i < sortItems.Length; i++)
        {
            var item = sortItems[i];
            var mode = sortModes[i];
            item.IsChecked = i == currentSortIndex;
            item.Click += (_, _) => ViewModel.SetSortMode(mode);
            sortSubItem.Items.Add(item);
        }

        flyout.Items.Add(sortSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var refreshItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Refresh"),
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        refreshItem.Click += async (_, _) =>
        {
            try
            {
                await ViewModel.RefreshFromConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(_localizationService.T("Widget.RefreshFailed"), ex.Message);
            }
        };
        flyout.Items.Add(refreshItem);

        return flyout;
    }

    private void AddCurrentWidgetContentActions(MenuFlyout flyout)
    {
        if (!ViewModel.FollowsDefaultStoragePath)
        {
            return;
        }

        var addFileItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.AddFile"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        addFileItem.Click += async (_, _) => await PickAndImportFilesAsync();
        flyout.Items.Add(addFileItem);
    }

    // ── More flyout ────────────────────────────────────────────

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();

        AddCreateWidgetItems(flyout);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var positionLock = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.LockPosition"),
            Icon = new FontIcon { Glyph = "\uE72E" },
            IsChecked = ViewModel.IsPositionLocked
        };
        positionLock.Click += TogglePositionLock_Click;
        flyout.Items.Add(positionLock);

        var sizeLock = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.LockSize"),
            Icon = new FontIcon { Glyph = "\uE9CE" },
            IsChecked = ViewModel.IsSizeLocked
        };
        sizeLock.Click += ToggleSizeLock_Click;
        flyout.Items.Add(sizeLock);

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(WidgetChromeMenuBuilder.Create(
            ViewModel.Config,
            _chromeDescriptor,
            _localizationService,
            SetChromeModeOverride));

        flyout.Items.Add(new MenuFlyoutSeparator());

        if (!ViewModel.FollowsDefaultStoragePath &&
            !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            var openFolder = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.OpenCurrentFolder"),
                Icon = new FontIcon { Glyph = "\uE838" }
            };
            openFolder.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                {
                    Win32Helper.OpenFile(ViewModel.MappedFolderPath);
                }
            };
            flyout.Items.Add(openFolder);
        }

        if (!ViewModel.FollowsDefaultStoragePath)
        {
            var changeMappedPathItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.ChangeMappedPath"),
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            changeMappedPathItem.Click += async (_, _) => await PickAndApplyMappedFolderAsync();
            flyout.Items.Add(changeMappedPathItem);
        }

        var rename = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        bool startRenameWhenClosed = false;
        rename.Click += (_, _) => startRenameWhenClosed = true;
        flyout.Closed += (_, _) =>
        {
            if (startRenameWhenClosed)
            {
                DispatcherQueue.TryEnqueue(StartRename);
            }
        };
        flyout.Items.Add(rename);

        var settingsItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Settings.Appearance.DetailTitle"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += (_, _) => App.Current.ShowSettings("AppearanceDetail");
        flyout.Items.Add(settingsItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteWidget = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Tooltip.DeleteWidget"),
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteWidget.Click += DeleteWidget_Click;
        flyout.Items.Add(deleteWidget);

        return flyout;
    }

    private void AddCreateWidgetItems(MenuFlyout flyout)
    {
        foreach (var descriptor in new WidgetContentFactory(_localizationService).GetCreateEntryDescriptors())
        {
            var createItem = new MenuFlyoutItem
            {
                Text = GetCreateEntryText(descriptor),
                Icon = new FontIcon { Glyph = descriptor.DefaultGlyph }
            };
            createItem.Click += async (_, _) =>
            {
                if (App.Current.WidgetManager is { } widgetManager)
                {
                    await widgetManager.CreateWidgetOfKindAsync(descriptor.WidgetKind);
                }
            };
            flyout.Items.Add(createItem);
        }

        var mapFolder = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.NewFolderMapping"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        mapFolder.Click += MapFolderButton_Click;
        flyout.Items.Add(mapFolder);
    }

    private string GetCreateEntryText(WidgetContentDescriptor descriptor)
    {
        return string.IsNullOrWhiteSpace(descriptor.CreateEntryTextKey)
            ? descriptor.DefaultTitle
            : _localizationService.T(descriptor.CreateEntryTextKey);
    }

    private MenuFlyout CreateNewWidgetFlyout()
    {
        var flyout = new MenuFlyout();
        AddCreateWidgetItems(flyout);
        return flyout;
    }

    // ── Delete confirmation flyouts ────────────────────────────

    private void ShowDeleteItemsConfirmFlyout(IReadOnlyList<WidgetItem> selectedItems)
    {
        var items = selectedItems.ToArray();
        int folderCount = items.Count(item => item.IsFolder);
        if (folderCount == 0)
        {
            _ = DeleteItemsWithoutConfirmAsync(items);
            return;
        }

        _itemDeleteConfirmFlyout?.Hide();

        string message = items.Length == 1
            ? _localizationService.T("Widget.DeleteFolderNoteOne")
            : _localizationService.Format("Widget.DeleteFolderNoteMany", folderCount);
        var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            new WidgetCompactConfirmationOptions(
                items.Length == 1
                    ? _localizationService.Format("Widget.DeleteItemsTitleOne", items[0].Name)
                    : _localizationService.Format("Widget.DeleteItemsTitleMany", items.Length),
                _localizationService.T("Widget.MoveToRecycleBin"),
                async () => await DeleteItemsWithoutConfirmAsync(items))
            {
                TitleGlyph = "\uE946",
                Message = message,
                CancelText = _localizationService.T("Common.Cancel")
            });

        _itemDeleteConfirmFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_itemDeleteConfirmFlyout, flyout))
            {
                return;
            }

            _itemDeleteConfirmFlyout = null;
            _isInlineFlyoutOpen = false;
            ReleaseInteractionLayer("file-delete-confirm-closed");
        };

        _isInlineFlyoutOpen = true;
        BeginInteractionLayer("file-delete-confirm-opened");

        var target = items.Length == 1
            ? FindItemSurface(items[0]) ?? GetActiveItemsView() as FrameworkElement ?? RootGrid
            : GetActiveItemsView() as FrameworkElement ?? RootGrid;
        flyout.ShowAt(target);
    }

    private void TrackMoreFlyoutAnchor(FrameworkElement target, Windows.Foundation.Point? position)
    {
        _lastMoreFlyoutTarget = target;
        _lastMoreFlyoutPosition = position;
    }

    private void ShowDeleteWidgetFlyout(FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        if (_deletePending || App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        _deleteWidgetFlyout?.Hide();
        var flyout = CreateDeleteWidgetFlyout(app.WidgetManager);
        _deleteWidgetFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_deleteWidgetFlyout, flyout))
            {
                return;
            }

            _isDeleteWidgetFlyoutOpen = false;
            _deleteWidgetFlyout = null;
            ReleaseInteractionLayer("file-delete-widget-closed");
        };

        _isDeleteWidgetFlyoutOpen = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            BeginInteractionLayer("file-delete-widget-opened");
            if (position is Windows.Foundation.Point point)
            {
                flyout.ShowAt(target, point);
            }
            else
            {
                flyout.ShowAt(target);
            }
        });
    }

    private MenuFlyout CreateDeleteWidgetFlyout(WidgetManager widgetManager)
    {
        var flyout = new MenuFlyout();
        flyout.ShouldConstrainToRootBounds = false;

        var titleItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("Widget.DeleteWidgetTitle", ViewModel.Name),
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = false
        };
        flyout.Items.Add(titleItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        bool canCleanupManagedFolder = widgetManager.CanCleanupManagedStorageForWidget(ViewModel.Config.Id);
        if (!canCleanupManagedFolder)
        {
            var noteItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.DeleteWidgetNote"),
                Icon = new FontIcon { Glyph = "\uE946" },
                IsEnabled = false
            };
            flyout.Items.Add(noteItem);

            var confirmItem = CreateDeleteActionItem(
                _localizationService.T("Widget.DeleteWidgetConfirm"),
                WidgetRemovalAction.RemoveWidgetOnly);
            flyout.Items.Add(confirmItem);
            flyout.Items.Add(CreateCancelDeleteItem());
            return flyout;
        }

        var managedInfoItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.DeleteManagedInfo"),
            Icon = new FontIcon { Glyph = "\uE8B7" },
            IsEnabled = false
        };
        flyout.Items.Add(managedInfoItem);

        flyout.Items.Add(CreateDeleteActionItem(
            _localizationService.T("Widget.KeepManagedFolder"),
            WidgetRemovalAction.RemoveWidgetOnly,
            "\uE8B7",
            false));
        flyout.Items.Add(CreateDeleteActionItem(
            _localizationService.T("Widget.MoveBackThenDeleteFolder"),
            WidgetRemovalAction.MoveManagedFolderContentsToDesktop,
            "\uE8CA",
            false));
        flyout.Items.Add(CreateDeleteActionItem(
            _localizationService.T("Widget.DeleteFolderToRecycleBin"),
            WidgetRemovalAction.DeleteManagedFolder,
            "\uE74D",
            true));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateCancelDeleteItem());
        return flyout;
    }

    private MenuFlyoutItem CreateDeleteActionItem(
        string text,
        WidgetRemovalAction removalAction,
        string glyph = "\uE74D",
        bool isDanger = true)
    {
        var icon = new FontIcon { Glyph = glyph };
        if (isDanger)
        {
            icon.Foreground = new SolidColorBrush(Colors.Red);
        }

        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = icon
        };
        item.Click += (_, _) => QueueDeleteWidget(removalAction);
        return item;
    }

    private MenuFlyoutItem CreateCancelDeleteItem()
    {
        var item = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cancel"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        item.Click += (_, _) => _deleteWidgetFlyout?.Hide();
        return item;
    }
}
