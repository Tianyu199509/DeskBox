// Copyright (c) DeskBox. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;
using System.Diagnostics;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
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
    private const double TrayMenuSeparatorHeight = 12;
    private const double TrayMenuMinimumHeight = 240;
    private const int TrayContextMenuFallbackOffsetPixels = 24;
    private const int TrayContextMenuEstimatedWidth = (int)TrayMenuItemWidth + 16;
    private const int MaxQueuedLogLines = 4096;
    private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB before rotation
    private const string TodoReminderNotificationSource = "source=todoReminder";
    private const string TodoReminderSourceValue = "todoReminder";
    private const string TodoReminderActionComplete = "complete";
    private const string TodoReminderActionSnooze = "snooze";
    private const string TodoReminderActionSnooze10 = "snooze10";
    private const string TodoReminderSnoozeInputId = "todoSnooze";
    private const string TodoReminderSnooze10Minutes = "10m";
    private const string TodoReminderSnooze30Minutes = "30m";
    private const string TodoReminderSnooze1Hour = "1h";
    private const string TodoReminderSnoozeTomorrow = "tomorrow";
    private const string TodoSnoozeConfirmationNotificationSource = "todoSnoozeConfirmation";
    private const string TodoSnoozeConfirmationNotificationGroup = "todo-feedback";
    private const string TodoSnoozeConfirmationNotificationTag = "todo-snooze-confirmation";
    private const string PendingNativeNotificationActivationFileName = "pending-notification-activation.txt";
    private const string PendingJumpListArgumentFileName = "pending-jumplist-arg.txt";
    private const string VerboseLoggingEnvironmentVariable = "DESKBOX_VERBOSE_LOG";
    private static readonly bool EnableVerboseLogging = IsEnabledEnvironmentValue(
        Environment.GetEnvironmentVariable(VerboseLoggingEnvironmentVariable));

    private static readonly string LogPath = DeskBoxDataPathService.Current.LogFilePath;
    private static readonly string PendingNativeNotificationActivationPath = Path.Combine(
        DeskBoxDataPathService.Current.RootPath,
        PendingNativeNotificationActivationFileName);
    private static readonly string PendingJumpListArgumentPath = Path.Combine(
        DeskBoxDataPathService.Current.RootPath,
        PendingJumpListArgumentFileName);
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
    private readonly Dictionary<WidgetKind, MenuFlyoutItem> _trayCreateWidgetItems = [];
    private MenuFlyoutItem? _trayOpenManagedStorageItem;
    private MenuFlyoutItem? _trayUpdateItem;
    private MenuFlyoutItem? _traySettingsItem;
    private MenuFlyoutItem? _trayExitItem;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboardingWindow;
    private NativeAppNotificationService? _nativeNotificationService;
    private TodoReminderService? _todoReminderService;
    private DisplayAreaWatcherService? _displayAreaWatcher;
    private bool _widgetsRaisedFromTray;
    private bool _hasUpdateAvailable;
    private bool _updateNotificationShown;
    private string _availableUpdateVersion = string.Empty;

    public static new App Current => (App)Application.Current;

    public static Microsoft.UI.Dispatching.DispatcherQueue UiDispatcherQueue { get; private set; } = null!;

    public bool IsStartupMode { get; set; }

    public AppDistributionService DistributionService { get; } = AppDistributionService.Current;
    public SettingsService SettingsService { get; } = new();
    public DeskBoxDataBackupService DataBackupService { get; } = new();
    public DeskBoxAttachmentHealthService AttachmentHealthService { get; } = new();
    public FileService FileService { get; } = new();
    public OrganizerService OrganizerService { get; }
    public IAppUpdateService AppUpdateService { get; } = AppUpdateServiceFactory.Create(AppDistributionService.Current);
    public QuickCaptureService QuickCaptureService { get; } = new();
    public QuickCaptureClipboardService? QuickCaptureClipboardService { get; private set; }
    public LocalizationService LocalizationService { get; private set; } = null!;
    public ThemeService ThemeService { get; private set; } = null!;
    public GlobalHotkeyService? GlobalHotkeyService { get; private set; }
    public WidgetManager? WidgetManager { get; private set; }
    public ResizeGuideOverlayService ResizeGuideOverlay { get; } = new();
    public NativeAppNotificationService? NativeNotificationService => _nativeNotificationService;
    public DisplayAreaWatcherService? DisplayAreaWatcher => _displayAreaWatcher;
    public TodoReminderService? TodoReminderService => _todoReminderService;
    public SettingsWindow? SettingsWindowInstance => _settingsWindow;

    public static bool IsVerboseLoggingEnabled => EnableVerboseLogging;

    public App()
    {
        // Register AUMID early so the taskbar button and Jump List work
        // for both packaged (MSIX) and unpackaged (Direct) distributions.
        JumpListService.RegisterAppUserModelId();

        Log("App() constructor start");
        bool launchedForStartup = IsStartupLaunch(Environment.GetCommandLineArgs());
        string? nativeNotificationActivationArguments = TryGetCurrentNativeNotificationActivationArguments();
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskBox_Activate_Event_7F3A9B2E");
        _singleInstanceMutex = new Mutex(true, "DeskBox_SingleInstance_Mutex_7F3A9B2E", out bool createdNew);
        if (!createdNew)
        {
            if (!string.IsNullOrWhiteSpace(nativeNotificationActivationArguments))
            {
                StorePendingNativeNotificationActivationArguments(nativeNotificationActivationArguments);
            }
            else if (launchedForStartup)
            {
                Log("Another instance running; startup launch exiting silently");
                Environment.Exit(0);
            }
            else
            {
                // Check for Jump List activation arguments from command line
                string? jumpListArg = JumpListService.TryGetJumpListArgument(
                    string.Join(' ', Environment.GetCommandLineArgs()));
                if (jumpListArg is not null)
                {
                    StorePendingJumpListArgument(jumpListArg);
                }
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
        StartupService.Configure(StartupServiceFactory.Create(DistributionService));
        OrganizerService = new OrganizerService(SettingsService, FileService);
        AppUpdateService.CheckCompleted += OnUpdateCheckCompleted;
        UnhandledException += OnUnhandledException;
        Log($"Distribution channel={DistributionService.ChannelName} packaged={DistributionService.IsPackaged}");
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
               WidgetManager?.QuickCaptureWidgets.Values.Any(entry => entry.Window.WindowHandle == rootHwnd) == true ||
               WidgetManager?.ContentWidgets.Values.Any(window => window.WindowHandle == rootHwnd) == true;
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
            TryRotateLogFileIfNeeded();
            File.AppendAllText(LogPath, builder.ToString());
        }
        catch
        {
        }
    }

    private static void TryRotateLogFileIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return;
            }

            var info = new FileInfo(LogPath);
            if (info.Length < MaxLogFileSizeBytes)
            {
                return;
            }

            // Rotate: current → .1, old .1 is deleted
            string rotatedPath = LogPath + ".1";
            if (File.Exists(rotatedPath))
            {
                File.Delete(rotatedPath);
            }

            File.Move(LogPath, rotatedPath);
        }
        catch
        {
            // If rotation fails (e.g., file locked), continue appending to the current file.
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

    private bool _isLaunched;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_isLaunched)
        {
            Log("OnLaunched skipped: already launched");
            return;
        }
        _isLaunched = true;

        using var perfScope = PerformanceLogger.Measure("App.OnLaunched", $"startup={IsStartupLaunch(args.Arguments)}");
        Log("OnLaunched start");

        try
        {
            IsStartupMode = IsStartupLaunch(args.Arguments);
            UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // A prepared restore is applied before any service reads or normalizes app data.
            DeskBoxRestoreApplyResult restoreResult = await DataBackupService.ApplyPendingRestoreAsync();

            // Capture the previous session's data before any startup normalization writes.
            await DataBackupService.CreateAutomaticSnapshotIfDueAsync();

            // Phase 1: Load settings (must complete first)
            await SettingsService.LoadAsync();

            // Sync resize snap setting
            ResizeGuideOverlay.IsSnapEnabled = SettingsService.Settings.ResizeSnapEnabled;

            // Phase 2: Initialize services that depend on settings (parallel)
            ThemeService = new ThemeService(SettingsService);
            LocalizationService = new LocalizationService(SettingsService);
            LocalizationService.LanguageChanged += OnLanguageChanged;

            var quickCaptureService = QuickCaptureService;
            var themeService = ThemeService;
            var localizationService = LocalizationService;

            // Parallel: theme refresh only. Clipboard event subscription must stay on the UI thread.
            var themeTask = Task.Run(() => themeService.RefreshAppearance());
            QuickCaptureClipboardService = new QuickCaptureClipboardService(SettingsService, quickCaptureService);
            QuickCaptureClipboardService.Refresh();

            // Parallel: independent UI setup
            CreateTrayIcon();
            RegisterActivationListener();

            await themeTask;
            if (GlobalHotkeyService is null)
            {
                try
                {
                    GlobalHotkeyService = new GlobalHotkeyService(SettingsService, localizationService, ToggleTrayWidgetsAsync);
                    Log("[Init] GlobalHotkeyService created");
                }
                catch (Exception ex)
                {
                    Log($"[Init] GlobalHotkeyService creation failed: {ex}");
                }
            }

            if (GlobalHotkeyService is { } hotkeySvc && _trayWindow is not null)
            {
                try
                {
                    var trayHwnd = WindowNative.GetWindowHandle(_trayWindow);
                    if (trayHwnd != IntPtr.Zero && !hotkeySvc.IsRegistered)
                    {
                        Log($"[Init] Late-attaching GlobalHotkeyService to tray hwnd=0x{trayHwnd.ToInt64():X}");
                        hotkeySvc.Attach(trayHwnd);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Init] GlobalHotkeyService late-attach failed: {ex}");
                }
            }
            WidgetManager = new WidgetManager(SettingsService, FileService, OrganizerService, themeService, quickCaptureService, localizationService);
            WidgetManager.TrayLayerStateChanged += UpdateTrayLayerStateText;

            // Phase 3: Restore widgets
            WidgetManager.SyncStorageFolderEntries();
            await WidgetManager.RestoreWidgetsAsync();

            StartTodoReminderService();
            StartNativeNotificationService();
            ShowDataRestoreResultNotification(restoreResult);

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

            ScheduleBackgroundUpdateCheck();
            ScheduleMemoryDiagnostics();
            SchedulePeriodicMemoryCleanup();

            // Start display area watcher for hot-plug detection
            _displayAreaWatcher = new DisplayAreaWatcherService(UiDispatcherQueue);
            _displayAreaWatcher.DisplaysChanged += OnDisplaysChanged;
            _displayAreaWatcher.Start();

            // Configure taskbar Jump List with quick actions
            _ = JumpListService.ConfigureAsync(LocalizationService);

            // Handle Jump List activation on first launch (not second instance)
            string? firstLaunchJumpArg = JumpListService.TryGetJumpListArgument(args.Arguments);
            if (firstLaunchJumpArg is not null)
            {
                _ = JumpListService.HandleActivationAsync(firstLaunchJumpArg);
            }

            Log("OnLaunched completed successfully");
        }
        catch (Exception ex)
        {
            Log($"Exception in OnLaunched: {ex}");
        }
    }

    /// <summary>
    /// Called when the set of displays changes (hot-plug, resolution change, etc.).
    /// Invalidates caches and triggers widget repositioning.
    /// </summary>
    private async void OnDisplaysChanged()
    {
        try
        {
            Log($"[DisplayAreaWatcher] Displays changed, triggering widget reposition");

            // Invalidate the desktop icon view cache since work areas may have changed
            WidgetLayerService.InvalidateDesktopIconViewCache();

            // Reposition all widgets to ensure they're on visible screens
            if (WidgetManager is not null)
            {
                await WidgetManager.RestoreWidgetPositionsAsync();
            }
        }
        catch (Exception ex)
        {
            Log($"[DisplayAreaWatcher] OnDisplaysChanged failed: {ex}");
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _memoryDiagnosticTimer;
    private CancellationTokenSource? _periodicGcCts;

    /// <summary>
    /// When perf logging is enabled, samples memory and cache statistics
    /// every 30 seconds so long-running memory trends can be observed.
    /// </summary>
    private void ScheduleMemoryDiagnostics()
    {
        if (!PerformanceLogger.IsEnabled)
        {
            return;
        }

        var dq = UiDispatcherQueue;
        if (dq is null)
        {
            return;
        }

        _memoryDiagnosticTimer = dq.CreateTimer();
        _memoryDiagnosticTimer.Interval = TimeSpan.FromSeconds(30);
        _memoryDiagnosticTimer.IsRepeating = true;
        _memoryDiagnosticTimer.Tick += (_, _) => PerformanceLogger.SampleMemory();
        _memoryDiagnosticTimer.Start();
        Log("[Perf] Memory diagnostics timer started (30s interval)");
    }

    /// <summary>
    /// Starts a background loop that periodically triggers GC to release
    /// native Composition/COM resources that the WinUI framework holds via
    /// finalizable wrappers.  Without this, high-frequency UI updates (e.g.
    /// media artwork) accumulate native memory that the GC only
    /// reclaims during gen2 collection, which may not happen for a long time.
    /// </summary>
    private void SchedulePeriodicMemoryCleanup()
    {
        _periodicGcCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for initial startup to settle before first collection.
                await Task.Delay(TimeSpan.FromMinutes(1), _periodicGcCts.Token);

                while (!_periodicGcCts.Token.IsCancellationRequested)
                {
                    // Run on a background thread to avoid blocking the UI.
                    // Two-pass collection: first pass promotes finalizable
                    // objects to the freachable queue, second pass reclaims
                    // them after finalizers have run.
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                    Win32Helper.TrimCurrentProcessWorkingSet();

                    if (PerformanceLogger.IsEnabled)
                    {
                        PerformanceLogger.SampleMemory();
                        App.Log("[Perf] Periodic GC cleanup completed");
                    }

                    await Task.Delay(TimeSpan.FromMinutes(2), _periodicGcCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                Log($"[Perf] Periodic GC cleanup error: {ex}");
            }
        });
        Log("[Perf] Periodic memory cleanup scheduled (2 min interval)");
    }

    private void ScheduleBackgroundUpdateCheck()
    {
        if (DistributionService.IsMicrosoftStore)
        {
            return;
        }

        if (!SettingsService.Settings.AutoCheckForUpdates)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IsStartupMode ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(12));
                var result = await AppUpdateService.CheckForUpdatesAsync();
                SettingsService.Settings.LastUpdateCheckAt = DateTimeOffset.Now;
                SettingsService.SaveDebounced(notifySubscribers: false);

                if (result.IsUpdateAvailable && result.Manifest is not null)
                {
                    Log($"[Update] New version available: {result.Manifest.Version}");
                }
                else if (result.Status == AppUpdateCheckStatus.Failed)
                {
                    Log($"[Update] Background check failed: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Update] Background check crashed: {ex}");
            }
        });
    }

    private void StartTodoReminderService()
    {
        _todoReminderService?.Dispose();
        _todoReminderService = new TodoReminderService(
            SettingsService,
            LocalizationService,
            UiDispatcherQueue,
            ShowTodoReminderNotification);
        _todoReminderService.Start();
    }

    private void StartNativeNotificationService()
    {
        _nativeNotificationService?.Dispose();
        _nativeNotificationService = new NativeAppNotificationService(
            activation => HandleNativeNotificationActivation(activation.Arguments, activation.UserInput));
        if (_nativeNotificationService.Register())
        {
            HandleCurrentNativeNotificationActivation();
        }
    }

    private void HandleCurrentNativeNotificationActivation()
    {
        NativeAppNotificationActivation? activation = TryGetCurrentNativeNotificationActivation();
        if (activation is not null)
        {
            HandleNativeNotificationActivation(activation.Arguments, activation.UserInput);
        }
    }

    private void HandleNativeNotificationActivation(
        string arguments,
        IReadOnlyDictionary<string, string> userInput)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => HandleNativeNotificationActivation(arguments, userInput));
            return;
        }

        App.Log($"[Notification] Native notification activated args={arguments}");
        var notificationArguments = ParseNotificationArguments(arguments);
        if (IsTodoReminderNotification(notificationArguments))
        {
            notificationArguments.TryGetValue("widgetId", out string? widgetId);
            notificationArguments.TryGetValue("itemId", out string? itemId);
            if (notificationArguments.TryGetValue("action", out string? action))
            {
                if (string.Equals(action, TodoReminderActionComplete, StringComparison.OrdinalIgnoreCase))
                {
                    _ = CompleteTodoReminderFromNotificationAsync(widgetId, itemId);
                    return;
                }

                if (string.Equals(action, TodoReminderActionSnooze10, StringComparison.OrdinalIgnoreCase))
                {
                    _ = SnoozeTodoReminderFromNotificationAsync(widgetId, itemId, TimeSpan.FromMinutes(10));
                    return;
                }

                if (string.Equals(action, TodoReminderActionSnooze, StringComparison.OrdinalIgnoreCase))
                {
                    string snoozeSelection = userInput.TryGetValue(TodoReminderSnoozeInputId, out string? selected)
                        ? selected
                        : TodoReminderSnooze10Minutes;
                    _ = SnoozeTodoReminderFromNotificationAsync(widgetId, itemId, snoozeSelection);
                    return;
                }
            }

            bool preferTodayFilter = notificationArguments.TryGetValue("view", out string? view) &&
                                     string.Equals(view, "today", StringComparison.OrdinalIgnoreCase);
            _ = ShowTodoWidgetFromNotificationAsync(widgetId, itemId, preferTodayFilter);
        }
        else
        {
            _ = RaiseTrayWidgetsAsync();
        }
    }

    private async Task CompleteTodoReminderFromNotificationAsync(string? widgetId, string? itemId)
    {
        try
        {
            if (_todoReminderService is not null)
            {
                bool completed = await _todoReminderService.CompleteAsync(widgetId, itemId);
                if (completed)
                {
                    await RefreshLoadedTodoWidgetAfterNotificationActionAsync(widgetId);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to complete Todo reminder: {ex}");
        }
    }

    private async Task ShowTodoWidgetFromNotificationAsync(
        string? widgetId = null,
        string? itemId = null,
        bool preferTodayFilter = false)
    {
        try
        {
            if (WidgetManager is not null)
            {
                await WidgetManager.ShowTodoReminderTargetAsync(widgetId, itemId, preferTodayFilter);
            }
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to show Todo from native notification: {ex}");
        }
    }

    private async Task SnoozeTodoReminderFromNotificationAsync(
        string? widgetId,
        string? itemId,
        string snoozeSelection)
    {
        try
        {
            if (_todoReminderService is null)
            {
                return;
            }

            DateTimeOffset? snoozedUntil = GetSnoozedUntilFromNotificationSelection(snoozeSelection);
            if (snoozedUntil is { } until)
            {
                bool snoozed = await _todoReminderService.SnoozeUntilAsync(widgetId, itemId, until);
                if (snoozed)
                {
                    await RefreshLoadedTodoWidgetAfterNotificationActionAsync(widgetId);
                    ShowTodoSnoozeConfirmationNotification(GetTodoSnoozeSelectionText(snoozeSelection));
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to snooze Todo reminder: {ex}");
        }
    }

    private async Task SnoozeTodoReminderFromNotificationAsync(
        string? widgetId,
        string? itemId,
        TimeSpan snoozeFor)
    {
        try
        {
            if (_todoReminderService is not null)
            {
                bool snoozed = await _todoReminderService.SnoozeAsync(widgetId, itemId, snoozeFor);
                if (snoozed)
                {
                    await RefreshLoadedTodoWidgetAfterNotificationActionAsync(widgetId);
                    ShowTodoSnoozeConfirmationNotification(GetTodoSnoozeDurationText(snoozeFor));
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to snooze Todo reminder: {ex}");
        }
    }

    private async Task RefreshLoadedTodoWidgetAfterNotificationActionAsync(string? widgetId)
    {
        if (WidgetManager is null ||
            string.IsNullOrWhiteSpace(widgetId) ||
            !WidgetManager.ContentWidgets.TryGetValue(widgetId, out var window))
        {
            return;
        }

        await (window.CurrentContent?.RefreshAsync() ?? Task.CompletedTask);
    }

    private void ShowTodoSnoozeConfirmationNotification(string snoozeText)
    {
        string title = LocalizationService.T("Todo.Menu.Snooze");
        string message = LocalizationService.Format("Todo.Snooze.Set", snoozeText);
        var arguments = new Dictionary<string, string>
        {
            ["source"] = TodoSnoozeConfirmationNotificationSource
        };

        if (_nativeNotificationService?.TryShow(
                title,
                message,
                arguments,
                options: new NativeAppNotificationOptions(
                    TodoSnoozeConfirmationNotificationTag,
                    TodoSnoozeConfirmationNotificationGroup)) == true)
        {
            Log($"[TodoReminder] Snooze confirmation notification shown text={snoozeText}");
            return;
        }

        if (_trayIcon is null)
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
                timeout: TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            Log($"[TodoReminder] Snooze confirmation fallback failed: {ex.Message}");
        }
    }

    private string GetTodoSnoozeSelectionText(string? selection)
    {
        return selection switch
        {
            TodoReminderSnooze30Minutes => LocalizationService.T("Todo.Snooze.30Minutes"),
            TodoReminderSnooze1Hour => LocalizationService.T("Todo.Snooze.OneHour"),
            TodoReminderSnoozeTomorrow => LocalizationService.T("Todo.Snooze.Tomorrow"),
            _ => LocalizationService.T("Todo.Snooze.10Minutes")
        };
    }

    private string GetTodoSnoozeDurationText(TimeSpan snoozeFor)
    {
        if (snoozeFor >= TimeSpan.FromMinutes(59) && snoozeFor <= TimeSpan.FromMinutes(61))
        {
            return LocalizationService.T("Todo.Snooze.OneHour");
        }

        if (snoozeFor >= TimeSpan.FromMinutes(29) && snoozeFor <= TimeSpan.FromMinutes(31))
        {
            return LocalizationService.T("Todo.Snooze.30Minutes");
        }

        return LocalizationService.T("Todo.Snooze.10Minutes");
    }

    private static DateTimeOffset? GetSnoozedUntilFromNotificationSelection(string? selection)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return selection switch
        {
            TodoReminderSnooze30Minutes => now.AddMinutes(30),
            TodoReminderSnooze1Hour => now.AddHours(1),
            TodoReminderSnoozeTomorrow => new DateTimeOffset(DateTime.Now.Date.AddDays(1).AddHours(9)),
            _ => now.AddMinutes(10)
        };
    }

    private void ShowTodoReminderNotification(TodoReminderNotification notification)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => ShowTodoReminderNotification(notification));
            return;
        }

        var arguments = new Dictionary<string, string>
        {
            ["source"] = TodoReminderSourceValue,
            ["widgetId"] = notification.WidgetId ?? string.Empty,
            ["itemId"] = notification.ItemId ?? string.Empty,
            ["view"] = notification.HasTodayDueItem ? "today" : "all"
        };
        List<NativeAppNotificationAction>? actions = null;
        List<NativeAppNotificationComboBox>? comboBoxes = null;
        if (notification.Count == 1 && !string.IsNullOrWhiteSpace(notification.ItemId))
        {
            comboBoxes =
            [
                new(
                    TodoReminderSnoozeInputId,
                    LocalizationService.T("Todo.Menu.Snooze"),
                    TodoReminderSnooze10Minutes,
                    [
                        new NativeAppNotificationComboBoxItem(TodoReminderSnooze10Minutes, LocalizationService.T("Todo.Snooze.10Minutes")),
                        new NativeAppNotificationComboBoxItem(TodoReminderSnooze30Minutes, LocalizationService.T("Todo.Snooze.30Minutes")),
                        new NativeAppNotificationComboBoxItem(TodoReminderSnooze1Hour, LocalizationService.T("Todo.Snooze.OneHour")),
                        new NativeAppNotificationComboBoxItem(TodoReminderSnoozeTomorrow, LocalizationService.T("Todo.Snooze.Tomorrow"))
                    ])
            ];
            actions =
            [
                new(
                    LocalizationService.T("Todo.Menu.MarkCompleted"),
                    new Dictionary<string, string>
                    {
                        ["source"] = TodoReminderSourceValue,
                        ["action"] = TodoReminderActionComplete,
                        ["widgetId"] = notification.WidgetId ?? string.Empty,
                        ["itemId"] = notification.ItemId ?? string.Empty
                    }),
                new(
                    LocalizationService.T("Todo.Menu.Snooze"),
                    new Dictionary<string, string>
                    {
                        ["source"] = TodoReminderSourceValue,
                        ["action"] = TodoReminderActionSnooze,
                        ["widgetId"] = notification.WidgetId ?? string.Empty,
                        ["itemId"] = notification.ItemId ?? string.Empty
                    },
                    TodoReminderSnoozeInputId)
            ];
        }

        if (_nativeNotificationService?.TryShow(
                notification.Title,
                notification.Message,
                arguments,
                actions,
                comboBoxes) == true)
        {
            Log($"[TodoReminder] Native notification shown count={notification.Count} widget={notification.WidgetId ?? "none"} item={notification.ItemId ?? "none"}");
            return;
        }

        if (_trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                notification.Title,
                notification.Message,
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: true,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(8));
            Log($"[TodoReminder] Tray notification fallback shown count={notification.Count}");
        }
        catch (Exception ex)
        {
            Log($"[TodoReminder] Tray notification failed: {ex.Message}");
        }
    }

    private static bool IsTodoReminderNotification(IReadOnlyDictionary<string, string> arguments)
    {
        return arguments.TryGetValue("source", out string? source) &&
               string.Equals(source, TodoReminderSourceValue, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseNotificationArguments(string arguments)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return parsed;
        }

        foreach (var pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separatorIndex]);
            string value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                parsed[key] = value;
            }
        }

        if (parsed.Count == 0 &&
            arguments.Contains(TodoReminderNotificationSource, StringComparison.OrdinalIgnoreCase))
        {
            parsed["source"] = TodoReminderSourceValue;
        }

        return parsed;
    }

    private static NativeAppNotificationActivation? TryGetCurrentNativeNotificationActivation()
    {
        try
        {
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind == ExtendedActivationKind.AppNotification &&
                activatedArgs.Data is AppNotificationActivatedEventArgs notificationArgs)
            {
                var userInput = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var input in notificationArgs.UserInput)
                {
                    if (!string.IsNullOrWhiteSpace(input.Key))
                    {
                        userInput[input.Key] = input.Value ?? string.Empty;
                    }
                }

                return new NativeAppNotificationActivation(notificationArgs.Argument, userInput);
            }
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to read native notification activation args: {ex.Message}");
        }

        return null;
    }

    private static string? TryGetCurrentNativeNotificationActivationArguments()
    {
        return TryGetCurrentNativeNotificationActivation()?.Arguments;
    }

    private static void StorePendingNativeNotificationActivationArguments(string arguments)
    {
        try
        {
            Directory.CreateDirectory(DeskBoxDataPathService.Current.RootPath);
            string tempPath = Path.Combine(
                DeskBoxDataPathService.Current.RootPath,
                $"pending-notification-activation.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, arguments);
            File.Move(tempPath, PendingNativeNotificationActivationPath, overwrite: true);
            Log($"[Notification] Forwarded native notification activation to running instance args={arguments}");
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to forward native notification activation args: {ex}");
        }
    }

    private static string? TakePendingNativeNotificationActivationArguments()
    {
        try
        {
            if (!File.Exists(PendingNativeNotificationActivationPath))
            {
                return null;
            }

            string arguments = File.ReadAllText(PendingNativeNotificationActivationPath);
            File.Delete(PendingNativeNotificationActivationPath);
            return string.IsNullOrWhiteSpace(arguments) ? null : arguments;
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to read forwarded native notification activation args: {ex}");
            return null;
        }
    }

    private static void StorePendingJumpListArgument(string argument)
    {
        try
        {
            Directory.CreateDirectory(DeskBoxDataPathService.Current.RootPath);
            File.WriteAllText(PendingJumpListArgumentPath, argument);
            Log($"[JumpList] Forwarded jump list activation to running instance arg={argument}");
        }
        catch (Exception ex)
        {
            Log($"[JumpList] Failed to forward jump list activation: {ex}");
        }
    }

    private static string? TakePendingJumpListArgument()
    {
        try
        {
            if (!File.Exists(PendingJumpListArgumentPath))
            {
                return null;
            }

            string argument = File.ReadAllText(PendingJumpListArgumentPath);
            File.Delete(PendingJumpListArgumentPath);
            return string.IsNullOrWhiteSpace(argument) ? null : argument;
        }
        catch (Exception ex)
        {
            Log($"[JumpList] Failed to read forwarded jump list activation: {ex}");
            return null;
        }
    }

    private void OnUpdateCheckCompleted(AppUpdateCheckResult result)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => OnUpdateCheckCompleted(result));
            return;
        }

        if (result.IsUpdateAvailable && result.Manifest is not null)
        {
            SetUpdateAvailableReminder(result.Manifest);
        }
        else if (result.Status == AppUpdateCheckStatus.UpToDate)
        {
            ClearUpdateAvailableReminder();
        }

        _settingsWindow?.RefreshUpdateStateFromService();
    }

    private void SetUpdateAvailableReminder(AppUpdateManifest manifest)
    {
        _hasUpdateAvailable = true;
        _availableUpdateVersion = manifest.Version;
        RefreshTrayMenuText();
        RefreshTrayToolTipText();

        if (_updateNotificationShown || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                LocalizationService.T("Tray.UpdateAvailableTitle"),
                LocalizationService.Format("Tray.UpdateAvailableMessage", manifest.Version),
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: true,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(8));
            _updateNotificationShown = true;
        }
        catch (Exception ex)
        {
            Log($"[Update] Tray notification failed: {ex.Message}");
        }
    }

    private void ClearUpdateAvailableReminder()
    {
        _hasUpdateAvailable = false;
        _availableUpdateVersion = string.Empty;
        RefreshTrayMenuText();
        RefreshTrayToolTipText();
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

        string? nativeNotificationActivationArguments = TakePendingNativeNotificationActivationArguments();
        if (!string.IsNullOrWhiteSpace(nativeNotificationActivationArguments))
        {
            HandleNativeNotificationActivation(
                nativeNotificationActivationArguments,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        string? jumpListArgument = TakePendingJumpListArgument();
        if (!string.IsNullOrWhiteSpace(jumpListArgument))
        {
            await JumpListService.HandleActivationAsync(jumpListArgument);
            return;
        }

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
        var mapFolderItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.NewFolderMapping"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        mapFolderItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, CreateFolderWidgetFromPickerAsync);

        var settingsItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Settings"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Setting)
        };
        settingsItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, OpenSettingsFromTray);

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
        updateItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, OpenAboutSettingsFromTray);

        var exitItem = new MenuFlyoutItem
        {
            Text = localization.T("Tray.Exit"),
            Width = TrayMenuItemWidth,
            Icon = new SymbolIcon(Symbol.Cancel)
        };
        exitItem.Click += async (_, _) => await RunTrayMenuActionAsync(contextMenu, ExitApplication);

        contextMenu.Opening += (_, _) =>
        {
            bool canCreateWidget = WidgetManager is not null;
            foreach (var item in _trayCreateWidgetItems.Values)
            {
                item.IsEnabled = canCreateWidget;
            }

            mapFolderItem.IsEnabled = canCreateWidget;
            contextMenu.MenuFlyoutPresenterStyle = CreateTrayMenuPresenterStyle(contextMenu);
        };

        _trayCreateWidgetItems.Clear();
        foreach (var descriptor in new WidgetContentFactory(LocalizationService).GetCreateEntryDescriptors())
        {
            var createItem = CreateTrayCreateWidgetItem(contextMenu, descriptor, localization);
            _trayCreateWidgetItems[descriptor.WidgetKind] = createItem;
            contextMenu.Items.Add(createItem);
        }

        contextMenu.Items.Add(mapFolderItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(openManagedStorageItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(updateItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        contextMenu.Items.Add(exitItem);

        _trayMapFolderItem = mapFolderItem;
        _trayOpenManagedStorageItem = openManagedStorageItem;
        _trayUpdateItem = updateItem;
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

            WindowExtensions.Hide(_trayWindow);
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

    private void ShowDataRestoreResultNotification(DeskBoxRestoreApplyResult result)
    {
        if (!result.HadPendingRestore)
        {
            return;
        }

        string title = LocalizationService.T(result.Succeeded
            ? "Settings.DataBackup.RestoreAppliedTitle"
            : "Settings.DataBackup.RestoreApplyFailedTitle");
        string message = result.Succeeded
            ? LocalizationService.T("Settings.DataBackup.RestoreAppliedBody")
            : LocalizationService.Format(
                "Settings.DataBackup.RestoreApplyFailedBody",
                result.ErrorMessage ?? string.Empty);

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
        catch (Exception ex)
        {
            Log($"[DataBackup] Restore result notification failed: {ex.Message}");
        }
    }

    private Style CreateTrayMenuPresenterStyle(MenuFlyout contextMenu)
    {
        int visibleCommandCount = contextMenu.Items.Count(item =>
            item is not MenuFlyoutSeparator && item.Visibility == Visibility.Visible);
        int visibleSeparatorCount = contextMenu.Items.Count(item =>
            item is MenuFlyoutSeparator && item.Visibility == Visibility.Visible);
        double fullContentHeight =
            (visibleCommandCount * TrayMenuItemMinHeight) +
            (visibleSeparatorCount * TrayMenuSeparatorHeight) +
            (TrayMenuVerticalPadding * 2);
        double presenterHeight = Math.Max(TrayMenuMinimumHeight, fullContentHeight);

        var style = new Style(typeof(MenuFlyoutPresenter))
        {
            BasedOn = (Style)Resources[typeof(MenuFlyoutPresenter)]
        };
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, presenterHeight));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, presenterHeight));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        return style;
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
        RefreshTrayToolTipText();
    }

    private void OnLanguageChanged()
    {
        Localized.RefreshAll(LocalizationService);
        RefreshTrayMenuText();
        RefreshTrayToolTipText();
    }

    private void RefreshTrayMenuText()
    {
        if (_trayMapFolderItem is not null)
        {
            _trayMapFolderItem.Text = LocalizationService.T("Common.NewFolderMapping");
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
        settingsWindow.ShowWindow();
    }

    private void OpenSettingsFromTray()
    {
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
    }

    private void OpenAboutSettingsFromTray()
    {
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        settingsWindow.ShowSection("About");
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
        if (_settingsWindow is null)
        {
            OpenSettings();
            return;
        }

        _settingsWindow.RefreshLocalizedContent();
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

    public void ShowSettings(string sectionTag)
    {
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        settingsWindow.ShowSection(sectionTag);
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

    private static int s_lightMemoryCleanupGeneration;

    internal static void ScheduleLightMemoryCleanup()
    {
        int generation = Interlocked.Increment(ref s_lightMemoryCleanupGeneration);
        App.UiDispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(2000);
            if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
            {
                return;
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            Localized.PruneDeadTargets();
            Win32Helper.TrimCurrentProcessWorkingSet();
        });
    }

    public async Task ShutdownForUpdateAsync()
    {
        Log("ShutdownForUpdateAsync invoked");
        await ShutdownApplicationAsync();
    }

    public async Task ShutdownForRestartAsync()
    {
        Log("ShutdownForRestartAsync invoked");
        await ShutdownApplicationAsync();
    }

    private async void ExitApplication()
    {
        Log("ExitApplication invoked");
        await ShutdownApplicationAsync();
    }

    private async Task ShutdownApplicationAsync()
    {
        // Stop the display area watcher FIRST, before closing any widgets,
        // so that no DisplaysChanged callback can fire during teardown
        // and access half-closed window objects.
        _displayAreaWatcher?.Dispose();
        _displayAreaWatcher = null;

        _memoryDiagnosticTimer?.Stop();
        _memoryDiagnosticTimer = null;
        _periodicGcCts?.Cancel();
        _periodicGcCts?.Dispose();
        _periodicGcCts = null;
        await SettingsService.SaveAsync();
        _nativeNotificationService?.Dispose();
        _nativeNotificationService = null;
        _todoReminderService?.Dispose();
        _todoReminderService = null;
        WidgetManager?.CloseAll();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _settingsWindow?.CloseForShutdown();
        _settingsWindow = null;
        _onboardingWindow?.Close();
        _onboardingWindow = null;
        _trayWindow?.Close();
        _trayWindow = null;
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
