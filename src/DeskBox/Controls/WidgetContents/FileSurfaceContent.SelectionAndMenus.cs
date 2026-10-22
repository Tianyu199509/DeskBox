using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private sealed record SelectionHit(
        WidgetItem Item,
        Border? Surface,
        Windows.Foundation.Rect Bounds);

    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private bool _selectionStartedWithRightButton;
    private bool _selectionPressOnRowBlank;
    private bool _suppressNextRightTap;
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
        // A stale one-shot suppress must never eat the next gesture.
        _suppressNextRightTap = false;

        bool isLeftButtonPressed =
            e.GetCurrentPoint(pointerSurface).Properties.IsLeftButtonPressed;
        bool isRightButtonPressed =
            e.GetCurrentPoint(pointerSurface).Properties.IsRightButtonPressed;
        _selectionPressOnRowBlank = false;
        if ((!isLeftButtonPressed && !isRightButtonPressed) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartBoxSelection(e.OriginalSource, e))
        {
            return;
        }

        // The right button joins Explorer's marquee: right-drag on blank
        // space sweeps a selection, and the release opens the context menu
        // for whatever it caught (blank-area menu when it caught nothing).
        _selectionStartedWithRightButton = isRightButtonPressed;
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
        bool completedRightMarquee =
            wasBoxSelecting && _selectionStartedWithRightButton;
        FinishBoxSelection(listView);
        _pendingPointerDragItems = [];
        _selectionStartedWithRightButton = false;
        _selectionPressOnRowBlank = false;
        if (wasBoxSelecting)
        {
            pointerSurface.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        if (completedRightMarquee)
        {
            // Explorer: the right-button marquee ends by opening the
            // context menu for the swept selection at the release point —
            // the blank-area menu when the sweep caught nothing. The menu
            // is placed through the view that hosts the items (the popover
            // menu path keys off that view), and a possible duplicate
            // RightTapped is swallowed by the one-shot suppress.
            _suppressNextRightTap = true;
            WidgetItem? selectionAnchor = GetSelectedItems().FirstOrDefault();
            if (selectionAnchor is not null)
            {
                ShowItemContextMenuForSelection(
                    listView,
                    selectionAnchor,
                    target => e.GetCurrentPoint(target).Position);
            }
            else
            {
                ShowContentAreaFlyoutAt(
                    listView,
                    target => e.GetCurrentPoint(target).Position);
            }
        }
    }

    private void Items_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            HandleItemsPointerCaptureLost(listView);
        }
    }

    private void HandleItemsPointerCaptureLost(ListViewBase listView)
    {
        FinishBoxSelection(listView);
        // Capture loss ends the gesture that staged the drag snapshot. A
        // stale snapshot keeps the deactivation selection clear guarded
        // (it reads as an active drag) exactly when the app opened from a
        // mid-press double-click has just taken the foreground away.
        _pendingPointerDragItems = [];
    }

    private bool CanStartBoxSelection(
        object? originalSource,
        PointerRoutedEventArgs e)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        if (FileItemSelectionGeometry.HasAncestor<ScrollBar>(source) ||
            FileItemSelectionGeometry.HasAncestor<ButtonBase>(source) ||
            FileItemSelectionGeometry.HasAncestor<TextBox>(source))
        {
            return false;
        }

        // A list row's blank remainder (outside the icon+label block) is
        // blank surface for the marquee — Explorer starts its rubber band
        // there for BOTH buttons, and a full list has no true blank left
        // to start from. A left press on row blank keeps Explorer's click
        // semantics: the view's built-in click still selects the row on
        // release, and the item-container drag is cancelled while the
        // marquee is pending (see Items_DragStarting). The primary
        // icon+label block stays item territory for both buttons.
        if (FindItemSurface(source) is { } surface)
        {
            _selectionPressOnRowBlank = surface.IsListMode &&
                !surface.IsPointWithinListPrimaryContent(
                    e.GetCurrentPoint(surface).Position);
            return _selectionPressOnRowBlank;
        }

        return !FileItemSelectionGeometry.IsWithinItemSurface(source) &&
               !FileItemSelectionGeometry.HasAncestor<SelectorItem>(source);
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

        // A marquee spends most of its life not changing the selection: the
        // pointer moves, the rectangle moves, but the same items stay inside it.
        // Re-applying in that case would clear and re-add every item (one
        // selection notification each) and walk the whole visual tree for
        // visuals that are already correct.
        var current = new HashSet<WidgetItem>(
            listView.SelectedItems.OfType<WidgetItem>());
        if (current.SetEquals(selected))
        {
            return;
        }

        _isSynchronizingSelection = true;
        try
        {
            // Only the delta is touched, so crossing a few items costs a few
            // notifications instead of one per item per pointer move.
            foreach (WidgetItem item in current
                         .Where(item => !selected.Contains(item))
                         .ToArray())
            {
                listView.SelectedItems.Remove(item);
            }

            foreach (WidgetItem item in listView.Items
                         .OfType<WidgetItem>()
                         .Where(item => selected.Contains(item) && !current.Contains(item)))
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

    private void RegisterOpenedItemSelectionSuppression(WidgetItem item)
    {
        _openedItemSelectionSuppression[item.Path] =
            Environment.TickCount64 + OpenedSelectionSuppressionMs;
    }

    private bool IsOpenSelectionSuppressed(WidgetItem item)
    {
        if (_openedItemSelectionSuppression.TryGetValue(
                item.Path,
                out long until))
        {
            if (Environment.TickCount64 < until)
            {
                return true;
            }

            _openedItemSelectionSuppression.Remove(item.Path);
        }

        return false;
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

    /// <summary>
    /// The enclosing item surface, or null on true blank space. A type
    /// walk, deliberately NOT <see cref="FindItemElement"/>: every element
    /// inside a row inherits the WidgetItem DataContext, so the
    /// DataContext-based walk stops at the innermost hit element and can
    /// never return the surface itself (which silently disabled the list
    /// right-click zone until this helper existed).
    /// </summary>
    private static FileItemSurface? FindItemSurface(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FileItemSurface surface)
            {
                return surface;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
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
            T,
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
            CanCreateManualStack: ViewModel.FileStacksEnabled,
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
            ShowSystemContextMenuAsync,
            ElevatedFileLauncher.CanRunAsAdministrator,
            RunAsAdministratorAsync,
            GetOpenWithMenuItems: GetOpenWithMenuItems,
            CanExtractArchiveItem,
            ExtractArchiveItemAsync,
            CanCompressSelection,
            CompressSelectionAsync,
            CreateItemShortcutAsync: CreateItemShortcutAsync,
            EmptyRecycleBinAsync: EmptyRecycleBinAsync);
    }

    private OpenWithMenuPayload? GetOpenWithMenuItems(WidgetItem item)
    {
        if (item.IsFolder ||
            !ShellOpenWithHelper.SupportsOpenWith(item.Path))
        {
            return null;
        }

        List<OpenWithHandlerEntry> apps;
        try
        {
            apps = ShellOpenWithHelper.GetRecommendedApps(item.Path)
                .ToList();
        }
        catch
        {
            apps = [];
        }

        var items = new List<MenuFlyoutItemBase>();
        foreach (OpenWithHandlerEntry app in apps)
        {
            OpenWithHandlerEntry captured = app;
            var appItem = new MenuFlyoutItem { Text = captured.UiName };
            appItem.Click += (_, _) =>
            {
                if (!captured.TryInvoke(item.Path))
                {
                    ShowFeedback(new WidgetFeedbackRequest(
                        T("Widget.Error.OperationIncomplete"),
                        WidgetFeedbackSeverity.Warning,
                        "open-with"));
                }
            };
            items.Add(appItem);
        }

        if (apps.Count > 0)
        {
            items.Add(new MenuFlyoutSeparator());
        }

        var chooseApp = new MenuFlyoutItem
        {
            Text = T("Widget.OpenWith.ChooseApp"),
            Icon = new FontIcon { Glyph = "" }
        };
        chooseApp.Click += (_, _) =>
        {
            if (!ShellOpenWithHelper.ShowChooseAppDialog(
                    _hostWindowHandle,
                    item.Path))
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.Error.OperationIncomplete"),
                    WidgetFeedbackSeverity.Warning,
                    "open-with-choose"));
            }
        };
        items.Add(chooseApp);

        // The Shell association handlers behind the entries must stay alive
        // until the submenu round ends (idempotent for the two close paths).
        bool disposed = false;

        // Prefetch app icons in the background: extraction starts the moment
        // the menu is built (right-click), so by the time the user hovers
        // Open With the icons are already decoded — opening the menu itself
        // never waits on icon I/O. Entries without an icon location stay
        // text-only, matching the native menu's tolerance.
        if (apps.Count > 0 &&
            Root.DispatcherQueue is { } iconDispatcher)
        {
            var iconPairs = new List<(MenuFlyoutItem Item, OpenWithHandlerEntry App)>();
            int appIndex = 0;
            foreach (MenuFlyoutItemBase entry in items)
            {
                if (entry is MenuFlyoutItem menuItem &&
                    appIndex < apps.Count &&
                    apps[appIndex].IconSourcePath is not null)
                {
                    iconPairs.Add((menuItem, apps[appIndex]));
                }

                if (entry is MenuFlyoutItem)
                {
                    appIndex++;
                }
            }

            if (iconPairs.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach ((MenuFlyoutItem item2, OpenWithHandlerEntry app)
                             in iconPairs)
                    {
                        if (disposed)
                        {
                            return;
                        }

                        byte[]? png = await Task.Run(() =>
                            IconHelper.GetIndexedIconPngBytes(
                                app.IconSourcePath!,
                                app.IconSourceIndex,
                                20));
                        if (png is null || disposed)
                        {
                            continue;
                        }

                        Microsoft.UI.Xaml.Media.Imaging.BitmapImage? image =
                            await IconHelper.CreateBitmapImageAsync(
                                iconDispatcher,
                                png,
                                20);
                        if (image is null || disposed)
                        {
                            continue;
                        }

                        iconDispatcher.TryEnqueue(() =>
                        {
                            if (disposed)
                            {
                                return;
                            }

                            item2.Icon = new ImageIcon { Source = image };
                        });
                    }
                });
            }
        }

        return new OpenWithMenuPayload(
            items,
            () =>
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                foreach (OpenWithHandlerEntry app in apps)
                {
                    app.Dispose();
                }
            });
    }

    private static bool CanExtractArchiveItem(WidgetItem item) =>
        !item.IsFolder &&
        string.Equals(
            Path.GetExtension(item.Path),
            ".zip",
            StringComparison.OrdinalIgnoreCase);

    private async Task ExtractArchiveItemAsync(WidgetItem item)
    {
        string? folderPath = ViewModel.CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(folderPath) ||
            TryBlockTransferOpen(item) ||
            TryBlockTransferMutation(folderPath))
        {
            return;
        }

        try
        {
            string destination = await ArchiveZipService.ExtractAllAsync(
                item.Path);
            _ = destination;
            await RunAsync(() => ViewModel.RefreshFolderContentsAsync());
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ArchiveZip] extract failed widget={WidgetId} " +
                $"archive='{item.Path}': {ex.Message}");
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.Zip.ExtractFailed"),
                WidgetFeedbackSeverity.Warning,
                "extract-zip"));
        }
    }

    private static bool CanCompressSelection(
        IReadOnlyList<WidgetItem> items)
    {
        if (items.Count == 0)
        {
            return false;
        }

        // Compressing an archive itself is a no-op almost nobody wants;
        // Explorer still offers it, the grid keeps the menu lean.
        bool singleZipArchive = items.Count == 1 &&
            string.Equals(
                Path.GetExtension(items[0].Path),
                ".zip",
                StringComparison.OrdinalIgnoreCase);
        return !singleZipArchive && items.All(item =>
            !string.IsNullOrWhiteSpace(item.Path));
    }

    private async Task CompressSelectionAsync(
        IReadOnlyList<WidgetItem> items,
        ArchiveCompressionFormat format)
    {
        List<WidgetItem> selection = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .ToList();
        if (selection.Count == 0 ||
            TryBlockTransferMutation(selection))
        {
            return;
        }

        try
        {
            string archivePath = await ArchiveZipService.CompressAsync(
                selection.Select(item => item.Path).ToList(),
                format);
            _ = archivePath;
            await RunAsync(() => ViewModel.RefreshFolderContentsAsync());
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ArchiveZip] compress failed widget={WidgetId}: " +
                ex.Message);
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.Zip.CompressFailed"),
                WidgetFeedbackSeverity.Warning,
                "compress-zip"));
        }
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

    /// <summary>
    /// Native "Create shortcut" verb: a new "name - Shortcut.lnk" beside the
    /// item, matching Explorer. Namespace links re-materialize the same
    /// namespace target; every other item gets a link pointing at the item
    /// itself (a link source nests exactly the way Explorer's verb does, and
    /// Windows resolves the chain on open).
    /// </summary>
    private async Task CreateItemShortcutAsync(WidgetItem item)
    {
        if (TryBlockTransferMutation(new[] { item }))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(item.Path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            string linkPath = FileService.GetAvailablePath(
                Path.Combine(directory, GetShortcutDisplayName(item.Path)));

            if (item.NamespaceShortcutKind != NamespaceShortcutKind.None &&
                !string.IsNullOrWhiteSpace(item.NamespaceShortcutParsingName))
            {
                ShortcutHelper.CreateShellNamespaceShortcut(
                    linkPath,
                    item.NamespaceShortcutParsingName,
                    T("Widget.CreateShortcut"));
            }
            else if (Directory.Exists(item.Path))
            {
                ShortcutHelper.CreateOrUpdateFolderShortcut(
                    linkPath,
                    item.Path,
                    T("Widget.CreateShortcut"));
            }
            else
            {
                // No icon override: the system renders the target's own icon
                // with the shortcut overlay, matching Explorer's native verb.
                DragDropPermissionService.CreateOrUpdateShortcut(
                    linkPath,
                    item.Path,
                    string.Empty,
                    iconPath: null);
            }

            await RunAsync(() => ViewModel.RefreshFolderContentsAsync());
        }
        catch (Exception ex)
        {
            App.Log(
                $"[CreateShortcut] failed widget={WidgetId} " +
                $"source={item.Path}: {ex.Message}");
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.Error.OperationIncomplete"),
                WidgetFeedbackSeverity.Warning,
                "create-shortcut"));
        }
    }

    /// <summary>
    /// Empty Recycle Bin for the bin's namespace shortcut — the desktop
    /// icon's own verb, with the system's confirmation and progress dialogs
    /// (flags 0). A bin emptied between the menu check and the click no-ops
    /// instead of popping the dialog for nothing.
    /// </summary>
    private async Task EmptyRecycleBinAsync()
    {
        try
        {
            if (ShellRecycleBinInterop.TryQueryTotalItemCount(out long totalItems) &&
                totalItems == 0)
            {
                return;
            }

            if (!ShellRecycleBinInterop.TryEmptyAllBins(_hostWindowHandle))
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.Error.OperationIncomplete"),
                    WidgetFeedbackSeverity.Warning,
                    "empty-recycle-bin"));
                return;
            }

            // The bin just emptied: its state-aware icon (empty vs. full
            // graphic for the same PIDL) must re-render even though the
            // shortcut file never changed.
            await RefreshRecycleBinItemIconsAsync();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[EmptyRecycleBin] failed widget={WidgetId}: {ex.Message}");
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.Error.OperationIncomplete"),
                WidgetFeedbackSeverity.Warning,
                "empty-recycle-bin"));
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// The popover is a no-activate window: DeskBox is not the foreground
    /// process at right-click, which voids the AllowSetForegroundWindow
    /// grant the native proxy needs for its menu — the menu tracked beneath
    /// the popover and died on the next click (1.5.9 Win10 VM acceptance
    /// 2026-10-20, fg=0 in the proxy timing log; main-surface menus all
    /// ran fg=1). The right-click is the last input event, so the transfer
    /// to the popover is permitted; the grant issued inside ShowAsync then
    /// becomes effective and the menu lands above the popover.
    /// </summary>
    private void TryBringStackPopoverToForeground()
    {
        try
        {
            if (_stackPopoverHostWindow is { WindowHandle: var handle } &&
                handle != IntPtr.Zero)
            {
                _ = Win32Helper.SetForegroundWindow(handle);
            }
        }
        catch
        {
            // Best-effort: without the transfer the menu still opens, it
            // just keeps the background-owner dismissal quirks.
        }
    }

    private async Task ShowSystemContextMenuAsync(WidgetItem item)
    {
        if (TryBlockTransferOpen(item))
        {
            return;
        }

        bool fromStackPopover = _stackPopoverItemsView?.Items
            .OfType<WidgetItem>()
            .Any(candidate => ReferenceEquals(candidate, item)) == true;
        if (fromStackPopover)
        {
            // The WinUI flyout closes before the native proxy menu opens. Keep
            // the stack host alive while focus temporarily belongs to the
            // proxy-owned HWND.
            _stackPopoverContextMenuOpen = true;
            _stackPopoverSystemContextMenuOpen = true;
            TryBringStackPopoverToForeground();
        }

        try
        {
            (int screenX, int screenY) = GetContextMenuScreenPoint(item);
            _lastItemContextScreenPoint = null;
            ShellContextMenuProxy.MenuResult result =
                await ShellContextMenuProxy.ShowAsync(
                    item.Path,
                    screenX,
                    screenY,
                    // The native proxy has no resources of its own; the host
                    // labels the rename entry it injects into the Shell menu.
                    T("Common.Rename"));
            if (result == ShellContextMenuProxy.MenuResult.CustomRename)
            {
                // The user picked the host-owned rename entry (a view-host
                // verb the pure Shell menu cannot offer); run the built-in
                // rename pipeline, the same one the WinUI flyout uses.
                await RenameItemAsync(item);
            }
            else if (result != ShellContextMenuProxy.MenuResult.Invoked &&
                result != ShellContextMenuProxy.MenuResult.Cancelled)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    SystemMenuFailureText(result),
                    WidgetFeedbackSeverity.Warning,
                    "system-context-menu"));
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShellContextMenuProxy] Product call failed " +
                $"widget={WidgetId}: {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.Error.OperationIncomplete"),
                WidgetFeedbackSeverity.Warning,
                "system-context-menu"));
        }
        finally
        {
            _lastItemContextScreenPoint = null;
            if (fromStackPopover)
            {
                _stackPopoverSystemContextMenuOpen = false;
                CompleteStackPopoverContextMenu();
            }
        }
    }

    /// <summary>
    /// Failure text for a native Shell menu round. InvokeCommand failures are
    /// classified by the proxy (unsupported verb, cancellation, permission)
    /// so the toast can say what happened instead of a generic "did not
    /// complete" for every HRESULT.
    /// </summary>
    private static string SystemMenuFailureText(
        ShellContextMenuProxy.MenuResult result) => result switch
    {
        ShellContextMenuProxy.MenuResult.FailedUnsupported =>
            LocalizeSystemMenuText("Widget.SystemMenu.Unsupported"),
        ShellContextMenuProxy.MenuResult.FailedCancelled =>
            LocalizeSystemMenuText("Widget.SystemMenu.Cancelled"),
        ShellContextMenuProxy.MenuResult.FailedAccessDenied =>
            LocalizeSystemMenuText("Widget.SystemMenu.AccessDenied"),
        _ => LocalizeSystemMenuText("Widget.Error.OperationIncomplete")
    };

    private static string LocalizeSystemMenuText(string key)
    {
        try
        {
            return global::DeskBox.App.Current?.LocalizationService?.T(key) ?? key;
        }
        catch
        {
            return key;
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
        var flyout = new MenuFlyout
        {
            // Monitor-bounds-only placement (see FileItemMenuBuilder): the
            // grid window is too small to host the menu near its edges.
            ShouldConstrainToRootBounds = false
        };
        string? mappedFolderPath = ViewModel.CurrentFolderPath;
        bool hasMappedFolder =
            !string.IsNullOrWhiteSpace(mappedFolderPath);

        var hostItems = new WidgetHostContextMenuOpeningEventArgs(flyout);
        HostContextMenuOpening?.Invoke(this, hostItems);

        var view = new MenuFlyoutSubItem
        {
            Text = T("Widget.View"),
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
        view.Items.Add(iconView);

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
        view.Items.Add(listView);
        view.Items.Add(CreateIconSizeMenu());
        flyout.Items.Add(view);

        var sortBy = new MenuFlyoutSubItem
        {
            Text = T("Widget.SortBy"),
            Icon = new FontIcon { Glyph = "\uE8CB" }
        };
        AddSortItem(
            sortBy,
            "Widget.Sort.Manual",
            WidgetSortMode.Manual);
        AddSortItem(
            sortBy,
            "Widget.Sort.Name",
            WidgetSortMode.Name);
        AddSortItem(
            sortBy,
            "Widget.Sort.Size",
            WidgetSortMode.Size);
        AddSortItem(
            sortBy,
            "Widget.Sort.Type",
            WidgetSortMode.Type);
        AddSortItem(
            sortBy,
            "Widget.Sort.DateModified",
            WidgetSortMode.DateModified);
        flyout.Items.Add(sortBy);

        MenuFlyoutItem refresh =
            CreateMenuItem("Common.Refresh", "\uE72C");
        refresh.KeyboardAcceleratorTextOverride = "F5";
        refresh.Click += async (_, _) =>
        {
            flyout.Hide();
            await RunAsync(RefreshAsync);
        };
        flyout.Items.Add(refresh);

        flyout.Items.Add(new MenuFlyoutSeparator());

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

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(CreateNewSubMenu(hasMappedFolder));

        flyout.Items.Add(new MenuFlyoutSeparator());

        if (hostItems.TitleStyleItem is not null)
        {
            flyout.Items.Add(hostItems.TitleStyleItem);
        }
        flyout.Items.Add(CreateStackSettingsMenu());

        MenuFlyoutItem openFolder = CreateMenuItem(
            "Widget.OpenStorageFolder",
            "\uE838");
        openFolder.IsEnabled = hasMappedFolder;
        openFolder.Click += (_, _) =>
        {
            flyout.Hide();
            if (hasMappedFolder && mappedFolderPath is not null)
            {
                // Keep the configured junction/symlink path for persistence,
                // but hand Explorer the physical target. RedirectionGuard can
                // reject ShellExecute's traversal of a user-created mount
                // point even though the target itself is accessible.
                string pathToOpen = FileService.TryResolveExistingPathForTraversal(
                        mappedFolderPath,
                        out string resolvedPath)
                    ? resolvedPath
                    : mappedFolderPath;
                Win32Helper.OpenFile(pathToOpen);
            }
        };
        flyout.Items.Add(openFolder);

        if (hostItems.CloseWidgetItem is not null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(hostItems.CloseWidgetItem);
        }
        return flyout;
    }

    private MenuFlyoutSubItem CreateNewSubMenu(bool hasMappedFolder)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = T("Widget.New"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };

        MenuFlyoutItem newFolder =
            CreateMenuItem("Common.NewFolder", "\uE8B7");
        newFolder.KeyboardAcceleratorTextOverride = "Ctrl+Shift+N";
        newFolder.IsEnabled = hasMappedFolder;
        newFolder.Click += async (_, _) =>
        {
            await CreateFolderInMappedLocationAsync();
        };
        menu.Items.Add(newFolder);

        var iconTargets =
            new List<(MenuFlyoutItem Item, string ProbeFileName)>();

        if (hasMappedFolder)
        {
            // Second pinned row, exactly like Explorer: the label comes
            // from the OS-localized .lnk ShellNew registration.
            if (ShellNewMenuHelper.TryGetShortcutNewTemplate(
                    out string shortcutText,
                    out string? shortcutBase))
            {
                MenuFlyoutItem newShortcut = new()
                {
                    Text = shortcutText
                };
                newShortcut.Click += async (_, _) =>
                {
                    await CreateShortcutInMappedLocationAsync(
                        shortcutBase ?? shortcutText);
                };
                menu.Items.Add(newShortcut);
                iconTargets.Add((newShortcut, "new.lnk"));
            }

            IReadOnlyList<ShellNewEntry> entries = [];
            try
            {
                entries = ShellNewMenuHelper.GetEntries();
            }
            catch
            {
                // The registry New list is best-effort; the pinned entries
                // above still work without it.
            }

            if (entries.Count > 0)
            {
                // No separator: Explorer's New submenu runs folder, shortcut
                // and file types as one flat list.
                foreach (ShellNewEntry entry in entries)
                {
                    ShellNewEntry captured = entry;
                    var newItem = new MenuFlyoutItem
                    {
                        Text = captured.DisplayName
                    };
                    newItem.Click += async (_, _) =>
                    {
                        await CreateFileFromShellNewEntryAsync(captured);
                    };
                    menu.Items.Add(newItem);
                    iconTargets.Add((newItem, "new" + captured.Extension));
                }
            }
        }

        PrefetchNewEntryIcons(iconTargets);

        return menu;
    }

    /// <summary>
    /// Fills the New submenu entries with their file-type icons in the
    /// background (same pattern as the Open With app icons): the shell is
    /// only asked for the type's icon by extension, and the menu stays
    /// responsive while the icons stream in. Entries without a type icon
    /// stay text-only, matching the native menu's tolerance.
    /// </summary>
    private void PrefetchNewEntryIcons(
        IReadOnlyList<(MenuFlyoutItem Item, string ProbeFileName)> targets)
    {
        if (targets.Count == 0 || Root.DispatcherQueue is not { } dispatcher)
        {
            return;
        }

        // No teardown flag: unlike the Open With prefetch, nothing here
        // owns COM lifetimes — a late TryEnqueue after the flyout closed
        // only touches an already-detached MenuFlyoutItem.
        _ = Task.Run(async () =>
        {
            foreach ((MenuFlyoutItem item, string probeFileName) in targets)
            {
                byte[]? png = await Task.Run(() =>
                    IconHelper.GetFileTypeIconPngBytes(probeFileName));
                if (png is null)
                {
                    continue;
                }

                Microsoft.UI.Xaml.Media.Imaging.BitmapImage? image =
                    await IconHelper.CreateBitmapImageAsync(
                        dispatcher,
                        png,
                        20);
                if (image is null)
                {
                    continue;
                }

                dispatcher.TryEnqueue(() =>
                {
                    item.Icon = new ImageIcon { Source = image };
                });
            }
        });
    }

    private async Task CreateShortcutInMappedLocationAsync(string baseName)
    {
        string? folderPath = ViewModel.CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(folderPath) ||
            TryBlockTransferMutation(folderPath))
        {
            return;
        }

        await RunAsync(async () =>
        {
            string targetPath = FileService.GetAvailablePath(
                Path.Combine(folderPath, baseName + ".lnk"));
            if (!ShellLinkVtableInterop.TryCreateEmptyShortcut(targetPath))
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.Error.OperationIncomplete"),
                    WidgetFeedbackSeverity.Warning,
                    "new-shortcut"));
                return;
            }

            await ViewModel.RefreshFolderContentsAsync();

            // The placeholder has no target yet; the Properties sheet's
            // Shortcut tab is where the user completes it (Explorer's own
            // New→Shortcut uses its in-process wizard for the same step).
            // Modal — the refresh below also picks up a rename made there.
            _ = Win32Helper.ShowFileProperties(_hostWindowHandle, targetPath);
            await ViewModel.RefreshFolderContentsAsync();
        });
    }

    private async Task CreateFileFromShellNewEntryAsync(
        ShellNewEntry entry)
    {
        string? folderPath = ViewModel.CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(folderPath) ||
            TryBlockTransferMutation(folderPath))
        {
            return;
        }

        await RunAsync(async () =>
        {
            string? createdPath = ShellNewMenuHelper.CreateFile(
                entry,
                folderPath,
                name => _localizationService.Format(
                    "Widget.NewFileName",
                    name));
            if (createdPath is null)
            {
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.Error.OperationIncomplete"),
                    WidgetFeedbackSeverity.Warning,
                    "new-file"));
                return;
            }

            await ViewModel.RefreshFolderContentsAsync();
            if (ViewModel.Items.FirstOrDefault(item =>
                    string.Equals(
                        item.Path,
                        createdPath,
                        StringComparison.OrdinalIgnoreCase)) is { } newFile)
            {
                await StartItemRenameAsync(newFile);
            }
        });
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
        var flyout = new MenuFlyout
        {
            // Monitor-bounds-only placement, same as the other grid menus.
            ShouldConstrainToRootBounds = false
        };
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
        _stackProjectionTransitionPending = true;
        ResetSelectionForStackProjectionChange();
        change();
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            ReconcileStackPopover();
            ResetSelectionForStackProjectionChange();
            try
            {
                ItemsGrid.UpdateLayout();
                ItemsList.UpdateLayout();
            }
            catch (InvalidOperationException)
            {
                // A recycled content host can be detached during a settings
                // transition. The next layout turn still establishes the
                // correct containers before drag preview is enabled.
            }
            // Collection reconciliation and container recycling can finish on
            // the following layout turn. Clear once more after that work so a
            // collapsed stack header cannot donate IsSelected to a child.
            if (!DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    ResetSelectionForStackProjectionChange();
                    _stackProjectionTransitionPending = false;
                }))
            {
                _stackProjectionTransitionPending = false;
            }
        }))
        {
            _stackProjectionTransitionPending = false;
        }
    }

    private void ResetSelectionForStackProjectionChange()
    {
        ResetBoxSelectionState();
        _pendingPointerDragItems = [];
        _openedItemSelectionSuppression.Clear();
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
