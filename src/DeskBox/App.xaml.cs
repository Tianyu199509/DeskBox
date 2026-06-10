// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
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
        "DeskBox_debug.log");

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static RegisteredWaitHandle? _activationRegistration;

    private TaskbarIcon? _trayIcon;
    private Window? _trayWindow;
    private Window? _dialogWindow;
    private Grid? _dialogHostRoot;
    private SettingsWindow? _settingsWindow;

    public static new App Current => (App)Application.Current;

    public static Microsoft.UI.Dispatching.DispatcherQueue MainWindowDispatcherQueue { get; private set; } = null!;

    public bool IsStartupMode { get; set; }

    public SettingsService SettingsService { get; } = new();
    public FileService FileService { get; } = new();
    public OrganizerService OrganizerService { get; }
    public ThemeService ThemeService { get; private set; } = null!;
    public WidgetManager? WidgetManager { get; private set; }
    public SettingsWindow? SettingsWindowInstance => _settingsWindow;

    public App()
    {
        Log("App() constructor start");
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskBox_Activate_Event_7F3A9B2E");
        _singleInstanceMutex = new Mutex(true, "DeskBox_SingleInstance_Mutex_7F3A9B2E", out bool createdNew);
        if (!createdNew)
        {
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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched start");

        try
        {
            IsStartupMode = args.Arguments?.Contains("--startup", StringComparison.OrdinalIgnoreCase) == true;
            MainWindowDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            ThemeService = new ThemeService(SettingsService);
            await SettingsService.LoadAsync();
            ThemeService.RefreshAppearance();

            WidgetManager = new WidgetManager(SettingsService, FileService, OrganizerService, ThemeService);

            CreateTrayIcon();
            RegisterActivationListener();

            await WidgetManager.RestoreWidgetsAsync();

            if (SettingsService.Settings.Widgets.Count(widget =>
                    widget.WidgetKind == WidgetKind.File &&
                    !widget.IsDisabled &&
                    !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id)) == 0 &&
                !IsStartupMode)
            {
                await WidgetManager.CreateNewWidgetAsync("\u6211\u7684\u684C\u9762");
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
                App.MainWindowDispatcherQueue?.TryEnqueue(() =>
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
        var contextMenu = new MenuFlyout();
        contextMenu.ShouldConstrainToRootBounds = false;
        contextMenu.MenuFlyoutPresenterStyle = CreateTrayMenuPresenterStyle();
        var trayMenuItemStyle = CreateTrayMenuItemStyle();
        var trayToggleMenuItemStyle = CreateTrayToggleMenuItemStyle();
        var mapFolderItem = new MenuFlyoutItem
        {
            Text = "\u6620\u5C04\u6587\u4EF6\u5939...",
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Folder),
            Style = trayMenuItemStyle
        };
        mapFolderItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, CreateFolderWidgetFromPickerAsync);

        var newWidgetItem = new MenuFlyoutItem
        {
            Text = "新建组件",
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Add),
            Style = trayMenuItemStyle
        };
        newWidgetItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, async () =>
        {
            if (WidgetManager is not null)
            {
                await WidgetManager.CreateNewWidgetAsync();
            }
        });

        var showAllItem = new MenuFlyoutItem
        {
            Text = "\u663E\u793A\u5168\u90E8\u7EC4\u4EF6",
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Preview),
            Style = trayMenuItemStyle
        };
        showAllItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, async () =>
        {
            if (WidgetManager is not null)
            {
                await WidgetManager.SetAllWidgetsVisibleAsync(true);
            }
        });

        var hideAllItem = new MenuFlyoutItem
        {
            Text = "\u9690\u85CF\u5168\u90E8\u7EC4\u4EF6",
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.UnPin),
            Style = trayMenuItemStyle
        };
        hideAllItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, async () =>
        {
            if (WidgetManager is not null)
            {
                await WidgetManager.SetAllWidgetsVisibleAsync(false);
            }
        });

        var settingsItem = new MenuFlyoutItem
        {
            Text = "\u8BBE\u7F6E",
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Setting),
            Style = trayMenuItemStyle
        };
        settingsItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, OpenSettings);

        var autoStartItem = new ToggleMenuFlyoutItem
        {
            Text = "\u5F00\u673A\u542F\u52A8",
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Play),
            IsChecked = StartupService.IsEnabled(),
            Style = trayToggleMenuItemStyle
        };
        autoStartItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, () =>
        {
            StartupService.SetEnabled(autoStartItem.IsChecked);
            SettingsService.Settings.AutoStart = autoStartItem.IsChecked;
            SettingsService.SaveDebounced();
        });

        var exitItem = new MenuFlyoutItem
        {
            Text = "\u9000\u51FA",
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Cancel),
            Style = trayMenuItemStyle
        };
        exitItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, ExitApplication);

        contextMenu.Opening += (_, _) =>
        {
            autoStartItem.IsChecked = StartupService.IsEnabled();
            mapFolderItem.IsEnabled = WidgetManager is not null;

            var widgetsSnapshot = GetTrayWidgetsSnapshot();
            int totalWidgets = widgetsSnapshot.Count;
            int visibleCount = widgetsSnapshot.Count(widget => widget.IsVisible);
            int hiddenCount = Math.Max(0, totalWidgets - visibleCount);

            showAllItem.IsEnabled = totalWidgets > 0 && hiddenCount > 0;
            hideAllItem.IsEnabled = visibleCount > 0;
        };

        contextMenu.Items.Add(mapFolderItem);
        contextMenu.Items.Add(newWidgetItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(showAllItem);
        contextMenu.Items.Add(hideAllItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(autoStartItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(exitItem);
        _trayWindow = new Window();
        _trayWindow.AppWindow.IsShownInSwitchers = false;
        AppBranding.ApplyWindowIcon(_trayWindow.AppWindow);
        _trayWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));

        _trayIcon = new TaskbarIcon
        {
            Icon = AppBranding.CreateTrayIcon(IsDarkThemeActive()),
            ToolTipText = "DeskBox \u684C\u9762\u6536\u7EB3",
            ContextMenuMode = ContextMenuMode.SecondWindow
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

    private static Style CreateTrayToggleMenuItemStyle()
    {
        return CreateTrayMenuItemStyleCore(typeof(ToggleMenuFlyoutItem), "DefaultToggleMenuFlyoutItemStyle");
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

        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_trayWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await WidgetManager.CreateFolderWidgetAsync(folder.Path);
        }
    }

    public async Task<ContentDialogResult> ShowAppDialogAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var hostRoot = await EnsureDialogHostRootAsync();
        if (hostRoot.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = hostRoot.XamlRoot;

        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (_dialogWindow is not null)
            {
                WindowExtensions.Hide(_dialogWindow);
            }
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(SettingsService, OrganizerService, ThemeService);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    private async void ExitApplication()
    {
        ThemeService.AppearanceChanged -= UpdateTrayIconAppearance;
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

    private async Task<Grid> EnsureDialogHostRootAsync()
    {
        if (_dialogWindow is null || _dialogHostRoot is null)
        {
            _dialogWindow = new Window();
            _dialogWindow.AppWindow.IsShownInSwitchers = false;
            AppBranding.ApplyWindowIcon(_dialogWindow.AppWindow);

            if (_dialogWindow.Content is not Grid hostRoot)
            {
                hostRoot = new Grid
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
                };
                _dialogWindow.Content = hostRoot;
            }

            _dialogHostRoot = hostRoot;
            ThemeService.TrackWindow(_dialogWindow);

            if (_dialogWindow.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
        }

        var dialogHwnd = WindowNative.GetWindowHandle(_dialogWindow);
        var dialogWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(dialogHwnd);
        var workArea = DisplayArea.GetFromWindowId(dialogWindowId, DisplayAreaFallback.Primary).WorkArea;
        _dialogWindow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - 560) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - 320) / 2),
            Math.Min(workArea.Width, 560),
            Math.Min(workArea.Height, 320)));

        _dialogWindow.Activate();

        for (int attempt = 0; attempt < 10 && _dialogHostRoot.XamlRoot is null; attempt++)
        {
            await Task.Delay(16);
        }

        return _dialogHostRoot;
    }

    private List<WidgetConfig> GetTrayWidgetsSnapshot()
    {
        return SettingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id))
            .OrderBy(widget => widget.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"Unhandled exception: {e.Exception}");
        e.Handled = true;
    }
}
