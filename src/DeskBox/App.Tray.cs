// Copyright (c) DeskBox. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using DrawingPoint = System.Drawing.Point;

namespace DeskBox;

/// <summary>
/// Tray icon management: creation, context menu, positioning, tooltip, and layer toggle.
/// Extracted from App.xaml.cs to reduce God Class complexity.
/// </summary>
public partial class App
{
    private void CreateTrayIcon()
    {
        var localization = LocalizationService;
        var contextMenu = new MenuFlyout();
        contextMenu.ShouldConstrainToRootBounds = false;
        var organizeDesktopItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.OrganizeDesktop"),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "\uE74C" }
        };
        organizeDesktopItem.Loaded += OnTrayMenuVisualLoaded;
        organizeDesktopItem.Click += async (_, _) =>
            await RunTraySettingsActionAsync(
                contextMenu,
                OpenDesktopOrganizationFromTray);

        var mapFolderItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.NewFolderMapping"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        mapFolderItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, CreateFolderWidgetFromPickerAsync);

        var addFeatureWidgetItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.AddFeatureWidget"),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        addFeatureWidgetItem.Click += async (_, _) =>
            await RunTraySettingsActionAsync(
                contextMenu,
                OpenFeatureWidgetsFromTray);

        var settingsItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Settings"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Setting)
        };
        settingsItem.Click += async (_, _) => await RunTraySettingsActionAsync(contextMenu, OpenSettingsFromTray);

        var openManagedStorageItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.OpenManagedStorage"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Folder)
        };
        openManagedStorageItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, OpenManagedStorageFromTray);

        var updateItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.UpdateAvailable"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Download),
            Visibility = Visibility.Collapsed
        };
        updateItem.Click += async (_, _) => await RunTraySettingsActionAsync(contextMenu, OpenAboutSettingsFromTray);

        var exitItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Exit"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Cancel)
        };
        exitItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, ExitApplication);

        _trayCreateWidgetItems.Clear();
        contextMenu.Items.Add(organizeDesktopItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var descriptor in new WidgetContentFactory(LocalizationService).GetCreateEntryDescriptors())
        {
            var createItem = CreateTrayCreateWidgetItem(contextMenu, descriptor, localization);
            _trayCreateWidgetItems[descriptor.WidgetKind] = createItem;
            contextMenu.Items.Add(createItem);
        }

        contextMenu.Items.Add(mapFolderItem);
        contextMenu.Items.Add(addFeatureWidgetItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(openManagedStorageItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(updateItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(exitItem);

        _trayOrganizeDesktopItem = organizeDesktopItem;
        _trayMapFolderItem = mapFolderItem;
        _trayAddFeatureWidgetItem = addFeatureWidgetItem;
        _trayOpenManagedStorageItem = openManagedStorageItem;
        _trayUpdateItem = updateItem;
        _traySettingsItem = settingsItem;
        _trayExitItem = exitItem;
        _trayContextMenu = contextMenu;
        PrepareTrayContextMenu(contextMenu);

        _trayWindow = new Window();
        _trayWindow.AppWindow.IsShownInSwitchers = false;
        AppBranding.ApplyWindowIcon(_trayWindow.AppWindow);
        _trayWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));

        _trayIcon = new TaskbarIcon
        {
            Icon = AppBranding.CreateTrayIcon(SettingsService.Settings.TrayIconStyle ?? "System", IsDarkThemeActive()),
            ToolTipText = localization.T("Tray.Tooltip"),
            ContextMenuMode = ContextMenuMode.SecondWindow,
            MenuActivation = PopupActivationMode.None,
            NoLeftClickDelay = true,
            RightClickCommand = new RelayCommand(ShowTrayContextMenuFromTray),
            LeftClickCommand = new RelayCommand(() =>
            {
                if (WidgetManager is not null)
                {
                    SafeFireAndForget(() => ToggleTrayWidgetsAsync("tray-icon"));
                }
            })
        };
        _trayIcon.SecondWindowContextMenuOpened += OnSecondWindowTrayContextMenuOpened;
        _trayIcon.ContextFlyout = contextMenu;

        if (_trayWindow.Content is null)
        {
            _trayWindow.Content = new Grid
            {
                Width = 1,
                Height = 1,
                MinWidth = 1,
                MinHeight = 1,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
        }

        if (_trayWindow.Content is Panel panel)
        {
            panel.Width = 1;
            panel.Height = 1;
            panel.MinWidth = 1;
            panel.MinHeight = 1;
            panel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            panel.Children.Clear();
            panel.Children.Add(_trayIcon);
        }

        ThemeService.TrackWindow(_trayWindow);
        _trayWindow.Activate();

        if (!_trayIcon.IsCreated)
        {
            // Keep the process at normal QoS. Tray creation must never opt the
            // whole application into a lower-priority efficiency mode.
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        }

        try
        {
            var trayHwnd = WindowNative.GetWindowHandle(_trayWindow);
            Log($"[Init] Attaching GlobalHotkeyService to tray hwnd=0x{trayHwnd.ToInt64():X}");
            GlobalHotkeyService?.Attach(trayHwnd);
            Log("[Init] GlobalHotkeyService attached");
        }
        catch (Exception ex)
        {
            Log($"[Init] GlobalHotkeyService attach failed: {ex}");
        }

        _trayWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (_trayWindow is null)
            {
                return;
            }

            WindowExtensions.Hide(_trayWindow, enableEfficiencyMode: false);
        });

        ThemeService.AppearanceChanged += UpdateTrayIconAppearance;
    }

    private MenuFlyoutItem CreateTrayCreateWidgetItem(
        MenuFlyout contextMenu,
        WidgetContentDescriptor descriptor,
        LocalizationService localization)
    {
        var item = new MenuFlyoutItem
        {
            Text = GetCreateEntryText(descriptor, localization),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = descriptor.DefaultGlyph }
        };
        item.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, async () =>
        {
            if (WidgetManager is not null)
            {
                await WidgetManager.CreateWidgetOfKindAsync(descriptor.WidgetKind);
            }
        });
        return item;
    }

    private void PrepareTrayContextMenu(MenuFlyout contextMenu)
    {
        bool canCreateWidget = WidgetManager is not null;
        if (_trayOrganizeDesktopItem is not null)
        {
            _trayOrganizeDesktopItem.IsEnabled = canCreateWidget;
        }

        foreach (var item in _trayCreateWidgetItems.Values)
        {
            item.IsEnabled = canCreateWidget;
        }

        if (_trayMapFolderItem is not null)
        {
            _trayMapFolderItem.IsEnabled = canCreateWidget;
        }

        contextMenu.MenuFlyoutPresenterStyle = CreateTrayMenuPresenterStyle();
    }

    private Style CreateTrayMenuPresenterStyle()
    {
        var style = new Style(typeof(MenuFlyoutPresenter))
        {
            BasedOn = (Style)Resources[typeof(MenuFlyoutPresenter)]
        };
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        // Remove the default MaxHeight so every menu item is visible without
        // scrolling — the tray menu is short enough to always fit on screen.
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity));
        return style;
    }

    private void OnTrayMenuVisualLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItemBase anchorItem)
        {
            ApplySecondWindowTrayPresenterSettings(anchorItem);
        }
    }

    private void OnSecondWindowTrayContextMenuOpened(object? sender, EventArgs args)
    {
        if (_trayOrganizeDesktopItem is not null)
        {
            ApplySecondWindowTrayPresenterSettings(_trayOrganizeDesktopItem);
        }
    }

    private void ApplySecondWindowTrayPresenterSettings(MenuFlyoutItemBase anchorItem)
    {
        MenuFlyoutPresenter? presenter = FindVisualAncestor<MenuFlyoutPresenter>(anchorItem);
        if (presenter is null)
        {
            return;
        }

        presenter.SetValue(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled);
        presenter.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled);
        presenter.MaxHeight = double.PositiveInfinity;

        bool popupConfigured = ConfigureOwningPopup(presenter);
        if (!_traySecondWindowSyncLogged)
        {
            Log($"[Tray] Synchronized SecondWindow presenter through public visual tree; popup={popupConfigured}");
            _traySecondWindowSyncLogged = true;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static bool ConfigureOwningPopup(MenuFlyoutPresenter presenter)
    {
        XamlRoot? xamlRoot = presenter.XamlRoot;
        if (xamlRoot is null)
        {
            return false;
        }

        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup.Child is not null && ContainsVisual(popup.Child, presenter))
            {
                popup.ShouldConstrainToRootBounds = false;
                return true;
            }
        }

        return false;
    }

    private static bool ContainsVisual(DependencyObject root, DependencyObject target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            if (ContainsVisual(VisualTreeHelper.GetChild(root, index), target))
            {
                return true;
            }
        }

        return false;
    }

    private void ShowTrayContextMenuFromTray()
    {
        if (_trayIcon is null ||
            !Win32Helper.GetCursorPos(out var cursor))
        {
            return;
        }

        if (_trayContextMenu is not null)
        {
            PrepareTrayContextMenu(_trayContextMenu);
        }

        var point = new DrawingPoint(cursor.X, cursor.Y);
        try
        {
            point = GetTrayContextMenuAnchorPoint(point);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to calculate tray context menu anchor: {ex}");
        }

        try
        {
            _trayIcon.ShowContextMenu(point);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to show tray context menu: {ex}");
        }
    }

    internal void ShowTrayContextMenuForOnboarding()
    {
        ShowTrayContextMenuFromTray();
    }

    internal async Task<bool> ToggleWidgetsForOnboardingAsync()
    {
        await ToggleTrayWidgetsAsync("onboarding");
        return WidgetManager?.HasVisibleWidgets == true;
    }

    private DrawingPoint GetTrayContextMenuAnchorPoint(DrawingPoint fallbackPoint)
    {
        if (TryGetTrayIconIdentity(out var trayIconWindowHandle, out var trayIconId) &&
            Win32Helper.TryGetNotifyIconRect(trayIconWindowHandle, trayIconId, out var iconRect) &&
            IsUsableTrayIconRect(iconRect))
        {
            return GetTrayContextMenuAnchorPointFromIconRect(iconRect, fallbackPoint);
        }

        return GetFallbackTrayContextMenuAnchorPoint(fallbackPoint);
    }

    private bool TryGetTrayIconIdentity(out IntPtr windowHandle, out Guid id)
    {
        windowHandle = IntPtr.Zero;
        id = Guid.Empty;

        if (_trayIcon is null)
        {
            return false;
        }

        H.NotifyIcon.Core.TrayIcon trayIcon = _trayIcon.TrayIcon;
        windowHandle = trayIcon.WindowHandle;
        id = trayIcon.Id;

        return windowHandle != IntPtr.Zero && id != Guid.Empty;
    }

    private static DrawingPoint GetTrayContextMenuAnchorPointFromIconRect(
        Win32Helper.RECT iconRect,
        DrawingPoint fallbackPoint)
    {
        int centerX = iconRect.Left + ((iconRect.Right - iconRect.Left) / 2);
        int centerY = iconRect.Top + ((iconRect.Bottom - iconRect.Top) / 2);
        var anchor = new DrawingPoint(centerX - (TrayContextMenuEstimatedWidth / 2), centerY);

        if (!Win32Helper.TryGetMonitorWorkArea(centerX, centerY, out var monitor, out var workArea))
        {
            return anchor;
        }

        var edge = GetNearestTaskbarEdge(iconRect, monitor, workArea);
        anchor = edge switch
        {
            TaskbarEdge.Bottom => new DrawingPoint(anchor.X, workArea.Bottom - 1),
            TaskbarEdge.Top => new DrawingPoint(anchor.X, workArea.Top),
            TaskbarEdge.Right => new DrawingPoint(workArea.Right - 1, centerY),
            TaskbarEdge.Left => new DrawingPoint(workArea.Left, centerY),
            _ => GetFallbackTrayContextMenuAnchorPoint(fallbackPoint)
        };

        return ClampPointToRect(anchor, monitor);
    }

    private static DrawingPoint GetFallbackTrayContextMenuAnchorPoint(DrawingPoint point)
    {
        if (!Win32Helper.TryGetMonitorWorkArea(point.X, point.Y, out var monitor, out var workArea))
        {
            return new DrawingPoint(
                point.X - (TrayContextMenuEstimatedWidth / 2),
                point.Y - TrayContextMenuFallbackOffsetPixels);
        }

        int x = point.X - (TrayContextMenuEstimatedWidth / 2);
        int y = point.Y;
        bool moved = false;

        if (workArea.Bottom < monitor.Bottom && y >= workArea.Bottom)
        {
            y = workArea.Bottom - 1;
            moved = true;
        }
        else if (workArea.Top > monitor.Top && y <= workArea.Top)
        {
            y = workArea.Top;
            moved = true;
        }

        if (workArea.Right < monitor.Right && x >= workArea.Right)
        {
            x = workArea.Right - 1;
            moved = true;
        }
        else if (workArea.Left > monitor.Left && x <= workArea.Left)
        {
            x = workArea.Left;
            moved = true;
        }

        if (!moved)
        {
            int distanceToBottom = Math.Abs(monitor.Bottom - y);
            int distanceToTop = Math.Abs(y - monitor.Top);
            int distanceToRight = Math.Abs(monitor.Right - x);
            int distanceToLeft = Math.Abs(x - monitor.Left);
            int nearestDistance = Math.Min(
                Math.Min(distanceToBottom, distanceToTop),
                Math.Min(distanceToRight, distanceToLeft));

            if (nearestDistance == distanceToBottom)
            {
                y -= TrayContextMenuFallbackOffsetPixels;
            }
            else if (nearestDistance == distanceToTop)
            {
                y += TrayContextMenuFallbackOffsetPixels;
            }
            else if (nearestDistance == distanceToRight)
            {
                x -= TrayContextMenuFallbackOffsetPixels;
            }
            else
            {
                x += TrayContextMenuFallbackOffsetPixels;
            }
        }

        return ClampPointToRect(new DrawingPoint(x, y), monitor);
    }

    private static bool IsUsableTrayIconRect(Win32Helper.RECT rect)
    {
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static TaskbarEdge GetNearestTaskbarEdge(
        Win32Helper.RECT iconRect,
        Win32Helper.RECT monitor,
        Win32Helper.RECT workArea)
    {
        if (workArea.Bottom < monitor.Bottom &&
            iconRect.Top >= workArea.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (workArea.Top > monitor.Top &&
            iconRect.Bottom <= workArea.Top)
        {
            return TaskbarEdge.Top;
        }

        if (workArea.Right < monitor.Right &&
            iconRect.Left >= workArea.Right)
        {
            return TaskbarEdge.Right;
        }

        if (workArea.Left > monitor.Left &&
            iconRect.Right <= workArea.Left)
        {
            return TaskbarEdge.Left;
        }

        int distanceToBottom = Math.Abs(monitor.Bottom - iconRect.Bottom);
        int distanceToTop = Math.Abs(iconRect.Top - monitor.Top);
        int distanceToRight = Math.Abs(monitor.Right - iconRect.Right);
        int distanceToLeft = Math.Abs(iconRect.Left - monitor.Left);
        int nearestDistance = Math.Min(
            Math.Min(distanceToBottom, distanceToTop),
            Math.Min(distanceToRight, distanceToLeft));

        if (nearestDistance == distanceToBottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (nearestDistance == distanceToTop)
        {
            return TaskbarEdge.Top;
        }

        return nearestDistance == distanceToRight
            ? TaskbarEdge.Right
            : TaskbarEdge.Left;
    }

    private static DrawingPoint ClampPointToRect(DrawingPoint point, Win32Helper.RECT rect)
    {
        return new DrawingPoint(
            Math.Clamp(point.X, rect.Left, rect.Right - 1),
            Math.Clamp(point.Y, rect.Top, rect.Bottom - 1));
    }

    private enum TaskbarEdge
    {
        Bottom,
        Top,
        Right,
        Left
    }

    private async Task RaiseTrayWidgetsAsync()
    {
        if (WidgetManager is null)
        {
            return;
        }

        bool? raised = await WidgetManager.RaiseWidgetsFromTrayAsync();
        if (raised.HasValue)
        {
            UpdateTrayLayerStateText(raised.Value);
        }
    }

    private async Task ToggleTrayWidgetsAsync(string source = "tray-toggle")
    {
        if (WidgetManager is null)
        {
            return;
        }

        await WidgetManager.ToggleWidgetsFromTrayAsync(source);
        UpdateTrayLayerStateText(WidgetManager.WidgetsRaisedFromTray);
        if (_onboardingWindow is not null)
        {
            OnboardingWidgetsVisibilityChanged?.Invoke(WidgetManager.HasVisibleWidgets);
        }
    }

    private async Task ToggleWidgetsFromDesktopDoubleClickAsync(
        DesktopDoubleClickSequence sequence)
    {
        if (WidgetManager is null)
        {
            return;
        }

        if (WidgetManager.ConsumeQuickRevealDesktopDoubleClickDismiss(sequence))
        {
            Log(
                "[DesktopDoubleClick] Consumed matching quick-reveal outside dismissal");
            return;
        }

        Log("[DesktopDoubleClick] Matched blank desktop sequence action=toggle");
        await ToggleTrayWidgetsAsync("desktop-double-click");
    }

    private void UpdateTrayLayerStateText(bool raised)
    {
        _widgetsRaisedFromTray = raised;
        RefreshTrayToolTipText();
    }

    private void RefreshTrayMenuText()
    {
        if (_trayOrganizeDesktopItem is not null)
        {
            _trayOrganizeDesktopItem.Text = LocalizationService.T(
                "Tray.OrganizeDesktop");
        }

        if (_trayMapFolderItem is not null)
        {
            _trayMapFolderItem.Text = LocalizationService.T("Common.NewFolderMapping");
        }

        if (_trayAddFeatureWidgetItem is not null)
        {
            _trayAddFeatureWidgetItem.Text = LocalizationService.T("Common.AddFeatureWidget");
        }

        foreach (var (widgetKind, item) in _trayCreateWidgetItems)
        {
            var descriptor = new WidgetContentFactory(LocalizationService).GetDescriptor(widgetKind);
            item.Text = GetCreateEntryText(descriptor, LocalizationService);
        }

        if (_traySettingsItem is not null)
        {
            _traySettingsItem.Text = LocalizationService.T("Tray.Settings");
        }

        if (_trayUpdateItem is not null)
        {
            _trayUpdateItem.Text = string.IsNullOrWhiteSpace(_availableUpdateVersion)
                ? LocalizationService.T("Tray.UpdateAvailable")
                : LocalizationService.Format("Tray.UpdateAvailableWithVersion", _availableUpdateVersion);
            _trayUpdateItem.Visibility = _hasUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_trayOpenManagedStorageItem is not null)
        {
            _trayOpenManagedStorageItem.Text = LocalizationService.T("Tray.OpenManagedStorage");
        }

        if (_trayExitItem is not null)
        {
            _trayExitItem.Text = LocalizationService.T("Tray.Exit");
        }
    }

    private void RefreshTrayToolTipText()
    {
        if (_trayIcon is null)
        {
            return;
        }

        if (_hasUpdateAvailable && !string.IsNullOrWhiteSpace(_availableUpdateVersion))
        {
            _trayIcon.ToolTipText = LocalizationService.Format("Tray.TooltipUpdateAvailable", _availableUpdateVersion);
            return;
        }

        _trayIcon.ToolTipText = _widgetsRaisedFromTray
            ? LocalizationService.T("Tray.TooltipRaised")
            : LocalizationService.T("Tray.Tooltip");
    }

    private static string GetCreateEntryText(WidgetContentDescriptor descriptor, LocalizationService localization)
    {
        return string.IsNullOrWhiteSpace(descriptor.CreateEntryTextKey)
            ? descriptor.DefaultTitle
            : localization.T(descriptor.CreateEntryTextKey);
    }

    private static async Task RunTrayMenuActionAsync(MenuFlyout contextMenu, Action action)
    {
        contextMenu.Hide();
        await Task.Yield();
        action();
    }

    private static async Task RunTrayMenuActionAsync(MenuFlyout contextMenu, Func<Task> action)
    {
        contextMenu.Hide();
        await Task.Yield();
        await action();
    }

    private async Task RunTraySettingsActionAsync(MenuFlyout contextMenu, Action action)
    {
        var widgetManager = WidgetManager;
        widgetManager?.BeginWidgetInteraction("tray-settings-opening");
        try
        {
            await RunTrayMenuActionAsync(contextMenu, action);
            // The tray flyout is hosted by a helper window. Keep the raised-widget
            // session stable while that window closes and Settings takes foreground.
            await Task.Delay(300);
        }
        finally
        {
            widgetManager?.EndWidgetInteraction("tray-settings-opened");
        }
    }

    internal async Task CreateFolderWidgetFromPickerAsync()
    {
        if (WidgetManager is null)
        {
            return;
        }

        string? folderPath = await FolderPickerService.PickFolderAsync(
            GetFolderPickerOwnerWindowHandle());
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            try
            {
                await WidgetManager.CreateFolderWidgetAsync(folderPath);
            }
            catch (Exception ex)
            {
                string title = LocalizationService.T("Common.NewFolderMapping");
                if (_nativeNotificationService?.TryShow(title, ex.Message) == true || _trayIcon is null)
                {
                    return;
                }

                try
                {
                    _trayIcon.ShowNotification(
                        title,
                        ex.Message,
                        NotificationIcon.Info,
                        customIconHandle: null,
                        largeIcon: false,
                        sound: false,
                        respectQuietTime: true,
                        realtime: false,
                        timeout: TimeSpan.FromSeconds(7));
                }
                catch (Exception notificationException)
                {
                    Log($"[WidgetMapping] Failed to show path conflict: {notificationException.Message}");
                }
            }
        }
    }

    internal IntPtr GetFolderPickerOwnerWindowHandle()
    {
        if (_trayWindow is null)
        {
            throw new InvalidOperationException("The tray owner window has not been created.");
        }

        IntPtr ownerHwnd = WindowNative.GetWindowHandle(_trayWindow);
        if (ownerHwnd == IntPtr.Zero || !Win32Helper.IsWindow(ownerHwnd))
        {
            throw new InvalidOperationException("The tray owner window handle is unavailable.");
        }

        return ownerHwnd;
    }

    private void OpenSettingsFromTray()
    {
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
    }

    private void OpenFeatureWidgetsFromTray()
    {
        ShowSettings("FeatureWidgets");
    }

    private void OpenDesktopOrganizationFromTray()
    {
        CancelBackgroundMemoryCleanup();
        ShowDesktopOrganizationWindow();
    }

    private void OpenAboutSettingsFromTray()
    {
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        settingsWindow.ShowSection("About");
    }

    private void OpenManagedStorageFromTray()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(SettingsService.Settings.DefaultManagedStorageRootPath);
        Directory.CreateDirectory(path);
        Win32Helper.OpenFile(path);
    }

    private bool IsDarkThemeActive()
    {
        return Win32Helper.IsSystemDarkMode();
    }

    private void UpdateTrayIconAppearance()
    {
        if (_trayIcon is null)
        {
            return;
        }

        string style = SettingsService.Settings.TrayIconStyle ?? "System";
        _trayIcon.Icon = AppBranding.CreateTrayIcon(style, IsDarkThemeActive());
    }

    public void UpdateTrayIcon()
    {
        UpdateTrayIconAppearance();
    }
}
