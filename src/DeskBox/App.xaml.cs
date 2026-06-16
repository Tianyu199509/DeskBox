// Copyright (c) DeskBox. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace DeskBox;

/// <summary>
/// Application bootstrap, tray menu, and widget lifecycle.
/// </summary>
public partial class App : Application
{
    private const double TrayMenuItemWidth = 176;
    private const double TrayMenuVerticalPadding = 4;
    private const double TrayMenuItemMinHeight = 36;
    private static readonly bool EnableVerboseLogging = false;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "DeskBox.log");

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static RegisteredWaitHandle? _activationRegistration;

    private TaskbarIcon? _trayIcon;
    private Window? _trayWindow;
    private MenuFlyoutItem? _trayMapFolderItem;
    private MenuFlyoutItem? _trayNewWidgetItem;
    private MenuFlyoutItem? _traySettingsItem;
    private MenuFlyoutItem? _trayExitItem;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboardingWindow;
    private bool _widgetsRaisedFromTray;

    public static new App Current => (App)Application.Current;

    public static Microsoft.UI.Dispatching.DispatcherQueue UiDispatcherQueue { get; private set; } = null!;

    public bool IsStartupMode { get; set; }

    public SettingsService SettingsService { get; } = new();
    public FileService FileService { get; } = new();
    public OrganizerService OrganizerService { get; }
    public LocalizationService LocalizationService { get; private set; } = null!;
    public ThemeService ThemeService { get; private set; } = null!;
    public WidgetManager? WidgetManager { get; private set; }
    public SettingsWindow? SettingsWindowInstance => _settingsWindow;

    public App()
    {
        Log("App() constructor start");
        bool launchedForStartup = IsStartupLaunch(Environment.GetCommandLineArgs());
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskBox_Activate_Event_7F3A9B2E");
        _singleInstanceMutex = new Mutex(true, "DeskBox_SingleInstance_Mutex_7F3A9B2E", out bool createdNew);
        if (!createdNew)
        {
            if (launchedForStartup)
            {
                Log("Another instance running; startup launch exiting silently");
                Environment.Exit(0);
            }

            Log("Another instance running, signaling existing instance");
            try
            {
                _activationEvent.Set();
            }
            catch (Exception ex)
            {
                Log($"Failed to signal existing instance: {ex}");
            }

            Environment.Exit(0);
        }

        InitializeComponent();
        OrganizerService = new OrganizerService(SettingsService, FileService);
        UnhandledException += OnUnhandledException;
    }

    public bool IsDeskBoxWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        Win32Helper.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return true;
        }

        IntPtr rootHwnd = Win32Helper.GetAncestor(hwnd, Win32Helper.GA_ROOT);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = hwnd;
        }

        if (_trayWindow is not null && rootHwnd == WindowNative.GetWindowHandle(_trayWindow))
        {
            return true;
        }

        if (_settingsWindow is not null && rootHwnd == WindowNative.GetWindowHandle(_settingsWindow))
        {
            return true;
        }

        if (_onboardingWindow is not null && rootHwnd == WindowNative.GetWindowHandle(_onboardingWindow))
        {
            return true;
        }

        return WidgetManager?.Widgets.Values.Any(entry => entry.Window.WindowHandle == rootHwnd) == true;
    }

    public static void Log(string msg)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch
        {
        }
    }

    public static void LogVerbose(string msg)
    {
        if (!EnableVerboseLogging)
        {
            return;
        }

        Log(msg);
    }

    private static bool IsStartupLaunch(IEnumerable<string> arguments)
    {
        return arguments.Any(IsStartupArgument);
    }

    private static bool IsStartupLaunch(string? arguments)
    {
        return !string.IsNullOrWhiteSpace(arguments) &&
            arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsStartupArgument);
    }

    private static bool IsStartupArgument(string argument)
    {
        return string.Equals(argument.Trim().Trim('"'), "--startup", StringComparison.OrdinalIgnoreCase);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        using var perfScope = PerformanceLogger.Measure("App.OnLaunched", $"startup={IsStartupLaunch(args.Arguments)}");
        Log("OnLaunched start");

        try
        {
            IsStartupMode = IsStartupLaunch(args.Arguments);
            UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            ThemeService = new ThemeService(SettingsService);
            await SettingsService.LoadAsync();
            LocalizationService = new LocalizationService(SettingsService);
            LocalizationService.LanguageChanged += OnLanguageChanged;
            ThemeService.RefreshAppearance();

            WidgetManager = new WidgetManager(SettingsService, FileService, OrganizerService, ThemeService, LocalizationService);
            WidgetManager.TrayLayerStateChanged += UpdateTrayLayerStateText;

            CreateTrayIcon();
            RegisterActivationListener();

            await WidgetManager.RestoreWidgetsAsync();

            if (SettingsService.Settings.Widgets.Count(widget =>
                    widget.WidgetKind == WidgetKind.File &&
                    !widget.IsDisabled &&
                    !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id)) == 0 &&
                !IsStartupMode)
            {
                await WidgetManager.CreateManagedWidgetAsync(LocalizationService.T("Widget.DefaultDesktopName"));
            }

            if (!IsStartupMode && !SettingsService.Settings.HasCompletedOnboarding)
            {
                ShowOnboarding();
            }

            Log("OnLaunched completed successfully");
        }
        catch (Exception ex)
        {
            Log($"Exception in OnLaunched: {ex}");
        }
    }

    private void RegisterActivationListener()
    {
        if (_activationEvent is null || _activationRegistration is not null)
        {
            return;
        }

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (_, _) =>
            {
                App.UiDispatcherQueue?.TryEnqueue(() =>
                {
                    _ = Current.HandleExternalActivationAsync();
                });
            },
            null,
            Timeout.Infinite,
            false);
    }

    private async Task HandleExternalActivationAsync()
    {
        Log("HandleExternalActivationAsync invoked");

        if (WidgetManager is not null)
        {
            bool hasConfiguredWidgets = SettingsService.Settings.Widgets.Any(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id));
            bool anyLoadedVisible = WidgetManager.Widgets.Values.Any(entry => entry.Window.Visible);

            if (hasConfiguredWidgets && !anyLoadedVisible)
            {
                await WidgetManager.SetAllWidgetsVisibleAsync(true);
            }
            else
            {
                var firstWidget = WidgetManager.Widgets.Values.FirstOrDefault().Window;
                firstWidget?.RevealFromTray();
            }
        }

        OpenSettings();
    }

    private void CreateTrayIcon()
    {
        var localization = LocalizationService;
        var contextMenu = new MenuFlyout();
        contextMenu.ShouldConstrainToRootBounds = false;
        contextMenu.MenuFlyoutPresenterStyle = CreateTrayMenuPresenterStyle();
        var trayMenuItemStyle = CreateTrayMenuItemStyle();
        var mapFolderItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.NewFolderMapping"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.OpenFile),
            Style = trayMenuItemStyle
        };
        mapFolderItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, CreateFolderWidgetFromPickerAsync);

        var newWidgetItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.NewWidget"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Add),
            Style = trayMenuItemStyle
        };
        newWidgetItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, async () =>
        {
            if (WidgetManager is not null)
            {
                await WidgetManager.CreateManagedWidgetAsync(LocalizationService.T("Widget.DefaultNameShort"));
            }
        });

        var settingsItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Settings"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Setting),
            Style = trayMenuItemStyle
        };
        settingsItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, OpenSettingsFromTray);

        var exitItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Exit"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Cancel),
            Style = trayMenuItemStyle
        };
        exitItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, ExitApplication);

        contextMenu.Opening += (_, _) =>
        {
            bool canCreateWidget = WidgetManager is not null;
            newWidgetItem.IsEnabled = canCreateWidget;
            mapFolderItem.IsEnabled = canCreateWidget;
        };

        contextMenu.Items.Add(newWidgetItem);
        contextMenu.Items.Add(mapFolderItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(exitItem);

        _trayMapFolderItem = mapFolderItem;
        _trayNewWidgetItem = newWidgetItem;
        _traySettingsItem = settingsItem;
        _trayExitItem = exitItem;

        _trayWindow = new Window();
        _trayWindow.AppWindow.IsShownInSwitchers = false;
        AppBranding.ApplyWindowIcon(_trayWindow.AppWindow);
        _trayWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));

        _trayIcon = new TaskbarIcon
        {
            Icon = AppBranding.CreateTrayIcon(IsDarkThemeActive()),
            ToolTipText = localization.T("Tray.Tooltip"),
            ContextMenuMode = ContextMenuMode.SecondWindow,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() =>
            {
                if (WidgetManager is not null)
                {
                    _ = ToggleTrayWidgetsAsync();
                }
            })
        };
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
            _trayIcon.ForceCreate();
        }

        _trayWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (_trayWindow is null)
            {
                return;
            }

            WindowExtensions.Hide(_trayWindow);
        });

        ThemeService.AppearanceChanged += UpdateTrayIconAppearance;
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

    private async Task ToggleTrayWidgetsAsync()
    {
        if (WidgetManager is null)
        {
            return;
        }

        if (WidgetManager.WidgetsRaisedFromTray)
        {
            await WidgetManager.SetAllWidgetsVisibleAsync(false);
            UpdateTrayLayerStateText(raised: false);
            return;
        }

        await RaiseTrayWidgetsAsync();
    }

    private void UpdateTrayLayerStateText(bool raised)
    {
        _widgetsRaisedFromTray = raised;
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = raised
                ? LocalizationService.T("Tray.TooltipRaised")
                : LocalizationService.T("Tray.TooltipNormal");
        }
    }

    private void OnLanguageChanged()
    {
        Localized.RefreshAll(LocalizationService);
        RefreshTrayMenuText();
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = _widgetsRaisedFromTray
                ? LocalizationService.T("Tray.TooltipRaised")
                : LocalizationService.T("Tray.Tooltip");
        }
    }

    private void RefreshTrayMenuText()
    {
        if (_trayMapFolderItem is not null)
        {
            _trayMapFolderItem.Text = LocalizationService.T("Common.NewFolderMapping");
        }

        if (_trayNewWidgetItem is not null)
        {
            _trayNewWidgetItem.Text = LocalizationService.T("Common.NewWidget");
        }

        if (_traySettingsItem is not null)
        {
            _traySettingsItem.Text = LocalizationService.T("Tray.Settings");
        }

        if (_trayExitItem is not null)
        {
            _trayExitItem.Text = LocalizationService.T("Tray.Exit");
        }
    }

    private static Style CreateTrayMenuPresenterStyle()
    {
        var presenterStyle = new Style
        {
            TargetType = typeof(MenuFlyoutPresenter)
        };

        if (Current.Resources.TryGetValue("DefaultMenuFlyoutPresenterStyle", out var basePresenterStyle) &&
            basePresenterStyle is Style defaultPresenterStyle)
        {
            presenterStyle.BasedOn = defaultPresenterStyle;
        }

        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, TrayMenuVerticalPadding, 0, TrayMenuVerticalPadding)));
        return presenterStyle;
    }

    private static Style CreateTrayMenuItemStyle()
    {
        return CreateTrayMenuItemStyleCore(typeof(MenuFlyoutItem), "DefaultMenuFlyoutItemStyle");
    }

    private static Style CreateTrayMenuItemStyleCore(Type targetType, string defaultStyleKey)
    {
        var itemStyle = new Style
        {
            TargetType = targetType
        };

        if (Current.Resources.TryGetValue(defaultStyleKey, out var baseStyle) &&
            baseStyle is Style defaultItemStyle)
        {
            itemStyle.BasedOn = defaultItemStyle;
        }

        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, TrayMenuItemMinHeight));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        return itemStyle;
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

    private async Task CreateFolderWidgetFromPickerAsync()
    {
        if (_trayWindow is null || WidgetManager is null)
        {
            return;
        }

        string? folderPath = FolderPickerService.PickFolder(IntPtr.Zero);
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            await WidgetManager.CreateFolderWidgetAsync(folderPath);
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(SettingsService, ThemeService, LocalizationService);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    private void OpenSettingsFromTray()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(SettingsService, ThemeService, LocalizationService);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.ActivateFromTray();
    }

    public void ShowSettings()
    {
        OpenSettings();
    }

    public void ShowOnboarding()
    {
        if (_onboardingWindow is null)
        {
            _onboardingWindow = new OnboardingWindow(SettingsService, LocalizationService);
            _onboardingWindow.Closed += (_, _) => _onboardingWindow = null;
            ThemeService.TrackWindow(_onboardingWindow);
        }

        _onboardingWindow.Activate();
    }

    private async void ExitApplication()
    {
        ThemeService.AppearanceChanged -= UpdateTrayIconAppearance;
        if (LocalizationService is not null)
        {
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        }
        await SettingsService.SaveAsync();
        WidgetManager?.CloseAll();
        _trayIcon?.Dispose();
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _trayWindow?.Close();
        Exit();
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

        _trayIcon.Icon = AppBranding.CreateTrayIcon(IsDarkThemeActive());
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"Unhandled exception: {e.Exception}");
        e.Handled = true;
    }
}
