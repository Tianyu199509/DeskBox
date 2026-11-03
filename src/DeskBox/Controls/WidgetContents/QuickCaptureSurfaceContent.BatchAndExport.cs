using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureSurfaceContent
{
    private void QuickCaptureItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!Win32Helper.IsKeyPressed(VirtualKey.Menu))
        {
            return;
        }

        if (sender is Border { DataContext: QuickCaptureItemViewModel item })
        {
            e.Handled = true;
            App.Current?.OpenQuickCapturePopoutWindow(item.Id, startAlwaysOnTop: true);
        }
    }

    private async void DetailPopoutButton_Click(object sender, RoutedEventArgs e)
    {
        await FlushPendingDetailSaveAsync();
        if (_detailItem is { } item)
        {
            App.Current?.OpenQuickCapturePopoutWindow(
                item.Id,
                startAlwaysOnTop: Win32Helper.IsKeyPressed(VirtualKey.Shift));
        }
    }

    private async void DetailMoreButton_Click(object sender, RoutedEventArgs e)
    {
        await FlushPendingDetailSaveAsync();
        if (_detailItem is not { } item)
        {
            MenuFlyout globalFlyout = CreateMirrorOnlyContextFlyout();
            globalFlyout.ShowAt(DetailMoreButton);
            return;
        }

        MenuFlyout flyout = CreateRichItemOrBatchContextFlyout([item], primaryItem: item, includeBasicActions: false);
        flyout.ShowAt(DetailMoreButton);
    }

    private MenuFlyout CreateMirrorOnlyContextFlyout()
    {
        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = false };
        flyout.Items.Add(CreateMirrorFolderSubmenu(flyout));
        return flyout;
    }

    private IReadOnlyList<QuickCaptureItemViewModel> ResolveRightTappedTargetItems(
        QuickCaptureItemViewModel clickedItem)
    {
        IReadOnlyList<QuickCaptureItemViewModel> selected = GetSelectedQuickCaptureItemsInVisibleOrder();
        if (selected.Count > 1 &&
            selected.Any(candidate => string.Equals(candidate.Id, clickedItem.Id, StringComparison.Ordinal)))
        {
            return selected;
        }

        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedItem = clickedItem;
        return [clickedItem];
    }

    private MenuFlyout CreateRichItemOrBatchContextFlyout(
        IReadOnlyList<QuickCaptureItemViewModel> targetItems,
        QuickCaptureItemViewModel primaryItem,
        bool includeBasicActions = true)
    {
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        bool isBatch = targetItems.Count > 1;

        if (includeBasicActions)
        {
            if (!isBatch)
            {
                var editItem = CreateContextMenuItem("QuickCapture.Edit", "\uE70F");
                editItem.Click += async (_, _) =>
                {
                    flyout.Hide();
                    await OpenDetailAfterSavingAsync(primaryItem);
                    if (_detailItem is not null)
                    {
                        BeginDetailEditing();
                    }
                };
                flyout.Items.Add(editItem);
            }

            if (!isBatch)
            {
                var popoutItem = CreateContextMenuItem("QuickCapture.Popout", "\uE8A7");
                popoutItem.Click += (_, _) =>
                {
                    flyout.Hide();
                    App.Current?.OpenQuickCapturePopoutWindow(primaryItem.Id, startAlwaysOnTop: false);
                };
                flyout.Items.Add(popoutItem);

                var stickyTopItem = CreateContextMenuItem("QuickCapture.PinOnTop", "\uE77A");
                stickyTopItem.Click += (_, _) =>
                {
                    flyout.Hide();
                    App.Current?.OpenQuickCapturePopoutWindow(primaryItem.Id, startAlwaysOnTop: true);
                };
                flyout.Items.Add(stickyTopItem);
            }

            bool allPinned = targetItems.All(entry => entry.IsPinned);
            var pinItem = new MenuFlyoutItem
            {
                Text = T(allPinned ? "QuickCapture.Unpin" : "QuickCapture.Pin"),
                Icon = new FontIcon { Glyph = "\uE718" }
            };
            pinItem.Click += async (_, _) =>
            {
                flyout.Hide();
                if (!isBatch)
                {
                    await ToggleItemPinnedWithFeedbackAsync(primaryItem);
                }
                else
                {
                    int changed = await ViewModel.SetPinnedAsync(
                        targetItems.Select(entry => entry.Id),
                        !allPinned);
                    if (changed > 0)
                    {
                        RaiseFeedback(
                            T(!allPinned ? "QuickCapture.PinnedSuccess" : "QuickCapture.UnpinnedSuccess"),
                            WidgetFeedbackSeverity.Success,
                            "quick-batch-pin");
                    }
                }
            };
            flyout.Items.Add(pinItem);

            var copyItem = new MenuFlyoutItem
            {
                Text = isBatch
                    ? _localizationService.Format("QuickCapture.CopySelected", targetItems.Count)
                    : T("Common.Copy"),
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyItem.Click += async (_, _) =>
            {
                flyout.Hide();
                if (isBatch)
                {
                    await CopySelectedQuickCaptureItemsAsync();
                }
                else
                {
                    await CopyItemWithFeedbackAsync(primaryItem);
                }
            };
            flyout.Items.Add(copyItem);

            flyout.Items.Add(isBatch
                ? CreateBatchAppearanceContextSubmenu(targetItems, flyout)
                : CreateAppearanceContextSubmenu(primaryItem, flyout));
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        else
        {
            var stickyTopItem = CreateContextMenuItem("QuickCapture.PinOnTop", "\uE77A");
            stickyTopItem.Click += (_, _) =>
            {
                flyout.Hide();
                App.Current?.OpenQuickCapturePopoutWindow(primaryItem.Id, startAlwaysOnTop: true);
            };
            flyout.Items.Add(stickyTopItem);
        }

        // Convert to Todo
        var convertTodoItem = CreateContextMenuItem("QuickCapture.ConvertToTodo", "\uE73A");
        convertTodoItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ConvertItemsToTodoWithFeedbackAsync(targetItems);
        };
        flyout.Items.Add(convertTodoItem);

        // Save to File Widget
        flyout.Items.Add(CreateSaveToFileWidgetSubmenu(targetItems, flyout));

        // Export .md
        var exportMdItem = CreateContextMenuItem("QuickCapture.ExportMarkdown", "\uEDE1");
        exportMdItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ExportItemsAsMarkdownWithPickerAsync(targetItems);
        };
        flyout.Items.Add(exportMdItem);

        // Merge notes (2+ selected)
        if (targetItems.Count >= 2)
        {
            var mergeItem = CreateContextMenuItem("QuickCapture.MergeNotes", "\uEA3C");
            mergeItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await MergeSelectedNotesWithFeedbackAsync(targetItems);
            };
            flyout.Items.Add(mergeItem);
        }

        // Archive / Unarchive
        bool allArchived = targetItems.All(entry => entry.IsArchived);
        var archiveItem = CreateContextMenuItem(
            allArchived ? "QuickCapture.Unarchive" : "QuickCapture.Archive",
            allArchived ? "\uE777" : "\uE7B8");
        archiveItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await SetItemsArchivedWithFeedbackAsync(targetItems, !allArchived);
        };
        flyout.Items.Add(archiveItem);

        flyout.Items.Add(CreateMirrorFolderSubmenu(flyout));

        if (includeBasicActions)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var deleteItem = CreateContextMenuItem("Common.Delete", "\uE74D");
            deleteItem.Click += async (_, _) =>
            {
                flyout.Hide();
                if (isBatch)
                {
                    await DeleteSelectedQuickCaptureItemsAsync(targetItems);
                }
                else
                {
                    await DeleteQuickCaptureItemAsync(primaryItem);
                }
            };
            flyout.Items.Add(deleteItem);
        }

        return flyout;
    }

    private MenuFlyoutSubItem CreateBatchAppearanceContextSubmenu(
        IReadOnlyList<QuickCaptureItemViewModel> items,
        MenuFlyout owner)
    {
        var submenu = new MenuFlyoutSubItem
        {
            Text = T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };

        foreach ((QuickCaptureAppearancePreset preset, string textKey) in new[]
                 {
                     (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
                     (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
                     (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
                     (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
                     (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
                     (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
                 })
        {
            var appearanceItem = new ToggleMenuFlyoutItem
            {
                Text = T(textKey),
                IsChecked = items.All(candidate => candidate.AppearancePreset == preset)
            };
            appearanceItem.Click += async (_, _) =>
            {
                owner.Hide();
                int updated = await ViewModel.SetAppearanceAsync(
                    items.Select(candidate => candidate.Id),
                    preset);
                if (updated > 0)
                {
                    await ViewModel.RefreshItemsAsync();
                }
            };
            submenu.Items.Add(appearanceItem);
        }

        return submenu;
    }

    private MenuFlyoutItemBase CreateSaveToFileWidgetSubmenu(
        IReadOnlyList<QuickCaptureItemViewModel> targetItems,
        MenuFlyout owner)
    {
        WidgetManager? wm = App.Current?.WidgetManager;
        IReadOnlyList<QuickCaptureFileWidgetTarget> targets =
            wm?.GetQuickCaptureFileWidgetTargets() ?? [];
        if (targets.Count == 0)
        {
            return new MenuFlyoutItem
            {
                Text = T("QuickCapture.NoFileWidgetTargets"),
                Icon = new FontIcon { Glyph = "\uE8B7" },
                IsEnabled = false
            };
        }

        var submenu = new MenuFlyoutSubItem
        {
            Text = T("QuickCapture.SaveToFileWidget"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };

        QuickCaptureFileWidgetTarget? lastTarget = wm?.GetLastQuickCaptureFileWidgetTarget();
        if (lastTarget is not null)
        {
            var quickLast = new MenuFlyoutItem
            {
                Text = _localizationService.Format("QuickCapture.SaveToLastFileWidget", lastTarget.Name),
                Icon = new FontIcon { Glyph = "\uE8F1" }
            };
            string lastId = lastTarget.WidgetId;
            quickLast.Click += async (_, _) =>
            {
                owner.Hide();
                await SaveItemsToFileWidgetWithFeedbackAsync(targetItems, lastId);
            };
            submenu.Items.Add(quickLast);
            submenu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (QuickCaptureFileWidgetTarget target in targets)
        {
            var targetMenuItem = new MenuFlyoutItem
            {
                Text = target.Name,
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            string targetId = target.WidgetId;
            targetMenuItem.Click += async (_, _) =>
            {
                owner.Hide();
                await SaveItemsToFileWidgetWithFeedbackAsync(targetItems, targetId);
            };
            submenu.Items.Add(targetMenuItem);
        }

        return submenu;
    }

    private MenuFlyoutSubItem CreateMirrorFolderSubmenu(MenuFlyout owner)
    {
        var submenu = new MenuFlyoutSubItem
        {
            Text = T("QuickCapture.MirrorFolderMenu"),
            Icon = new FontIcon { Glyph = "\uE8F4" }
        };

        string? activePath = ViewModel.MirrorFolderPath;
        if (!string.IsNullOrWhiteSpace(activePath))
        {
            var currentInfo = new MenuFlyoutItem
            {
                Text = _localizationService.Format(
                    "QuickCapture.MirrorFolderActiveFormat",
                    Path.GetFileName(activePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                IsEnabled = false
            };
            submenu.Items.Add(currentInfo);

            var openFolderItem = CreateContextMenuItem("QuickCapture.OpenMirrorFolder", "\uE8DA");
            openFolderItem.Click += async (_, _) =>
            {
                owner.Hide();
                if (Directory.Exists(activePath))
                {
                    try
                    {
                        await Windows.System.Launcher.LaunchFolderAsync(
                            await Windows.Storage.StorageFolder.GetFolderFromPathAsync(activePath));
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[QuickCaptureSurface] Open mirror folder failed: {ex.Message}");
                    }
                }
            };
            submenu.Items.Add(openFolderItem);
            submenu.Items.Add(new MenuFlyoutSeparator());
        }

        var chooseFolderItem = CreateContextMenuItem("QuickCapture.SetMirrorFolder", "\uE8B7");
        chooseFolderItem.Click += async (_, _) =>
        {
            owner.Hide();
            await PickAndSetMirrorFolderAsync();
        };
        submenu.Items.Add(chooseFolderItem);

        if (!string.IsNullOrWhiteSpace(activePath))
        {
            var clearFolderItem = CreateContextMenuItem("QuickCapture.ClearMirrorFolder", "\uE711");
            clearFolderItem.Click += async (_, _) =>
            {
                owner.Hide();
                await ViewModel.SetMirrorFolderPathAsync(null);
                RaiseFeedback(
                    T("QuickCapture.MirrorFolderCleared"),
                    WidgetFeedbackSeverity.Success,
                    "quick-mirror-cleared");
            };
            submenu.Items.Add(clearFolderItem);
        }

        return submenu;
    }

    private async Task ConvertItemsToTodoWithFeedbackAsync(
        IReadOnlyList<QuickCaptureItemViewModel> targetItems)
    {
        if (App.Current?.WidgetManager is not { } wm)
        {
            return;
        }

        try
        {
            QuickCaptureItem[] models = targetItems.Select(entry => entry.ToModel()).ToArray();
            int count = await wm.ConvertQuickCaptureItemsToTodoAsync(models);
            if (count > 0)
            {
                RaiseFeedback(
                    _localizationService.Format("QuickCapture.ConvertedToTodoCount", count),
                    WidgetFeedbackSeverity.Success,
                    "quick-converted-todo");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Convert to Todo failed: {ex.Message}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-convert-todo-error");
        }
    }

    private async Task SaveItemsToFileWidgetWithFeedbackAsync(
        IReadOnlyList<QuickCaptureItemViewModel> targetItems,
        string targetWidgetId)
    {
        if (App.Current?.WidgetManager is not { } wm)
        {
            return;
        }

        int saved = 0;
        try
        {
            string imgPrefix = T("QuickCapture.ImageExportFileNamePrefix");
            foreach (QuickCaptureItemViewModel entry in targetItems)
            {
                string? dest = await wm.SaveQuickCaptureItemToFileWidgetAsync(
                    entry.ToModel(),
                    targetWidgetId,
                    imgPrefix);
                if (!string.IsNullOrWhiteSpace(dest))
                {
                    saved++;
                }
            }

            RaiseFeedback(
                T(saved > 0 ? "QuickCapture.SavedToFileWidget" : "QuickCapture.SaveToFileWidgetFailed"),
                saved > 0 ? WidgetFeedbackSeverity.Success : WidgetFeedbackSeverity.Error,
                "quick-save-filewidget");
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Save to File Widget failed: {ex.Message}");
            RaiseFeedback(
                T("QuickCapture.SaveToFileWidgetFailed"),
                WidgetFeedbackSeverity.Error,
                "quick-save-filewidget-error");
        }
    }

    private async Task ExportItemsAsMarkdownWithPickerAsync(
        IReadOnlyList<QuickCaptureItemViewModel> targetItems)
    {
        try
        {
            if (NativeFileOpenDialog.TryActivateActiveFolderDialog())
            {
                return;
            }

            IntPtr foreground = Win32Helper.GetForegroundWindow();
            string? folder = await NativeFileOpenDialog.RunOnDedicatedStaAsync(
                foreground,
                NativeFileOpenDialog.ShowPickFolder);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            QuickCaptureItem[] models = targetItems.Select(entry => entry.ToModel()).ToArray();
            IReadOnlyList<string> written = await QuickCaptureMarkdownMirrorService.ExportItemsToDirectoryAsync(
                models,
                folder,
                T("QuickCapture.Name"));
            if (written.Count > 0)
            {
                RaiseFeedback(
                    _localizationService.Format("QuickCapture.ExportedMarkdownCount", written.Count),
                    WidgetFeedbackSeverity.Success,
                    "quick-export-md");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Export Markdown failed: {ex.Message}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-export-md-error");
        }
    }

    private async Task PickAndSetMirrorFolderAsync()
    {
        try
        {
            if (NativeFileOpenDialog.TryActivateActiveFolderDialog())
            {
                return;
            }

            IntPtr foreground = Win32Helper.GetForegroundWindow();
            string? folder = await NativeFileOpenDialog.RunOnDedicatedStaAsync(
                foreground,
                NativeFileOpenDialog.ShowPickFolder);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            await ViewModel.SetMirrorFolderPathAsync(folder);
            if (App.Current?.QuickCaptureMirrorService is { } mirror)
            {
                await mirror.SyncNowAsync();
            }

            RaiseFeedback(
                _localizationService.Format(
                    "QuickCapture.MirrorFolderActiveFormat",
                    Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                WidgetFeedbackSeverity.Success,
                "quick-mirror-set");
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureSurface] Set mirror folder failed: {ex.Message}");
            RaiseFeedback(
                T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-mirror-error");
        }
    }

    private async Task MergeSelectedNotesWithFeedbackAsync(
        IReadOnlyList<QuickCaptureItemViewModel> targetItems)
    {
        if (targetItems.Count < 2)
        {
            return;
        }

        string[] ids = targetItems.Select(entry => entry.Id).ToArray();
        QuickCaptureMergeResult mergeResult = await ViewModel.MergeItemsWithSnapshotsAsync(ids);
        QuickCaptureItem? merged = mergeResult.MergedItem;
        if (merged is null)
        {
            return;
        }

        ItemsList.SelectedItems.Clear();
        await ViewModel.RefreshItemsAsync();
        QuickCaptureItemViewModel? mergedVm = ViewModel.Items.FirstOrDefault(
            entry => string.Equals(entry.Id, merged.Id, StringComparison.Ordinal));
        if (mergedVm is not null)
        {
            ItemsList.SelectedItem = mergedVm;
            await OpenDetailAfterSavingAsync(mergedVm);
        }

        string mergedId = merged.Id;
        IReadOnlyList<QuickCaptureDeletedItemSnapshot> sourceSnapshots = mergeResult.SourceSnapshots;
        RaiseFeedback(
            T("QuickCapture.MergedNotesSuccess"),
            WidgetFeedbackSeverity.Success,
            "quick-merge-success",
            sourceSnapshots.Count > 0 ? T("Common.Undo") : null,
            sourceSnapshots.Count > 0
                ? async () =>
                {
                    if (await ViewModel.RestoreMergedItemsAsync(mergedId, sourceSnapshots))
                    {
                        if (_detailItem?.Id == mergedId)
                        {
                            _detailItem = null;
                            _isDetailEditing = false;
                            _detailHasUnsavedChanges = false;
                            _showDetailInSinglePane = false;
                        }

                        await ViewModel.RefreshItemsAsync();
                        ApplyResponsiveLayout();
                        ReconcileDetailSelection();
                    }
                }
                : null);
        if (sourceSnapshots.Count > 0)
        {
            ScheduleDeletedSnapshotsAttachmentRelease(sourceSnapshots);
        }
    }

    private async Task SetItemsArchivedWithFeedbackAsync(
        IReadOnlyList<QuickCaptureItemViewModel> targetItems,
        bool isArchived)
    {
        string[] ids = targetItems.Select(entry => entry.Id).ToArray();
        int changed = await ViewModel.SetArchivedAsync(ids, isArchived);
        if (changed <= 0)
        {
            return;
        }

        if (isArchived &&
            _detailItem is not null &&
            ids.Contains(_detailItem.Id, StringComparer.Ordinal) &&
            !ViewModel.ShowArchived)
        {
            _detailItem = null;
            _isDetailEditing = false;
            _detailHasUnsavedChanges = false;
            _showDetailInSinglePane = false;
        }

        ItemsList.SelectedItems.Clear();
        await ViewModel.RefreshItemsAsync();
        ApplyResponsiveLayout();
        ReconcileDetailSelection();

        RaiseFeedback(
            T(isArchived ? "QuickCapture.ArchivedSuccess" : "QuickCapture.UnarchivedSuccess"),
            WidgetFeedbackSeverity.Success,
            isArchived ? "quick-archived" : "quick-unarchived");
    }
}
