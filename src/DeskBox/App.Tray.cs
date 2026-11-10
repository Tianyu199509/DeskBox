// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using DrawingPoint = System.Drawing.Point;

namespace DeskBox;

/// <summary>
/// Pure Win32 tray icon management: creation, native themed popup menu,
/// positioning, tooltip, balloon fallback, and layer toggle.
/// </summary>
public partial class App
{
    /// <summary>Tray icon style that keeps the notification-area icon absent
    /// for the whole session (see <see cref="IsTrayIconHiddenByUserSetting"/>).</summary>
    private const string TrayIconStyleHidden = "Hidden";

    private const int TrayCommandOrganizeDesktop = 1;
    private const int TrayCommandMapFolder = 2;
    private const int TrayCommandAddFeatureWidget = 3;
    private const int TrayCommandOpenManagedStorage = 4;
    private const int TrayCommandUpdateAvailable = 5;
    private const int TrayCommandSettings = 6;
    private const int TrayCommandExit = 7;
    private const int TrayCommandCreateAiWidget = 8;
    private const int TrayCommandCreateWidgetFirst = 100;

    internal IntPtr TrayHostWindowHandle => _trayHost?.WindowHandle ?? IntPtr.Zero;

    // Reads the Core slice, not the AppSettings facade: the facade-access
    // ratchet forbids new passthrough reads (SettingsSliceOwnershipContract).
    private bool IsTrayIconHiddenByUserSetting() =>
        SettingsService.Settings.Core.TrayIconStyle == TrayIconStyleHidden;

    private void CreateTrayIcon()
    {
        _trayHost?.Dispose();
        _trayHost = new NativeTrayIconHost("DeskBox Tray Host");
        _trayHost.SetIconPath(AppBranding.ResolveTrayIconPath(
            SettingsService.Settings.Core.TrayIconStyle ?? "System",
            IsDarkThemeActive()));
        RefreshTrayToolTipText();

        _trayHost.RightClickRequested += ShowTrayContextMenuFromTray;
        _trayHost.LeftClickRequested += OnTrayLeftClickRequested;

        if (IsTrayIconHiddenByUserSetting())
        {
            _trayHost.DesiredVisible = false;
            Log("[Tray] Tray icon hidden by user setting; skipping icon creation");
        }
        else
        {
            _trayHost.DesiredVisible = true;
            StartTrayIconCreationWithRetry();
        }

        try
        {
            if (GlobalHotkeyService is { } globalHotkeyService)
            {
                IntPtr trayHwnd = TrayHostWindowHandle;
                Log($"[Init] Attaching GlobalHotkeyService to tray hwnd=0x{trayHwnd.ToInt64():X}");
                globalHotkeyService.Attach(trayHwnd);
                Log("[Init] GlobalHotkeyService attached");
            }
        }
        catch (Exception ex)
        {
            Log($"[Init] GlobalHotkeyService attach failed: {ex}");
        }

        ThemeService.AppearanceChanged -= UpdateTrayIconAppearance;
        ThemeService.AppearanceChanged += UpdateTrayIconAppearance;
    }

    private void OnTrayLeftClickRequested()
    {
        TrayContextMenuWindow.DismissActiveIfOpen();
        if (WidgetManager is not null)
        {
            SafeFireAndForget(() => ToggleTrayWidgetsAsync(
                DeskBox.Services.TrayToggleRequestQueue.TrayIconSource));
        }
    }

    /// <summary>Attempts per tray creation pass through the early-logon window
    /// where the taskbar may not exist yet (roughly the first 30 seconds after
    /// logon in the field reports).</summary>
    private const int TrayCreationMaxAttempts = 15;

    private static readonly TimeSpan TrayCreationRetryDelay = TimeSpan.FromSeconds(2);

    private Task<bool>? _trayIconCreationTask;

    /// <summary>
    /// Creates the tray icon, retrying through the early-logon race where
    /// Shell_NotifyIconW fails before Explorer's taskbar finishes starting.
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
                if (IsTrayIconHiddenByUserSetting())
                {
                    if (_trayHost is not null)
                    {
                        _trayHost.DesiredVisible = false;
                    }

                    Log("[Tray] Tray icon hidden by user setting; creation retry stopped");
                    return false;
                }

                if (_trayHost is { IsCreated: false })
                {
                    _ = _trayHost.TryCreateIcon();
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
    /// must be registered with the shell and own a live Win32 host window.
    /// </summary>
    private bool IsTraySurfaceUsable()
    {
        try
        {
            return _trayHost is { IsCreated: true } trayHost &&
                trayHost.WindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindow(trayHost.WindowHandle);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Tray surface probe failed: {ex.Message}");
            return false;
        }
    }

    internal IReadOnlyList<NativeTrayMenuEntry> BuildTrayMenuEntries()
    {
        bool canCreateWidget = WidgetManager is not null;
        return BuildTrayMenuEntriesCore(
            LocalizationService,
            canCreateWidget,
            _hasUpdateAvailable,
            _availableUpdateVersion,
            () => RunTraySettingsActionAsync(OpenDesktopOrganizationFromTray),
            kind => RunTrayMenuActionAsync(async () =>
            {
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateWidgetOfKindAsync(kind);
                }
            }),
            () => RunTrayMenuActionAsync(() => CreateFolderWidgetFromPickerAsync()),
            () => RunTraySettingsActionAsync(OpenFeatureWidgetsFromTray),
            () => RunTrayMenuActionAsync(OpenManagedStorageFromTray),
            () => RunTraySettingsActionAsync(OpenAboutSettingsFromTray),
            () => RunTraySettingsActionAsync(OpenSettingsFromTray),
            () => RunTrayMenuActionAsync(ExitApplication),
            () => RunTraySettingsActionAsync(OpenAiCreateStudioFromTray));
    }

    internal static IReadOnlyList<NativeTrayMenuEntry> BuildTrayMenuEntriesCore(
        LocalizationService localization,
        bool canCreateWidget,
        bool hasUpdateAvailable,
        string? availableUpdateVersion,
        Func<Task> onOrganizeDesktop,
        Func<WidgetKind, Task> onCreateWidgetOfKind,
        Func<Task> onMapFolder,
        Func<Task> onAddFeatureWidget,
        Func<Task> onOpenManagedStorage,
        Func<Task> onOpenUpdate,
        Func<Task> onOpenSettings,
        Func<Task> onExit,
        Func<Task>? onCreateAiWidget = null)
    {
        var entries = new List<NativeTrayMenuEntry>(18)
        {
            NativeTrayMenuEntry.Item(
                TrayCommandOrganizeDesktop,
                localization.T("Tray.OrganizeDesktop"),
                "\uE74C",
                canCreateWidget,
                onOrganizeDesktop),
            NativeTrayMenuEntry.Separator,
        };

        int nextCreateCommandId = TrayCommandCreateWidgetFirst;
        foreach (WidgetContentDescriptor descriptor in new WidgetContentFactory(localization).GetCreateEntryDescriptors())
        {
            WidgetKind kind = descriptor.WidgetKind;
            entries.Add(NativeTrayMenuEntry.Item(
                nextCreateCommandId++,
                GetCreateEntryText(descriptor, localization),
                descriptor.DefaultGlyph,
                canCreateWidget,
                () => onCreateWidgetOfKind(kind)));
        }

        entries.Add(NativeTrayMenuEntry.Item(
            TrayCommandMapFolder,
            localization.T("Common.NewFolderMapping"),
            "\uE8E5",
            canCreateWidget,
            onMapFolder));
        entries.Add(NativeTrayMenuEntry.Item(
            TrayCommandAddFeatureWidget,
            localization.T("Common.AddFeatureWidget"),
            "\uE710",
            isEnabled: true,
            onAddFeatureWidget));
        if (onCreateAiWidget is not null)
        {
            entries.Add(NativeTrayMenuEntry.Item(
                TrayCommandCreateAiWidget,
                localization.T("Common.CreateAiWidget"),
                "\uE945",
                isEnabled: true,
                onCreateAiWidget));
        }
        entries.Add(NativeTrayMenuEntry.Separator);
        entries.Add(NativeTrayMenuEntry.Item(
            TrayCommandOpenManagedStorage,
            localization.T("Tray.OpenManagedStorage"),
            "\uE8B7",
            isEnabled: true,
            onOpenManagedStorage));
        entries.Add(NativeTrayMenuEntry.Separator);

        if (hasUpdateAvailable)
        {
            string updateText = string.IsNullOrWhiteSpace(availableUpdateVersion)
                ? localization.T("Tray.UpdateAvailable")
                : localization.Format("Tray.UpdateAvailableWithVersion", availableUpdateVersion);
            entries.Add(NativeTrayMenuEntry.Item(
                TrayCommandUpdateAvailable,
                updateText,
                "\uE896",
                isEnabled: true,
                onOpenUpdate));
        }

        entries.Add(NativeTrayMenuEntry.Item(
            TrayCommandSettings,
            localization.T("Tray.Settings"),
            "\uE713",
            isEnabled: true,
            onOpenSettings));
        entries.Add(NativeTrayMenuEntry.Separator);
        entries.Add(NativeTrayMenuEntry.Item(
            TrayCommandExit,
            localization.T("Tray.Exit"),
            "\uE711",
            isEnabled: true,
            onExit));

        return entries;
    }

    /// <summary>Suppresses a second deferred menu open while the first is
    /// still landing or blocking inside TrackPopupMenuEx's modal loop (the
    /// loop pumps dispatcher items, so a naive re-enqueue would open a nested
    /// menu). Monotonic TickCount64, not wall clock.</summary>
    private long _trayContextMenuReentrySuppressedUntilTick;

    private void ShowTrayContextMenuFromTray()
    {
        if (_trayHost is null ||
            !Win32Helper.GetCursorPos(out var cursor))
        {
            return;
        }

        if (Environment.TickCount64 < _trayContextMenuReentrySuppressedUntilTick)
        {
            return;
        }

        var clickPoint = new DrawingPoint(cursor.X, cursor.Y);
        // Tray notifications run inside NativeTrayIconHost's WndProc.
        // Building or showing the menu there re-enters win32k from a message
        // callback (the drag-freeze family), so capture the click anchor now
        // and open the menu one dispatcher turn later, outside the callback frame.
        UiDispatch.PostAfterCallback(() =>
        {
            if (Environment.TickCount64 < _trayContextMenuReentrySuppressedUntilTick)
            {
                return;
            }

            _trayContextMenuReentrySuppressedUntilTick =
                Environment.TickCount64 + 30_000;
            try
            {
                ShowTrayContextMenuAt(clickPoint);
            }
            finally
            {
                _trayContextMenuReentrySuppressedUntilTick = 0;
            }
        });
    }

    private void ShowTrayContextMenuAt(DrawingPoint point)
    {
        if (_trayHost is not { } trayHost)
        {
            return;
        }

        bool isRightToLeft = string.Equals(
            LocalizationService.CurrentCultureName,
            LocalizationService.LanguageArabic,
            StringComparison.OrdinalIgnoreCase);

        TrayMenuPlacement placement;
        try
        {
            placement = GetTrayContextMenuPlacement(point, isRightToLeft);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Failed to calculate tray context menu anchor: {ex}");
            placement = new TrayMenuPlacement(
                point,
                isRightToLeft ? TrayMenuHorizontalAlignment.Right : TrayMenuHorizontalAlignment.Left,
                TrayMenuVerticalAlignment.Bottom);
        }

        try
        {
            IReadOnlyList<NativeTrayMenuEntry> entries = BuildTrayMenuEntries();
            bool isDarkMenuTheme = ThemeService is not null
                ? ThemeService.EffectiveTheme == ElementTheme.Dark
                : IsDarkThemeActive();

            if (TrayContextMenuWindow.TryShow(
                    entries,
                    placement.Anchor,
                    placement.HorizontalAlignment,
                    placement.VerticalAlignment,
                    isDarkMenuTheme,
                    isRightToLeft,
                    EnsureXamlLoopLifelineIfNoOtherWindows,
                    invoked =>
                    {
                        if (invoked.Action is not null)
                        {
                            SafeFireAndForget(invoked.Action, $"tray-menu-{invoked.CommandId}");
                        }
                    }))
            {
                return;
            }

            int selectedCommand = trayHost.ShowContextMenu(
                placement.Anchor.X,
                placement.Anchor.Y,
                entries,
                isDarkMenuTheme,
                isRightToLeft,
                placement.HorizontalAlignment,
                placement.VerticalAlignment);
            if (selectedCommand <= 0)
            {
                return;
            }

            foreach (NativeTrayMenuEntry entry in entries)
            {
                if (!entry.IsSeparator &&
                    entry.CommandId == selectedCommand &&
                    entry.IsEnabled &&
                    entry.Action is not null)
                {
                    SafeFireAndForget(entry.Action, $"tray-menu-{selectedCommand}");
                    break;
                }
            }
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

    private readonly record struct TrayMenuPlacement(
        DrawingPoint Anchor,
        TrayMenuHorizontalAlignment HorizontalAlignment,
        TrayMenuVerticalAlignment VerticalAlignment);

    private TrayMenuPlacement GetTrayContextMenuPlacement(
        DrawingPoint fallbackPoint,
        bool isRightToLeft)
    {
        if (TryGetTrayIconIdentity(out IntPtr trayIconWindowHandle, out Guid trayIconId, out uint trayNumericId) &&
            Win32Helper.TryGetNotifyIconRect(trayIconWindowHandle, trayIconId, out var iconRect, trayNumericId) &&
            IsUsableTrayIconRect(iconRect))
        {
            int centerX = iconRect.Left + ((iconRect.Right - iconRect.Left) / 2);
            int centerY = iconRect.Top + ((iconRect.Bottom - iconRect.Top) / 2);
            if (Win32Helper.TryGetMonitorWorkArea(centerX, centerY, out var monitor, out var workArea))
            {
                TaskbarEdge edge = GetNearestTaskbarEdge(iconRect, monitor, workArea);
                return edge switch
                {
                    TaskbarEdge.Bottom => new TrayMenuPlacement(
                        ClampPointToRect(new DrawingPoint(centerX, Math.Min(workArea.Bottom - 1, iconRect.Top)), monitor),
                        TrayMenuHorizontalAlignment.Center,
                        TrayMenuVerticalAlignment.Bottom),
                    TaskbarEdge.Top => new TrayMenuPlacement(
                        ClampPointToRect(new DrawingPoint(centerX, Math.Max(workArea.Top, iconRect.Bottom)), monitor),
                        TrayMenuHorizontalAlignment.Center,
                        TrayMenuVerticalAlignment.Top),
                    TaskbarEdge.Right => new TrayMenuPlacement(
                        ClampPointToRect(new DrawingPoint(Math.Min(workArea.Right - 1, iconRect.Left), centerY), monitor),
                        TrayMenuHorizontalAlignment.Right,
                        TrayMenuVerticalAlignment.Center),
                    TaskbarEdge.Left => new TrayMenuPlacement(
                        ClampPointToRect(new DrawingPoint(Math.Max(workArea.Left, iconRect.Right), centerY), monitor),
                        TrayMenuHorizontalAlignment.Left,
                        TrayMenuVerticalAlignment.Center),
                    _ => new TrayMenuPlacement(
                        GetFallbackTrayContextMenuAnchorPoint(fallbackPoint),
                        isRightToLeft ? TrayMenuHorizontalAlignment.Right : TrayMenuHorizontalAlignment.Left,
                        TrayMenuVerticalAlignment.Bottom),
                };
            }

            return new TrayMenuPlacement(
                new DrawingPoint(centerX, centerY),
                TrayMenuHorizontalAlignment.Center,
                TrayMenuVerticalAlignment.Bottom);
        }

        DrawingPoint fallbackAnchor = GetTrayContextMenuAnchorPoint(fallbackPoint);
        return new TrayMenuPlacement(
            fallbackAnchor,
            isRightToLeft ? TrayMenuHorizontalAlignment.Right : TrayMenuHorizontalAlignment.Left,
            TrayMenuVerticalAlignment.Bottom);
    }

    private DrawingPoint GetTrayContextMenuAnchorPoint(DrawingPoint fallbackPoint)
    {
        if (TryGetTrayIconIdentity(out var trayIconWindowHandle, out var trayIconId, out uint trayNumericId) &&
            Win32Helper.TryGetNotifyIconRect(trayIconWindowHandle, trayIconId, out var iconRect, trayNumericId) &&
            IsUsableTrayIconRect(iconRect))
        {
            return GetTrayContextMenuAnchorPointFromIconRect(iconRect, fallbackPoint);
        }

        return GetFallbackTrayContextMenuAnchorPoint(fallbackPoint);
    }

    private bool TryGetTrayIconIdentity(out IntPtr windowHandle, out Guid id, out uint numericId)
    {
        windowHandle = IntPtr.Zero;
        id = Guid.Empty;
        numericId = 0;

        if (_trayHost is not { } trayHost)
        {
            return false;
        }

        windowHandle = trayHost.WindowHandle;
        id = trayHost.IconId;
        numericId = trayHost.NumericId;
        return windowHandle != IntPtr.Zero && (id != Guid.Empty || numericId != 0);
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

        // The left-click notification runs inside NativeTrayIconHost's WndProc;
        // the toggle preamble (cancel animations, per-window restores) must not
        // run on that callback frame. One yield leaves the frame before any
        // window work; UiDispatch keeps running inline on the UI thread.
        await Task.Yield();

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
        // Native tray menu entries are materialized lazily on right-click from
        // LocalizationService and current update state, keeping zero resident
        // XAML menu controls at idle.
    }

    private void RefreshTrayToolTipText()
    {
        if (_trayHost is null)
        {
            return;
        }

        if (_hasUpdateAvailable && !string.IsNullOrWhiteSpace(_availableUpdateVersion))
        {
            _trayHost.SetToolTipText(
                LocalizationService.Format("Tray.TooltipUpdateAvailable", _availableUpdateVersion));
            return;
        }

        _trayHost.SetToolTipText(_widgetsRaisedFromTray
            ? LocalizationService.T("Tray.TooltipRaised")
            : LocalizationService.T("Tray.TooltipNormal"));
    }

    private static string GetCreateEntryText(WidgetContentDescriptor descriptor, LocalizationService localization)
    {
        return string.IsNullOrWhiteSpace(descriptor.CreateEntryTextKey)
            ? descriptor.DefaultTitle
            : localization.T(descriptor.CreateEntryTextKey);
    }

    private int _isFolderMappingPickerFlowActive;

    private static async Task RunTrayMenuActionAsync(Action action)
    {
        await Task.Yield();
        action();
    }

    private static async Task RunTrayMenuActionAsync(Func<Task> action)
    {
        await Task.Yield();
        await action();
    }

    private async Task RunTraySettingsActionAsync(Action action)
    {
        var widgetManager = WidgetManager;
        widgetManager?.BeginWidgetInteraction("tray-settings-opening");
        try
        {
            // TrackPopupMenuEx is a native modal loop that has already closed
            // before returning the selected command, so no SecondWindow helper
            // settle delay is needed.
            await RunTrayMenuActionAsync(action);
        }
        finally
        {
            widgetManager?.EndWidgetInteraction("tray-settings-opened");
        }
    }

    internal async Task CreateFolderWidgetFromPickerAsync(IntPtr preferredOwnerHwnd = default)
    {
        if (Interlocked.CompareExchange(ref _isFolderMappingPickerFlowActive, 1, 0) != 0)
        {
            if (_folderMappingConflictWindow is { } activeConflictWindow)
            {
                activeConflictWindow.Activate();
                _ = Win32Helper.SetForegroundWindow(
                    WindowNative.GetWindowHandle(activeConflictWindow));
            }
            else
            {
                _ = Task.Run(NativeFileOpenDialog.TryActivateActiveFolderDialog);
            }

            return;
        }

        try
        {
            while (true)
            {
                IntPtr ownerHwnd;
                string ownerLabel;
                if (IsUsablePickerOwner(preferredOwnerHwnd))
                {
                    ownerHwnd = preferredOwnerHwnd;
                    ownerLabel = "widget";
                }
                else
                {
                    ownerHwnd = GetFolderPickerOwner(out PickerOwnerSource ownerSource);
                    ownerLabel = DescribePickerOwner(ownerSource);
                }

                FolderPickResult pick = await FolderPickerService.PickFolderWithOutcomeAsync(
                    ownerHwnd,
                    ownerLabel);
                if (pick.Outcome == FolderPickOutcome.Failed)
                {
                    ShowFolderMappingFailure(LocalizationService.Format(
                        "Widget.MapFolder.PickerFailedBody",
                        $"0x{pick.HResult:X8}"));
                    return;
                }

                if (string.IsNullOrWhiteSpace(pick.Folder))
                {
                    return;
                }

                bool repick = await TryCreateFolderWidgetCoreAsync(pick.Folder);
                if (!repick)
                {
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isFolderMappingPickerFlowActive, 0);
        }
    }

    internal async Task TryCreateFolderWidgetAsync(string folderPath)
    {
        _ = await TryCreateFolderWidgetCoreAsync(folderPath);
    }

    private async Task<bool> TryCreateFolderWidgetCoreAsync(string folderPath)
    {
        if (WidgetManager is not { } widgetManager)
        {
            return false;
        }

        (FileWidgetPathConflict? conflict, FileWidgetPathRelation relation, string storageRoot) =
            await widgetManager.EvaluateExternalMappingConflictAsync(folderPath);
        if (conflict is not null)
        {
            return await HandleFolderMappingConflictAsync(
                folderPath,
                conflict,
                relation,
                storageRoot);
        }

        try
        {
            await widgetManager.CreateFolderWidgetCoreAsync(
                folderPath,
                skipConflictCheck: true);
        }
        catch (Exception ex)
        {
            ShowFolderMappingFailure(ex.Message);
        }

        return false;
    }

    private async Task<bool> HandleFolderMappingConflictAsync(
        string folderPath,
        FileWidgetPathConflict conflict,
        FileWidgetPathRelation relation,
        string storageRoot)
    {
        if (WidgetManager is not { } widgetManager)
        {
            return false;
        }

        string kindToken;
        string relationToken = DescribeFileWidgetRelation(relation);
        string body;
        string? conflictingWidgetId = null;
        if (conflict.Kind == FileWidgetPathConflictKind.ExistingWidget &&
            conflict.ConflictingWidget is { } existing)
        {
            kindToken = "existing-widget";
            body = LocalizationService.Format(
                "Widget.MapFolder.ConflictDialogWidgetBody",
                existing.Name);
            conflictingWidgetId = existing.Id;
        }
        else
        {
            kindToken = "storage-root";
            body = widgetManager.FormatFileWidgetPathConflictMessage(
                relation,
                LocalizationService.T("WidgetTitleIcon.Label.ManagedStorage"),
                storageRoot);
            if (relation == FileWidgetPathRelation.CandidateInsideOther)
            {
                body += "\n\n" + LocalizationService.T("Widget.MapFolder.ConflictInsideExtraHint");
            }
        }

        Log($"[WidgetMapping] outcome=conflict kind={kindToken} relation={relationToken}");

        FolderMappingConflictWindow window = ShowFolderMappingConflictWindow(
            body,
            FolderMappingConflictWindow.PlanButtons(conflict.Kind, relation));
        switch (await window.Completion)
        {
            case FolderMappingConflictResult.OpenWidget when conflictingWidgetId is not null:
                try
                {
                    await widgetManager.ShowWidgetAsync(conflictingWidgetId, reveal: true);
                }
                catch (Exception ex)
                {
                    Log($"[WidgetMapping] Could not reveal the conflicting widget: {ex.Message}");
                }

                return false;

            case FolderMappingConflictResult.ChangeStorage:
                ShowSettings("FileStorageSettings");
                return false;

            case FolderMappingConflictResult.Repick:
                return true;

            default:
                return false;
        }
    }

    private Window? _folderMappingConflictWindow;

    private FolderMappingConflictWindow ShowFolderMappingConflictWindow(
        string body,
        FolderMappingConflictButtonPlan plan)
    {
        EnsureXamlLoopLifelineIfNoOtherWindows();
        if (_folderMappingConflictWindow is not null)
        {
            try
            {
                _folderMappingConflictWindow.Close();
            }
            catch (Exception ex)
            {
                Log($"[WidgetMapping] Stale conflict window close failed: {ex.Message}");
            }

            _folderMappingConflictWindow = null;
        }

        var window = new FolderMappingConflictWindow(LocalizationService, body, plan);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_folderMappingConflictWindow, window))
            {
                _folderMappingConflictWindow = null;
            }
        };
        ThemeService.TrackWindow(window);
        _folderMappingConflictWindow = window;
        window.Activate();
        return window;
    }

    private static string DescribeFileWidgetRelation(FileWidgetPathRelation relation) => relation switch
    {
        FileWidgetPathRelation.SameDirectory => "same",
        FileWidgetPathRelation.CandidateInsideOther => "inside",
        FileWidgetPathRelation.CandidateContainsOther => "contains",
        _ => "unresolvable",
    };

    internal bool TryShowTrayBalloonNotification(
        string title,
        string message,
        TrayBalloonIconKind iconKind,
        bool largeIcon = false)
    {
        if (_trayHost is null)
        {
            return false;
        }

        try
        {
            return _trayHost.TryShowBalloonNotification(
                title,
                message,
                iconKind,
                largeIcon: largeIcon,
                sound: false,
                respectQuietTime: true);
        }
        catch (Exception ex)
        {
            Log($"[Tray] Balloon notification fallback failed: {ex.Message}");
            return false;
        }
    }

    private void ShowFolderMappingFailure(string message)
    {
        string title = LocalizationService.T("Common.NewFolderMapping");
        if (_nativeNotificationService?.TryShow(
                title,
                message,
                options: new NativeAppNotificationOptions(
                    Tag: FolderMappingConflictNotificationTag,
                    Group: FolderMappingNotificationGroup)) == true)
        {
            return;
        }

        _ = TryShowTrayBalloonNotification(title, message, TrayBalloonIconKind.Info);
    }

    internal IntPtr GetFolderPickerOwner(out PickerOwnerSource source)
    {
        IntPtr trayHwnd = TrayHostWindowHandle;
        IntPtr settingsHwnd = GetVisibleSettingsWindowHandle();
        IntPtr helperHwnd = _folderPickerHelperWindowHandle;

        PickerOwnerSource resolved = PickerOwnerResolutionPolicy.Resolve(
            IsUsablePickerOwner(trayHwnd),
            IsUsablePickerOwner(settingsHwnd) &&
                Win32Helper.IsWindowVisible(settingsHwnd),
            IsUsablePickerOwner(helperHwnd));
        source = resolved;
        return resolved switch
        {
            PickerOwnerSource.TrayWindow => trayHwnd,
            PickerOwnerSource.SettingsWindow => settingsHwnd,
            PickerOwnerSource.CachedHelperWindow => helperHwnd,
            _ => RecreateFolderPickerHelperWindow(),
        };
    }

    internal static string DescribePickerOwner(PickerOwnerSource source) => source switch
    {
        PickerOwnerSource.TrayWindow => "tray",
        PickerOwnerSource.SettingsWindow => "settings",
        PickerOwnerSource.CachedHelperWindow => "helper-cached",
        _ => "helper-fresh",
    };

    private static bool IsUsablePickerOwner(IntPtr ownerHwnd)
    {
        return ownerHwnd != IntPtr.Zero &&
            Win32Helper.IsWindow(ownerHwnd) &&
            !Win32Helper.IsWindowCloaked(ownerHwnd);
    }

    private IntPtr RecreateFolderPickerHelperWindow()
    {
        NativeTrayIconHost.DestroyTransientOwnerWindow(ref _folderPickerHelperWindowHandle);
        _folderPickerHelperWindowHandle = NativeTrayIconHost.CreateTransientOwnerWindow("DeskBox Picker Host");
        return _folderPickerHelperWindowHandle;
    }

    private void OpenSettingsFromTray()
    {
        Log("[Tray] Settings selected in primary instance");
        OpenSettings();
    }

    private void OpenFeatureWidgetsFromTray()
    {
        ShowSettings("FeatureWidgets");
    }

    private void OpenAiCreateStudioFromTray()
    {
        ShowCustomWidgetStudio();
    }

    private void OpenDesktopOrganizationFromTray()
    {
        CancelBackgroundMemoryCleanup();
        ShowDesktopOrganizationWindow();
    }

    private void OpenAboutSettingsFromTray()
    {
        CancelBackgroundMemoryCleanup();
        ShowSettings("About");
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
        TrayContextMenuWindow.DismissActiveIfOpen();
        if (_trayHost is null)
        {
            return;
        }

        string style = SettingsService.Settings.Core.TrayIconStyle ?? "System";
        if (style == TrayIconStyleHidden)
        {
            _trayHost.DesiredVisible = false;
            if (_trayHost.IsCreated)
            {
                try
                {
                    _ = _trayHost.TryRemoveIcon();
                    Log("[Tray] Tray icon removed (hidden by user setting)");
                }
                catch (Exception ex)
                {
                    Log($"[Tray] Failed to remove the hidden tray icon: {ex.Message}");
                }
            }

            return;
        }

        _trayHost.DesiredVisible = true;
        _trayHost.SetIconPath(AppBranding.ResolveTrayIconPath(style, IsDarkThemeActive()));
        if (!_trayHost.IsCreated)
        {
            try
            {
                if (_trayHost.TryCreateIcon())
                {
                    Log("[Tray] Tray icon re-created after leaving the hidden style");
                }
            }
            catch (Exception ex)
            {
                Log($"[Tray] Failed to re-create the tray icon after unhide: {ex.Message}");
            }
        }

        ScheduleTrayContextMenuPrewarm();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _trayMenuPrewarmTimer;

    private void ScheduleTrayContextMenuPrewarm()
    {
        if (IsShuttingDown || IsTrayIconHiddenByUserSetting())
        {
            return;
        }

        if (UiDispatcherQueue is not { } queue)
        {
            return;
        }

        if (_trayMenuPrewarmTimer is null)
        {
            _trayMenuPrewarmTimer = queue.CreateTimer();
            _trayMenuPrewarmTimer.Interval = TimeSpan.FromMilliseconds(1200);
            _trayMenuPrewarmTimer.IsRepeating = false;
            _trayMenuPrewarmTimer.Tick += OnTrayMenuPrewarmTimerTick;
        }

        _trayMenuPrewarmTimer.Stop();
        _trayMenuPrewarmTimer.Start();
    }

    private void OnTrayMenuPrewarmTimerTick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (IsShuttingDown || IsTrayIconHiddenByUserSetting())
        {
            return;
        }

        bool isDarkMenuTheme = ThemeService is not null
            ? ThemeService.EffectiveTheme == ElementTheme.Dark
            : IsDarkThemeActive();
        TrayContextMenuWindow.WarmUpIfNeeded(
            EnsureXamlLoopLifelineIfNoOtherWindows,
            isDarkMenuTheme);
    }

    public void UpdateTrayIcon()
    {
        UpdateTrayIconAppearance();
    }

    /// <summary>
    /// WinUI 3's DXamlCore posts WM_QUIT when the last constructed
    /// Microsoft.UI.Xaml.Window closes. Because the tray host is now a pure
    /// Win32 HWND, we keep zero XAML windows when none are needed, and lazily
    /// create one bare unactivated Window (no Content, no Activate, no
    /// swapchain) only if a transient XAML window is used while zero widget
    /// windows exist.
    /// </summary>
    internal void EnsureXamlLoopLifelineIfNoOtherWindows()
    {
        if (IsShuttingDown ||
            _xamlLoopLifelineWindow is not null ||
            WidgetManager?.LoadedWidgetCount > 0)
        {
            return;
        }

        try
        {
            var lifeline = new Window { Title = "DeskBox XAML Lifeline" };
            WindowShellState.TryHideFromSwitchers(lifeline.AppWindow);
            _xamlLoopLifelineWindow = lifeline;
        }
        catch (Exception ex)
        {
            Log($"[Tray] XAML loop lifeline creation skipped: {ex.Message}");
        }
    }

    private void CloseTrayAndLifelineForShutdown()
    {
        if (_trayMenuPrewarmTimer is not null)
        {
            _trayMenuPrewarmTimer.Stop();
            _trayMenuPrewarmTimer.Tick -= OnTrayMenuPrewarmTimerTick;
            _trayMenuPrewarmTimer = null;
        }

        TrayContextMenuWindow.DismissActiveIfOpen();
        TrayContextMenuWindow.Shutdown();
        NativeTrayIconHost.DestroyTransientOwnerWindow(ref _folderPickerHelperWindowHandle);
        try
        {
            _trayHost?.Dispose();
            _trayHost = null;
        }
        catch (Exception ex)
        {
            Log($"[Shutdown] Native tray host dispose failed: {ex.Message}");
        }

        try
        {
            _xamlLoopLifelineWindow?.Close();
            _xamlLoopLifelineWindow = null;
        }
        catch (Exception ex)
        {
            Log($"[Shutdown] XAML loop lifeline close failed: {ex.Message}");
        }
    }
}
