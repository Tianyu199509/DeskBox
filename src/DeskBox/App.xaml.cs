// Copyright (c) DeskBox. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;
using System.Diagnostics;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DrawingPoint = System.Drawing.Point;
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
    private const int TrayContextMenuFallbackOffsetPixels = 24;
    private const int TrayContextMenuEstimatedWidth = (int)TrayMenuItemWidth + 16;
    private const int MaxQueuedLogLines = 4096;
    private const string VerboseLoggingEnvironmentVariable = "DESKBOX_VERBOSE_LOG";
    private static readonly bool EnableVerboseLogging = IsEnabledEnvironmentValue(
        Environment.GetEnvironmentVariable(VerboseLoggingEnvironmentVariable));

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "DeskBox.log");
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> s_logQueue = new();
    private static readonly SemaphoreSlim s_logSignal = new(0);
    private static int s_logWorkerStarted;
    private static int s_pendingLogLineCount;
    private static int s_logDirectoryEnsured;

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static RegisteredWaitHandle? _activationRegistration;

    private TaskbarIcon? _trayIcon;
    private Window? _trayWindow;
    private MenuFlyoutItem? _trayMapFolderItem;
    private MenuFlyoutItem? _trayNewWidgetItem;
    private MenuFlyoutItem? _trayOpenManagedStorageItem;
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
    public QuickCaptureService QuickCaptureService { get; } = new();
    public QuickCaptureClipboardService? QuickCaptureClipboardService { get; private set; }
    public LocalizationService LocalizationService { get; private set; } = null!;
    public ThemeService ThemeService { get; private set; } = null!;
    public GlobalHotkeyService? GlobalHotkeyService { get; private set; }
    public WidgetManager? WidgetManager { get; private set; }
    public SettingsWindow? SettingsWindowInstance => _settingsWindow;

    public static bool IsVerboseLoggingEnabled => EnableVerboseLogging;

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
        Log($"Process integrity {GetProcessIntegrityReport()} pid={Environment.ProcessId} processPath={Environment.ProcessPath ?? "unknown"} baseDir={AppContext.BaseDirectory}");
        Log($"Process parent {GetParentProcessReport()} commandLine={Environment.CommandLine}");
        Log($"UAC {GetUacPolicyReport()}");
        Log($"AppCompat {GetAppCompatReport()}");
    }

    private static string GetProcessIntegrityReport()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return $"isAdminRole={principal.IsInRole(WindowsBuiltInRole.Administrator)} {GetProcessTokenReport(GetCurrentProcess())}";
        }
        catch (Exception ex)
        {
            return $"unknown error={ex.Message}";
        }
    }

    private static string GetAppCompatReport()
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
        string? currentUser = GetAppCompatLayerValue(Registry.CurrentUser, exePath);
        string? localMachine = GetAppCompatLayerValue(Registry.LocalMachine, exePath);

        return $"exe='{exePath}' hkcu={(string.IsNullOrWhiteSpace(currentUser) ? "none" : currentUser)} " +
               $"hklm={(string.IsNullOrWhiteSpace(localMachine) ? "none" : localMachine)}";
    }

    private static string GetParentProcessReport()
    {
        try
        {
            if (!TryGetParentProcessId(Environment.ProcessId, out uint parentProcessId) || parentProcessId == 0)
            {
                return "unknown";
            }

            string parentName = "unknown";
            try
            {
                parentName = Process.GetProcessById((int)parentProcessId).ProcessName;
            }
            catch
            {
            }

            string parentTokenReport = GetProcessTokenReport(parentProcessId);
            return $"ppid={parentProcessId} parent={parentName} {parentTokenReport}";
        }
        catch (Exception ex)
        {
            return $"unknown error={ex.Message}";
        }
    }

    private static string GetProcessTokenReport(uint processId)
    {
        IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return $"token=unavailable error={Marshal.GetLastWin32Error()}";
        }

        try
        {
            return GetProcessTokenReport(processHandle);
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static string GetProcessTokenReport(IntPtr processHandle)
    {
        string tokenElevated = TryGetTokenElevation(processHandle, out bool isTokenElevated)
            ? isTokenElevated.ToString()
            : "unknown";
        string integrityLevel = TryGetIntegrityLevel(processHandle, out string level)
            ? level
            : "unknown";

        return $"tokenElevated={tokenElevated} integrity={integrityLevel}";
    }

    private static string GetUacPolicyReport()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
            object? enableLua = key?.GetValue("EnableLUA");
            object? consentPrompt = key?.GetValue("ConsentPromptBehaviorAdmin");
            object? promptOnSecureDesktop = key?.GetValue("PromptOnSecureDesktop");

            return $"EnableLUA={FormatRegistryValue(enableLua)} " +
                   $"ConsentPromptBehaviorAdmin={FormatRegistryValue(consentPrompt)} " +
                   $"PromptOnSecureDesktop={FormatRegistryValue(promptOnSecureDesktop)}";
        }
        catch (Exception ex)
        {
            return $"unknown error={ex.Message}";
        }
    }

    private static string FormatRegistryValue(object? value)
    {
        return value is null ? "missing" : value.ToString() ?? "unknown";
    }

    private static bool TryGetParentProcessId(int processId, out uint parentProcessId)
    {
        parentProcessId = 0;
        const uint Th32csSnapProcess = 0x00000002;

        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return false;
            }

            do
            {
                if (entry.th32ProcessID == (uint)processId)
                {
                    parentProcessId = entry.th32ParentProcessID;
                    return true;
                }
            }
            while (Process32Next(snapshot, ref entry));

            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string? GetAppCompatLayerValue(RegistryKey? rootKey, string exePath)
    {
        if (rootKey is null)
        {
            return null;
        }

        try
        {
            using var key = rootKey.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            if (key?.GetValue(exePath) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            return $"error:{ex.Message}";
        }

        return null;
    }

    private static bool TryGetTokenElevation(IntPtr processHandle, out bool isElevated)
    {
        isElevated = false;
        if (!OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            int length = Marshal.SizeOf<TokenElevation>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenInformationClass.TokenElevation, buffer, length, out _))
                {
                    return false;
                }

                var elevation = Marshal.PtrToStructure<TokenElevation>(buffer);
                isElevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static bool TryGetIntegrityLevel(IntPtr processHandle, out string level)
    {
        level = string.Empty;
        if (!OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            _ = GetTokenInformation(tokenHandle, TokenInformationClass.TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenInformationClass.TokenIntegrityLevel, buffer, length, out _))
                {
                    return false;
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                IntPtr subAuthorityCount = GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCount == IntPtr.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(subAuthorityCount);
                if (count == 0)
                {
                    return false;
                }

                IntPtr integrityRidPointer = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                int integrityRid = Marshal.ReadInt32(integrityRidPointer);
                level = FormatIntegrityLevel(integrityRid);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static string FormatIntegrityLevel(int integrityRid)
    {
        return integrityRid switch
        {
            < SecurityMandatoryLowRid => $"Untrusted(0x{integrityRid:X})",
            < SecurityMandatoryMediumRid => $"Low(0x{integrityRid:X})",
            < SecurityMandatoryHighRid => $"Medium(0x{integrityRid:X})",
            < SecurityMandatorySystemRid => $"High(0x{integrityRid:X})",
            < SecurityMandatoryProtectedProcessRid => $"System(0x{integrityRid:X})",
            _ => $"Protected(0x{integrityRid:X})"
        };
    }

    private const uint TokenQuery = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SecurityMandatoryLowRid = 0x1000;
    private const int SecurityMandatoryMediumRid = 0x2000;
    private const int SecurityMandatoryHighRid = 0x3000;
    private const int SecurityMandatorySystemRid = 0x4000;
    private const int SecurityMandatoryProtectedProcessRid = 0x5000;

    private enum TokenInformationClass
    {
        TokenElevation = 20,
        TokenIntegrityLevel = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

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

        return WidgetManager?.Widgets.Values.Any(entry => entry.Window.WindowHandle == rootHwnd) == true ||
               WidgetManager?.QuickCaptureWidgets.Values.Any(entry => entry.Window.WindowHandle == rootHwnd) == true;
    }

    public static void Log(string msg)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            if (Interlocked.Increment(ref s_pendingLogLineCount) > MaxQueuedLogLines)
            {
                Interlocked.Decrement(ref s_pendingLogLineCount);
                return;
            }

            s_logQueue.Enqueue(line);
            EnsureLogWorkerStarted();
            s_logSignal.Release();
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

    /// <summary>
    /// Safely execute an async action from an event handler, catching and logging any exceptions.
    /// Use this instead of async void to prevent unhandled exceptions from crashing the app.
    /// </summary>
    public static async void SafeFireAndForget(Func<Task> action, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log($"[SafeFireAndForget] Unhandled exception in {caller}: {ex}");
        }
    }

    private static void EnsureLogWorkerStarted()
    {
        if (Interlocked.CompareExchange(ref s_logWorkerStarted, 1, 0) == 0)
        {
            _ = Task.Run(ProcessLogQueueAsync);
        }
    }

    private static async Task ProcessLogQueueAsync()
    {
        while (true)
        {
            await s_logSignal.WaitAsync().ConfigureAwait(false);
            DrainLogQueue();
        }
    }

    private static void DrainLogQueue()
    {
        var builder = new System.Text.StringBuilder();
        while (s_logQueue.TryDequeue(out string? line))
        {
            Interlocked.Decrement(ref s_pendingLogLineCount);
            builder.Append(line);
        }

        if (builder.Length == 0)
        {
            return;
        }

        try
        {
            EnsureLogDirectory();
            File.AppendAllText(LogPath, builder.ToString());
        }
        catch
        {
        }
    }

    private static void EnsureLogDirectory()
    {
        if (Volatile.Read(ref s_logDirectoryEnsured) != 0)
        {
            return;
        }

        string? dir = Path.GetDirectoryName(LogPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        Volatile.Write(ref s_logDirectoryEnsured, 1);
    }

    private static bool IsEnabledEnvironmentValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Trim() is "1" or "true" or "TRUE" or "yes" or "YES" or "on" or "ON";
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

            // Phase 1: Load settings (must complete first)
            await SettingsService.LoadAsync();

            // Phase 2: Initialize services that depend on settings (parallel)
            ThemeService = new ThemeService(SettingsService);
            LocalizationService = new LocalizationService(SettingsService);
            LocalizationService.LanguageChanged += OnLanguageChanged;

            var quickCaptureService = QuickCaptureService;
            var themeService = ThemeService;
            var localizationService = LocalizationService;

            // Parallel: theme refresh + clipboard service init
            var themeTask = Task.Run(() => themeService.RefreshAppearance());
            var clipboardTask = Task.Run(() =>
            {
                var clipboardService = new QuickCaptureClipboardService(SettingsService, quickCaptureService);
                clipboardService.Refresh();
                return clipboardService;
            });

            // Parallel: independent UI setup
            CreateTrayIcon();
            RegisterActivationListener();

            await Task.WhenAll(themeTask, clipboardTask);

            QuickCaptureClipboardService = await clipboardTask;
            GlobalHotkeyService = new GlobalHotkeyService(SettingsService, localizationService, ToggleTrayWidgetsAsync);
            WidgetManager = new WidgetManager(SettingsService, FileService, OrganizerService, themeService, quickCaptureService, localizationService);
            WidgetManager.TrayLayerStateChanged += UpdateTrayLayerStateText;

            // Phase 3: Restore widgets
            WidgetManager.SyncStorageFolderEntries();
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
                SettingsService.Settings.HasCompletedOnboarding = true;
                await SettingsService.SaveAsync();
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

        var openManagedStorageItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.OpenManagedStorage"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Folder),
            Style = trayMenuItemStyle
        };
        openManagedStorageItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, OpenManagedStorageFromTray);

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
        contextMenu.Items.Add(openManagedStorageItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(exitItem);

        _trayMapFolderItem = mapFolderItem;
        _trayNewWidgetItem = newWidgetItem;
        _trayOpenManagedStorageItem = openManagedStorageItem;
        _traySettingsItem = settingsItem;
        _trayExitItem = exitItem;

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

        GlobalHotkeyService?.Attach(WindowNative.GetWindowHandle(_trayWindow));

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

    private void ShowTrayContextMenuFromTray()
    {
        if (_trayIcon is null ||
            !Win32Helper.GetCursorPos(out var cursor))
        {
            return;
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

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        var trayIconProperty = _trayIcon.GetType().GetProperty("TrayIcon", flags);
        object? trayIcon = trayIconProperty?.GetValue(_trayIcon);
        if (trayIcon is null)
        {
            return false;
        }

        var trayIconType = trayIcon.GetType();
        object? windowHandleValue = trayIconType.GetProperty("WindowHandle", flags)?.GetValue(trayIcon);
        object? idValue = trayIconType.GetProperty("Id", flags)?.GetValue(trayIcon);

        windowHandle = windowHandleValue switch
        {
            IntPtr ptr => ptr,
            _ => IntPtr.Zero
        };

        if (idValue is Guid guid)
        {
            id = guid;
        }

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

    private async Task ToggleTrayWidgetsAsync()
    {
        if (WidgetManager is null)
        {
            return;
        }

        if (WidgetManager.ShouldHideWidgetsForTrayToggle())
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

        if (_trayOpenManagedStorageItem is not null)
        {
            _trayOpenManagedStorageItem.Text = LocalizationService.T("Tray.OpenManagedStorage");
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
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        if (WidgetManager?.WidgetsRaisedFromTray == true)
        {
            settingsWindow.ActivateFromTray();
            return;
        }

        settingsWindow.Activate();
    }

    private void OpenSettingsFromTray()
    {
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ActivateFromTray();
    }

    private SettingsWindow CreateSettingsWindow()
    {
        _settingsWindow = new SettingsWindow(SettingsService, ThemeService, LocalizationService);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ScheduleLightMemoryCleanup();
        };
        return _settingsWindow;
    }

    public void RefreshSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Close();
        }

        OpenSettings();
    }

    private void OpenManagedStorageFromTray()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(SettingsService.Settings.DefaultManagedStorageRootPath);
        Directory.CreateDirectory(path);
        Win32Helper.OpenFile(path);
    }

    public void ShowSettings()
    {
        OpenSettings();
    }

    public void ShowOnboarding()
    {
        bool shouldRestartIntro = _onboardingWindow is not null;
        if (_onboardingWindow is null)
        {
            _onboardingWindow = new OnboardingWindow(SettingsService, LocalizationService);
            _onboardingWindow.Closed += (_, _) =>
            {
                _onboardingWindow = null;
                ScheduleLightMemoryCleanup();
            };
            ThemeService.TrackWindow(_onboardingWindow);
        }

        _onboardingWindow.Activate();
        if (shouldRestartIntro)
        {
            _onboardingWindow.RestartIntro();
        }
    }

    private static void ScheduleLightMemoryCleanup()
    {
        App.UiDispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(2000);
            GC.Collect(1, GCCollectionMode.Optimized, blocking: false, compacting: false);
        });
    }

    private async void ExitApplication()
    {
        Log("ExitApplication invoked");
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

        string style = SettingsService.Settings.TrayIconStyle ?? "System";
        _trayIcon.Icon = AppBranding.CreateTrayIcon(style, IsDarkThemeActive());
    }

    public void UpdateTrayIcon()
    {
        UpdateTrayIconAppearance();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"Unhandled exception: {e.Exception}");
        e.Handled = true;
    }
}
