// Copyright (c) DeskBox. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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
        AttachTrayMenuItemInteraction(
            organizeDesktopItem,
            () => RunTraySettingsActionAsync(
                contextMenu,
                OpenDesktopOrganizationFromTray));

        var mapFolderItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.NewFolderMapping"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        AttachTrayMenuItemInteraction(
            mapFolderItem,
            () => RunTrayMenuActionAsync(contextMenu, CreateFolderWidgetFromPickerAsync));

        var addFeatureWidgetItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.AddFeatureWidget"),
            Width = TrayMenuItemWidth,
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        AttachTrayMenuItemInteraction(
            addFeatureWidgetItem,
            () => RunTraySettingsActionAsync(
                contextMenu,
                OpenFeatureWidgetsFromTray));

        var settingsItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Settings"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Setting)
        };
        AttachTrayMenuItemInteraction(
            settingsItem,
            () => RunTraySettingsActionAsync(contextMenu, OpenSettingsFromTray));

        var openManagedStorageItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.OpenManagedStorage"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Folder)
        };
        AttachTrayMenuItemInteraction(
            openManagedStorageItem,
            () => RunTrayMenuActionAsync(contextMenu, OpenManagedStorageFromTray));

        var updateItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.UpdateAvailable"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Download),
            Visibility = Visibility.Collapsed
        };
        AttachTrayMenuItemInteraction(
            updateItem,
            () => RunTraySettingsActionAsync(contextMenu, OpenAboutSettingsFromTray));

        var exitItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Exit"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Cancel)
        };
        AttachTrayMenuItemInteraction(
            exitItem,
            () => RunTrayMenuActionAsync(contextMenu, ExitApplication));

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

        _trayWindow = new Window { Title = "DeskBox Tray Host" };
        AppBranding.ApplyWindowIcon(_trayWindow.AppWindow);
        // Early-logon sessions can reject these windowing calls (E_NOTIMPL is
        // observed on IsShownInSwitchers); a 1x1 host window that leaks into
        // Alt+Tab is the acceptable degraded state, failing startup is not.
        WindowShellState.TryHideFromSwitchers(_trayWindow.AppWindow);
        try
        {
            _trayWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        }
        catch (Exception ex)
        {
            Log($"[Tray] Tray window resize not applied: {ex.Message}");
        }

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
                    SafeFireAndForget(() => ToggleTrayWidgetsAsync(
                        DeskBox.Services.TrayToggleRequestQueue.TrayIconSource));
                }
            })
        };
        _trayIcon.SecondWindowContextMenuOpened += OnSecondWindowTrayContextMenuOpened;
        // The flyout setter synchronously builds H.NotifyIcon's second window,
        // which early-logon sessions can reject with E_NOTIMPL before the
        // shell desktop is ready. Retry on the tray creation cadence and fall
        // back to the native menu instead of failing startup (feedback 343).
        if (!TryAssignTrayContextFlyout())
        {
            SafeFireAndForget(() => RetryTrayContextFlyoutAssignmentAsync());
        }

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

        try
        {
            ThemeService.TrackWindow(_trayWindow);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Tray window theme tracking not applied: {ex.Message}");
        }

        try
        {
            // The tray icon is created independently of this window, so a
            // failed activation only costs the hidden host window's state.
            _trayWindow.Activate();
        }
        catch (Exception ex)
        {
            Log($"[Tray] Tray window activation not applied: {ex.Message}");
        }

        StartTrayIconCreationWithRetry();

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

    /// <summary>Attempts per tray creation pass through the early-logon window
    /// where the taskbar may not exist yet (roughly the first 30 seconds after
    /// logon in the field reports).</summary>
    private const int TrayCreationMaxAttempts = 15;

    private static readonly TimeSpan TrayCreationRetryDelay = TimeSpan.FromSeconds(2);

    private Task<bool>? _trayIconCreationTask;

    /// <summary>
    /// Creates the tray icon, retrying through the early-logon race that makes
    /// the first attempt fail (H.NotifyIcon TryCreate failures and E_NOTIMPL
    /// windowing calls both recover once the shell finishes starting). The
    /// first attempt runs inline so the normal path is unchanged; later
    /// attempts continue on the UI thread while the rest of startup proceeds.
    /// </summary>
    private Task<bool> StartTrayIconCreationWithRetry()
    {
        _trayIconCreationTask ??= CreateTrayIconSurfaceWithRetryAsync();
        return _trayIconCreationTask;
    }

    private async Task<bool> CreateTrayIconSurfaceWithRetryAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (_trayIcon is { IsCreated: false })
                {
                    // Keep the process at normal QoS. Tray creation must never
                    // opt the whole application into a lower-priority
                    // efficiency mode.
                    _trayIcon.ForceCreate(enablesEfficiencyMode: false);
                }

                if (IsTraySurfaceUsable())
                {
                    MarkStartupLifelineEstablished();
                    if (attempt > 1)
                    {
                        Log($"[Tray] Tray surface created on retry attempt {attempt}");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"[Tray] Tray creation attempt {attempt} failed: {ex.Message}");
            }

            if (attempt >= TrayCreationMaxAttempts)
            {
                Log(
                    "[Tray] Tray surface was not created after " +
                    $"{attempt} attempts; continuing without the tray icon");
                return false;
            }

            await Task.Delay(TrayCreationRetryDelay);
        }
    }

    /// <summary>
    /// Mirrors the tray surface checks the AOT smoke harness pins: the icon
    /// must exist, own a message window, and have a real host window. Once this
    /// holds, startup has something the user can act on and exceptions stop
    /// being fatal.
    /// </summary>
    private bool IsTraySurfaceUsable()
    {
        try
        {
            return _trayIcon is { IsCreated: true } trayIcon &&
                trayIcon.TrayIcon.WindowHandle != IntPtr.Zero &&
                _trayWindow is not null &&
                WindowNative.GetWindowHandle(_trayWindow) != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log($"[Tray] Tray surface probe failed: {ex.Message}");
            return false;
        }
    }

    private bool TryAssignTrayContextFlyout()
    {
        try
        {
            if (_trayIcon is null)
            {
                return false;
            }

            _trayIcon.ContextFlyout = _trayContextMenu;
            return true;
        }
        catch (Exception ex)
        {
            Log($"[Tray] Tray context flyout assignment failed: {ex.Message}");
            _trayContextMenuModePolicy.RecordFailure();
            return false;
        }
    }

    private async Task RetryTrayContextFlyoutAssignmentAsync()
    {
        for (int attempt = 2; attempt <= TrayCreationMaxAttempts; attempt++)
        {
            await Task.Delay(TrayCreationRetryDelay);
            if (_trayIcon is not { } trayIcon)
            {
                return;
            }

            if (TryAssignTrayContextFlyout())
            {
                Log($"[Tray] Tray context flyout assigned on retry attempt {attempt}");
                _trayContextMenuModePolicy.RecordSuccess();
                return;
            }

            if (_trayContextMenuModePolicy.IsDegradedToPopupMenu)
            {
                try
                {
                    SwitchTrayContextMenuToNativeMode(trayIcon);
                }
                catch (Exception ex)
                {
                    Log($"[Tray] Failed to degrade tray context menu to native PopupMenu mode: {ex.Message}");
                }

                return;
            }
        }
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
        AttachTrayMenuItemInteraction(item, () => RunTrayMenuActionAsync(contextMenu, async () =>
        {
            if (WidgetManager is not null)
            {
                await WidgetManager.CreateWidgetOfKindAsync(descriptor.WidgetKind);
            }
        }));
        return item;
    }

    /// <summary>
    /// Wires one tray menu item so both invocation paths work: the XAML click
    /// path (SecondWindow flyout mode) and the command path (the library's
    /// native PopupMenu fallback only executes Command, never Click). WinUI
    /// raises Click and executes the command back-to-back on the same
    /// dispatch, so a per-item gate keeps each interaction single-shot.
    /// </summary>
    private static void AttachTrayMenuItemInteraction(MenuFlyoutItem item, Func<Task> interaction)
    {
        var gate = new TrayMenuInteractionGate(action => UiDispatcherQueue.TryEnqueue(() => action()));
        async Task RunInteractionOnceAsync()
        {
            if (!gate.TryEnter())
            {
                return;
            }

            await interaction();
        }

        item.Click += async (_, _) => await RunInteractionOnceAsync();
        item.Command = new RelayCommand(async () => await RunInteractionOnceAsync());
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

    private readonly TrayContextMenuModePolicy _trayContextMenuModePolicy = new();

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

        if (_trayContextMenuModePolicy.IsDegradedToPopupMenu)
        {
            ShowTrayContextMenuInNativeMode(point);
            return;
        }

        try
        {
            _trayIcon.ShowContextMenu(point);
            _trayContextMenuModePolicy.RecordSuccess();
        }
        catch (Exception ex) when (IsSecondWindowHostFailure(ex))
        {
            Log($"[Tray] Tray context menu SecondWindow host failed: {ex.Message}");
            RecoverTrayContextMenuAfterSecondWindowFailure(point);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to show tray context menu: {ex}");
        }
    }

    private static bool IsSecondWindowHostFailure(Exception exception)
    {
        // H.NotifyIcon 2.5.0-beta.1's second window can fail its prewarm race
        // (Activate is synchronously followed by SW_HIDE, so Loaded never
        // fires), leaving the host frame without a XamlRoot; every flyout
        // ShowAt then throws this ArgumentException.
        return exception is ArgumentException &&
            exception.Message.Contains("XamlRoot", StringComparison.Ordinal);
    }

    private void ShowTrayContextMenuInNativeMode(DrawingPoint point)
    {
        if (_trayIcon is not { } trayIcon)
        {
            return;
        }

        // ContextMenuMode is already PopupMenu, so the same public call routes
        // to the library's native menu without touching the second-window host.
        try
        {
            trayIcon.ShowContextMenu(point);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to show native tray context menu: {ex.Message}");
        }
    }

    private void RecoverTrayContextMenuAfterSecondWindowFailure(DrawingPoint point)
    {
        if (_trayIcon is not { } trayIcon)
        {
            return;
        }

        if (_trayContextMenuModePolicy.RecordFailure())
        {
            DegradeTrayContextMenuToNativeMode(trayIcon, point);
            return;
        }

        try
        {
            // Re-assigning the flyout makes the library rebuild its hidden
            // second window from scratch; the replaced window leaks once,
            // which beta.1 makes unavoidable for this one-shot retry.
            trayIcon.ContextFlyout = null;
            trayIcon.ContextFlyout = _trayContextMenu;
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to rebuild tray context menu SecondWindow host: {ex.Message}");
            if (_trayContextMenuModePolicy.RecordFailure())
            {
                DegradeTrayContextMenuToNativeMode(trayIcon, point);
            }

            return;
        }

        try
        {
            trayIcon.ShowContextMenu(point);
            _trayContextMenuModePolicy.RecordSuccess();
        }
        catch (Exception ex) when (IsSecondWindowHostFailure(ex))
        {
            Log($"[Tray] Tray context menu SecondWindow host failed after rebuild: {ex.Message}");
            if (_trayContextMenuModePolicy.RecordFailure())
            {
                DegradeTrayContextMenuToNativeMode(trayIcon, point);
            }
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to show tray context menu after host rebuild: {ex.Message}");
        }
    }

    private void DegradeTrayContextMenuToNativeMode(TaskbarIcon trayIcon, DrawingPoint point)
    {
        try
        {
            SwitchTrayContextMenuToNativeMode(trayIcon);
            ShowTrayContextMenuInNativeMode(point);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to degrade tray context menu to native PopupMenu mode: {ex.Message}");
        }
    }

    private void SwitchTrayContextMenuToNativeMode(TaskbarIcon trayIcon)
    {
        trayIcon.ContextMenuMode = ContextMenuMode.PopupMenu;
        Log("[Tray] Tray context menu degraded to native PopupMenu mode (SecondWindow host failed to initialize)");
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
            : LocalizationService.T("Tray.TooltipNormal");
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
            // In degraded PopupMenu mode the menu is a native modal, so there is
            // no helper window to wait for.
            if (!_trayContextMenuModePolicy.IsDegradedToPopupMenu)
            {
                await Task.Delay(300);
            }
        }
        finally
        {
            widgetManager?.EndWidgetInteraction("tray-settings-opened");
        }
    }

    internal async Task CreateFolderWidgetFromPickerAsync()
    {
        string? folderPath = await FolderPickerService.PickFolderAsync(
            GetFolderPickerOwnerWindowHandle());
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            await TryCreateFolderWidgetAsync(folderPath);
        }
    }

    /// <summary>
    /// Creates a mapped widget, or resolves a path conflict visibly: the
    /// conflicting widget is revealed, or the storage settings open. A toast
    /// alone was easy to miss and left users thinking mapping was broken.
    /// </summary>
    internal async Task TryCreateFolderWidgetAsync(string folderPath)
    {
        if (WidgetManager is not { } widgetManager)
        {
            return;
        }

        if (widgetManager.TryGetFileWidgetPathConflict(folderPath, out FileWidgetPathConflict? conflict) &&
            conflict is not null)
        {
            await HandleFolderMappingConflictAsync(conflict);
            return;
        }

        try
        {
            await widgetManager.CreateFolderWidgetAsync(folderPath);
        }
        catch (Exception ex)
        {
            ShowFolderMappingFailure(ex.Message);
        }
    }

    private async Task HandleFolderMappingConflictAsync(FileWidgetPathConflict conflict)
    {
        if (conflict.Kind == FileWidgetPathConflictKind.ExistingWidget &&
            conflict.ConflictingWidget is { } existing &&
            WidgetManager is { } widgetManager)
        {
            ShowFolderMappingFailure(LocalizationService.Format(
                "Widget.MapFolder.ConflictWidgetHint",
                existing.Name));
            try
            {
                await widgetManager.ShowWidgetAsync(existing.Id, reveal: true);
            }
            catch (Exception ex)
            {
                Log($"[WidgetMapping] Could not reveal the conflicting widget: {ex.Message}");
            }

            return;
        }

        ShowFolderMappingFailure(LocalizationService.T("Widget.MapFolder.ConflictRootHint"));
        ShowSettings("FileStorageSettings");
    }

    private void ShowFolderMappingFailure(string message)
    {
        string title = LocalizationService.T("Common.NewFolderMapping");
        if (_nativeNotificationService?.TryShow(title, message) == true || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
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

    internal IntPtr GetFolderPickerOwnerWindowHandle()
    {
        // A picker dialog follows its owner's virtual desktop and DWM cloak
        // state: when the tray host stays behind on a desktop the user has
        // switched away from, it is shell-cloaked and a dialog owned by it
        // never composes (issue 489). Prefer the tray host while it is
        // usable, then a window that is demonstrably on the active desktop,
        // and recreate the hidden helper as a last resort — a fresh window
        // always lands on the active desktop.
        IntPtr trayHwnd = _trayWindow is null
            ? IntPtr.Zero
            : WindowNative.GetWindowHandle(_trayWindow);
        IntPtr settingsHwnd = _settingsWindow is null
            ? IntPtr.Zero
            : WindowNative.GetWindowHandle(_settingsWindow);
        IntPtr helperHwnd = _folderPickerHelperWindow is null
            ? IntPtr.Zero
            : WindowNative.GetWindowHandle(_folderPickerHelperWindow);

        return PickerOwnerResolutionPolicy.Resolve(
            IsUsablePickerOwner(trayHwnd),
            IsUsablePickerOwner(settingsHwnd) &&
                Win32Helper.IsWindowVisible(settingsHwnd),
            IsUsablePickerOwner(helperHwnd)) switch
        {
            PickerOwnerSource.TrayWindow => trayHwnd,
            PickerOwnerSource.SettingsWindow => settingsHwnd,
            PickerOwnerSource.CachedHelperWindow => helperHwnd,
            _ => RecreateFolderPickerHelperWindow()
        };
    }

    private static bool IsUsablePickerOwner(IntPtr ownerHwnd)
    {
        return ownerHwnd != IntPtr.Zero &&
            Win32Helper.IsWindow(ownerHwnd) &&
            !Win32Helper.IsWindowCloaked(ownerHwnd);
    }

    private IntPtr RecreateFolderPickerHelperWindow()
    {
        if (_folderPickerHelperWindow is not null)
        {
            try
            {
                _folderPickerHelperWindow.Close();
            }
            catch (Exception ex)
            {
                Log($"[FolderPicker] Stale helper window close failed: {ex.Message}");
            }

            _folderPickerHelperWindow = null;
        }

        // Same shape as the tray host (1x1, out of the switcher), never
        // activated: the native hwnd exists from construction, and creating
        // the window now assigns it to the active virtual desktop.
        var helper = new Window { Title = "DeskBox Picker Host" };
        WindowShellState.TryHideFromSwitchers(helper.AppWindow);
        try
        {
            helper.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        }
        catch (Exception ex)
        {
            Log($"[FolderPicker] Helper window resize not applied: {ex.Message}");
        }

        _folderPickerHelperWindow = helper;
        return WindowNative.GetWindowHandle(helper);
    }

    private void OpenSettingsFromTray()
    {
        Log("[Tray] Settings selected in primary instance");
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

/// <summary>
/// Session state for the tray context menu's SecondWindow-to-PopupMenu
/// fallback: consecutive host failures are counted across right-clicks, a
/// successful open resets the count, and reaching the threshold degrades the
/// session permanently — the native menu is the terminal escape hatch.
/// </summary>
internal sealed class TrayContextMenuModePolicy
{
    internal const int DegradationFailureThreshold = 2;

    private int _consecutiveSecondWindowFailures;
    private bool _degradedToPopupMenu;

    internal int ConsecutiveSecondWindowFailures => _consecutiveSecondWindowFailures;

    internal bool IsDegradedToPopupMenu => _degradedToPopupMenu;

    /// <summary>Resets the failure count after a successful open. Degradation
    /// itself is terminal for the session.</summary>
    internal void RecordSuccess()
    {
        _consecutiveSecondWindowFailures = 0;
    }

    /// <summary>
    /// Records a second-window host failure and reports whether this call
    /// crossed the degradation threshold.
    /// </summary>
    internal bool RecordFailure()
    {
        _consecutiveSecondWindowFailures++;
        if (_degradedToPopupMenu ||
            _consecutiveSecondWindowFailures < DegradationFailureThreshold)
        {
            return false;
        }

        _degradedToPopupMenu = true;
        return true;
    }
}

/// <summary>
/// Single-shot gate for one tray menu item interaction. In SecondWindow mode
/// WinUI raises the item's Click event and executes its command back-to-back
/// on the same dispatch, while the native PopupMenu fallback only executes
/// the command. The gate lets the first path win and re-arms on the next
/// dispatcher pass so the next interaction works again.
/// </summary>
internal sealed class TrayMenuInteractionGate
{
    private readonly Action<Action> _scheduleReset;
    private int _entered;

    internal TrayMenuInteractionGate(Action<Action> scheduleReset)
    {
        _scheduleReset = scheduleReset;
    }

    /// <summary>true for the first caller of the current interaction.</summary>
    internal bool TryEnter()
    {
        if (Interlocked.Exchange(ref _entered, 1) != 0)
        {
            return false;
        }

        _scheduleReset(Reset);
        return true;
    }

    internal void Reset()
    {
        _entered = 0;
    }
}
