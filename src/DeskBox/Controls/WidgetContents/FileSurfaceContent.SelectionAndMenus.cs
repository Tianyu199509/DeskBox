using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Controls;
using DeskBox.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private sealed record SelectionHit(
        WidgetItem Item,
        Border? Surface,
        Windows.Foundation.Rect Bounds);

    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private bool _isSynchronizingSelection;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<WidgetItem> _selectionSnapshot = [];
    private List<SelectionHit> _selectionHits = [];
    private (int X, int Y)? _lastItemContextScreenPoint;

    private void Items_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            HandleItemsPointerPressed(listView, listView, e);
        }
    }

    private void HandleItemsPointerPressed(
        ListViewBase listView,
        UIElement pointerSurface,
        PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(pointerSurface).Properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartBoxSelection(e.OriginalSource))
        {
            return;
        }

        if (ReferenceEquals(listView, _stackPopoverItemsView))
        {
            listView.Focus(FocusState.Programmatic);
        }
        else
        {
            Root.Focus(FocusState.Programmatic);
        }
        ClearOtherWidgetSelections();
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        Canvas selectionOverlay = ResolveSelectionOverlay(listView);
        _selectionStartPoint =
            e.GetCurrentPoint(selectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot =
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
                ? GetSelectedItems().ToList()
                : [];
        _selectionHits = [];

        if (!Win32Helper.IsKeyPressed(
                Windows.System.VirtualKey.Control))
        {
            listView.SelectedItems.Clear();
            UpdateSelectionCommandBar();
        }
    }

    private void Items_PointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            HandleItemsPointerMoved(listView, listView, e);
        }
    }

    private void HandleItemsPointerMoved(
        ListViewBase listView,
        UIElement pointerSurface,
        PointerRoutedEventArgs e)
    {
        if (!_selectionPointerPressed)
        {
            return;
        }

        Canvas selectionOverlay = ResolveSelectionOverlay(listView);
        _selectionCurrentPoint =
            e.GetCurrentPoint(selectionOverlay).Position;
        if (!_isBoxSelecting &&
            FileItemSelectionGeometry.GetDragDistance(
                _selectionStartPoint,
                _selectionCurrentPoint) < 6)
        {
            return;
        }

        if (!_isBoxSelecting)
        {
            _isBoxSelecting = true;
            pointerSurface.CapturePointer(e.Pointer);
            CacheSelectionHits(listView, selectionOverlay);
        }

        UpdateSelectionRectangle(listView);
        ApplySelectionPreview(listView);
        e.Handled = true;
    }

    private void Items_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            HandleItemsPointerReleased(listView, listView, e);
        }
    }

    private void HandleItemsPointerReleased(
        ListViewBase listView,
        UIElement pointerSurface,
        PointerRoutedEventArgs e)
    {
        bool wasBoxSelecting = _isBoxSelecting;
        FinishBoxSelection(listView);
        _pendingPointerDragItems = [];
        if (wasBoxSelecting)
        {
            pointerSurface.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void Items_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishBoxSelection(listView);
        }
    }

    private void HandleItemsPointerCaptureLost(ListViewBase listView) =>
        FinishBoxSelection(listView);

    private bool CanStartBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return !FileItemSelectionGeometry.IsWithinItemSurface(source) &&
               !FileItemSelectionGeometry.HasAncestor<ScrollBar>(source) &&
               !FileItemSelectionGeometry.HasAncestor<ButtonBase>(source) &&
               !FileItemSelectionGeometry.HasAncestor<TextBox>(source);
    }

    private void CacheSelectionHits(
        ListViewBase listView,
        Canvas selectionOverlay)
    {
        _selectionHits = [];
        foreach (WidgetItem item in listView.Items
                     .OfType<WidgetItem>()
                     .Where(item => item is not WidgetStackItem))
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            FrameworkElement target =
                FindDescendantByTag(container, "InteractiveSurface") ??
                container;
            Windows.Foundation.Point topLeft =
                target.TransformToVisual(selectionOverlay)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
            _selectionHits.Add(new SelectionHit(
                item,
                target as Border,
                new Windows.Foundation.Rect(
                    topLeft.X,
                    topLeft.Y,
                    target.ActualWidth,
                    target.ActualHeight)));
        }
    }

    private void ApplySelectionPreview(ListViewBase listView)
    {
        Windows.Foundation.Rect selectionRect =
            FileItemSelectionGeometry.GetSelectionRect(
                _selectionStartPoint,
                _selectionCurrentPoint);
        var selected = new HashSet<WidgetItem>(_selectionSnapshot);
        foreach (SelectionHit hit in _selectionHits)
        {
            if (FileItemSelectionGeometry.Intersects(selectionRect, hit.Bounds))
            {
                selected.Add(hit.Item);
            }
        }

        _isSynchronizingSelection = true;
        try
        {
            listView.SelectedItems.Clear();
            foreach (WidgetItem item in listView.Items
                         .OfType<WidgetItem>()
                         .Where(selected.Contains))
            {
                listView.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();
    }

    private void UpdateSelectionRectangle(ListViewBase listView)
    {
        Windows.Foundation.Rect rect =
            FileItemSelectionGeometry.GetSelectionRect(
                _selectionStartPoint,
                _selectionCurrentPoint);
        Border selectionRectangle = ResolveSelectionRectangle(listView);
        Canvas.SetLeft(selectionRectangle, rect.X);
        Canvas.SetTop(selectionRectangle, rect.Y);
        selectionRectangle.Width = rect.Width;
        selectionRectangle.Height = rect.Height;
        selectionRectangle.Visibility =
            rect.Width > 0 && rect.Height > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void FinishBoxSelection(ListViewBase listView)
    {
        if (!_selectionPointerPressed && !_isBoxSelecting)
        {
            return;
        }

        if (_isBoxSelecting)
        {
            ApplySelectionPreview(listView);
        }

        ResetBoxSelectionState();
        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();
    }

    private Canvas ResolveSelectionOverlay(ListViewBase listView) =>
        ReferenceEquals(listView, _stackPopoverItemsView) &&
        _stackPopoverSelectionOverlay is { } popoverOverlay
            ? popoverOverlay
            : SelectionOverlay;

    private Border ResolveSelectionRectangle(ListViewBase listView) =>
        ReferenceEquals(listView, _stackPopoverItemsView) &&
        _stackPopoverSelectionRectangle is { } popoverRectangle
            ? popoverRectangle
            : SelectionRectangle;

    private void ResetBoxSelectionState()
    {
        _selectionPointerPressed = false;
        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionHits = [];
        HideSelectionRectangle(SelectionRectangle);
        if (_stackPopoverSelectionRectangle is { } popoverRectangle)
        {
            HideSelectionRectangle(popoverRectangle);
        }
    }

    private static void HideSelectionRectangle(Border rectangle)
    {
        rectangle.Visibility = Visibility.Collapsed;
        rectangle.Width = 0;
        rectangle.Height = 0;
    }

    public void ClearItemSelection()
    {
        ItemsGrid.SelectedItems.Clear();
        ItemsList.SelectedItems.Clear();
        _stackPopoverItemsView?.SelectedItems.Clear();
        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();
    }

    private void ClearSelection() => ClearItemSelection();

    private FrameworkElement? FindItemSurface(WidgetItem item)
    {
        WidgetItem? displayedItem = FindDisplayedItem(item);
        if (displayedItem is null ||
            GetActiveItemsView().ContainerFromItem(displayedItem)
                is not SelectorItem)
        {
            return null;
        }

        return _itemSurfaces.FirstOrDefault(surface =>
            ReferenceEquals(surface.DataContext, displayedItem));
    }

    private WidgetItem? FindDisplayedItem(WidgetItem item)
    {
        ListViewBase activeView = GetActiveItemsView();
        WidgetItem? referenceMatch = activeView.Items
            .OfType<WidgetItem>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate, item));
        if (referenceMatch is not null)
        {
            return referenceMatch;
        }

        return activeView.Items
            .OfType<WidgetItem>()
            .FirstOrDefault(candidate =>
                candidate is not WidgetStackItem &&
                string.Equals(
                    candidate.Path,
                    item.Path,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static WidgetItem? FindItemFromSource(object? source)
    {
        return FindItemElement(source)?.DataContext as WidgetItem;
    }

    private static FrameworkElement? FindItemElement(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement
                {
                    DataContext: WidgetItem
                } element)
            {
                return element;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private MenuFlyout CreateItemFlyout(WidgetItem item)
    {
        return FileItemMenuBuilder.CreateItemFlyout(
            item,
            CreateFileItemMenuActions());
    }

    private MenuFlyout CreateMultiSelectionFlyout()
    {
        return FileItemMenuBuilder.CreateMultiSelectionFlyout(
            CreateFileItemMenuActions());
    }

    private FileItemMenuActions CreateFileItemMenuActions()
    {
        return new FileItemMenuActions(
            CreateMenuItem,
            ActivateItemAsync,
            cut => RunAsync(
                () => CopySelectionToClipboardAsync(cut)),
            RenameItemAsync,
            CopySelectedPathsToClipboard,
            item =>
            {
                if (!TryBlockTransferOpen(item))
                {
                    ViewModel.ShowInExplorer(item);
                }
            },
            ShowFileProperties,
            CanMoveItemsBackToDesktop,
            _ => MoveSelectedItemsBackToDesktopAsync(),
            items => DeleteItemsAsync(items),
            GetSelectedItems,
            CanCreateManualStack: true,
            items =>
            {
                if (TryBlockTransferMutation(items))
                {
                    return;
                }

                ApplyStackProjectionChange(() =>
                    _ = ViewModel.CreateManualStack(items));
            },
            ViewModel.CanRemoveItemFromStack,
            item =>
            {
                if (TryBlockTransferMutation(item))
                {
                    return;
                }

                ApplyStackProjectionChange(() =>
                    _ = ViewModel.RemoveItemFromStack(item));
            },
            ClearSelection,
            ShowSystemContextMenu,
            ElevatedFileLauncher.CanRunAsAdministrator,
            RunAsAdministratorAsync);
    }

    private void ShowFileProperties(WidgetItem item)
    {
        if (TryBlockTransferOpen(item))
        {
            return;
        }

        if (!ShellContextMenuHelper.ShowProperties(
                _hostWindowHandle,
                item.Path))
        {
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.Error.OperationIncomplete"),
                WidgetFeedbackSeverity.Warning,
                "file-properties"));
        }
    }

    private void ShowSystemContextMenu(WidgetItem item)
    {
        if (TryBlockTransferOpen(item))
        {
            return;
        }

        (int screenX, int screenY) = GetContextMenuScreenPoint(item);
        _lastItemContextScreenPoint = null;
        ShellContextMenuHelper.NativeMenuResult result =
            ShellContextMenuHelper.ShowContextMenu(
                _hostWindowHandle,
                item.Path,
                screenX,
                screenY);
        if (result == ShellContextMenuHelper.NativeMenuResult.Failed)
        {
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.Error.OperationIncomplete"),
                WidgetFeedbackSeverity.Warning,
                "system-context-menu"));
        }
    }

    private async Task RunAsAdministratorAsync(WidgetItem item)
    {
        if (TryBlockTransferOpen(item))
        {
            return;
        }

        ElevatedFileLaunchResult result =
            ElevatedFileLauncher.RunAsAdministrator(
            _hostWindowHandle,
            item);
        switch (result.Status)
        {
            case ElevatedFileLaunchStatus.Launched:
            case ElevatedFileLaunchStatus.Cancelled:
                break;

            case ElevatedFileLaunchStatus.AlreadyRunningUnelevated:
            case ElevatedFileLaunchStatus.NoNewProcess:
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.RunAsAdministrator.AlreadyRunning"),
                    WidgetFeedbackSeverity.Warning,
                    "run-as-administrator-existing"));
                break;

            case ElevatedFileLaunchStatus.NotElevated:
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.RunAsAdministrator.NotElevated"),
                    WidgetFeedbackSeverity.Warning,
                    "run-as-administrator-not-elevated"));
                break;

            default:
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.Error.OperationIncomplete"),
                    WidgetFeedbackSeverity.Warning,
                    "run-as-administrator"));
                break;
        }

        await Task.CompletedTask;
    }

    private (int ScreenX, int ScreenY) GetContextMenuScreenPoint(
        WidgetItem item)
    {
        if (_lastItemContextScreenPoint is { } previous)
        {
            return (previous.X, previous.Y);
        }

        if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) &&
            IsScreenPointInsideElement(Root, cursor.X, cursor.Y))
        {
            return (cursor.X, cursor.Y);
        }

        FrameworkElement? target = FindItemSurface(item);
        Win32Helper.RECT windowBounds = default;
        bool hasWindowBounds = Win32Helper.GetWindowRect(
            _hostWindowHandle,
            out windowBounds);
        if (target is null ||
            target.XamlRoot is null ||
            !hasWindowBounds)
        {
            return hasWindowBounds
                ? (windowBounds.Left, windowBounds.Top)
                : (0, 0);
        }

        try
        {
            Windows.Foundation.Point topLeft = target.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = target.XamlRoot.RasterizationScale;
            if (scale <= 0)
            {
                scale = 1;
            }

            return (
                windowBounds.Left + (int)Math.Round(
                    (topLeft.X + target.ActualWidth / 2) * scale),
                windowBounds.Top + (int)Math.Round(
                    (topLeft.Y + target.ActualHeight / 2) * scale));
        }
        catch (InvalidOperationException)
        {
            return (windowBounds.Left, windowBounds.Top);
        }
    }

    private MenuFlyout CreateContentAreaFlyout()
    {
        var flyout = new MenuFlyout();
        string? mappedFolderPath = ViewModel.CurrentFolderPath;
        bool hasMappedFolder =
            !string.IsNullOrWhiteSpace(mappedFolderPath);

        MenuFlyoutItem refresh =
            CreateMenuItem("Common.Refresh", "\uE72C");
        refresh.KeyboardAcceleratorTextOverride = "F5";
        refresh.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(RefreshAsync);
        };
        flyout.Items.Add(refresh);

        MenuFlyoutItem paste =
            CreateMenuItem("Common.Paste", "\uE77F");
        paste.KeyboardAcceleratorTextOverride = "Ctrl+V";
        paste.IsEnabled = hasMappedFolder && CanPasteFromClipboard();
        paste.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(PasteFromClipboardAsync);
        };
        flyout.Items.Add(paste);

        MenuFlyoutItem newFolder =
            CreateMenuItem("Common.NewFolder", "\uE8B7");
        newFolder.KeyboardAcceleratorTextOverride = "Ctrl+Shift+N";
        newFolder.IsEnabled = hasMappedFolder;
        newFolder.Click += async (_, _) =>
        {
            flyout.Hide();
            await CreateFolderInMappedLocationAsync();
        };
        flyout.Items.Add(newFolder);

        MenuFlyoutItem openFolder = CreateMenuItem(
            "Widget.OpenStorageFolder",
            "\uE838");
        openFolder.IsEnabled = hasMappedFolder;
        openFolder.Click += (_, _) =>
        {
            flyout.Hide();
            if (hasMappedFolder && mappedFolderPath is not null)
            {
                Win32Helper.OpenFile(mappedFolderPath);
            }
        };
        flyout.Items.Add(openFolder);

        var hostItems = new WidgetHostContextMenuOpeningEventArgs(flyout);
        HostContextMenuOpening?.Invoke(this, hostItems);
        if (hostItems.TitleStyleItem is not null)
        {
            flyout.Items.Add(hostItems.TitleStyleItem);
        }

        var viewAndSort = new MenuFlyoutSubItem
        {
            Text = T("Widget.ViewAndSort"),
            Icon = new FontIcon { Glyph = "\uE8CB" }
        };
        var iconView = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.IconView"),
            IsChecked = ViewModel.IsIconMode
        };
        iconView.Click += (_, _) =>
        {
            if (!ViewModel.IsIconMode)
            {
                ToggleViewButton_Click(this, new RoutedEventArgs());
            }
        };
        viewAndSort.Items.Add(iconView);

        var listView = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.ListView"),
            IsChecked = ViewModel.IsListMode
        };
        listView.Click += (_, _) =>
        {
            if (!ViewModel.IsListMode)
            {
                ToggleViewButton_Click(this, new RoutedEventArgs());
            }
        };
        viewAndSort.Items.Add(listView);
        viewAndSort.Items.Add(CreateIconSizeMenu());
        viewAndSort.Items.Add(new MenuFlyoutSeparator());
        AddSortItem(
            viewAndSort,
            "Widget.Sort.Manual",
            WidgetSortMode.Manual);
        AddSortItem(
            viewAndSort,
            "Widget.Sort.Name",
            WidgetSortMode.Name);
        AddSortItem(
            viewAndSort,
            "Widget.Sort.Size",
            WidgetSortMode.Size);
        AddSortItem(
            viewAndSort,
            "Widget.Sort.Type",
            WidgetSortMode.Type);
        AddSortItem(
            viewAndSort,
            "Widget.Sort.DateModified",
            WidgetSortMode.DateModified);
        flyout.Items.Add(viewAndSort);
        flyout.Items.Add(CreateStackSettingsMenu());

        if (hostItems.CloseWidgetItem is not null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(hostItems.CloseWidgetItem);
        }
        return flyout;
    }

    private MenuFlyoutSubItem CreateIconSizeMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Settings.IconSize.Title")
        };
        foreach (double size in FileWidgetIconSizePolicy.Steps)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{size:0}",
                IsChecked = Math.Abs(ViewModel.EffectiveIconSize - size) < 0.01
            };
            item.Click += (_, _) => ViewModel.SetIconSizeOverride(size);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem
        {
            Text = T("Widget.IconSize.FollowGlobal"),
            IsEnabled = ViewModel.Config.IconSizeOverride is not null,
            Icon = new FontIcon { Glyph = "\uE777" }
        };
        reset.Click += (_, _) => ViewModel.SetIconSizeOverride(null);
        menu.Items.Add(reset);
        return menu;
    }

    private async Task CreateFolderInMappedLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(
                ViewModel.CurrentFolderPath))
        {
            return;
        }

        if (TryBlockTransferMutation(ViewModel.CurrentFolderPath))
        {
            return;
        }

        await RunAsync(async () =>
        {
            string folderPath = FileService.GetAvailablePath(
                Path.Combine(
                    ViewModel.CurrentFolderPath!,
                    T("Widget.NewFolderName")));
            Directory.CreateDirectory(folderPath);
            await ViewModel.RefreshFolderContentsAsync();
            if (ViewModel.Items.FirstOrDefault(item =>
                    string.Equals(
                        item.Path,
                        folderPath,
                        StringComparison.OrdinalIgnoreCase)) is { } newFolder)
            {
                await StartItemRenameAsync(newFolder);
            }
        });
    }

    private async Task PickMappedFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation =
                PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr owner = Win32Helper.GetAncestor(
            foreground,
            Win32Helper.GA_ROOT);
        InitializeWithWindow.Initialize(
            picker,
            owner == IntPtr.Zero ? foreground : owner);
        Windows.Storage.StorageFolder? folder =
            await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await RunAsync(
                () => ViewModel.UpdateMappedFolderPathAsync(
                    folder.Path));
        }
    }

    private void AddSortItem(
        MenuFlyoutSubItem parent,
        string localizationKey,
        WidgetSortMode mode)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = T(localizationKey),
            IsChecked = ViewModel.Config.SortMode == mode
        };
        item.Click += (_, _) =>
        {
            if (mode != WidgetSortMode.Manual)
            {
                ViewModel.ClearStackDisplayOrderOverride();
            }

            ViewModel.SetSortMode(mode);
        };
        parent.Items.Add(item);
    }

    private MenuFlyoutSubItem CreateStackSettingsMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Widget.Stack.Menu"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        var followDefaults = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.FollowDefaults"),
            IsChecked = ViewModel.FileStacksFollowGlobalDefaults
        };
        followDefaults.Click += (_, _) =>
        {
            followDefaults.IsChecked = true;
            ApplyStackProjectionChange(
                ViewModel.ClearFileStackOverrides);
        };
        menu.Items.Add(followDefaults);

        var enabled = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.EnableForWidget"),
            IsChecked = ViewModel.FileStacksEnabled
        };
        enabled.Click += (_, _) =>
            ApplyStackProjectionChange(() =>
                ViewModel.SetFileStacksEnabledOverride(enabled.IsChecked));
        menu.Items.Add(enabled);
        menu.Items.Add(CreateStackOpenModeMenu());
        menu.Items.Add(new MenuFlyoutSeparator());

        var defaultGrouping = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.UseDefaultGrouping"),
            IsChecked = ViewModel.FileStackGroupByFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        defaultGrouping.Click += (_, _) =>
        {
            defaultGrouping.IsChecked = true;
            ApplyStackProjectionChange(() =>
                ViewModel.SetFileStackGroupByOverride(null));
        };
        menu.Items.Add(defaultGrouping);
        AddStackGroupingItem(
            menu,
            SettingsService.FileStackGroupByKind,
            "Settings.FileStacks.GroupBy.Kind");
        AddStackGroupingItem(
            menu,
            SettingsService.FileStackGroupByDateModified,
            "Settings.FileStacks.GroupBy.DateModified");
        AddStackGroupingItem(
            menu,
            SettingsService.FileStackGroupByCustom,
            "Settings.FileStacks.GroupBy.Custom");

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateStackThresholdMenu());
        menu.Items.Add(CreateStackOrderMenu());
        if (ViewModel.HasDisabledStacks)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var restore = new MenuFlyoutSubItem
            {
                Text = T("Widget.Stack.RestoreGroups"),
                Icon = new FontIcon { Glyph = "\uE8EE" }
            };
            Dictionary<string, string> names =
                WidgetFileStackSettings.GetStackNameOverrides(
                    ViewModel.Config);
            foreach (string key in
                     WidgetFileStackSettings.GetDisabledStacks(
                         ViewModel.Config))
            {
                string label = GetDisabledStackDisplayName(key, names);
                var restoreItem = new MenuFlyoutItem
                {
                    Text = label
                };
                restoreItem.Click += (_, _) =>
                    ApplyStackProjectionChange(() =>
                        ViewModel.SetStackDisabled(key, false));
                restore.Items.Add(restoreItem);
            }
            menu.Items.Add(restore);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem editRules = CreateMenuItem(
            "Settings.FileStacks.Custom.Rules.Title",
            "\uE713");
        editRules.Click += (_, _) =>
            App.Current.ShowSettings("FileStackSettings");
        menu.Items.Add(editRules);

        return menu;
    }

    private void AddStackGroupingItem(
        MenuFlyoutSubItem menu,
        string groupBy,
        string localizationKey)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = T(localizationKey),
            IsChecked =
                !ViewModel.FileStackGroupByFollowsGlobal &&
                string.Equals(
                    ViewModel.FileStackGroupBy,
                    groupBy,
                    StringComparison.Ordinal)
        };
        item.Click += (_, _) =>
            ApplyStackProjectionChange(() =>
                ViewModel.SetFileStackGroupByOverride(groupBy));
        menu.Items.Add(item);
    }

    private MenuFlyoutSubItem CreateStackThresholdMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Settings.FileStacks.Threshold.Title")
        };
        var useDefault = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.UseDefaultThreshold"),
            IsChecked = ViewModel.FileStackThresholdFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        useDefault.Click += (_, _) =>
        {
            useDefault.IsChecked = true;
            ApplyStackProjectionChange(() =>
                ViewModel.SetFileStackThresholdOverride(null));
        };
        menu.Items.Add(useDefault);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (int threshold in new[] { 2, 3, 5 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.Format(
                    "Settings.FileStacks.Threshold.Option",
                    threshold),
                IsChecked =
                    !ViewModel.FileStackThresholdFollowsGlobal &&
                    ViewModel.FileStackThreshold == threshold,
                IsEnabled = ViewModel.FileStacksEnabled
            };
            item.Click += (_, _) =>
                ApplyStackProjectionChange(() =>
                    ViewModel.SetFileStackThresholdOverride(threshold));
            menu.Items.Add(item);
        }

        return menu;
    }

    private MenuFlyoutSubItem CreateStackOrderMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Settings.FileStacks.OrderBy.Title")
        };
        var useDefault = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.UseDefaultOrder"),
            IsChecked = ViewModel.FileStackOrderByFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        useDefault.Click += (_, _) =>
        {
            useDefault.IsChecked = true;
            ApplyStackProjectionChange(() =>
                ViewModel.SetFileStackOrderByOverride(null));
        };
        menu.Items.Add(useDefault);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByWidget,
            "Settings.FileStacks.OrderBy.Widget");
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByName,
            "Settings.FileStacks.OrderBy.Name");
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByDateAdded,
            "Settings.FileStacks.OrderBy.DateAdded");
        AddStackOrderItem(
            menu,
            SettingsService.FileStackOrderByDateModified,
            "Settings.FileStacks.OrderBy.DateModified");
        return menu;
    }

    private MenuFlyoutSubItem CreateStackOpenModeMenu()
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Settings.FileStacks.OpenMode.Title")
        };
        var useDefault = new ToggleMenuFlyoutItem
        {
            Text = T("Widget.Stack.UseDefaultOpenMode"),
            IsChecked = ViewModel.FileStackOpenModeFollowsGlobal
        };
        useDefault.Click += (_, _) =>
        {
            useDefault.IsChecked = true;
            ViewModel.SetFileStackOpenModeOverride(null);
        };
        menu.Items.Add(useDefault);
        menu.Items.Add(new MenuFlyoutSeparator());

        AddStackOpenModeItem(
            menu,
            SettingsService.FileStackOpenModeInline,
            "Settings.FileStacks.OpenMode.Inline");
        AddStackOpenModeItem(
            menu,
            SettingsService.FileStackOpenModePopover,
            "Settings.FileStacks.OpenMode.Popover");
        return menu;
    }

    private void AddStackOpenModeItem(
        MenuFlyoutSubItem menu,
        string openMode,
        string localizationKey)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = T(localizationKey),
            IsChecked =
                !ViewModel.FileStackOpenModeFollowsGlobal &&
                string.Equals(
                    ViewModel.FileStackOpenMode,
                    openMode,
                    StringComparison.Ordinal)
        };
        item.Click += (_, _) =>
            ViewModel.SetFileStackOpenModeOverride(openMode);
        menu.Items.Add(item);
    }

    private void AddStackOrderItem(
        MenuFlyoutSubItem menu,
        string orderBy,
        string localizationKey)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = T(localizationKey),
            IsChecked =
                !ViewModel.FileStackOrderByFollowsGlobal &&
                string.Equals(
                    ViewModel.FileStackOrderBy,
                    orderBy,
                    StringComparison.Ordinal),
            IsEnabled = ViewModel.FileStacksEnabled
        };
        item.Click += (_, _) =>
            ApplyStackProjectionChange(() =>
                ViewModel.SetFileStackOrderByOverride(orderBy));
        menu.Items.Add(item);
    }

    private MenuFlyout CreateStackFlyout(WidgetStackItem stack)
    {
        var flyout = new MenuFlyout();
        MenuFlyoutItem toggle = new()
        {
            Text = T(stack.IsExpanded
                ? "Widget.Stack.Collapse"
                : "Widget.Stack.Expand"),
            Icon = new FontIcon { Glyph = stack.ChevronGlyph }
        };
        toggle.Click += (_, _) => ToggleStackFromInput(stack);
        flyout.Items.Add(toggle);

        MenuFlyoutItem rename = CreateMenuItem(
            "Widget.Stack.Rename",
            "\uE8AC");
        rename.Click += async (_, _) =>
            await RenameStackAsync(stack);
        flyout.Items.Add(rename);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem moveUp = CreateMenuItem(
            "Widget.Stack.MoveUp",
            "\uE74A");
        moveUp.IsEnabled = ViewModel.CanMoveStackUp(stack.StackKey);
        moveUp.Click += (_, _) =>
            ViewModel.MoveStackUp(stack.StackKey);
        flyout.Items.Add(moveUp);

        MenuFlyoutItem moveDown = CreateMenuItem(
            "Widget.Stack.MoveDown",
            "\uE74B");
        moveDown.IsEnabled = ViewModel.CanMoveStackDown(stack.StackKey);
        moveDown.Click += (_, _) =>
            ViewModel.MoveStackDown(stack.StackKey);
        flyout.Items.Add(moveDown);

        MenuFlyoutItem disable = CreateMenuItem(
            stack.IsManual
                ? "Widget.Stack.Dissolve"
                : "Widget.Stack.DisableGroup",
            "\uE748");
        disable.Click += (_, _) =>
            ApplyStackProjectionChange(() =>
                _ = ViewModel.DissolveStack(stack));
        flyout.Items.Add(disable);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem selectContents = CreateMenuItem(
            "Widget.Stack.SelectContents",
            "\uE762");
        selectContents.Click += (_, _) =>
            SelectStackMembers(stack);
        flyout.Items.Add(selectContents);

        MenuFlyoutItem copyPaths = CreateMenuItem(
            "Widget.Stack.CopyContentPaths",
            "\uE8C8");
        copyPaths.Click += (_, _) =>
        {
            CopyPathsToClipboard(stack.Members);
        };
        flyout.Items.Add(copyPaths);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem editRules = CreateMenuItem(
            "Settings.FileStacks.Custom.Rules.Title",
            "\uE713");
        editRules.Click += (_, _) =>
            App.Current.ShowSettings("FileStackSettings");
        flyout.Items.Add(editRules);
        return flyout;
    }

    private string GetDisabledStackDisplayName(
        string key,
        IReadOnlyDictionary<string, string> names)
    {
        if (names.TryGetValue(key, out string? customName) &&
            !string.IsNullOrWhiteSpace(customName))
        {
            return customName;
        }

        if (string.Equals(
                key,
                "Custom:Other",
                StringComparison.Ordinal))
        {
            return T("Widget.Stack.Category.Other");
        }

        if (key.StartsWith("Custom:", StringComparison.Ordinal))
        {
            string ruleId = key["Custom:".Length..];
            FileStackCustomRule? rule = _settingsService.Settings
                .FileStackCustomRules
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Id,
                    ruleId,
                    StringComparison.Ordinal));
            if (rule is not null && !string.IsNullOrWhiteSpace(rule.Name))
            {
                return rule.Name.Trim();
            }

            return T("Settings.FileStacks.GroupBy.Custom");
        }

        return Enum.TryParse(
            key,
            ignoreCase: false,
            out WidgetStackCategory category)
                ? T($"Widget.Stack.Category.{category}")
                : key;
    }

    private MenuFlyoutItem CreateMenuItem(
        string localizationKey,
        string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = T(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    private void CopySelectedPathsToClipboard()
    {
        CopyPathsToClipboard(GetSelectedItems());
    }

    private void CopyPathsToClipboard(
        IEnumerable<WidgetItem> items)
    {
        string[] paths = items
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        string text = string.Join(Environment.NewLine, paths);
        var package = new DataPackage();
        package.SetText(text);
        DeskBoxClipboardWriteScope.MarkWrite(
            text: text,
            paths: paths);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                "Widget.CopyPathCount",
                paths.Length),
            WidgetFeedbackSeverity.Success,
            "file-copy-path"));
    }

    private bool CanMoveItemsBackToDesktop()
    {
        return !string.IsNullOrWhiteSpace(
                ViewModel.MappedFolderPath) &&
            !TryGetSelectedTransferState(out _);
    }

    private async Task MoveSelectedItemsBackToDesktopAsync()
    {
        IReadOnlyList<WidgetItem> items = GetSelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        if (TryBlockTransferMutation(items))
        {
            return;
        }

        await RunAsync(async () =>
        {
            int moved = await ViewModel.MoveItemsBackToDesktopAsync(
                items,
                useShellProgress: true,
                ownerWindowHandle: _hostWindowHandle);
            _cutClipboardPaths = [];
            ApplyCutState();
            ShowFeedback(new WidgetFeedbackRequest(
                moved > 0
                    ? _localizationService.Format(
                        "Widget.MovedBackToDesktop",
                        moved)
                    : T("Widget.NoItemsMoved"),
                moved > 0
                    ? WidgetFeedbackSeverity.Success
                    : WidgetFeedbackSeverity.Info,
                "file-move-desktop"));
        });
    }

    private void ApplyStackProjectionChange(Action change)
    {
        ResetSelectionForStackProjectionChange();
        change();
        DispatcherQueue.TryEnqueue(() =>
        {
            ReconcileStackPopover();
            ResetSelectionForStackProjectionChange();
            // Collection reconciliation and container recycling can finish on
            // the following layout turn. Clear once more after that work so a
            // collapsed stack header cannot donate IsSelected to a child.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ResetSelectionForStackProjectionChange);
        });
    }

    private void ResetSelectionForStackProjectionChange()
    {
        ResetBoxSelectionState();
        _pendingPointerDragItems = [];
        _pressedStack = null;
        _stackPointerDragStarted = false;
        ItemsGrid.SelectedItems.Clear();
        ItemsList.SelectedItems.Clear();
        _stackPopoverItemsView?.SelectedItems.Clear();
        UpdateSelectionCommandBar();
        UpdateItemSurfaceVisuals();
    }

    private void SelectStackMembers(WidgetStackItem stack)
    {
        ResetSelectionForStackProjectionChange();
        if (ViewModel.UsesStackPopover)
        {
            ShowStackPopover(stack);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_stackPopoverItemsView is not { } listView)
                {
                    return;
                }

                listView.SelectedItems.Clear();
                foreach (WidgetItem member in stack.Members)
                {
                    if (listView.Items.Contains(member))
                    {
                        listView.SelectedItems.Add(member);
                    }
                }
                UpdateSelectionCommandBar();
            });
            return;
        }

        ViewModel.SetStackExpanded(stack, true);
        DispatcherQueue.TryEnqueue(() =>
        {
            ListViewBase listView = GetActiveItemsView();
            listView.SelectedItems.Clear();
            foreach (WidgetItem member in stack.Members)
            {
                if (listView.Items.Contains(member))
                {
                    listView.SelectedItems.Add(member);
                }
            }

            UpdateSelectionCommandBar();
        });
    }
}
