// Copyright (c) DeskBox. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskBox.Controls.WidgetContents;
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
using Microsoft.Extensions.DependencyInjection;
using DrawingPoint = System.Drawing.Point;
using WinRT.Interop;

namespace DeskBox;

/// <summary>
/// Application bootstrap, tray menu, and widget lifecycle.
/// </summary>
public partial class App : Application
{
    private const double TrayMenuItemWidth = 176;
    private const int TrayContextMenuFallbackOffsetPixels = 24;
    private const int TrayContextMenuEstimatedWidth = (int)TrayMenuItemWidth + 16;
    private const int VisibleIdleMemoryCheckIntervalSeconds = 5;
    private const int VisibleIdleMemoryMinimumCooldownSeconds = 60;
    private const int HiddenWorkingSetTrimCooldownSeconds = 30 * 60;
    private const string UpdateInstallResultArgument = "--update-install-result";
    private const int MaxQueuedLogLines = 4096;
    private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB before rotation
    private const string TodoReminderNotificationSource = "source=todoReminder";
    private const string TodoReminderSourceValue = TodoNotificationActivationRouter.SourceValue;
    private const string TodoReminderActionComplete = TodoNotificationActivationRouter.ActionComplete;
    private const string TodoReminderActionSnooze = TodoNotificationActivationRouter.ActionSnooze;
    private const string TodoReminderSnoozeInputId = TodoNotificationActivationRouter.SnoozeInputId;
    private const string TodoReminderSnooze10Minutes = TodoNotificationActivationRouter.Snooze10Minutes;
    private const string TodoReminderSnooze30Minutes = TodoNotificationActivationRouter.Snooze30Minutes;
    private const string TodoReminderSnooze1Hour = TodoNotificationActivationRouter.Snooze1Hour;
    private const string TodoReminderSnoozeTomorrow = TodoNotificationActivationRouter.SnoozeTomorrow;
    private const string TodoSnoozeConfirmationNotificationSource = "todoSnoozeConfirmation";
    private const string TodoSnoozeConfirmationNotificationGroup = "todo-feedback";
    private const string TodoSnoozeConfirmationNotificationTag = "todo-snooze-confirmation";
    private const string PendingJumpListArgumentFileName = "pending-jumplist-arg.txt";
    private const string VerboseLoggingEnvironmentVariable = "DESKBOX_VERBOSE_LOG";
    private static readonly bool EnableVerboseLogging = IsEnabledEnvironmentValue(
        Environment.GetEnvironmentVariable(VerboseLoggingEnvironmentVariable));

    private static readonly string LogPath = DeskBoxDataPathService.Current.LogFilePath;
    private static readonly NativeNotificationActivationEnvelopeStore
        PendingNativeNotificationActivationStore = new(
            DeskBoxDataPathService.Current.RootPath);
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
    private MenuFlyout? _trayContextMenu;
    private bool _traySecondWindowSyncLogged;
    private MenuFlyoutItem? _trayOrganizeDesktopItem;
    private MenuFlyoutItem? _trayMapFolderItem;
    private MenuFlyoutItem? _trayAddFeatureWidgetItem;
    private readonly Dictionary<WidgetKind, MenuFlyoutItem> _trayCreateWidgetItems = [];
    private MenuFlyoutItem? _trayOpenManagedStorageItem;
    private MenuFlyoutItem? _trayUpdateItem;
    private MenuFlyoutItem? _traySettingsItem;
    private MenuFlyoutItem? _trayExitItem;
    private SettingsWindow? _settingsWindow;
    private OnboardingWindow? _onboardingWindow;
    private string? _onboardingRaisedFileWidgetId;
    internal event Action<int>? OnboardingFileImportCompleted;
    internal event Action<bool>? OnboardingWidgetsVisibilityChanged;
    private NativeAppNotificationService? _nativeNotificationService;
    private TodoReminderService? _todoReminderService;
    private DisplayAreaWatcherService? _displayAreaWatcher;
    private DisplayTopologyTransitionCoordinator? _displayTopologyTransitionCoordinator;
    private AppLifecycleRecoveryWatcher? _lifecycleRecoveryWatcher;
    private EverythingSearchService? _everythingSearchService;
    private SearchEngineService? _searchEngineService;
    private FileMetaService? _fileMetaService;
    private SearchHotkeyService? _searchHotkeyService;
    private SearchPopupWindow? _searchPopupWindow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visibleIdleMemoryMaintenanceTimer;
    private long _lastVisibleIdleCollectionAllocatedBytes;
    private bool _hasCompletedVisibleIdleCollection;
    private int _visibleIdleMemoryMaintenanceRunning;
    private readonly VisibleIdleMemoryTracker _visibleIdleMemoryTracker = new(
        TimeSpan.FromSeconds(
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds),
        TimeSpan.FromSeconds(
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds));
    private int _transientWindowReleaseGeneration;
    private DateTimeOffset _lastHiddenWorkingSetTrimAt = DateTimeOffset.MinValue;
    private SearchHistoryService? _searchHistoryService;
    private SearchResultActionService? _searchActionService;
    private bool _widgetsRaisedFromTray;
    private bool _hasUpdateAvailable;
    private bool _updateNotificationShown;
    private DateTimeOffset _lastSettingsPersistenceNotificationAt = DateTimeOffset.MinValue;
    private string _availableUpdateVersion = string.Empty;
    private int _externalStateRecoveryScheduled;
    private bool _externalActivationReady;
    private bool _externalActivationRequestedWhileBusy;
    private bool _externalActivationHandling;
    private DateTimeOffset? _lastBareExternalActivationAtUtc;
    private readonly bool _processStartupLaunchDetected;

    public static new App Current => (App)Application.Current;

    public static Microsoft.UI.Dispatching.DispatcherQueue UiDispatcherQueue { get; private set; } = null!;

    public bool IsStartupMode { get; set; }

    public AppDistributionService DistributionService { get; } = AppDistributionService.Current;
    public ServiceProvider Services { get; private set; } = null!;
    public SettingsService SettingsService { get; private set; } = null!;
    public DeskBoxDataBackupService DataBackupService { get; private set; } = null!;
    public DeskBoxAttachmentHealthService AttachmentHealthService { get; private set; } = null!;
    public FileService FileService { get; private set; } = null!;
    public OrganizerService OrganizerService { get; private set; } = null!;
    public IAppUpdateService AppUpdateService { get; private set; } = null!;
    public QuickCaptureService QuickCaptureService { get; private set; } = null!;
    public QuickCaptureClipboardService? QuickCaptureClipboardService { get; private set; }
    public LocalizationService LocalizationService { get; private set; } = null!;
    public ThemeService ThemeService { get; private set; } = null!;
    public GlobalHotkeyService? GlobalHotkeyService { get; private set; }
    public DesktopDoubleClickActivationService? DesktopDoubleClickActivationService { get; private set; }
    public SearchHotkeyService? SearchHotkeyService => _searchHotkeyService;
    public SearchEngineService? SearchEngineService => _searchEngineService;
    public EverythingSearchService? EverythingSearchService => _everythingSearchService;
    public AppDiagnosticsService? DiagnosticsService => _diagnosticsService;
    internal SearchHistoryService? SearchHistoryService => _searchHistoryService;
    public SearchResultActionService? SearchActionService => _searchActionService;
    internal bool IsSearchPopupCreated => _searchPopupWindow is not null;
    internal bool IsSearchPopupVisible => _searchPopupWindow?.IsPopupVisible == true;
    internal bool IsEverythingSearchConnected =>
        _everythingSearchService?.CurrentSnapshot.State == EverythingConnectionState.Connected;
    internal int SearchMetaCacheCount => _fileMetaService?.CachedIconCount ?? 0;
    public WidgetManager? WidgetManager { get; private set; }
    public ResizeGuideOverlayService ResizeGuideOverlay { get; private set; } = null!;
    public NativeAppNotificationService? NativeNotificationService => _nativeNotificationService;
    public DisplayAreaWatcherService? DisplayAreaWatcher => _displayAreaWatcher;
    public TodoReminderService? TodoReminderService => _todoReminderService;
    public DesktopAutoOrganizationWatcher? DesktopAutoOrganizationWatcher { get; private set; }
    public SettingsWindow? SettingsWindowInstance => _settingsWindow;

    public static bool IsVerboseLoggingEnabled => EnableVerboseLogging;

    public App()
    {
        // Register AUMID early so the taskbar button and Jump List work
        // for both packaged (MSIX) and unpackaged (Direct) distributions.
        JumpListService.RegisterAppUserModelId();

        Log("App() constructor start");
        _processStartupLaunchDetected = StartupLaunchPolicy.IsStartupLaunch(
            Environment.GetCommandLineArgs(),
            isStartupTaskActivation: IsStartupTaskActivation());
        NativeAppNotificationActivation? nativeNotificationActivation =
            TryGetCurrentNativeNotificationActivation();
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            DeskBoxDataPathService.Current.ActivationEventName);
        _singleInstanceMutex = new Mutex(
            true,
            DeskBoxDataPathService.Current.SingleInstanceMutexName,
            out bool createdNew);
        if (!createdNew)
        {
            string? jumpListArg = nativeNotificationActivation is null &&
                !_processStartupLaunchDetected
                    ? JumpListService.TryGetJumpListArgument(
                        string.Join(' ', Environment.GetCommandLineArgs()))
                    : null;
            string activationKind = nativeNotificationActivation is not null
                ? "notification"
                : _processStartupLaunchDetected
                    ? "startup"
                    : jumpListArg is not null
                        ? $"jump-list:{jumpListArg}"
                        : "bare";
            Log(
                $"[Activation] Secondary instance kind={activationKind} " +
                $"argumentCount={Math.Max(0, Environment.GetCommandLineArgs().Length - 1)} " +
                $"{GetParentProcessReport()}");

            if (nativeNotificationActivation is not null)
            {
                NativeNotificationActivationEnvelopeWriteResult writeResult =
                    PendingNativeNotificationActivationStore.Store(nativeNotificationActivation);
                Log(
                    $"[Notification] Forwarded typed activation envelope " +
                    $"disposition={writeResult.Disposition} " +
                    $"envelope={writeResult.Envelope?.EnvelopeId ?? "none"} " +
                    $"userInput={writeResult.Envelope?.UserInput.Count ?? 0} " +
                    $"error={writeResult.Error ?? "none"}");
            }
            else if (_processStartupLaunchDetected)
            {
                Log("Another instance running; startup launch exiting silently");
                Environment.Exit(0);
            }
            else
            {
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

        // Build DI container and resolve core services
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDeskBoxServices();
        Services = serviceCollection.BuildServiceProvider();

        SettingsService = Services.GetRequiredService<SettingsService>();
        SettingsService.PersistenceFailed += OnSettingsPersistenceFailed;
        _ = LegacySearchIndexCleanupService.TryCleanup();
        DataBackupService = Services.GetRequiredService<DeskBoxDataBackupService>();
        AttachmentHealthService = Services.GetRequiredService<DeskBoxAttachmentHealthService>();
        DiagnosticsBundleService = Services.GetRequiredService<DeskBoxDiagnosticsBundleService>();
        FileService = Services.GetRequiredService<FileService>();
        OrganizerService = Services.GetRequiredService<OrganizerService>();
        AppUpdateService = Services.GetRequiredService<IAppUpdateService>();
        QuickCaptureService = Services.GetRequiredService<QuickCaptureService>();
        ResizeGuideOverlay = Services.GetRequiredService<ResizeGuideOverlayService>();

        StartupService.Configure(StartupServiceFactory.Create(DistributionService));
        if (StartupService.Current is DirectStartupService directStartupService)
        {
            directStartupService.TryMigrateLegacyRegistration();
        }
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
    private const int HeapOptimizeResources = 3;
    private const uint HeapOptimizeResourcesCurrentVersion = 1;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapOptimizeResourcesInformation
    {
        public uint Version;
        public uint Flags;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSetInformation(
        IntPtr heapHandle,
        int heapInformationClass,
        ref HeapOptimizeResourcesInformation heapInformation,
        nuint heapInformationLength);

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

        return WidgetManager?.IsWidgetWindow(rootHwnd) == true;
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

    private static bool IsStartupTaskActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind ==
                   ExtendedActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            Log($"Failed to inspect startup-task activation: {ex.Message}");
            return false;
        }
    }

    private static string? TryGetUpdateInstallOutcome(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index].Trim().Trim('"');
            if (string.Equals(argument, UpdateInstallResultArgument, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
            {
                return NormalizeUpdateInstallOutcome(arguments[index + 1]);
            }

            string prefix = UpdateInstallResultArgument + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeUpdateInstallOutcome(argument[prefix.Length..]);
            }
        }

        return null;
    }

    private static string? NormalizeUpdateInstallOutcome(string? outcome)
    {
        return outcome?.Trim().Trim('"').ToLowerInvariant() switch
        {
            "cancelled" => "cancelled",
            "path-mismatch" => "path-mismatch",
            "failed" => "failed",
            _ => null
        };
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

        bool startupTaskActivation = IsStartupTaskActivation();
        bool isStartupLaunch = StartupLaunchPolicy.IsStartupLaunch(
            Environment.GetCommandLineArgs(),
            args.Arguments,
            startupTaskActivation);
        using var perfScope = PerformanceLogger.Measure(
            "App.OnLaunched",
            $"startup={isStartupLaunch}");
        Log("OnLaunched start");

        try
        {
            string? updateInstallOutcome = TryGetUpdateInstallOutcome(Environment.GetCommandLineArgs());
            IsStartupMode = _processStartupLaunchDetected || isStartupLaunch;
            UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            WidgetSegmentedLayoutHelper.Initialize(UiDispatcherQueue);

            // A prepared restore is applied before any service reads or normalizes app data.
            DeskBoxRestoreApplyResult restoreResult = await DataBackupService.ApplyPendingRestoreAsync();
            bool hadSettingsBeforeStartup = File.Exists(Path.Combine(
                DeskBoxDataPathService.Current.DataDirectory,
                "settings.json"));

            // Capture the previous session's data before any startup normalization writes.
            await DataBackupService.CreateAutomaticSnapshotIfDueAsync();

            // Phase 1: Load settings (must complete first)
            await SettingsService.LoadAsync();

            // Sync widget move/resize snap settings.
            ResizeGuideOverlay.IsSnapEnabled = SettingsService.Settings.ResizeSnapEnabled;
            ResizeGuideOverlay.SnapSpacingDips = SettingsService.Settings.WidgetSnapSpacing;

            // Phase 2: Initialize services that depend on settings (parallel)
            ThemeService = Services.GetRequiredService<ThemeService>();
            LocalizationService = Services.GetRequiredService<LocalizationService>();
            LocalizationService.LanguageChanged += OnLanguageChanged;

            var quickCaptureService = QuickCaptureService;
            var themeService = ThemeService;
            var localizationService = LocalizationService;

            // Parallel: theme refresh only. Clipboard event subscription must stay on the UI thread.
            var themeTask = Task.Run(() => themeService.RefreshAppearance());
            RefreshQuickCaptureClipboardService();

            // Parallel: independent UI setup
            CreateTrayIcon();
            InitializeLifecycleRecoveryWatcher();

            await themeTask;

            if (FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Search))
            {
                // The search shell is lightweight. Everything owns the filename index;
                // DeskBox does not scan, preload, or watch the filesystem at startup.
                EnsureSearchServices();
            }
            else
            {
                Log("[Search] Feature disabled; search services were not initialized");
            }

            WidgetManager = new WidgetManager(SettingsService, FileService, OrganizerService, themeService, quickCaptureService, localizationService);
            WidgetManager.TrayLayerStateChanged += UpdateTrayLayerStateText;
            DesktopDoubleClickActivationService = new DesktopDoubleClickActivationService(
                SettingsService,
                ToggleWidgetsFromDesktopDoubleClickAsync);
            DesktopDoubleClickActivationService.RefreshRegistration();
            _displayTopologyTransitionCoordinator = new DisplayTopologyTransitionCoordinator(
                UiDispatcherQueue,
                DisplayAreaWatcherService.CaptureCurrentSignature,
                async (generation, reasons) =>
                    WidgetManager is null ||
                    await WidgetManager.RestoreWidgetPositionsAsync(generation, reasons));

            // Phase 3: Restore widgets
            int recoveredDesktopItems = await new DesktopOrganizationTransaction(
                SettingsService,
                FileService).RecoverPendingAsync();
            if (recoveredDesktopItems > 0)
            {
                Log($"[DesktopOrganization] Recovered {recoveredDesktopItems} items from an interrupted transaction.");
            }
            // A detached storage drive must not abort widget restoration.
            bool managedStorageRootUnavailable = !WidgetManager.SyncStorageFolderEntries();
            Task<bool>? startupDesktopLayerReadinessTask = null;
            if (IsStartupMode)
            {
                WidgetLayerService.BeginStartupDesktopLayerAttachmentDeferral();
                startupDesktopLayerReadinessTask =
                    WidgetLayerService.WaitForDesktopIconViewReadyAsync();
            }

            try
            {
                await WidgetManager.RestoreWidgetsAsync();
            }
            catch
            {
                if (startupDesktopLayerReadinessTask is not null)
                {
                    WidgetLayerService.EndStartupDesktopLayerAttachmentDeferral();
                }

                throw;
            }

            if (startupDesktopLayerReadinessTask is not null)
            {
                _ = CompleteStartupDesktopLayerInitializationAsync(
                    startupDesktopLayerReadinessTask,
                    WidgetManager);
            }

            InitializeGlobalHotkeyService(localizationService);

            RefreshTodoReminderService();
            StartNativeNotificationService();
            await CompleteExternalActivationInitializationAsync();
            ShowDataRestoreResultNotification(restoreResult);
            ShowSettingsLoadRecoveryNotification();
            if (managedStorageRootUnavailable)
            {
                ShowManagedStorageUnavailableNotification();
            }
            if (!hadSettingsBeforeStartup ||
                SettingsService.LastLoadRecoveryState == SettingsLoadRecoveryState.DefaultsAfterFailure)
            {
                ShowRecoverySnapshotAvailableNotification(
                    await DataBackupService.GetLatestRecoverySnapshotAsync());
            }

            await EnsureInitialFileWidgetSetupAsync(isInteractiveLaunch: !IsStartupMode);

            DesktopAutoOrganizationWatcher = new DesktopAutoOrganizationWatcher(
                SettingsService,
                OrganizerService,
                WidgetManager);
            DesktopAutoOrganizationWatcher.ItemOrganized += ShowDesktopAutoOrganizationNotification;
            DesktopAutoOrganizationWatcher.Start();

            await EnsureOnboardingAsync(isInteractiveLaunch: !IsStartupMode);

            ScheduleBackgroundUpdateCheck();
            _diagnosticsService = new AppDiagnosticsService(UiDispatcherQueue);
            _diagnosticsService.StartAll();

            // Start display area watcher for hot-plug detection
            _displayAreaWatcher = new DisplayAreaWatcherService(UiDispatcherQueue);
            _displayAreaWatcher.DisplaysChanged += OnDisplaysChanged;
            _displayAreaWatcher.Start();

            // Configure taskbar Jump List with quick actions
            _ = JumpListService.ConfigureAsync(LocalizationService);

            // Handle Jump List activation on first launch (not second instance)
            string? firstLaunchJumpArg =
                JumpListService.TryGetJumpListArgument(args.Arguments) ??
                JumpListService.TryGetJumpListArgument(
                    string.Join(' ', Environment.GetCommandLineArgs()));
            if (firstLaunchJumpArg is not null)
            {
                _ = JumpListService.HandleActivationAsync(firstLaunchJumpArg);
            }

            StartVisibleIdleMemoryMaintenance();
            if (!string.IsNullOrWhiteSpace(updateInstallOutcome))
            {
                ShowSettings("About");
                _settingsWindow?.QueueUpdateInstallResultDialog(updateInstallOutcome);
            }

            Log("OnLaunched completed successfully");
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
            StartAotShortcutSmokeIfRequested();
            StartAotShellSmokeIfRequested();
            StartAotQuickAccessMutationSmokeIfRequested();
            StartAotMusicVolumeReadSmokeIfRequested();
            StartAotMusicVolumeMutationSmokeIfRequested();
            StartAotMusicVolumeSessionMutationSmokeIfRequested();
            StartAotManagedUiSmokeIfRequested();
            StartAotHotkeySmokeIfRequested();
            StartAotTodoRecurrenceReminderSmokeIfRequested();
            StartAotTodoNotificationLifecycleSmokeIfRequested();
            StartAotTodoNotificationActivationSmokeIfRequested();
            StartAotTodoNotificationForwardingSmokeIfRequested();
            StartAotTodoNotificationSurfaceSmokeIfRequested();
            StartAotTodoNotificationUserClickSmokeIfRequested();
#endif
        }
        catch (Exception ex)
        {
            Log($"Exception in OnLaunched: {ex}");
        }
    }

    private async Task CompleteStartupDesktopLayerInitializationAsync(
        Task<bool> readinessTask,
        WidgetManager widgetManager)
    {
        bool explorerDesktopReady = false;
        try
        {
            // Widget startup already has a 2.3-second temporary presentation.
            // Let that finish independently while the read-only Explorer probe runs.
            Task widgetPresentationSettled = Task.Delay(TimeSpan.FromMilliseconds(2400));
            explorerDesktopReady = await readinessTask;
            await widgetPresentationSettled;
        }
        catch (Exception ex)
        {
            Log($"[Startup] Explorer desktop readiness probe failed: {ex}");
        }
        finally
        {
            WidgetLayerService.EndStartupDesktopLayerAttachmentDeferral();
        }

        if (!ReferenceEquals(WidgetManager, widgetManager))
        {
            return;
        }

        Log(
            $"[Startup] Applying deferred widget desktop layer " +
            $"explorerReady={explorerDesktopReady}");
        widgetManager.RefreshVisibleWidgetDesktopLayers(
            "startup-explorer-desktop-ready");
    }

    private void InitializeGlobalHotkeyService(LocalizationService localizationService)
    {
        if (GlobalHotkeyService is null)
        {
            try
            {
                GlobalHotkeyService = new GlobalHotkeyService(
                    SettingsService,
                    localizationService,
                    () => ToggleTrayWidgetsAsync("global-hotkey"));
                Log("[Init] GlobalHotkeyService created after widget restore");
            }
            catch (Exception ex)
            {
                Log($"[Init] GlobalHotkeyService creation failed: {ex}");
            }
        }

        if (GlobalHotkeyService is not { } hotkeyService || _trayWindow is null)
        {
            return;
        }

        try
        {
            IntPtr trayWindowHandle = WindowNative.GetWindowHandle(_trayWindow);
            if (trayWindowHandle != IntPtr.Zero && !hotkeyService.IsRegistered)
            {
                Log(
                    $"[Init] Attaching GlobalHotkeyService after widget restore " +
                    $"hwnd=0x{trayWindowHandle.ToInt64():X}");
                hotkeyService.Attach(trayWindowHandle);
            }
        }
        catch (Exception ex)
        {
            Log($"[Init] GlobalHotkeyService attach failed: {ex}");
        }
    }

    /// <summary>
    /// Called when the set of displays changes (hot-plug, resolution change, etc.).
    /// Invalidates caches and triggers widget repositioning.
    /// </summary>
    private void OnDisplaysChanged()
    {
        try
        {
            Log("[DisplayAreaWatcher] Displays changed, queueing stable widget reposition");

            // Invalidate the desktop icon view cache since work areas may have changed
            WidgetLayerService.InvalidateDesktopIconViewCache();

            RequestDisplayTopologyRestore("display-area-watcher");
        }
        catch (Exception ex)
        {
            Log($"[DisplayAreaWatcher] OnDisplaysChanged failed: {ex}");
        }
    }

    internal void RequestDisplayTopologyRestore(string reason)
    {
        _displayTopologyTransitionCoordinator?.RequestRestore(reason);
    }

    private AppDiagnosticsService? _diagnosticsService;

    private void InitializeLifecycleRecoveryWatcher()
    {
        if (_trayWindow is null)
        {
            return;
        }

        try
        {
            IntPtr trayHwnd = WindowNative.GetWindowHandle(_trayWindow);
            if (trayHwnd != IntPtr.Zero)
            {
                _lifecycleRecoveryWatcher = new AppLifecycleRecoveryWatcher(
                    trayHwnd,
                    UiDispatcherQueue,
                    OnLifecycleRecoveryRequested,
                    FlushSettingsForEndSession);
            }
        }
        catch (Exception ex)
        {
            Log($"[Lifecycle] Recovery watcher initialization failed: {ex.Message}");
        }
    }

    private void OnLifecycleRecoveryRequested(string reason)
    {
        Log($"[Lifecycle] Recovery signal received: {reason}");
        _diagnosticsService?.RecordLifecycleEvent(reason);
        WidgetLayerService.InvalidateDesktopIconViewCache();
        _displayAreaWatcher?.RefreshNow();
        RequestDisplayTopologyRestore("lifecycle-" + reason);

        bool requiresExternalRecovery =
            reason.Contains("resume", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("session-", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("explorer-restart", StringComparison.OrdinalIgnoreCase);
        if (requiresExternalRecovery)
        {
            try
            {
                GlobalHotkeyService?.RefreshRegistration();
                DesktopDoubleClickActivationService?.RefreshRegistration();
            }
            catch (Exception ex)
            {
                Log($"[Lifecycle] Global hotkey recovery failed for {reason}: {ex.Message}");
            }

            ScheduleExternalStateRecovery();
            if (_everythingSearchService is not null &&
                SettingsService.Settings.SearchEverythingEnabled)
            {
                _ = _everythingSearchService.RefreshConnectionAsync();
            }
        }
    }

    private void FlushSettingsForEndSession(string reason)
    {
        Log($"[Lifecycle] Flushing settings for {reason}.");
        try
        {
            Task<bool> flushTask = Task.Run(
                () => SettingsService.FlushPendingSaveAsync(notifySubscribers: false));
            if (!flushTask.Wait(TimeSpan.FromSeconds(3)))
            {
                Log($"[Lifecycle] Settings flush timed out for {reason}.");
            }
            else if (!flushTask.Result)
            {
                Log($"[Lifecycle] Settings flush failed for {reason}.");
            }
        }
        catch (Exception ex)
        {
            Log($"[Lifecycle] Settings flush threw for {reason}: {ex}");
        }
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

    internal void RefreshQuickCaptureClipboardService(bool captureCurrent = false)
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() =>
                RefreshQuickCaptureClipboardService(captureCurrent));
            return;
        }

        AppSettings settings = SettingsService.Settings;
        bool shouldListen =
            settings.QuickCaptureEnabled &&
            settings.QuickCaptureClipboardEnabled &&
            FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture);
        if (!shouldListen)
        {
            if (QuickCaptureClipboardService is not null)
            {
                QuickCaptureClipboardService.Dispose();
                QuickCaptureClipboardService = null;
                Log("[QuickCaptureClipboard] Inactive service released");
            }

            return;
        }

        if (QuickCaptureClipboardService is null)
        {
            QuickCaptureClipboardService = new QuickCaptureClipboardService(
                SettingsService,
                QuickCaptureService);
            Log("[QuickCaptureClipboard] Service initialized on demand");
        }

        QuickCaptureClipboardService.Refresh();
        if (captureCurrent)
        {
            QuickCaptureClipboardService.CaptureCurrent();
        }
    }

    internal TodoReminderService? RefreshTodoReminderService(bool checkNow = false)
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => RefreshTodoReminderService(checkNow));
            return _todoReminderService;
        }

        AppSettings settings = SettingsService.Settings;
        bool shouldRun =
            settings.TodoReminderEnabled &&
            FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        // The audit harness intentionally starts several notification fixtures
        // with reminder polling disabled, then drives the real product service
        // directly. Keep that explicit test-only reachability out of retail.
        shouldRun = shouldRun ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AotTodoNotificationSmokeEnvironmentVariable)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AotTodoNotificationActivationSmokeEnvironmentVariable)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AotTodoNotificationForwardingSmokeEnvironmentVariable)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AotTodoNotificationSurfaceSmokeEnvironmentVariable)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AotTodoNotificationUserClickEnvironmentVariable)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                AotTodoRecurrenceReminderSmokeEnvironmentVariable));
#endif
        if (!shouldRun)
        {
            if (_todoReminderService is not null)
            {
                _todoReminderService.Dispose();
                _todoReminderService = null;
                Log("[TodoReminder] Inactive service released");
            }

            return null;
        }

        if (_todoReminderService is null)
        {
            StartTodoReminderService();
            Log("[TodoReminder] Service initialized on demand");
        }
        else
        {
            _todoReminderService.Refresh();
        }

        if (checkNow && _todoReminderService is { } reminderService)
        {
            _ = reminderService.CheckNowAsync(DateTimeOffset.Now);
        }

        return _todoReminderService;
    }

    private void StartTodoReminderService()
    {
        _todoReminderService?.Dispose();
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        if (TryGetAotTodoNotificationForwardingClock() is not null)
        {
            _todoReminderService = new TodoReminderService(
                SettingsService,
                LocalizationService,
                UiDispatcherQueue,
                ShowTodoReminderNotification,
                widgetId => new TodoWidgetStore(widgetId),
                GetTodoNotificationActivationNow);
            _todoReminderService.Start();
            return;
        }
#endif
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
            HandleNativeNotificationActivation);
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
            HandleNativeNotificationActivation(activation);
        }
    }

    private void HandleNativeNotificationActivation(
        NativeAppNotificationActivation activation)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => HandleNativeNotificationActivation(activation));
            return;
        }

        App.Log(
            $"[Notification] Native notification activated " +
            $"source={activation.Source} sourcePid={activation.SourceProcessId} " +
            $"envelope={activation.EnvelopeId ?? "none"} args={activation.Arguments}");
        OnNativeNotificationActivationObserved(activation);
        var notificationArguments = ParseNotificationArguments(activation.Arguments);
        if (IsTodoReminderNotification(notificationArguments))
        {
            _ = RouteTodoNotificationActivationAsync(
                notificationArguments,
                activation.UserInput,
                activation);
        }
        else if (notificationArguments.TryGetValue("type", out string? notificationType) &&
                 string.Equals(
                     notificationType,
                     "desktop-organization",
                     StringComparison.OrdinalIgnoreCase))
        {
            if (notificationArguments.TryGetValue("action", out string? action) &&
                string.Equals(action, "undo", StringComparison.OrdinalIgnoreCase) &&
                notificationArguments.TryGetValue("historyId", out string? historyId))
            {
                _ = UndoDesktopOrganizationFromNotificationAsync(historyId);
                return;
            }

            _ = RaiseTrayWidgetsAsync();
        }
        else
        {
            _ = RaiseTrayWidgetsAsync();
        }
    }

    private void ShowDesktopAutoOrganizationNotification(
        DesktopAutoOrganizationCompleted completed)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() =>
                ShowDesktopAutoOrganizationNotification(completed));
            return;
        }

        _nativeNotificationService?.TryShow(
            LocalizationService.T("DesktopOrganization.Notification.Title"),
            LocalizationService.Format(
                "DesktopOrganization.Notification.Body",
                completed.FileName,
                completed.TargetWidgetName),
            new Dictionary<string, string>
            {
                ["type"] = "desktop-organization",
                ["historyId"] = completed.HistoryId
            },
            [
                new NativeAppNotificationAction(
                    LocalizationService.T("DesktopOrganization.Notification.Undo"),
                    new Dictionary<string, string>
                    {
                        ["type"] = "desktop-organization",
                        ["action"] = "undo",
                        ["historyId"] = completed.HistoryId
                    })
            ],
            options: new NativeAppNotificationOptions(
                Tag: "desktop-auto-organization",
                Group: "desktop-organization"));
    }

    private async Task UndoDesktopOrganizationFromNotificationAsync(string historyId)
    {
        try
        {
            OrganizationHistoryEntry? history = SettingsService.Settings.RecentOrganizationHistory
                .FirstOrDefault(entry =>
                    string.Equals(entry.Id, historyId, StringComparison.Ordinal));
            await OrganizerService.UndoAsync(historyId);
            if (WidgetManager is not null && history is not null)
            {
                foreach (string widgetId in history.Items
                             .Select(item => item.TargetWidgetId)
                             .Where(id => !string.IsNullOrWhiteSpace(id))
                             .Distinct(StringComparer.Ordinal))
                {
                    await WidgetManager.RefreshFileWidgetAsync(widgetId);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[DesktopAutoOrganization] Notification undo failed: {ex}");
        }
    }

    private async Task<TodoNotificationActivationRouteResult?> RouteTodoNotificationActivationAsync(
        IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> userInput,
        NativeAppNotificationActivation? activation = null)
    {
        try
        {
            TodoNotificationActivationRouteResult result =
                await TodoNotificationActivationRouter.RouteAsync(
                    arguments,
                    userInput,
                    _todoReminderService,
                    GetTodoNotificationActivationNow,
                    GetTodoNotificationActivationTimeZone(),
                    ShowTodoWidgetFromNotificationAsync,
                    RefreshLoadedTodoWidgetAfterNotificationActionAsync,
                    selection =>
                    {
                        ShowTodoSnoozeConfirmationNotification(
                            GetTodoSnoozeSelectionText(selection));
                        return Task.CompletedTask;
                    });
            Log(
                $"[Notification] Todo activation routed disposition={result.Disposition} " +
                $"success={result.Succeeded} widget={result.WidgetId ?? "none"} " +
                $"item={result.ItemId ?? "none"} action={result.Action ?? "open"} " +
                $"snooze={result.SnoozeSelection ?? "none"} " +
                $"targetPresented={result.TargetPresented} " +
                $"refreshCompleted={result.RefreshCompleted}");
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
            RecordAotTodoNotificationSurfaceRoute(result);
#endif
            OnTodoNotificationActivationRouteObserved(activation, result);
            return result;
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to route Todo reminder activation: {ex}");
            return null;
        }
    }

    private static DateTimeOffset GetTodoNotificationActivationNow()
    {
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        DateTimeOffset? controlledClock = TryGetAotTodoNotificationForwardingClock();
        controlledClock ??= TryGetAotTodoNotificationSurfaceClock();
        controlledClock ??= TryGetAotTodoNotificationUserClickClock();
        if (controlledClock is not null)
        {
            return controlledClock.Value;
        }
#endif
        return DateTimeOffset.Now;
    }

    private static TimeZoneInfo GetTodoNotificationActivationTimeZone()
    {
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        TimeZoneInfo? controlledTimeZone = TryGetAotTodoNotificationForwardingTimeZone();
        if (controlledTimeZone is not null)
        {
            return controlledTimeZone;
        }
#endif
        return TimeZoneInfo.Local;
    }

    private async Task<bool> ShowTodoWidgetFromNotificationAsync(
        string? widgetId = null,
        string? itemId = null,
        bool preferTodayFilter = false)
    {
        if (WidgetManager is null)
        {
            return false;
        }

        TodoReminderTargetPresentationResult presentation =
            await WidgetManager.ShowTodoReminderTargetAsync(
                widgetId,
                itemId,
                preferTodayFilter);
        Log(
            $"[Notification] Todo target presentation widget={presentation.WidgetId} " +
            $"item={presentation.ItemId ?? "none"} hwnd={presentation.WindowHandle} " +
            $"visible={presentation.Visible} xamlRoot={presentation.HasXamlRoot} " +
            $"itemPresented={presentation.ItemPresented} " +
            $"targetPresented={presentation.TargetPresented}");
        return presentation.TargetPresented;
    }

    private async Task<bool> RefreshLoadedTodoWidgetAfterNotificationActionAsync(
        string? widgetId)
    {
        if (WidgetManager is null ||
            string.IsNullOrWhiteSpace(widgetId) ||
            !WidgetManager.ContentWidgets.TryGetValue(widgetId, out var window))
        {
            return false;
        }

        await window.ContentReadyTask;
        if (window.CurrentContent is not TodoWidgetContentAdapter adapter ||
            adapter.View is not TodoWidgetContent todoContent)
        {
            return false;
        }

        await adapter.RefreshAsync();
        bool surfaceCommitted =
            await WidgetManager.WaitForTodoReminderSurfaceCommitAsync(todoContent);
        bool completed = window.Visible &&
            surfaceCommitted;
        Log(
            $"[Notification] Todo visible refresh widget={widgetId} " +
            $"hwnd={window.WindowHandle.ToInt64()} visible={window.Visible} " +
            $"xamlRoot={todoContent.XamlRoot is not null} " +
            $"surfaceCommitted={surfaceCommitted} " +
            $"completed={completed}");
        return completed;
    }

    private void ShowTodoSnoozeConfirmationNotification(string snoozeText)
    {
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        if (ShouldSuppressAotTodoNotificationForwardingSystemNotification() ||
            ShouldSuppressAotTodoNotificationSurfaceSystemNotification() ||
            ShouldSuppressAotTodoNotificationUserClickConfirmation())
        {
            Log("[AotTodoNotificationForwarding] Suppressed fixture snooze confirmation notification.");
            return;
        }
#endif

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

    private void ShowTodoReminderNotification(TodoReminderNotification notification)
    {
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcherQueue)
        {
            dispatcherQueue.TryEnqueue(() => ShowTodoReminderNotification(notification));
            return;
        }

        if (TryShowNativeTodoReminderNotification(notification))
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

    private bool TryShowNativeTodoReminderNotification(
        TodoReminderNotification notification,
        NativeAppNotificationOptions? options = null)
    {
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

        return _nativeNotificationService?.TryShow(
                notification.Title,
                notification.Message,
                arguments,
                actions,
                comboBoxes,
                options) == true;
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

        foreach (var pair in arguments.Split(
                     ['&', ';'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
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
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        NativeAppNotificationActivation? controlledActivation =
            TryGetAotTodoNotificationForwardingActivation();
        if (controlledActivation is not null)
        {
            return controlledActivation;
        }
#endif

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

                return new NativeAppNotificationActivation(
                    notificationArgs.Argument,
                    userInput,
                    NativeAppNotificationActivationSource.CurrentAppInstance,
                    DateTimeOffset.UtcNow,
                    Environment.ProcessId);
            }
        }
        catch (Exception ex)
        {
            Log($"[Notification] Failed to read native notification activation args: {ex.Message}");
        }

        return null;
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

    private async Task CompleteExternalActivationInitializationAsync()
    {
        _externalActivationReady = true;

        // The named auto-reset event retains a signal while startup is still
        // restoring services. Consume that signal on the UI thread before
        // registering the long-lived wait, then drain every queued envelope.
        bool startupSignalPending = false;
        try
        {
            startupSignalPending = _activationEvent?.WaitOne(0) == true;
        }
        catch (Exception ex)
        {
            Log($"[Activation] Failed to inspect the startup activation event: {ex.Message}");
        }

        try
        {
            if (startupSignalPending)
            {
                await HandleExternalActivationAsync();
            }
            else
            {
                DrainPendingNativeNotificationActivations();
            }
        }
        finally
        {
            RegisterActivationListener();
        }
    }

    private async Task HandleExternalActivationAsync()
    {
        if (!_externalActivationReady)
        {
            _externalActivationRequestedWhileBusy = true;
            Log("HandleExternalActivationAsync deferred until services are ready");
            return;
        }

        if (_externalActivationHandling)
        {
            _externalActivationRequestedWhileBusy = true;
            return;
        }

        _externalActivationHandling = true;
        Log("HandleExternalActivationAsync invoked");
        try
        {
            do
            {
                _externalActivationRequestedWhileBusy = false;
                ScheduleExternalStateRecovery();

                bool activationHandled = DrainPendingNativeNotificationActivations();
                string? jumpListArgument = TakePendingJumpListArgument();
                if (!string.IsNullOrWhiteSpace(jumpListArgument))
                {
                    await JumpListService.HandleActivationAsync(jumpListArgument);
                    activationHandled = true;
                }

                if (activationHandled)
                {
                    continue;
                }

                DateTimeOffset activationAtUtc = DateTimeOffset.UtcNow;
                bool settingsWindowOpen = _settingsWindow is not null;
                bool coalesceBareActivation =
                    ExternalActivationPolicy.ShouldCoalesceBareActivation(
                        _lastBareExternalActivationAtUtc,
                        activationAtUtc,
                        settingsWindowOpen);
                _lastBareExternalActivationAtUtc = activationAtUtc;
                if (coalesceBareActivation)
                {
                    Log(
                        "[Activation] Coalesced duplicate bare activation " +
                        $"windowMs={ExternalActivationPolicy.BareActivationDuplicateWindow.TotalMilliseconds:F0} " +
                        "settingsOpen=true");
                    continue;
                }

                await EnsureInitialFileWidgetSetupAsync(isInteractiveLaunch: true);
                if (await EnsureOnboardingAsync(isInteractiveLaunch: true))
                {
                    continue;
                }

                if (WidgetManager is not null)
                {
                    bool hasConfiguredWidgets = SettingsService.Settings.Widgets.Any(widget =>
                        widget.WidgetKind == WidgetKind.File &&
                        !widget.IsDisabled &&
                        !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id));
                    bool anyLoadedVisible = WidgetManager.HasVisibleFileWidgets;
                    BareExternalActivationAction fallbackAction =
                        ExternalActivationPolicy.DecideBareActivation(
                            new BareExternalActivationContext(
                                hasConfiguredWidgets,
                                anyLoadedVisible));
                    Log(
                        $"[Activation] Bare fallback action={fallbackAction} " +
                        $"configuredFileWidgets={hasConfiguredWidgets} " +
                        $"visibleFileWidgets={anyLoadedVisible}");

                    if (fallbackAction ==
                        BareExternalActivationAction.RestoreAllWidgetsAndOpenSettings)
                    {
                        await WidgetManager.SetAllWidgetsVisibleAsync(true);
                    }
                }

                OpenSettings();
            }
            while (_externalActivationRequestedWhileBusy);
        }
        finally
        {
            _externalActivationHandling = false;
        }
    }

    private bool DrainPendingNativeNotificationActivations()
    {
        bool handled = false;
        const int maxDrainCount = 128;
        for (int index = 0; index < maxDrainCount; index++)
        {
            NativeNotificationActivationEnvelopeTakeResult takeResult =
                PendingNativeNotificationActivationStore.TryTakeNext();
            switch (takeResult.Disposition)
            {
                case NativeNotificationActivationEnvelopeTakeDisposition.Empty:
                    return handled;
                case NativeNotificationActivationEnvelopeTakeDisposition.Consumed
                    when takeResult.Envelope is { } envelope:
                    handled = true;
                    Log(
                        $"[Notification] Consumed forwarded activation envelope " +
                        $"envelope={envelope.EnvelopeId} sourcePid={envelope.SourceProcessId} " +
                        $"userInput={envelope.UserInput.Count} legacy={envelope.IsLegacyArgumentsOnly}");
                    OnPendingNativeNotificationActivationConsumed(envelope);
                    HandleNativeNotificationActivation(
                        new NativeAppNotificationActivation(
                            envelope.Arguments,
                            envelope.UserInput,
                            envelope.ActivationSource,
                            envelope.CreatedAtUtc,
                            envelope.SourceProcessId,
                            envelope.EnvelopeId));
                    break;
                case NativeNotificationActivationEnvelopeTakeDisposition.Rejected:
                    handled = true;
                    Log(
                        $"[Notification] Rejected forwarded activation envelope " +
                        $"path={takeResult.Path ?? "none"} error={takeResult.Error ?? "unknown"}");
                    OnPendingNativeNotificationActivationRejected(
                        takeResult.Path,
                        takeResult.Error);
                    break;
                default:
                    Log(
                        $"[Notification] Failed to drain forwarded activation envelope " +
                        $"path={takeResult.Path ?? "none"} error={takeResult.Error ?? "unknown"}");
                    return handled;
            }
        }

        if (PendingNativeNotificationActivationStore.HasPendingActivation)
        {
            Log(
                $"[Notification] Forwarded activation drain yielded after " +
                $"{maxDrainCount} envelopes; scheduling the next batch.");
            try
            {
                // The auto-reset event retains this continuation even during
                // startup, before the long-lived listener is registered.
                _activationEvent?.Set();
            }
            catch (Exception ex)
            {
                Log($"[Notification] Failed to schedule the next activation batch: {ex.Message}");
            }
        }

        return handled;
    }

    partial void OnPendingNativeNotificationActivationConsumed(
        NativeNotificationActivationEnvelope envelope);

    partial void OnPendingNativeNotificationActivationRejected(
        string? path,
        string? error);

    partial void OnNativeNotificationActivationObserved(
        NativeAppNotificationActivation activation);

    partial void OnTodoNotificationActivationRouteObserved(
        NativeAppNotificationActivation? activation,
        TodoNotificationActivationRouteResult result);

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

    private void ShowSettingsLoadRecoveryNotification()
    {
        switch (SettingsService.LastLoadRecoveryState)
        {
            case SettingsLoadRecoveryState.RecoveredFromBackup:
                ShowSettingsNotification(
                    "Settings.Persistence.RecoveredTitle",
                    "Settings.Persistence.RecoveredBody",
                    NotificationIcon.Info);
                break;
            case SettingsLoadRecoveryState.DefaultsAfterFailure:
                ShowSettingsNotification(
                    "Settings.Persistence.ResetTitle",
                    "Settings.Persistence.ResetBody",
                    NotificationIcon.Warning);
                break;
        }

        if (SettingsService.LastPersistenceFailure is { } failure)
        {
            OnSettingsPersistenceFailed(failure);
        }
    }

    private void OnSettingsPersistenceFailed(SettingsPersistenceFailure failure)
    {
        Log(
            $"[SettingsService] Persistence failure operation={failure.Operation} " +
            $"at={failure.OccurredAt:O} message={failure.Message}");
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(() => OnSettingsPersistenceFailed(failure));
            return;
        }

        if (DateTimeOffset.UtcNow - _lastSettingsPersistenceNotificationAt <
            TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastSettingsPersistenceNotificationAt = DateTimeOffset.UtcNow;
        ShowSettingsNotification(
            "Settings.Persistence.SaveFailedTitle",
            "Settings.Persistence.SaveFailedBody",
            NotificationIcon.Warning);
    }

    private void ShowSettingsNotification(
        string titleKey,
        string bodyKey,
        NotificationIcon icon)
    {
        if (LocalizationService is null)
        {
            return;
        }

        string title = LocalizationService.T(titleKey);
        string message = LocalizationService.T(bodyKey);
        if (_nativeNotificationService?.TryShow(title, message) == true || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                icon,
                customIconHandle: null,
                largeIcon: false,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log($"[SettingsService] Persistence notification failed: {ex.Message}");
        }
    }

    private void ShowManagedStorageUnavailableNotification()
    {
        if (LocalizationService is null)
        {
            return;
        }

        string title = LocalizationService.T("Settings.ManagedPath.UnavailableTitle");
        string message = LocalizationService.T("Settings.ManagedPath.UnavailableBody");
        if (_nativeNotificationService?.TryShow(title, message) == true || _trayIcon is null)
        {
            return;
        }

        try
        {
            _trayIcon.ShowNotification(
                title,
                message,
                NotificationIcon.Warning,
                customIconHandle: null,
                largeIcon: false,
                sound: false,
                respectQuietTime: true,
                realtime: false,
                timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log($"[WidgetManager] Managed storage unavailable notification failed: {ex.Message}");
        }
    }

    private void ShowRecoverySnapshotAvailableNotification(DeskBoxBackupSnapshotInfo? snapshot)
    {
        if (snapshot is null || LocalizationService is null)
        {
            return;
        }

        string title = LocalizationService.T("Settings.Recovery.AvailableTitle");
        string message = LocalizationService.T("Settings.Recovery.AvailableBody");
        Log($"[DataBackup] Recovery snapshot available after fresh settings start: {snapshot.Path}");

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
                timeout: TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log($"[DataBackup] Recovery snapshot notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A second-instance activation is also a useful lifecycle signal: it is
    /// commonly produced when the user returns after lock, sleep, Explorer
    /// restart, or a mapped-drive reconnection. Reconcile lightweight external
    /// state without rebuilding the whole application on every activation.
    /// </summary>
    private void ScheduleExternalStateRecovery()
    {
        if (Interlocked.Exchange(ref _externalStateRecoveryScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(650).ConfigureAwait(false);
                UiDispatcherQueue?.TryEnqueue(() => _ = RecoverExternalStateAsync());
            }
            catch (Exception ex)
            {
                Log($"[Lifecycle] External state recovery scheduling failed: {ex.Message}");
                Volatile.Write(ref _externalStateRecoveryScheduled, 0);
            }
        });
    }

    private async Task RecoverExternalStateAsync()
    {
        try
        {
            RefreshQuickCaptureClipboardService(captureCurrent: true);

            if (WidgetManager is not null)
            {
                string[] fileWidgetIds = SettingsService.Settings.Widgets
                    .Where(widget => widget.WidgetKind == WidgetKind.File &&
                                     !widget.IsDisabled &&
                                     !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id))
                    .Select(widget => widget.Id)
                    .ToArray();

                foreach (string widgetId in fileWidgetIds)
                {
                    await WidgetManager.RefreshFileWidgetAsync(widgetId);
                }
            }

            Log("[Lifecycle] External state recovery completed.");
        }
        catch (Exception ex)
        {
            Log($"[Lifecycle] External state recovery failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _externalStateRecoveryScheduled, 0);
        }
    }

    /// <summary>
    /// Runs an explicit external-state reconciliation from the diagnostics
    /// page. Unlike the debounced lifecycle path this method is awaitable so
    /// the UI can report when file widgets have finished refreshing.
    /// </summary>
    public async Task ForceExternalStateRecoveryAsync()
    {
        await RecoverExternalStateAsync();
        _displayAreaWatcher?.RefreshNow();
        if (_everythingSearchService is not null &&
            SettingsService.Settings.SearchEverythingEnabled)
        {
            await _everythingSearchService.RefreshConnectionAsync();
        }
    }

    private void OnLanguageChanged()
    {
        Localized.RefreshAll(LocalizationService);
        RefreshTrayMenuText();
        RefreshTrayToolTipText();
    }

    private void OpenSettings()
    {
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
    }

    private SettingsWindow CreateSettingsWindow()
    {
        _settingsWindow = new SettingsWindow(SettingsService, ThemeService, LocalizationService);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ScheduleLightMemoryCleanup(completedHeavyOperation: true);
            ScheduleBackgroundMemoryCleanup();
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

    public void ShowSettings()
    {
        OpenSettings();
    }

    public void ShowSettings(string sectionTag)
    {
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        settingsWindow.ShowSection(sectionTag);
    }

    public void ShowGlanceSettings(string widgetId)
    {
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        settingsWindow.ShowGlanceSection(widgetId);
    }

    private async Task EnsureInitialFileWidgetSetupAsync(bool isInteractiveLaunch)
    {
        if (WidgetManager is null)
        {
            return;
        }

        AppSettings settings = SettingsService.Settings;
        InitialFileWidgetSetupDecision decision =
            InitialFileWidgetSetupPolicy.Evaluate(new InitialFileWidgetSetupSnapshot(
                isInteractiveLaunch,
                SettingsService.LastLoadRecoveryState,
                settings.HasResolvedInitialFileWidgetSetup,
                InitialFileWidgetSetupPolicy.HasConfiguredFileWidget(settings)));
        Log(
            $"[InitialFileWidgetSetup] decision={decision} interactive={isInteractiveLaunch} " +
            $"loadState={SettingsService.LastLoadRecoveryState} " +
            $"resolved={settings.HasResolvedInitialFileWidgetSetup}");

        if (decision == InitialFileWidgetSetupDecision.ResolveExistingConfiguration)
        {
            settings.HasResolvedInitialFileWidgetSetup = true;
            await SettingsService.SaveAsync();
            return;
        }

        if (decision != InitialFileWidgetSetupDecision.CreateDefaultWidget)
        {
            return;
        }

        // CreateManagedWidgetAsync persists the new widget config. Set the
        // one-time marker first so both values are written by the same save.
        settings.HasResolvedInitialFileWidgetSetup = true;
        try
        {
            await WidgetManager.CreateInitialManagedWidgetAsync(
                LocalizationService.T("Widget.DefaultDesktopName"));
        }
        catch
        {
            // Directory creation can fail before a widget config exists. Keep
            // the setup pending in that case so a later interactive launch can
            // retry after the storage problem has been corrected.
            if (!InitialFileWidgetSetupPolicy.HasConfiguredFileWidget(settings))
            {
                settings.HasResolvedInitialFileWidgetSetup = false;
            }

            throw;
        }
    }

    private async Task<bool> EnsureOnboardingAsync(bool isInteractiveLaunch)
    {
        if (!isInteractiveLaunch || SettingsService.Settings.HasCompletedOnboarding)
        {
            return false;
        }

        // Completion is recorded only when the user finishes or explicitly
        // skips the guide. Closing the window leaves the current step resumable.
        ShowOnboarding(resumeProgress: true);
        return true;
    }

    public void ShowOnboarding(bool resumeProgress = false)
    {
        CancelBackgroundMemoryCleanup();
        int initialStep = resumeProgress
            ? SettingsService.Settings.OnboardingStepIndex
            : 0;
        bool shouldRestartIntro = _onboardingWindow is not null;
        if (_onboardingWindow is null)
        {
            _onboardingWindow = new OnboardingWindow(
                SettingsService,
                LocalizationService,
                initialStep);
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
            _onboardingWindow.RestartIntro(initialStep);
        }
    }

    internal async Task<bool> ShowFirstFileWidgetForOnboardingAsync()
    {
        if (WidgetManager is null)
        {
            return false;
        }

        WidgetConfig? firstFileWidget = SettingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !SettingsService.Settings.DeletedWidgetIds.Contains(widget.Id))
            .OrderByDescending(widget => widget.FollowsDefaultStoragePath)
            .FirstOrDefault();
        if (firstFileWidget is null)
        {
            return false;
        }

        bool shown = await WidgetManager.ShowWidgetAsync(
            firstFileWidget.Id,
            reveal: false,
            autoRestoreOnReveal: false);
        if (!shown)
        {
            return false;
        }

        _onboardingRaisedFileWidgetId = firstFileWidget.Id;
        WidgetManager.SetWidgetOnboardingTopMost(
            firstFileWidget.Id,
            isTopMost: true);
        return true;
    }

    internal void ReleaseOnboardingFileWidgetRaise()
    {
        string? widgetId = _onboardingRaisedFileWidgetId;
        _onboardingRaisedFileWidgetId = null;
        if (widgetId is not null)
        {
            WidgetManager?.SetWidgetOnboardingTopMost(
                widgetId,
                isTopMost: false);
        }
    }

    internal bool HasVisibleWidgetsForOnboarding =>
        WidgetManager?.HasVisibleWidgets == true;

    internal void NotifyOnboardingFileImportCompleted(int importedItemCount)
    {
        if (importedItemCount > 0 && _onboardingWindow is not null)
        {
            OnboardingFileImportCompleted?.Invoke(importedItemCount);
        }
    }

    private static int s_lightMemoryCleanupGeneration;
    private static int s_pendingHeavyMemoryCleanup;
    private static int s_activeHeavyMemoryCleanupCount;
    private static int s_backgroundMemoryCleanupGeneration;
    private static long s_memoryCleanupEpoch;

    /// <summary>
    /// Changes only after a process working-set trim has made previously warmed
    /// XAML pages non-resident. A forced GC alone does not invalidate a live
    /// capsule layout and therefore must not make every widget cold again.
    /// </summary>
    internal static long MemoryCleanupEpoch => Volatile.Read(ref s_memoryCleanupEpoch);

    /// <summary>
    /// Raised (possibly from a background thread) after a working-set trim
    /// invalidated warmed state. Observers must marshal to their own threads.
    /// </summary>
    internal static event Action? MemoryCleanupEpochAdvanced;

    private static void AdvanceMemoryCleanupEpoch(string reason)
    {
        long cleanupEpoch = Interlocked.Increment(ref s_memoryCleanupEpoch);
        PerformanceLogger.Mark(
            "MemoryCleanupEpochAdvanced",
            $"epoch={cleanupEpoch} reason={reason}");
        Action? handlers = MemoryCleanupEpochAdvanced;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log($"[MemoryCleanup] Epoch observer failed: {ex.Message}");
            }
        }
    }

    private void StartVisibleIdleMemoryMaintenance()
    {
        if (_visibleIdleMemoryMaintenanceTimer is null)
        {
            _visibleIdleMemoryMaintenanceTimer = UiDispatcherQueue.CreateTimer();
            _visibleIdleMemoryMaintenanceTimer.IsRepeating = false;
            _visibleIdleMemoryMaintenanceTimer.Tick += VisibleIdleMemoryMaintenanceTimer_Tick;
        }

        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(SettingsService.Settings);
        bool visibleIdleCleanupEnabled =
            ConfigureVisibleIdleMemoryTracker(performance);
        if (!visibleIdleCleanupEnabled)
        {
            _visibleIdleMemoryMaintenanceTimer.Stop();
            return;
        }

        var activity = CaptureMemoryCleanupActivity();
        _visibleIdleMemoryTracker.Observe(
            DateTimeOffset.UtcNow,
            visibleIdleCleanupEnabled &&
            MemoryCleanupPolicy.IsVisibleIdleCandidate(activity));
        ScheduleVisibleIdleMemoryMaintenance(
            TimeSpan.FromSeconds(VisibleIdleMemoryCheckIntervalSeconds));
    }

    private void ScheduleVisibleIdleMemoryMaintenance(TimeSpan delay)
    {
        if (_visibleIdleMemoryMaintenanceTimer is null)
        {
            return;
        }

        _visibleIdleMemoryMaintenanceTimer.Stop();
        _visibleIdleMemoryMaintenanceTimer.Interval = delay;
        _visibleIdleMemoryMaintenanceTimer.Start();
    }

    private async void VisibleIdleMemoryMaintenanceTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Interlocked.Exchange(ref _visibleIdleMemoryMaintenanceRunning, 1) != 0)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref s_pendingHeavyMemoryCleanup) != 0 ||
                Volatile.Read(ref s_activeHeavyMemoryCleanupCount) != 0)
            {
                return;
            }

            EffectivePerformanceSettings performance =
                PerformanceSettingsPolicy.Resolve(SettingsService.Settings);
            if (!ConfigureVisibleIdleMemoryTracker(performance))
            {
                return;
            }

            int visibleIdleDelaySeconds =
                performance.VisibleIdleCacheCleanupDelaySeconds;
            var activity = CaptureMemoryCleanupActivity();
            bool isVisibleIdleCandidate =
                MemoryCleanupPolicy.IsVisibleIdleCandidate(activity);
            DateTimeOffset maintenanceDueAt = DateTimeOffset.UtcNow;
            if (!_visibleIdleMemoryTracker.Observe(
                    maintenanceDueAt,
                    isVisibleIdleCandidate))
            {
                return;
            }

            long totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            long allocatedSinceLastCollection = _hasCompletedVisibleIdleCollection
                ? Math.Max(0, totalAllocatedBytes - _lastVisibleIdleCollectionAllocatedBytes)
                : totalAllocatedBytes;
            long managedHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes;
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBefore = process.WorkingSet64;
            long privateBytesBefore = process.PrivateMemorySize64;

            Localized.PruneDeadTargets();
            _fileMetaService?.Clear();
            IconHelper.IdleIconCacheReleaseResult cacheRelease =
                IconHelper.ReleaseIdleCaches(
                    allWidgetsHidden: false,
                    clearVisibleCaches: performance.ClearVisibleIdleCaches);

            bool shouldCollectManagedMemory =
                MemoryCleanupPolicy.ShouldCollectVisibleIdleManagedMemory(
                    activity,
                    managedHeapBytes,
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    allocatedSinceLastCollection,
                    _hasCompletedVisibleIdleCollection);
            if (shouldCollectManagedMemory)
            {
                PerformanceLogger.Mark(
                    "VisibleIdleMemoryCollectionTriggered",
                    $"managedMB={managedHeapBytes / (1024.0 * 1024):F1} " +
                    $"workingSetMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
                    $"privateMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
                    $"allocatedSinceMB={allocatedSinceLastCollection / (1024.0 * 1024):F1}");
                PerformanceLogger.SampleMemory("visible-idle-collection-before");

                await Task.Run(static () =>
                {
                    // Visible widgets keep their XAML trees and data intact. This
                    // collection only reclaims unreachable managed objects/finalizers.
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Forced,
                        blocking: true,
                        compacting: false);
                    GC.WaitForPendingFinalizers();
                });

                _lastVisibleIdleCollectionAllocatedBytes = totalAllocatedBytes;
                _hasCompletedVisibleIdleCollection = true;
                PerformanceLogger.SampleMemory("visible-idle-collection-after");
            }

            process.Refresh();
            var trimActivity = CaptureMemoryCleanupActivity();
            bool hasBlockingVisualWork =
                WidgetManager?.HasActiveVisualWork == true;
            bool shouldTrimWorkingSet =
                MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
                    trimActivity,
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    hasBlockingVisualWork);
            bool trimmedWorkingSet = false;
            if (shouldTrimWorkingSet)
            {
                PerformanceLogger.SampleMemory(
                    "visible-idle-working-set-trim-before");
                trimmedWorkingSet =
                    await Task.Run(Win32Helper.TrimCurrentProcessWorkingSet);
                if (trimmedWorkingSet)
                {
                    AdvanceMemoryCleanupEpoch(
                        "visible-idle-working-set-trim");
                }
                process.Refresh();
                PerformanceLogger.SampleMemory(
                    "visible-idle-working-set-trim-after");
            }

            bool releasedIdleCaches =
                cacheRelease.ReleasedThumbnails > 0 ||
                cacheRelease.ReleasedDecodedBitmaps > 0 ||
                cacheRelease.ReleasedIconByteEntries > 0;
            bool performedMaintenance =
                shouldCollectManagedMemory ||
                releasedIdleCaches ||
                trimmedWorkingSet;
            bool trimThresholdReached =
                process.WorkingSet64 >=
                    MemoryCleanupPolicy.VisibleIdleWorkingSetThresholdBytes &&
                process.PrivateMemorySize64 >=
                    MemoryCleanupPolicy.VisibleIdlePrivateBytesThreshold;
            bool trimAttemptBlocked =
                trimThresholdReached &&
                (!MemoryCleanupPolicy.IsVisibleIdleCandidate(trimActivity) ||
                    hasBlockingVisualWork);
            bool trimRetryPending =
                trimAttemptBlocked ||
                (shouldTrimWorkingSet && !trimmedWorkingSet);
            if (performedMaintenance && !trimRetryPending)
            {
                _visibleIdleMemoryTracker.CommitMaintenance(
                    DateTimeOffset.UtcNow);
            }
            Action<string> writeMaintenanceLog =
                performedMaintenance
                ? Log
                : LogVerbose;
            writeMaintenanceLog(
                $"[Memory] Visible idle cleanup completed idleSeconds={visibleIdleDelaySeconds} " +
                $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
                $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
                $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
                $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
                $"collected={shouldCollectManagedMemory} " +
                $"trimmed={trimmedWorkingSet} " +
                $"blockingVisualWork={hasBlockingVisualWork} " +
                $"trimRetryPending={trimRetryPending} " +
                $"releasedThumbs={cacheRelease.ReleasedThumbnails} " +
                $"releasedBitmaps={cacheRelease.ReleasedDecodedBitmaps} " +
                $"releasedIconBytes={cacheRelease.ReleasedIconByteEntries}");
        }
        catch (Exception ex)
        {
            Log($"[Memory] Visible idle maintenance failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _visibleIdleMemoryMaintenanceRunning, 0);
            if (_visibleIdleMemoryMaintenanceTimer is not null &&
                ConfigureVisibleIdleMemoryTracker(
                    PerformanceSettingsPolicy.Resolve(SettingsService.Settings)))
            {
                ScheduleVisibleIdleMemoryMaintenance(
                    TimeSpan.FromSeconds(VisibleIdleMemoryCheckIntervalSeconds));
            }
        }
    }

    private void StopVisibleIdleMemoryMaintenance()
    {
        if (_visibleIdleMemoryMaintenanceTimer is null)
        {
            return;
        }

        _visibleIdleMemoryMaintenanceTimer.Stop();
        _visibleIdleMemoryMaintenanceTimer.Tick -= VisibleIdleMemoryMaintenanceTimer_Tick;
        _visibleIdleMemoryMaintenanceTimer = null;
        _visibleIdleMemoryTracker.Reset();
    }

    private void MarkVisibleIdleMemoryCollectionBaseline()
    {
        _lastVisibleIdleCollectionAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _hasCompletedVisibleIdleCollection = true;
    }

    private bool ConfigureVisibleIdleMemoryTracker(
        EffectivePerformanceSettings performance)
    {
        int delaySeconds = performance.VisibleIdleCacheCleanupDelaySeconds;
        if (delaySeconds == PerformanceSettingsPolicy.CleanupNever)
        {
            _visibleIdleMemoryTracker.Reset();
            return false;
        }

        int cooldownSeconds = Math.Max(
            VisibleIdleMemoryMinimumCooldownSeconds,
            delaySeconds);
        _visibleIdleMemoryTracker.Configure(
            TimeSpan.FromSeconds(delaySeconds),
            TimeSpan.FromSeconds(cooldownSeconds));
        return true;
    }

    private MemoryCleanupActivitySnapshot CaptureMemoryCleanupActivity()
    {
        var widgetManager = WidgetManager;
        bool isDeskBoxForeground = IsDeskBoxWindow(Win32Helper.GetForegroundWindow());
        bool isPointerOverDeskBox =
            Win32Helper.GetCursorPos(out var cursor) &&
            IsDeskBoxWindow(Win32Helper.WindowFromPoint(cursor));
        return new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: widgetManager?.HasVisibleWidgets == true,
            IsWidgetInteractionActive: widgetManager?.IsWidgetInteractionActive == true,
            IsSettingsOpen: _settingsWindow is not null,
            IsOnboardingOpen: _onboardingWindow is not null,
            IsSearchPopupVisible: _searchPopupWindow?.IsPopupVisible == true,
            IsDeskBoxForeground: isDeskBoxForeground,
            IsPointerOverDeskBox: isPointerOverDeskBox);
    }

    internal static void CancelBackgroundMemoryCleanup()
    {
        Interlocked.Increment(ref s_backgroundMemoryCleanupGeneration);
        NotifyMemoryCleanupActivity();
    }

    internal static void NotifyPerformanceSettingsChanged()
    {
        CancelBackgroundMemoryCleanup();
        App app = Current;
        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings);
        IconHelper.ConfigurePerformanceCacheBudget(performance.CacheBudget);
        app._visibleIdleMemoryTracker.Reset();
        if (app.ConfigureVisibleIdleMemoryTracker(performance))
        {
            app.ScheduleVisibleIdleMemoryMaintenance(
                TimeSpan.FromSeconds(VisibleIdleMemoryCheckIntervalSeconds));
        }
        else
        {
            app._visibleIdleMemoryMaintenanceTimer?.Stop();
        }
        app.CancelTransientWindowRelease();
        if (app._searchPopupWindow is { IsPopupVisible: false })
        {
            app.ScheduleTransientWindowRelease();
        }

        if (app.WidgetManager?.HasVisibleWidgets != false ||
            app._settingsWindow is not null ||
            app._onboardingWindow is not null ||
            app._searchPopupWindow?.IsPopupVisible == true)
        {
            return;
        }

        ScheduleBackgroundMemoryCleanup();
    }

    internal static void NotifyMemoryCleanupActivity()
    {
        if (Application.Current is App app)
        {
            app._visibleIdleMemoryTracker.Reset();
        }
    }

    internal bool CanRunCompactExpansionWarmup =>
        _settingsWindow is null &&
        _onboardingWindow is null &&
        _searchPopupWindow?.IsPopupVisible != true &&
        WidgetManager?.IsWidgetInteractionActive != true &&
        Volatile.Read(ref s_pendingHeavyMemoryCleanup) == 0 &&
        Volatile.Read(ref s_activeHeavyMemoryCleanupCount) == 0;

    internal bool CanRunCriticalCompactExpansionWarmup =>
        Volatile.Read(ref s_pendingHeavyMemoryCleanup) == 0 &&
        Volatile.Read(ref s_activeHeavyMemoryCleanupCount) == 0;

    private void CancelTransientWindowRelease()
    {
        Interlocked.Increment(ref _transientWindowReleaseGeneration);
    }

    private void ScheduleTransientWindowRelease()
    {
        int generation = Interlocked.Increment(
            ref _transientWindowReleaseGeneration);
        SearchPopupWindow? popup = _searchPopupWindow;
        if (popup is null || popup.IsPopupVisible)
        {
            return;
        }

        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(SettingsService.Settings);
        int delaySeconds = performance.TransientWindowReleaseDelaySeconds;
        if (delaySeconds == PerformanceSettingsPolicy.CleanupNever)
        {
            return;
        }

        PerformanceLogger.Mark(
            "TransientWindowReleaseScheduled",
            $"window=search mode={performance.Mode} delaySeconds={delaySeconds}");
        UiDispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            if (generation != Volatile.Read(
                    ref _transientWindowReleaseGeneration) ||
                !ReferenceEquals(_searchPopupWindow, popup) ||
                popup.IsPopupVisible)
            {
                return;
            }

            PerformanceLogger.Mark(
                "TransientWindowReleaseTriggered",
                $"window=search hiddenSeconds={delaySeconds}");
            try
            {
                popup.Close();
            }
            catch (Exception ex)
            {
                Log($"[Memory] Hidden search window release failed: {ex.Message}");
            }
        });
    }

    internal static void ScheduleBackgroundMemoryCleanup()
    {
        App app = Current;
        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings);
        int generation = Interlocked.Increment(ref s_backgroundMemoryCleanupGeneration);
        if (performance.HiddenCacheCleanupDelaySeconds ==
            PerformanceSettingsPolicy.CleanupNever)
        {
            PerformanceLogger.Mark(
                "BackgroundMemoryCleanupDisabled",
                $"mode={performance.Mode}");
            return;
        }

        int softDelaySeconds = performance.HiddenCacheCleanupDelaySeconds;
        int deepDelaySeconds = performance.HiddenDeepCleanupDelaySeconds;
        int workingSetTrimDelaySeconds =
            performance.HiddenIdleWorkingSetTrimDelaySeconds;
        PerformanceLogger.Mark(
            "BackgroundMemoryCleanupScheduled",
            $"mode={performance.Mode} " +
            $"softDelaySeconds={softDelaySeconds} " +
            $"deepDelaySeconds={deepDelaySeconds} " +
            $"trimDelaySeconds={workingSetTrimDelaySeconds}");

        UiDispatcherQueue?.TryEnqueue(() =>
        {
            SafeFireAndForget(() =>
                app.RunBackgroundCacheCleanupScheduleAsync(
                    generation,
                    softDelaySeconds,
                    deepDelaySeconds));
            if (workingSetTrimDelaySeconds !=
                PerformanceSettingsPolicy.CleanupNever)
            {
                SafeFireAndForget(() =>
                    app.RunHiddenWorkingSetTrimScheduleAsync(
                        generation,
                        workingSetTrimDelaySeconds));
            }
        });
    }

    private async Task RunBackgroundCacheCleanupScheduleAsync(
        int generation,
        int softDelaySeconds,
        int deepDelaySeconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(softDelaySeconds));
        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
        {
            return;
        }

        if (!CanRunBackgroundMemoryCleanup())
        {
            PerformanceLogger.Mark(
                "BackgroundMemoryCleanupSkipped",
                "reason=foreground-active");
            return;
        }

        PerformanceLogger.Mark("BackgroundMemorySoftCleanupTriggered");
        await RunBackgroundSoftMemoryCleanupAsync(
            generation,
            softDelaySeconds);

        if (deepDelaySeconds == PerformanceSettingsPolicy.CleanupNever)
        {
            return;
        }

        int remainingDelaySeconds = Math.Max(
            1,
            deepDelaySeconds - softDelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(remainingDelaySeconds));
        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
        {
            return;
        }

        if (!CanRunBackgroundMemoryCleanup())
        {
            PerformanceLogger.Mark(
                "BackgroundMemoryDeepCleanupSkipped",
                "reason=foreground-active");
            return;
        }

        PerformanceLogger.Mark("BackgroundMemoryDeepCleanupTriggered");
        HiddenWidgetResourceReleaseResult releasedWidgetResources =
            WidgetManager?.ReleaseLongHiddenWidgetResources() ?? default;
        PerformanceLogger.Mark(
            "LongHiddenWidgetResourcesReleased",
            $"hosts={releasedWidgetResources.ContentHostCount} " +
            $"cachedContents={releasedWidgetResources.CachedContentCount}");
        ScheduleLightMemoryCleanup(
            completedHeavyOperation: true,
            requiredBackgroundGeneration: generation);
    }

    private async Task RunHiddenWorkingSetTrimScheduleAsync(
        int generation,
        int workingSetTrimDelaySeconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(workingSetTrimDelaySeconds));
        for (int retry = 0; retry < 12; retry++)
        {
            if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
                !CanRunBackgroundMemoryCleanup())
            {
                return;
            }

            if (Volatile.Read(ref s_pendingHeavyMemoryCleanup) == 0 &&
                Volatile.Read(ref s_activeHeavyMemoryCleanupCount) == 0)
            {
                await TryRunHiddenWorkingSetTrimAsync(
                    generation,
                    workingSetTrimDelaySeconds);
                return;
            }

            await Task.Delay(250);
        }

        PerformanceLogger.Mark(
            "WorkingSetTrimSkipped",
            "reason=cleanup-busy");
    }

    private bool CanRunBackgroundMemoryCleanup() =>
        WidgetManager is
        {
            HasVisibleWidgets: false,
            IsWidgetInteractionActive: false
        } &&
        _settingsWindow is null &&
        _onboardingWindow is null &&
        _searchPopupWindow?.IsPopupVisible != true;

    private async Task RunBackgroundSoftMemoryCleanupAsync(
        int generation,
        int hiddenDelaySeconds)
    {
        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long privateBytesBefore = process.PrivateMemorySize64;

        Localized.PruneDeadTargets();
        _fileMetaService?.Clear();
        IconHelper.IdleIconCacheReleaseResult cacheRelease =
            IconHelper.ReleaseIdleCaches(allWidgetsHidden: true);

        Interlocked.Increment(ref s_activeHeavyMemoryCleanupCount);
        try
        {
            await Task.Run(static () =>
            {
                // Hidden widgets can safely finalize unreachable WinUI wrappers,
                // but avoid LOH compaction here so a short tray hide stays cheap.
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: false);
                GC.WaitForPendingFinalizers();
            });
            MarkVisibleIdleMemoryCollectionBaseline();
        }
        finally
        {
            Interlocked.Decrement(ref s_activeHeavyMemoryCleanupCount);
        }

        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            Log("[Memory] Background soft cleanup cancelled after collection because UI became active");
            return;
        }

        process.Refresh();
        Log(
            $"[Memory] Background soft cleanup completed hiddenSeconds={hiddenDelaySeconds} " +
            $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
            $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
            $"trimmed=false " +
            $"releasedThumbs={cacheRelease.ReleasedThumbnails} " +
            $"releasedBitmaps={cacheRelease.ReleasedDecodedBitmaps} " +
            $"releasedIconBytes={cacheRelease.ReleasedIconByteEntries} " +
            $"releasedEstimatedMB={cacheRelease.ReleasedEstimatedBytes / (1024.0 * 1024):F1}");
    }

    private async Task TryRunHiddenWorkingSetTrimAsync(
        int generation,
        int hiddenSeconds)
    {
        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup() ||
            Volatile.Read(ref s_pendingHeavyMemoryCleanup) != 0 ||
            Volatile.Read(ref s_activeHeavyMemoryCleanupCount) != 0)
        {
            return;
        }

        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(SettingsService.Settings);
        if (performance.HiddenIdleWorkingSetTrimDelaySeconds != hiddenSeconds)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int trimCooldownSeconds = string.Equals(
                performance.Mode,
                PerformanceSettingsPolicy.ModeCustom,
                StringComparison.Ordinal)
            ? Math.Max(PerformanceSettingsPolicy.CleanupAfter1Minute, hiddenSeconds)
            : HiddenWorkingSetTrimCooldownSeconds;
        if (now - _lastHiddenWorkingSetTrimAt <
            TimeSpan.FromSeconds(trimCooldownSeconds))
        {
            PerformanceLogger.Mark(
                "WorkingSetTrimSkipped",
                "reason=cooldown");
            return;
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
        var activity = CaptureMemoryCleanupActivity();
        bool isCustomMode = string.Equals(
            performance.Mode,
            PerformanceSettingsPolicy.ModeCustom,
            StringComparison.Ordinal);
        bool shouldTrimWorkingSet = isCustomMode
            ? MemoryCleanupPolicy.ShouldTrimHiddenIdleWorkingSet(
                activity,
                process.WorkingSet64)
            : MemoryCleanupPolicy.ShouldTrimResourceSaverHiddenWorkingSet(
                activity,
                process.WorkingSet64,
                memoryInfo.MemoryLoadBytes,
                memoryInfo.HighMemoryLoadThresholdBytes);
        if (!shouldTrimWorkingSet)
        {
            PerformanceLogger.Mark(
                "WorkingSetTrimSkipped",
                $"reason={(isCustomMode ? "below-threshold" : "no-pressure")} " +
                $"mode={performance.Mode} hiddenSeconds={hiddenSeconds} " +
                $"workingSetMB={process.WorkingSet64 / (1024.0 * 1024):F1}");
            return;
        }

        long workingSetBefore = process.WorkingSet64;
        long privateBytesBefore = process.PrivateMemorySize64;
        PerformanceLogger.SampleMemory("hidden-working-set-trim-before");
        bool trimmedWorkingSet =
            await Task.Run(Win32Helper.TrimCurrentProcessWorkingSet);
        if (trimmedWorkingSet)
        {
            _lastHiddenWorkingSetTrimAt = now;
            AdvanceMemoryCleanupEpoch("hidden-working-set-trim");
        }
        process.Refresh();
        PerformanceLogger.SampleMemory("hidden-working-set-trim-after");
        Log(
            $"[Memory] Hidden working-set trim completed " +
            $"mode={performance.Mode} " +
            $"hiddenSeconds={hiddenSeconds} " +
            $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
            $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
            $"trimmed={trimmedWorkingSet}");
    }

    internal static void ScheduleLightMemoryCleanup(
        bool completedHeavyOperation = false,
        int? requiredBackgroundGeneration = null)
    {
        if (completedHeavyOperation && requiredBackgroundGeneration is null)
        {
            Interlocked.Exchange(ref s_pendingHeavyMemoryCleanup, 1);
        }

        int generation = Interlocked.Increment(ref s_lightMemoryCleanupGeneration);
        App.UiDispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(2000);
            if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
            {
                return;
            }

            // A tray restore increments the background generation. Do not let
            // a cleanup that was armed by the previous hidden lifetime run its
            // forced GC/heap trim after the widgets are visible again.
            if (requiredBackgroundGeneration is int requiredGeneration &&
                requiredGeneration !=
                    Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
            {
                PerformanceLogger.Mark(
                    "BackgroundMemoryCleanupCancelled",
                    "reason=widgets-restored");
                return;
            }

            Localized.PruneDeadTargets();

            var memoryInfo = GC.GetGCMemoryInfo();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBefore = process.WorkingSet64;
            long privateBytesBefore = process.PrivateMemorySize64;
            // Always consume the pending marker. Using it as the right-hand side
            // of an || expression leaves it stuck at 1 whenever this invocation
            // already represents a completed heavy operation. That permanently
            // blocks visible-idle maintenance and compact-expansion warmup.
            bool hadPendingHeavyCleanup =
                Interlocked.Exchange(ref s_pendingHeavyMemoryCleanup, 0) != 0;
            bool heavyCleanupRequested =
                completedHeavyOperation || hadPendingHeavyCleanup;
            bool underMemoryPressure =
                memoryInfo.HeapSizeBytes >= 256L * 1024 * 1024 ||
                process.PrivateMemorySize64 >= 512L * 1024 * 1024;
            if (heavyCleanupRequested || underMemoryPressure)
            {
                Interlocked.Increment(ref s_activeHeavyMemoryCleanupCount);
                try
                {
                    await Task.Run(() =>
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;

                        // WinUI windows own reference-tracked COM objects whose native
                        // resources are often released by managed finalizers. A single
                        // non-blocking/optimized collection can leave both the wrappers
                        // and their native allocations behind, so complete the standard
                        // collect-finalize-collect sequence after a heavy UI teardown.
                        GC.Collect(
                            GC.MaxGeneration,
                            GCCollectionMode.Forced,
                            blocking: true,
                            compacting: true);
                        GC.WaitForPendingFinalizers();
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(
                            GC.MaxGeneration,
                            GCCollectionMode.Forced,
                            blocking: true,
                            compacting: true);

                        // The Windows LFH retains empty segments after a burst of WinUI
                        // window creation and teardown. Ask every process heap to release
                        // those caches only on this delayed heavy-cleanup path.
                        OptimizeNativeHeapResources();
                    });
                    Current.MarkVisibleIdleMemoryCollectionBaseline();
                    PerformanceLogger.SampleMemory("heavy-cleanup-completed");

                    process.Refresh();
                    Log(
                        $"[Memory] Deep cleanup completed " +
                        $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
                        $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
                        $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
                        $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
                        $"trimmed=false");
                }
                finally
                {
                    Interlocked.Decrement(ref s_activeHeavyMemoryCleanupCount);
                }
            }
        });
    }

    private static void OptimizeNativeHeapResources()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long privateBytesBefore = process.PrivateMemorySize64;
            var information = new HeapOptimizeResourcesInformation
            {
                Version = HeapOptimizeResourcesCurrentVersion
            };
            bool succeeded = HeapSetInformation(
                IntPtr.Zero,
                HeapOptimizeResources,
                ref information,
                (nuint)Marshal.SizeOf<HeapOptimizeResourcesInformation>());
            int error = succeeded ? 0 : Marshal.GetLastWin32Error();
            process.Refresh();
            long privateBytesAfter = process.PrivateMemorySize64;

            if (PerformanceLogger.IsEnabled)
            {
                Log(
                    $"[Perf] NativeHeapOptimize success={succeeded} error={error} " +
                    $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
                    $"privateAfterMB={privateBytesAfter / (1024.0 * 1024):F1}");
            }
        }
        catch (Exception ex)
        {
            if (PerformanceLogger.IsEnabled)
            {
                Log($"[Perf] NativeHeapOptimize failed: {ex.Message}");
            }
        }
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
        StopVisibleIdleMemoryMaintenance();

        // Stop the display area watcher FIRST, before closing any widgets,
        // so that no DisplaysChanged callback can fire during teardown
        // and access half-closed window objects.
        _displayAreaWatcher?.Dispose();
        _displayAreaWatcher = null;
        _displayTopologyTransitionCoordinator?.Dispose();
        _displayTopologyTransitionCoordinator = null;
        _lifecycleRecoveryWatcher?.Dispose();
        _lifecycleRecoveryWatcher = null;

        _diagnosticsService?.Dispose();
        _diagnosticsService = null;
        QuickCaptureClipboardService?.Dispose();
        QuickCaptureClipboardService = null;
        if (DesktopAutoOrganizationWatcher is not null)
        {
            DesktopAutoOrganizationWatcher.ItemOrganized -=
                ShowDesktopAutoOrganizationNotification;
            DesktopAutoOrganizationWatcher.Dispose();
        }
        DesktopAutoOrganizationWatcher = null;
        // Dispose live surfaces before the final flush. A surface may commit a
        // last drag/order snapshot while it is being torn down.
        DesktopDoubleClickActivationService?.Dispose();
        DesktopDoubleClickActivationService = null;
        WidgetManager?.CloseAll();
        await SettingsService.FlushPendingSaveAsync(notifySubscribers: false);
        SettingsService.PersistenceFailed -= OnSettingsPersistenceFailed;
        _nativeNotificationService?.Dispose();
        _nativeNotificationService = null;
        _todoReminderService?.Dispose();
        _todoReminderService = null;
        // Dispose hotkey services FIRST so their WH_KEYBOARD_LL hooks are
        // removed before the tray window is destroyed.  If the hooks remain
        // installed while the owning window is torn down, the OS may briefly
        // keep the gesture key in a "pressed" state, leaving keys like 'D'
        // appearing stuck even after the app exits.
        DisposeSearchServices();
        GlobalHotkeyService?.Dispose();
        GlobalHotkeyService = null;

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
        _desktopOrganizationWindow?.CloseForShutdown();
        _desktopOrganizationWindow = null;
        _settingsWindow?.CloseForShutdown();
        _settingsWindow = null;
        _onboardingWindow?.Close();
        _onboardingWindow = null;
        _trayWindow?.Close();
        _trayWindow = null;
        Exit();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"Unhandled exception: {e.Exception}");
        e.Handled = true;
    }

    // ─── Search Services ─────────────────────────────────────────────

    private void EnsureSearchFeatureShell()
    {
        if (!FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Search))
        {
            return;
        }

        try
        {
            if (_searchHistoryService is null)
            {
                _searchHistoryService = new SearchHistoryService();
                Log("[Search] Lightweight history service initialized");
            }

            if (_searchHotkeyService is null)
            {
                _searchHotkeyService = new SearchHotkeyService(
                    SettingsService,
                    ToggleSearchPopupAsync);
                if (_trayWindow is not null)
                {
                    var trayHwnd = WindowNative.GetWindowHandle(_trayWindow);
                    if (trayHwnd != IntPtr.Zero)
                    {
                        _searchHotkeyService.Attach(trayHwnd);
                        Log("[Search] Hotkey service attached");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Search] Lightweight service initialization failed: {ex}");
            _searchHotkeyService?.Dispose();
            _searchHotkeyService = null;
            _searchHistoryService = null;
        }
    }

    private void EnsureSearchServices()
    {
        EnsureSearchFeatureShell();
        if (_searchHistoryService is null)
        {
            return;
        }

        if (_searchEngineService is not null)
        {
            return;
        }

        try
        {
            _everythingSearchService = new EverythingSearchService(SettingsService);
            _searchEngineService = new SearchEngineService(
                SettingsService,
                LocalizationService,
                _everythingSearchService,
                QuickCaptureService);
            _searchActionService = new SearchResultActionService(SettingsService);

            Log("[Search] Everything IPC provider initialized without a DeskBox file index");
        }
        catch (Exception ex)
        {
            Log($"[Search] Initialization failed: {ex}");
            DisposeSearchServices();
            EnsureSearchFeatureShell();
        }
    }

    internal SearchEngineService? EnsureSearchServicesForUserAction()
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            Log("[Search] Explicit maintenance action ignored outside the UI thread");
            return null;
        }

        if (!FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Search))
        {
            return null;
        }

        EnsureSearchServices();
        return _searchEngineService;
    }

    internal void SetSearchFeatureEnabled(bool enabled)
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => SetSearchFeatureEnabled(enabled));
            return;
        }

        if (enabled)
        {
            EnsureSearchServices();
            PerformanceLogger.SampleMemory("search-enabled");
            return;
        }

        DisposeSearchServices();
        PerformanceLogger.SampleMemory("search-disabled");
    }

    private void DisposeSearchServices()
    {
        CancelTransientWindowRelease();
        var popup = _searchPopupWindow;
        _searchPopupWindow = null;
        if (popup is not null)
        {
            try
            {
                popup.Close();
            }
            catch (Exception ex)
            {
                Log($"[Search] Popup close failed during cleanup: {ex.Message}");
            }
        }

        _fileMetaService?.Dispose();
        _fileMetaService = null;
        _searchHotkeyService?.Dispose();
        _searchHotkeyService = null;
        _searchEngineService?.Dispose();
        _searchEngineService = null;
        _everythingSearchService = null;
        _searchHistoryService = null;
        _searchActionService = null;
        Log("[Search] Services disposed");
        ScheduleLightMemoryCleanup(completedHeavyOperation: true);
    }

    private Task ToggleSearchPopupAsync()
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => _ = ToggleSearchPopupAsync());
            return Task.CompletedTask;
        }

        // If the search feature widget has been disabled by the user,
        // the hotkey should not be able to invoke the popup either.
        if (!FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Search))
        {
            Log("[Search] Popup toggle blocked: search feature widget is disabled");
            return Task.CompletedTask;
        }

        if (_searchPopupWindow?.IsPopupVisible == true)
        {
            _searchPopupWindow.HidePopup();
            return Task.CompletedTask;
        }

        OpenSearchPopupCore(initialQuery: null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Public entry point used by the search widget (and other direct UI callers).
    /// Unlike the global hotkey, this is intentionally idempotent: queued repeat clicks
    /// can only focus the popup and can never hide the window that the first click opened.
    /// </summary>
    public void OpenSearchPopup()
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(OpenSearchPopup);
            return;
        }

        OpenSearchPopupCore(initialQuery: null);
    }

    /// <summary>
    /// Opens the search popup with a pre-filled query and immediately executes the search.
    /// Used by search history items in the widget.
    /// </summary>
    public void OpenSearchPopupWithQuery(string query)
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => OpenSearchPopupWithQuery(query));
            return;
        }

        OpenSearchPopupCore(query);
    }

    private void OpenSearchPopupCore(string? initialQuery)
    {
        if (!FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Search))
        {
            return;
        }

        EnsureSearchServices();
        if (_searchEngineService is null)
        {
            return;
        }

        CancelBackgroundMemoryCleanup();

        if (_searchPopupWindow is null)
        {
            CreateSearchPopupWindow();
        }

        if (_searchPopupWindow is not { } popup)
        {
            return;
        }

        PerformanceLogger.Mark(
            "SearchPopupOpenRequested",
            $"shellReady=true visible={popup.IsPopupVisible} " +
            $"everythingState={_everythingSearchService?.CurrentSnapshot.State}");

        if (string.IsNullOrWhiteSpace(initialQuery))
        {
            popup.ShowPopup();
        }
        else
        {
            popup.ShowPopupWithQuery(initialQuery);
        }

        // Everything is queried only after the user types and has explicitly enabled it.
    }

    private void CreateSearchPopupWindow()
    {
        if (_searchEngineService is null || _searchHistoryService is null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _fileMetaService?.Dispose();
        _fileMetaService = new FileMetaService();
        var viewModel = new ViewModels.SearchPopupViewModel(
            _searchEngineService,
            SettingsService,
            LocalizationService,
            _searchHistoryService,
            _fileMetaService);
        var popup = new SearchPopupWindow(viewModel, SettingsService, LocalizationService);
        _searchPopupWindow = popup;
        popup.ActionRequested += OnSearchActionRequested;
        popup.ContentRequested += OnSearchContentRequested;
        popup.PopupShown += (_, _) =>
        {
            CancelTransientWindowRelease();
            CancelBackgroundMemoryCleanup();
        };
        popup.PopupHidden += (_, _) =>
        {
            ScheduleTransientWindowRelease();
            ScheduleBackgroundMemoryCleanup();
        };
        popup.Closed += (_, _) =>
        {
            CancelTransientWindowRelease();
            if (ReferenceEquals(_searchPopupWindow, popup))
            {
                _searchPopupWindow = null;
            }

            _fileMetaService?.Dispose();
            _fileMetaService = null;
            PerformanceLogger.SampleMemory("search-popup-closed");
            // Closing a transient popup already detaches its visual tree. Do not
            // promote that normal release into a compacting GC while visible
            // widgets are still warm.
            ScheduleLightMemoryCleanup();
            ScheduleBackgroundMemoryCleanup();
        };
        viewModel.HidePopupCallback = () => popup.HidePopup();
        stopwatch.Stop();
        Log($"[Search] Popup shell created in {stopwatch.ElapsedMilliseconds} ms");
        PerformanceLogger.SampleMemory("search-popup-created");
    }

    private void OnSearchActionRequested(object? sender, string actionId)
    {
        _ = HandleSearchActionAsync(actionId);
    }

    private void OnSearchContentRequested(object? sender, Models.SearchResultItem item)
    {
        _ = HandleSearchContentAsync(item);
    }

    private async Task HandleSearchContentAsync(Models.SearchResultItem item)
    {
        if (WidgetManager is null)
        {
            return;
        }

        switch (item.Kind)
        {
            case Models.SearchResultKind.Todo:
                await WidgetManager.ShowTodoReminderTargetAsync(
                    item.TodoWidgetId,
                    item.TodoItemId,
                    preferTodayFilter: false);
                break;

            case Models.SearchResultKind.QuickCapture:
                var window = await WidgetManager.CreateOrShowQuickCaptureWidgetAsync();
                await window.RevealItemAsync(item.QuickCaptureItemId);
                break;
        }
    }

    private async Task HandleSearchActionAsync(string actionId)
    {
        switch (actionId)
        {
            case "new-todo":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateTodoWidgetAsync(focusNewInput: true);
                }
                break;

            case "new-note":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateOrShowQuickCaptureWidgetAsync(focusNewInput: true);
                }
                break;

            case "open-settings":
                ShowSettings("SearchSettings");
                break;

            case "toggle-widgets":
                await ToggleTrayWidgetsAsync("action-command");
                break;

            case "toggle-theme":
                ToggleTheme();
                break;

            case "open-todo":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateTodoWidgetAsync();
                }
                break;

            case "open-quickcapture":
                if (WidgetManager is not null)
                {
                    await WidgetManager.CreateOrShowQuickCaptureWidgetAsync();
                }
                break;
        }
    }

    private void ToggleTheme()
    {
        var settings = SettingsService.Settings;
        settings.Theme = settings.Theme switch
        {
            "Light" => "Dark",
            "Dark" => "Light",
            _ => "Light"
        };
        SettingsService.SaveDebounced();
        ThemeService.RefreshAppearance();
    }
}
