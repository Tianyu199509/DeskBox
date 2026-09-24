// Copyright (c) DeskBox. All rights reserved.

using CommunityToolkit.Mvvm.Input;
using DeskBox.Controls.WidgetContents;
using DeskBox.Features.Todo;
using DeskBox.Features.Search;
using DeskBox.Features.Backup;
using DeskBox.Features.QuickCapture;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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
    private const int BackgroundMemoryCleanupRetryDelaySeconds = 5;
    private const int BackgroundMemoryCleanupMaximumRetryDelaySeconds = 30;
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
    private NativeNotificationActivationBootstrap? _nativeNotificationBootstrap;
    private bool _initialNotificationHandled;
    private TodoReminderRuntime? _todoReminderRuntime;
    private TodoSettingsCoordinator? _todoSettings;
    private SearchSettingsCoordinator? _searchSettings;
    private BackupSettingsCoordinator? _backupSettings;
    private QuickCaptureSettingsCoordinator? _quickCaptureSettings;
    private QuickCaptureClipboardRuntime? _quickCaptureClipboardRuntime;
    private BackupRuntime? _backupRuntime;
    private readonly ShutdownSequence _shutdownSequence = new(Log);
    private static int s_shutdownRequested;
    private static bool IsShuttingDown => Volatile.Read(ref s_shutdownRequested) != 0;
    private TodoReminderService? _todoReminderService => _todoReminderRuntime?.Current as TodoReminderService;
    private DisplayAreaWatcherService? _displayAreaWatcher;
    private DisplayTopologyTransitionCoordinator? _displayTopologyTransitionCoordinator;
    private AppLifecycleRecoveryWatcher? _lifecycleRecoveryWatcher;
    private HookHealthWatchdog? _hookHealthWatchdog;
    private EverythingSearchService? _everythingSearchService;
    private SearchEngineService? _searchEngineService;
    private FileMetaService? _fileMetaService;
    private SearchHotkeyService? _searchHotkeyService;
    private SearchPopupWindow? _searchPopupWindow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visibleIdleMemoryMaintenanceTimer;
    private int _visibleIdleMemoryMaintenanceRunning;
    private readonly VisibleIdleMemoryTracker _visibleIdleMemoryTracker = new(
        TimeSpan.FromSeconds(
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds),
        TimeSpan.FromSeconds(
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds));
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
    private bool _cloudBackupUnverifiedToastShown;

    public static new App Current => (App)Application.Current;

    public static Microsoft.UI.Dispatching.DispatcherQueue UiDispatcherQueue { get; private set; } = null!;

    public bool IsStartupMode { get; set; }

    public AppDistributionService DistributionService { get; } = AppDistributionService.Current;
    public ServiceProvider Services { get; private set; } = null!;
    public SettingsService SettingsService { get; private set; } = null!;
    public DeskBoxDataBackupService DataBackupService { get; private set; } = null!;
    internal CloudBackupService CloudBackupService { get; private set; } = null!;
    public DeskBoxAttachmentHealthService AttachmentHealthService { get; private set; } = null!;
    public FileService FileService { get; private set; } = null!;
    public OrganizerService OrganizerService { get; private set; } = null!;
    public ManagedStorageDesktopShortcutService ManagedStorageDesktopShortcutService { get; private set; } = null!;
    public IAppUpdateService AppUpdateService { get; private set; } = null!;
    public QuickCaptureService QuickCaptureService { get; private set; } = null!;
    public QuickCaptureClipboardService? QuickCaptureClipboardService =>
        _quickCaptureClipboardRuntime?.Current as QuickCaptureClipboardService;
    internal QuickCaptureSettingsCoordinator QuickCaptureSettings =>
        _quickCaptureSettings ?? throw new InvalidOperationException("Quick Capture is not initialized.");
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
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            DeskBoxDataPathService.Current.ActivationEventName);
        _singleInstanceMutex = new Mutex(
            true,
            DeskBoxDataPathService.Current.SingleInstanceMutexName,
            out bool createdNew);
        bool notificationLaunch = NativeNotificationActivationBootstrap.IsNotificationLaunch(
            Environment.GetCommandLineArgs());
        if (createdNew || notificationLaunch)
        {
            s_startupLaunchQuietExit = _processStartupLaunchDetected || notificationLaunch;
            // Cover constructor/COM initialization as well as OnLaunched.
            StartStartupWatchdog();
            EnsureNativeNotificationService();
        }
        NativeAppNotificationActivation? nativeNotificationActivation =
            TryGetCurrentNativeNotificationActivation();
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
            else if (notificationLaunch)
            {
                Log("[Notification] Secondary notification activation unavailable; leaving the primary instance running");
                _nativeNotificationService?.Dispose();
                DrainLogQueue();
                Environment.Exit(1);
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

            _nativeNotificationService?.Dispose();
            DrainLogQueue();
            Environment.Exit(0);
        }

        InitializeComponent();

        // Build DI container and resolve core services
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDeskBoxServices();
        Services = serviceCollection.BuildServiceProvider();

        SettingsService = Services.GetRequiredService<SettingsService>();
        SettingsService.PersistenceFailed += OnSettingsPersistenceFailed;
        DataBackupService = Services.GetRequiredService<DeskBoxDataBackupService>();
        DataBackupService.AutomaticSnapshotFallbackDetected += OnAutomaticBackupFallbackDetected;
        CloudBackupService = Services.GetRequiredService<CloudBackupService>();
        CloudBackupService.BackupRunCompleted += OnCloudBackupRunCompleted;
        _ = LegacySearchIndexCleanupService.TryCleanup();
        AttachmentHealthService = Services.GetRequiredService<DeskBoxAttachmentHealthService>();
        DiagnosticsBundleService = Services.GetRequiredService<DeskBoxDiagnosticsBundleService>();
        FileService = Services.GetRequiredService<FileService>();
        OrganizerService = Services.GetRequiredService<OrganizerService>();
        ManagedStorageDesktopShortcutService =
            Services.GetRequiredService<ManagedStorageDesktopShortcutService>();
        AppUpdateService = Services.GetRequiredService<IAppUpdateService>();
        QuickCaptureService = Services.GetRequiredService<QuickCaptureService>();
        ResizeGuideOverlay = Services.GetRequiredService<ResizeGuideOverlayService>();

        StartupService.Configure(StartupServiceFactory.Create(DistributionService, SettingsService));
        AppUpdateService.CheckCompleted += OnUpdateCheckCompleted;
        UnhandledException += OnUnhandledException;
        Log($"Distribution channel={DistributionService.ChannelName} packaged={DistributionService.IsPackaged}");
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
        if (!AppDistributionService.Current.IsPackaged)
            return false;
        try
        {
            // The platform API does not enter Windows App SDK notification
            // deserialization. Startup detection must also work if notifications
            // cannot be registered on this machine.
            return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind ==
                   Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
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

    private void ScheduleShellContextMenuPrewarm()
    {
        if (!SettingsService.Settings.FileItemSystemContextMenuEnabled)
        {
            return;
        }

        // Warm the native context-menu server in the background once startup
        // work settles, so the first right-click in a widget does not pay the
        // cold Shell handler-loading cost.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                ShellContextMenuProxy.Prewarm();
            }
            catch (Exception ex)
            {
                Log($"[ShellContextMenu] Prewarm failed: {ex.Message}");
            }
        });
    }

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
        // Set before the watchdog arms: a task-triggered launch has no user
        // watching, so its fatal path must exit quietly and let the task's
        // restart policy retry instead of parking a modal dialog on an
        // unattended desktop while still holding the single-instance mutex.
        s_startupLaunchQuietExit = isStartupLaunch;
        using var perfScope = PerformanceLogger.Measure(
            "App.OnLaunched",
            $"startup={isStartupLaunch}");
        Log("OnLaunched start");
        // Armed before the try: a startup that stalls (rather than throwing)
        // would otherwise hold the single-instance mutex with no UI at all.
        StartStartupWatchdog();

        try
        {
            string? updateInstallOutcome = TryGetUpdateInstallOutcome(Environment.GetCommandLineArgs());
            IsStartupMode = _processStartupLaunchDetected || isStartupLaunch;
            RunCriticalStartupStep("dispatcher-init", () =>
            {
                UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                WidgetSegmentedLayoutHelper.Initialize(UiDispatcherQueue);
            });

            // The diagnostic report walks a snapshot of every process on the
            // machine plus several registry hives; none of it gates startup,
            // so keep it off the UI thread's first-render path. The queued
            // log writer is thread-safe.
            _ = Task.Run(() =>
            {
                try
                {
                    Log($"Process integrity {GetProcessIntegrityReport()} pid={Environment.ProcessId} processPath={Environment.ProcessPath ?? "unknown"} baseDir={AppContext.BaseDirectory}");
                    Log($"Process parent {GetParentProcessReport()} commandLine={Environment.CommandLine}");
                    Log($"UAC {GetUacPolicyReport()}");
                    Log($"AppCompat {GetAppCompatReport()}");
                }
                catch (Exception ex)
                {
                    Log($"Startup diagnostics failed: {ex.Message}");
                }
            });

            // A prepared restore is applied before any service reads or normalizes app data.
            // From here to the widget-restoration phase every step is a feature,
            // not a lifeline: a failure must leave a usable app instead of
            // blocking startup entirely.
            DeskBoxRestoreApplyResult restoreResult = DeskBoxRestoreApplyResult.NoPendingRestore;
            await RunOptionalStartupStepAsync(
                "apply-pending-restore",
                async () => restoreResult = await DataBackupService.ApplyPendingRestoreAsync());
            bool hadSettingsBeforeStartup = File.Exists(Path.Combine(
                DeskBoxDataPathService.Current.DataDirectory,
                "settings.json"));

            // Capture the previous session's data before any startup normalization
            // writes. Settings are not loaded yet, so the backup schedule and folder
            // are read from the raw settings file; unreadable values fall back to
            // the defaults, matching the pre-setting behavior.
            RunOptionalStartupStep("read-backup-options", () =>
                DataBackupService.UpdateAutomaticBackupOptions(
                    DataBackupSettingsPolicy.ReadStartupOptions(
                        Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "settings.json"))));
            _backupRuntime = new BackupRuntime(
                new BackupBackend(SettingsService, DataBackupService, CloudBackupService),
                new DispatcherBackupTimer(UiDispatcherQueue),
                completion => Log($"[BackupRuntime] kind={completion.Kind} scheduled={completion.Scheduled} " +
                    $"outcome={completion.Outcome}" + (completion.Error is null ? string.Empty : $" error={completion.Error}")));
            // The snapshot copy tolerates concurrent writers (per-file length
            // and write-time stability checks with retries), so it runs
            // alongside the early startup phases instead of gating the tray,
            // theme, and settings load behind a full copy+zip of the data
            // directory. It is awaited before the widget-restoration phase,
            // whose normalization writes are the first bulk mutation of the
            // session, so the captured snapshot still reflects the previous
            // session.
            Task startupAutomaticSnapshotTask = RunOptionalStartupStepAsync(
                "automatic-snapshot",
                async () => await _backupRuntime.RunScheduledLocalAsync());

            // Phase 1: Load settings (must complete first)
            MarkStartupProgress();
            await RunCriticalStartupStepAsync("settings-load", () => SettingsService.LoadAsync());
            RefreshAutomaticBackupOptionsFromSettings();
            SettingsService.SettingsChanged += OnBackupSettingsChanged;
            SettingsService.SettingsChanged += OnMaterialCapabilitySettingsChanged;
            RunOptionalStartupStep("automatic-backup-timer", StartAutomaticBackupTimer);
            string requestedCornerPreference = SettingsService.Settings.WidgetCornerPreference;
            string effectiveCornerPreference =
                WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                    requestedCornerPreference);
            Log(
                $"[Appearance] OS build={WindowsCompatibilityService.OsBuild}, " +
                $"requested corners={requestedCornerPreference}, " +
                $"effective corners={effectiveCornerPreference}");

            // Sync widget move/resize snap settings.
            RunOptionalStartupStep("resize-guide-settings", () =>
            {
                ResizeGuideOverlay.IsSnapEnabled = SettingsService.Settings.ResizeSnapEnabled;
                ResizeGuideOverlay.SnapSpacingDips = SettingsService.Settings.WidgetSnapSpacing;
            });

            // Phase 2: Initialize services that depend on settings (parallel)
            RunCriticalStartupStep("core-services", () =>
            {
                ThemeService = Services.GetRequiredService<ThemeService>();
                LocalizationService = Services.GetRequiredService<LocalizationService>();
                LocalizationService.LanguageChanged += OnLanguageChanged;
            });

            _todoReminderRuntime = new TodoReminderRuntime(CreateTodoReminderService);
            _todoSettings = new TodoSettingsCoordinator(
                SettingsService,
                enabled => WidgetManager!.SetTodoEnabledAsync(enabled, reveal: enabled),
                checkNow => RefreshTodoReminderService(checkNow));
            _searchSettings = new SearchSettingsCoordinator(
                SettingsService, LocalizationService,
                () => _everythingSearchService,
                () => EnsureSearchServicesForUserAction()?.EverythingProvider,
                () => _searchHotkeyService,
                enabled => _searchEngineService?.SetDeskBoxContentSearchEnabled(enabled),
                (enabled, reveal) => WidgetManager is { } manager
                    ? manager.ApplySearchWindowStateAsync(enabled, reveal)
                    : SettingsService.SaveAsync(),
                SetSearchFeatureEnabled,
                action => UiDispatcherQueue.TryEnqueue(() => action()),
                ex => Log($"[Search] Feature enablement failed: {ex}"));
            _backupSettings = new BackupSettingsCoordinator(
                SettingsService, DataBackupService, CloudBackupService,
                () => _backupRuntime?.RefreshOptions());
            _quickCaptureClipboardRuntime = new QuickCaptureClipboardRuntime(
                () => !IsShuttingDown &&
                    FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.QuickCapture) &&
                    SettingsService.Settings.QuickCapture.QuickCaptureClipboardEnabled,
                () => new QuickCaptureClipboardService(SettingsService, QuickCaptureService),
                ex => Log($"[QuickCaptureClipboard] Stop failed: {ex}"));
            _quickCaptureSettings = new QuickCaptureSettingsCoordinator(
                SettingsService, _quickCaptureClipboardRuntime,
                (enabled, reveal) => WidgetManager is { } manager
                    ? manager.ApplyQuickCaptureWindowStateAsync(enabled, reveal)
                    : SettingsService.SaveAsync(),
                action => UiDispatcherQueue.TryEnqueue(() => action()),
                ex => Log($"[QuickCapture] Settings operation failed: {ex}"),
                limit => QuickCaptureService.TrimRecentItemsAsync(limit));

            var quickCaptureService = QuickCaptureService;
            var themeService = ThemeService;
            var localizationService = LocalizationService;

            // Parallel: theme refresh only. Clipboard event subscription must stay on the UI thread.
            Task themeTask = RunOptionalStartupStepAsync(
                "theme-refresh",
                () => Task.Run(() => themeService.RefreshAppearance()));
            RunOptionalStartupStep("quick-capture-clipboard", () => RefreshQuickCaptureClipboardService());

            // Parallel: independent UI setup. The tray is the lifeline: once its
            // icon is up the user can act on the process again.
            MarkStartupProgress();
            RunCriticalStartupStep("tray-icon", CreateTrayIcon);
            RunOptionalStartupStep("lifecycle-recovery-watcher", InitializeLifecycleRecoveryWatcher);

            await themeTask;

            if (FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Search))
            {
                // The search shell is lightweight. Everything owns the filename index;
                // DeskBox does not scan, preload, or watch the filesystem at startup.
                RunOptionalStartupStep("search-shell", EnsureSearchServices);
            }
            else
            {
                Log("[Search] Feature disabled; search services were not initialized");
            }

            RunCriticalStartupStep("widget-manager", () =>
            {
                WidgetManager = new WidgetManager(SettingsService, FileService, OrganizerService,
                    themeService, quickCaptureService, localizationService, _todoSettings,
                    _quickCaptureSettings, _searchSettings);
                WidgetManager.TrayLayerStateChanged += UpdateTrayLayerStateText;
                // Lets a quick-reveal raise promote already-open DeskBox surfaces
                // (search popup, settings, desktop organization) above the raised
                // widget group; the reverse order is handled per-window at show.
                WidgetManager.AuxiliaryWindowProvider = GetRaisedBandAuxiliaryWindowHandles;
            });
            MarkStartupProgress();
            // Non-null past this point: the critical step above rethrows on
            // failure, so the launch never reaches here without a manager.
            WidgetManager widgetManager = WidgetManager!;
            RunOptionalStartupStep("desktop-double-click-activation", () =>
            {
                DesktopDoubleClickActivationService = new DesktopDoubleClickActivationService(
                    SettingsService,
                    ToggleWidgetsFromDesktopDoubleClickAsync);
                DesktopDoubleClickActivationService.RefreshRegistration();
            });
            RunOptionalStartupStep("display-topology-coordinator", () =>
                _displayTopologyTransitionCoordinator = new DisplayTopologyTransitionCoordinator(
                    UiDispatcherQueue,
                    DisplayAreaWatcherService.CaptureCurrentSignature,
                    async (generation, reasons) =>
                        WidgetManager is null ||
                        await WidgetManager.RestoreWidgetPositionsAsync(generation, reasons)));

            // Phase 3: Restore widgets (the startup snapshot must finish
            // before the restoration phase starts writing normalized state).
            await startupAutomaticSnapshotTask;
            if (IsShuttingDown) return;
            int recoveredDesktopItems = 0;
            await RunOptionalStartupStepAsync(
                "desktop-organization-recovery",
                async () => recoveredDesktopItems = await new DesktopOrganizationTransaction(
                    SettingsService,
                    FileService).RecoverPendingAsync());
            if (recoveredDesktopItems > 0)
            {
                Log($"[DesktopOrganization] Recovered {recoveredDesktopItems} items from an interrupted transaction.");
            }
            // A detached storage drive must not abort widget restoration.
            bool managedStorageRootUnavailable = false;
            RunOptionalStartupStep("storage-folder-entries", () =>
                managedStorageRootUnavailable = !widgetManager.SyncStorageFolderEntries());
            Task<bool>? startupDesktopLayerReadinessTask = null;
            if (IsStartupMode)
            {
                RunOptionalStartupStep("desktop-layer-deferral", () =>
                {
                    WidgetLayerService.BeginStartupDesktopLayerAttachmentDeferral();
                    startupDesktopLayerReadinessTask =
                        WidgetLayerService.WaitForDesktopIconViewReadyAsync();
                });
            }

            try
            {
                await widgetManager.RestoreWidgetsAsync();
            }
            catch (Exception ex)
            {
                // Restoration is not the lifeline: the tray icon still gives the
                // user a way back in, so a failure here degrades instead of
                // blocking startup.
                Log($"[Startup] Optional step 'restore-widgets' failed: {ex}");
                RecordStartupDegradation("restore-widgets", ex.ToString());
                if (startupDesktopLayerReadinessTask is not null)
                {
                    startupDesktopLayerReadinessTask = null;
                    WidgetLayerService.EndStartupDesktopLayerAttachmentDeferral();
                }
            }

            if (startupDesktopLayerReadinessTask is not null)
            {
                SafeFireAndForget(
                    () => CompleteStartupDesktopLayerInitializationAsync(
                        startupDesktopLayerReadinessTask,
                        widgetManager),
                    "startup-desktop-layer");
            }

            RunOptionalStartupStep("global-hotkey-service", () => InitializeGlobalHotkeyService(localizationService));
            RunOptionalStartupStep("shell-context-menu-prewarm", ScheduleShellContextMenuPrewarm);

            RunOptionalStartupStep("todo-reminders", () => RefreshTodoReminderService());
            RunOptionalStartupStep("native-notifications", StartNativeNotificationService);
            await RunOptionalStartupStepAsync(
                "external-activation-initialization",
                CompleteExternalActivationInitializationAsync);
            RunOptionalStartupStep("restore-result-notification", () => ShowDataRestoreResultNotification(restoreResult));
            RunOptionalStartupStep("settings-recovery-notification", ShowSettingsLoadRecoveryNotification);
            if (managedStorageRootUnavailable)
            {
                RunOptionalStartupStep("storage-unavailable-notification", ShowManagedStorageUnavailableNotification);
            }
            if (!hadSettingsBeforeStartup ||
                SettingsService.LastLoadRecoveryState == SettingsLoadRecoveryState.DefaultsAfterFailure)
            {
                await RunOptionalStartupStepAsync("recovery-snapshot-notification", async () =>
                    ShowRecoverySnapshotAvailableNotification(
                        await DataBackupService.GetLatestRecoverySnapshotAsync()));
            }

            await RunOptionalStartupStepAsync(
                "initial-file-widget-setup",
                () => EnsureInitialFileWidgetSetupAsync(isInteractiveLaunch: !IsStartupMode));
            await RunOptionalStartupStepAsync(
                "managed-storage-desktop-shortcut",
                () => ManagedStorageDesktopShortcutService.SyncAsync());

            RunOptionalStartupStep("desktop-auto-organization-watcher", () =>
            {
                DesktopAutoOrganizationWatcher = new DesktopAutoOrganizationWatcher(
                    SettingsService,
                    OrganizerService,
                    widgetManager);
                DesktopAutoOrganizationWatcher.ItemOrganized += ShowDesktopAutoOrganizationNotification;
                DesktopAutoOrganizationWatcher.Start();
            });

            await RunOptionalStartupStepAsync(
                "onboarding",
                () => EnsureOnboardingAsync(isInteractiveLaunch: !IsStartupMode));

            RunOptionalStartupStep("background-update-check", ScheduleBackgroundUpdateCheck);
            RunOptionalStartupStep("diagnostics-service", () =>
            {
                _diagnosticsService = new AppDiagnosticsService(UiDispatcherQueue);
                _diagnosticsService.StartAll();
            });

            // Start display area watcher for hot-plug detection
            RunOptionalStartupStep("display-area-watcher", () =>
            {
                _displayAreaWatcher = new DisplayAreaWatcherService(UiDispatcherQueue);
                _displayAreaWatcher.DisplaysChanged += OnDisplaysChanged;
                _displayAreaWatcher.Start();
                if (_trayWindow is not null)
                {
                    // The tray window outlives every widget window, so subscribing
                    // there is what retires the fallback poll. This runs after the
                    // recovery watcher's own attach, which is earlier in startup.
                    _displayAreaWatcher.AttachToMessageWindow(
                        WindowNative.GetWindowHandle(_trayWindow));
                }
            });
            RunOptionalStartupStep("virtual-display-advisor", () =>
                VirtualDisplayAdvisor.WarnIfPrimaryDisplayIsVirtual(
                    (titleKey, bodyKey) => ShowSettingsNotification(
                        titleKey,
                        bodyKey,
                        NotificationIcon.Warning)));
            MaterialCapabilityAdvisor.Initialize(OnMaterialCapabilitySettingsChanged);
            RunOptionalStartupStep("material-capability-advisor", () =>
                MaterialCapabilityAdvisor.WarnIfMaterialDegraded(
                    SettingsService.Settings.WidgetShell.WidgetMaterialType,
                    (titleKey, bodyKey) => ShowSettingsNotification(
                        titleKey,
                        bodyKey,
                        NotificationIcon.Warning)));

            // Configure taskbar Jump List with quick actions
            SafeFireAndForget(
                () => JumpListService.ConfigureAsync(LocalizationService),
                "jump-list-configure");

            // Handle Jump List activation on first launch (not second instance)
            string? firstLaunchJumpArg =
                JumpListService.TryGetJumpListArgument(args.Arguments) ??
                JumpListService.TryGetJumpListArgument(
                    string.Join(' ', Environment.GetCommandLineArgs()));
            if (firstLaunchJumpArg is not null)
            {
                SafeFireAndForget(
                    () => JumpListService.HandleActivationAsync(firstLaunchJumpArg),
                    "jump-list-activation");
            }

            RunOptionalStartupStep("idle-memory-maintenance", StartVisibleIdleMemoryMaintenance);
            RunOptionalStartupStep("quiescence-working-set-trim", StartQuiescenceWorkingSetTrim);
            if (!string.IsNullOrWhiteSpace(updateInstallOutcome))
            {
                RunOptionalStartupStep("update-install-result", () =>
                {
                    ShowSettings("About");
                    _settingsWindow?.QueueUpdateInstallResultDialog(updateInstallOutcome!);
                });
            }

            // The process must not keep running startup with nothing the user
            // can act on: it would own the single-instance mutex and swallow
            // every later launch.
            await EnsureStartupProducedUsableSurfaceAsync();

            EnsureStartupPipeline().WriteSummary();
            Log("OnLaunched completed successfully");
            // Startup registration does not gate the first usable widgets.
            // DirectStartupService serializes migration with user toggle changes.
            _ = Task.Run(() =>
            {
                if (StartupService.Current is DirectStartupService directStartupService)
                {
                    directStartupService.TryMigrateLegacyRegistration();
                }

                ApplyDefaultAutoStartOnce();
            });
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
            // Anything reaching this point failed before the tray lifeline was
            // established, so the process has nothing to offer and must not
            // keep holding the single-instance mutex.
            FailStartup("exception during startup", ex);
        }
    }

    /// <summary>
    /// Applies the one-time autostart default. Runs off the UI thread because
    /// registering the logon task shells out to schtasks; only the settings
    /// write returns to the dispatcher. Development data roots (portable runs,
    /// smoke harnesses) never touch the machine's startup registration.
    /// </summary>
    private void ApplyDefaultAutoStartOnce()
    {
        try
        {
            if (DeskBoxDataPathService.Current.IsDevelopmentRoot ||
                !AutoStartDefaultPolicy.ShouldApply(SettingsService.Settings))
            {
                return;
            }

            StartupRegistrationState initialState = StartupService.Current.GetState();
            StartupRegistrationState effective =
                AutoStartDefaultPolicy.Resolve(StartupService.Current);
            bool enabled = AutoStartDefaultPolicy.IsEnabledState(effective);

            void Commit()
            {
                SettingsService.Settings.AutoStart = enabled;
                if (AutoStartDefaultPolicy.ShouldMarkApplied(effective))
                {
                    SettingsService.Settings.AutoStartDefaultApplied = true;
                }

                SettingsService.SaveDebounced();
            }

            if (UiDispatcherQueue is { } dispatcher && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(Commit);
            }
            else
            {
                Commit();
            }

            Log(
                $"[AutoStart] One-time default applied: initial={initialState} effective={effective} mirror={enabled}");
        }
        catch (Exception ex)
        {
            Log($"[AutoStart] One-time default failed: {ex.Message}");
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
            VirtualDisplayAdvisor.WarnIfPrimaryDisplayIsVirtual(
                (titleKey, bodyKey) => ShowSettingsNotification(
                    titleKey,
                    bodyKey,
                    NotificationIcon.Warning));
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

        // Silent hook removal produces no message at all, so liveness needs an
        // active probe rather than another window message.
        if (Environment.GetEnvironmentVariable("DESKBOX_DISABLE_HOOK_WATCHDOG") is null)
        {
            try
            {
                _hookHealthWatchdog = new HookHealthWatchdog(
                    UiDispatcherQueue,
                    static message => Log(message));
                _hookHealthWatchdog.Watch(() => GlobalHotkeyService);
                _hookHealthWatchdog.Watch(() => _searchHotkeyService);
                _hookHealthWatchdog.Watch(() => DesktopDoubleClickActivationService);
            }
            catch (Exception ex)
            {
                Log($"[Lifecycle] Hook watchdog initialization failed: {ex.Message}");
                _hookHealthWatchdog?.Dispose();
                _hookHealthWatchdog = null;
            }
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
            reason.Contains("explorer-restart", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("display-power-on", StringComparison.OrdinalIgnoreCase);
        if (requiresExternalRecovery)
        {
            try
            {
                GlobalHotkeyService?.RefreshRegistration();
                DesktopDoubleClickActivationService?.RefreshRegistration();
                _searchHotkeyService?.RefreshRegistration();
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
        if (IsShuttingDown) return;
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() =>
                RefreshQuickCaptureClipboardService(captureCurrent));
            return;
        }

        _quickCaptureSettings?.RefreshFromSettings(captureCurrent);
    }

    internal TodoReminderService? RefreshTodoReminderService(bool checkNow = false)
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            if (!UiDispatcherQueue.TryEnqueue(() =>
            {
                try { RefreshTodoReminderService(checkNow); }
                catch (Exception ex) { Log($"[TodoReminder] Reconciliation failed: {ex}"); }
            }))
            {
                Log("[TodoReminder] Reconciliation skipped: UI dispatcher is shutting down");
            }
            return _todoReminderService;
        }

        if (_todoSettings is null || _todoSettings.IsStopped || _todoReminderRuntime is null)
        {
            return null;
        }
        bool forceActive = false;
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        // The audit harness intentionally starts several notification fixtures
        // with reminder polling disabled, then drives the real product service
        // directly. Keep that explicit test-only reachability out of retail.
        forceActive =
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
        bool hadSession = _todoReminderRuntime.Current is not null;
        _todoReminderRuntime.Reconcile(_todoSettings.Read(), forceActive);
        if (!hadSession && _todoReminderRuntime.Current is not null)
            Log("[TodoReminder] Service initialized on demand");
        else if (hadSession && _todoReminderRuntime.Current is null)
            Log("[TodoReminder] Inactive service released");

        if (checkNow && _todoReminderService is { } reminderService)
        {
            _ = reminderService.CheckNowAsync(DateTimeOffset.Now);
        }

        return _todoReminderService;
    }

    private TodoReminderService CreateTodoReminderService()
    {
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        if (TryGetAotTodoNotificationForwardingClock() is not null)
        {
            return new TodoReminderService(
                SettingsService,
                LocalizationService,
                UiDispatcherQueue,
                ShowTodoReminderNotification,
                widgetId => new TodoWidgetStore(widgetId),
                GetTodoNotificationActivationNow);
        }
#endif
        return new TodoReminderService(
            SettingsService,
            LocalizationService,
            UiDispatcherQueue,
            ShowTodoReminderNotification);
    }

    private void StartNativeNotificationService()
    {
        EnsureNativeNotificationService();
        HandleCurrentNativeNotificationActivation();
    }

    private void EnsureNativeNotificationService()
    {
        // Keep the constructor's registration and wait handle alive. Replacing
        // it here could lose a notification received before settings/widgets load.
        NativeAppNotificationService service = _nativeNotificationService ??=
            new NativeAppNotificationService(QueueNativeNotificationActivation);
        _nativeNotificationBootstrap ??= new NativeNotificationActivationBootstrap(
            () => service.Register(),
            ReadCurrentNativeNotificationActivation,
            ex => Log($"[Notification] Activation bootstrap unavailable: {ex}"));
    }

    private static void QueueNativeNotificationActivation(NativeAppNotificationActivation activation)
    {
        try
        {
            NativeNotificationActivationEnvelopeWriteResult result =
                PendingNativeNotificationActivationStore.Store(activation);
            Log($"[Notification] Queued activation disposition={result.Disposition} error={result.Error ?? "none"}");
            if (result.Disposition == NativeNotificationActivationEnvelopeWriteDisposition.Stored)
                _activationEvent?.Set();
        }
        catch (Exception ex)
        {
            Log($"[Notification] Activation queue failed: {ex}");
        }
    }

    private void HandleCurrentNativeNotificationActivation()
    {
        if (_initialNotificationHandled)
            return;
        NativeAppNotificationActivation? activation = TryGetCurrentNativeNotificationActivation();
        if (activation is not null)
        {
            _initialNotificationHandled = true;
            QueueNativeNotificationActivation(activation);
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
            OrganizationHistoryEntry? history = SettingsService.OrganizationHistory.Entries
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

    private NativeAppNotificationActivation? TryGetCurrentNativeNotificationActivation()
    {
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        NativeAppNotificationActivation? controlledActivation =
            TryGetAotTodoNotificationForwardingActivation();
        if (controlledActivation is not null)
        {
            return controlledActivation;
        }
#endif

        return _nativeNotificationBootstrap?.Capture();
    }

    private NativeAppNotificationActivation? ReadCurrentNativeNotificationActivation() =>
        _nativeNotificationService?.ReadCurrentActivation();

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
                    SafeFireAndForget(
                        () => Current.HandleExternalActivationAsync(),
                        "external-activation");
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
                bool settingsWindowOpen = _settingsWindow is { IsVisibleToUser: true };
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

    private void RefreshAutomaticBackupOptionsFromSettings()
    {
        _backupRuntime?.RefreshOptions();
    }

    private void OnMaterialCapabilitySettingsChanged()
    {
        // SettingsChanged can arrive off the UI thread and the notification
        // callback owns UI objects, so marshal through the app dispatcher.
        UiDispatcherQueue?.TryEnqueue(() =>
            MaterialCapabilityAdvisor.WarnIfMaterialDegraded(
                SettingsService.Settings.WidgetShell.WidgetMaterialType,
                (titleKey, bodyKey) => ShowSettingsNotification(
                    titleKey,
                    bodyKey,
                    NotificationIcon.Warning)));
    }

    private void OnBackupSettingsChanged()
    {
        if (IsShuttingDown) return;
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(OnBackupSettingsChanged);
            return;
        }
        _backupRuntime?.RefreshOptions(checkSchedule: true);
    }

    private void StartAutomaticBackupTimer()
    {
        _backupRuntime?.Start();
    }

    private void OnCloudBackupRunCompleted(CloudBackupRunCompletedInfo info)
    {
        if (IsShuttingDown) return;
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(() => OnCloudBackupRunCompleted(info));
            return;
        }

        // An accepted-but-unverified upload is a degraded success, not a
        // failure: toast at most once per session so a laggy DAV listing
        // can't spam every interval, while a silently-dropping server still
        // surfaces instead of hiding behind the "last success" stamp.
        if (info is { Uploaded: true, UploadUnverified: true })
        {
            if (_cloudBackupUnverifiedToastShown)
            {
                return;
            }

            _cloudBackupUnverifiedToastShown = true;
            ShowSettingsNotification(
                "Settings.CloudBackup.UploadUnverified.Title",
                "Settings.CloudBackup.UploadUnverified.Body",
                NotificationIcon.Warning);
            return;
        }

        // Only the FIRST failure of a scheduled streak toasts — the
        // settings status row carries the rest, so a dead endpoint can't
        // spam a notification on every interval tick. Manual failures are
        // already shown by the settings page that triggered them.
        if (info.Uploaded || !info.WasScheduled || !info.IsFirstFailureSinceSuccess)
        {
            return;
        }

        ShowSettingsNotification(
            "Settings.CloudBackup.ScheduledFailure.Title",
            "Settings.CloudBackup.ScheduledFailure.Body",
            NotificationIcon.Warning);
    }

    private void OnAutomaticBackupFallbackDetected()
    {
        if (IsShuttingDown) return;
        if (UiDispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(OnAutomaticBackupFallbackDetected);
            return;
        }

        ShowSettingsNotification(
            "Settings.DataBackup.AutomaticBackupDirectory.FallbackTitle",
            "Settings.DataBackup.AutomaticBackupDirectory.FallbackBody",
            NotificationIcon.Warning);
    }

    private void ShowSettingsNotification(
        string titleKey,
        string bodyKey,
        NotificationIcon icon)
    {
        if (LocalizationService is null || IsShuttingDown)
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
        if (IsShuttingDown) return;
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
        _settingsWindow = new SettingsWindow(
            SettingsService, ThemeService, LocalizationService,
            new TodoSettingsViewModel(
                _todoSettings ?? throw new InvalidOperationException("Todo settings are not initialized."),
                ex => Log($"[TodoSettings] Update failed: {ex}")),
            new SearchSettingsViewModel(
                _searchSettings ?? throw new InvalidOperationException("Search settings are not initialized."),
                action => UiDispatcherQueue.TryEnqueue(() => action()),
                ex => Log($"[SearchSettings] Operation failed: {ex}")),
            new BackupSettingsViewModel(
                _backupSettings ?? throw new InvalidOperationException("Backup settings are not initialized."),
                action => UiDispatcherQueue.TryEnqueue(() => action()),
                ex => Log($"[BackupSettings] Operation failed: {ex}")),
            new BackupRestoreActions(SettingsService, CloudBackupService, DataBackupService,
                ShutdownForRestartAsync),
            _quickCaptureSettings ?? throw new InvalidOperationException("Quick Capture is not initialized."),
            _searchSettings ?? throw new InvalidOperationException("Search settings are not initialized."),
            _backupRuntime ?? throw new InvalidOperationException("Backup runtime is not initialized."));
        _settingsWindow.Closed += SettingsWindow_ClosedForApp;
        return _settingsWindow;
    }

    private void SettingsWindow_ClosedForApp(object sender, WindowEventArgs args)
    {
        // Hide-and-reuse cancels at AppWindow.Closing and never reaches this
        // event; Closed only ever runs for real destruction (shutdown).
        if (sender is SettingsWindow settingsWindow)
        {
            settingsWindow.Closed -= SettingsWindow_ClosedForApp;
        }

        if (ReferenceEquals(_settingsWindow, sender))
        {
            _settingsWindow = null;
        }

        ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        ScheduleBackgroundMemoryCleanup("settings-closed");
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
        if (IsShuttingDown) return;
        OpenSettings();
    }

    public void ShowSettings(string sectionTag)
    {
        if (IsShuttingDown) return;
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        settingsWindow.ShowSection(sectionTag);
    }

    public void ShowGlanceSettings(string widgetId)
    {
        if (IsShuttingDown) return;
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
                ScheduleBackgroundMemoryCleanup("onboarding-closed");
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
    private static int s_backgroundMemoryCleanupGeneration;
    private static CancellationTokenSource?
        s_backgroundMemoryCleanupCancellationSource;
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

    private void VisibleIdleMemoryMaintenanceTimer_Tick(
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

            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long workingSetBefore = process.WorkingSet64;
            long privateBytesBefore = process.PrivateMemorySize64;

            Localized.PruneDeadTargets();
            IconHelper.IdleIconCacheReleaseResult cacheRelease =
                IconHelper.ReleaseIdleCaches(
                    TimeSpan.FromSeconds(Math.Max(1, visibleIdleDelaySeconds)));
            process.Refresh();
            _visibleIdleMemoryTracker.CommitMaintenance(
                DateTimeOffset.UtcNow);

            // Pre-1.4.5 visible-idle trim, restored with its original bloat
            // thresholds: only while the user is fully away (the idle contract
            // above) and only when both footprints are genuinely large, so an
            // idle desktop neither pays constant trim/fault churn nor jitters
            // an ambient animation from back-faults.
            bool visibleIdleTrimmed = false;
            if (SettingsService.Settings.IdleWorkingSetTrimEnabled)
            {
                MemoryCleanupActivitySnapshot trimActivity =
                    CaptureMemoryCleanupActivity();
                process.Refresh();
                if (MemoryCleanupPolicy.ShouldTrimVisibleIdleWorkingSet(
                        trimActivity,
                        process.WorkingSet64,
                        process.PrivateMemorySize64))
                {
                    visibleIdleTrimmed = Win32Helper.TrimWorkingSet();
                    CompleteWorkingSetTrim(visibleIdleTrimmed, "visible-idle");
                    if (visibleIdleTrimmed)
                    {
                        process.Refresh();
                    }
                }
            }

            Log(
                $"[Memory] Visible idle maintenance completed idleSeconds={visibleIdleDelaySeconds} " +
                $"workingSetTrimmed={visibleIdleTrimmed} " +
                $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
                $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
                $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
                $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
                $"forcedCollection=false warmCachePreserved=true " +
                $"releasedThumbs={cacheRelease.ReleasedThumbnails} " +
                $"releasedBitmaps={cacheRelease.ReleasedDecodedBitmaps} " +
                $"releasedIconBytes={cacheRelease.ReleasedIconByteEntries} " +
                $"releasedEstimatedMB={cacheRelease.ReleasedEstimatedBytes / (1024.0 * 1024):F1} " +
                $"interactiveCacheTrim={cacheRelease.ReleasedAnything} " +
                $"reason=visible-ux-preservation");
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
        WidgetMemoryVisibilitySnapshot visibility =
            widgetManager?.CaptureMemoryCleanupVisibilitySnapshot() ?? default;
        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        bool isDeskBoxForeground =
            foregroundWindow != IntPtr.Zero &&
            Win32Helper.IsWindowVisible(foregroundWindow) &&
            IsDeskBoxWindow(foregroundWindow);
        bool isPointerOverDeskBox =
            Win32Helper.GetCursorPos(out var cursor) &&
            IsDeskBoxWindow(Win32Helper.WindowFromPoint(cursor));
        return new MemoryCleanupActivitySnapshot(
            HasVisibleWidgets: visibility.HasNativeVisibleWidgets,
            IsWidgetInteractionActive: widgetManager?.IsWidgetInteractionActive == true,
            IsSettingsOpen: _settingsWindow is { IsVisibleToUser: true },
            IsOnboardingOpen: _onboardingWindow is not null,
            IsSearchPopupVisible: _searchPopupWindow?.IsPopupVisible == true,
            IsDeskBoxForeground: isDeskBoxForeground,
            IsPointerOverDeskBox: isPointerOverDeskBox,
            IsDesktopOrganizationOpen: _desktopOrganizationWindow is not null);
    }

    private static void CancelBackgroundMemoryCleanupDelay()
    {
        CancellationTokenSource? cancellationSource = Interlocked.Exchange(
            ref s_backgroundMemoryCleanupCancellationSource,
            null);
        if (cancellationSource is not null)
        {
            try
            {
                cancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal static void CancelBackgroundMemoryCleanup(string reason = "activity")
    {
        CancelBackgroundMemoryCleanupDelay();
        Current._immediateHiddenWorkingSetTrimTracker.CancelPending();

        int generation = Interlocked.Increment(
            ref s_backgroundMemoryCleanupGeneration);
        PerformanceLogger.Mark(
            "BackgroundMemoryCleanupCancelled",
            $"generation={generation} reason={reason}");
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
        if (app.WidgetManager is null ||
            app.WidgetManager
                .CaptureMemoryCleanupVisibilitySnapshot()
                .HasNativeVisibleWidgets ||
            app._settingsWindow is { IsVisibleToUser: true } ||
            app._onboardingWindow is not null ||
            app._searchPopupWindow?.IsPopupVisible == true)
        {
            return;
        }

        ScheduleBackgroundMemoryCleanup("performance-settings-changed");
    }

    internal static void NotifyMemoryCleanupActivity()
    {
        if (Application.Current is App app)
        {
            app._visibleIdleMemoryTracker.Reset();
            app.NoteQuiescenceWorkingSetTrimActivity();
        }
    }

    internal bool CanRunCompactExpansionWarmup =>
        _settingsWindow is not { IsVisibleToUser: true } &&
        _onboardingWindow is null &&
        _searchPopupWindow?.IsPopupVisible != true &&
        WidgetManager?.IsWidgetInteractionActive != true;

    internal bool CanRunCriticalCompactExpansionWarmup => true;

    private readonly record struct MemoryCleanupDiagnosticSnapshot(
        long WorkingSetBytes,
        long PrivateBytes,
        long ManagedHeapBytes,
        int ThumbnailCacheCount,
        long ThumbnailEstimatedBytes,
        int IconCacheCount,
        int SearchMetaCacheCount,
        int ShellKindCacheCount,
        int ShortcutMetadataCacheCount,
        int DecodedBitmapCacheCount,
        long DecodedBitmapEstimatedBytes,
        int QuickCaptureDetailImageDecodeCount,
        long QuickCaptureDetailImageEstimatedBytes,
        int GlanceCompactBackgroundDecodeCount,
        long GlanceCompactBackgroundEstimatedBytes,
        int GlanceStoreCount,
        int WeatherPathGateCount,
        WidgetMemoryVisibilitySnapshot Visibility);

    private sealed class BackgroundMemoryCleanupOutcomeState
    {
        internal string Status = "scheduled";
        internal string Detail = "pending";
        internal string CleanupScope =
            PerformanceSettingsPolicy.DefaultHiddenCacheCleanupScope;
        internal string SoftCleanupResult = "not-run";
        internal string DeepReclaimResult = "not-run";
        internal bool DeepReclaimExecuted;
        internal long DeepReclaimDurationMilliseconds;
        internal long DeepReclaimReleasedHeapBytes;
        internal int ReleasedSearchIconEntries;
        internal int ReleasedShellKindEntries;
        internal int ReleasedShortcutMetadataEntries;
        internal int ReleasedThumbnailEntries;
        internal int ReleasedDecodedBitmapEntries;
        internal int ReleasedIconByteEntries;
        internal long ReleasedEstimatedBytes;
        internal int ReleasedCachedContentCount;
        internal BackgroundMemoryCleanupStage CompletedStages =
            BackgroundMemoryCleanupStage.None;
        internal int ActivityRetryCount;
        internal int MaintainedContentHostCount;
    }

    private static MemoryCleanupDiagnosticSnapshot
        CaptureMemoryCleanupDiagnosticSnapshot()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            WidgetMemoryVisibilitySnapshot visibility =
                Current.WidgetManager?
                    .CaptureMemoryCleanupVisibilitySnapshot() ?? default;
            return new MemoryCleanupDiagnosticSnapshot(
                process.WorkingSet64,
                process.PrivateMemorySize64,
                GC.GetGCMemoryInfo().HeapSizeBytes,
                PerformanceLogger.ThumbnailCacheCount,
                PerformanceLogger.ThumbnailEstimatedBytes,
                PerformanceLogger.IconCacheCount,
                Current.SearchMetaCacheCount,
                FileService.ShellKindCacheEntryCount,
                ShortcutHelper.StoredMetadataCacheEntryCount,
                PerformanceLogger.DecodedBitmapCacheCount,
                PerformanceLogger.DecodedBitmapEstimatedBytes,
                PerformanceLogger.QuickCaptureDetailImageDecodeCount,
                PerformanceLogger.QuickCaptureDetailImageEstimatedBytes,
                PerformanceLogger.GlanceCompactBackgroundDecodeCount,
                PerformanceLogger.GlanceCompactBackgroundEstimatedBytes,
                GlanceWidgetStore.CachedWidgetStoreCount,
                WeatherCacheStore.PathGateCount,
                visibility);
        }
        catch
        {
            return default;
        }
    }

    private static void LogBackgroundMemoryCleanupOutcome(
        int generation,
        string triggerReason,
        string mode,
        int softDelaySeconds,
        int deepDelaySeconds,
        long scheduledTimestamp,
        MemoryCleanupDiagnosticSnapshot before,
        BackgroundMemoryCleanupOutcomeState state)
    {
        MemoryCleanupDiagnosticSnapshot after =
            CaptureMemoryCleanupDiagnosticSnapshot();
        MemoryCleanupActivitySnapshot activity =
            Current.CaptureMemoryCleanupActivity();
        string details =
            $"generation={generation} status={state.Status} " +
            $"detail={state.Detail} trigger={triggerReason} mode={mode} " +
            $"cleanupScope={state.CleanupScope} " +
            $"softCleanupResult={state.SoftCleanupResult} " +
            $"deepReclaimResult={state.DeepReclaimResult} " +
            $"deepReclaimExecuted={state.DeepReclaimExecuted} " +
            $"deepReclaimDurationMs={state.DeepReclaimDurationMilliseconds} " +
            $"deepReclaimReleasedHeapMB={state.DeepReclaimReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"hiddenSeconds={Stopwatch.GetElapsedTime(scheduledTimestamp).TotalSeconds:F1} " +
            $"softDelaySeconds={softDelaySeconds} " +
            $"deepDelaySeconds={deepDelaySeconds} " +
            $"completedStages={state.CompletedStages} " +
            $"activityRetries={state.ActivityRetryCount} " +
            $"forcedCollection={state.DeepReclaimExecuted} workingSetTrimmed=false " +
            $"fullViewRebuilds=0 warmViewsPreserved=true " +
            $"workingSetBeforeMB={before.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={after.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={before.PrivateBytes / (1024.0 * 1024):F1} " +
            $"privateAfterMB={after.PrivateBytes / (1024.0 * 1024):F1} " +
            $"managedHeapBeforeMB={before.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"managedHeapAfterMB={after.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"privateMinusGcHeapBeforeEstimateMB={Math.Max(0, before.PrivateBytes - before.ManagedHeapBytes) / (1024.0 * 1024):F1} " +
            $"privateMinusGcHeapAfterEstimateMB={Math.Max(0, after.PrivateBytes - after.ManagedHeapBytes) / (1024.0 * 1024):F1} " +
            $"thumbCacheBefore={before.ThumbnailCacheCount} " +
            $"thumbCacheAfter={after.ThumbnailCacheCount} " +
            $"thumbCacheBeforeMB={before.ThumbnailEstimatedBytes / (1024.0 * 1024):F1} " +
            $"thumbCacheAfterMB={after.ThumbnailEstimatedBytes / (1024.0 * 1024):F1} " +
            $"iconCacheBefore={before.IconCacheCount} " +
            $"iconCacheAfter={after.IconCacheCount} " +
            $"searchMetaCacheBefore={before.SearchMetaCacheCount} " +
            $"searchMetaCacheAfter={after.SearchMetaCacheCount} " +
            $"decodedBitmapBefore={before.DecodedBitmapCacheCount} " +
            $"decodedBitmapAfter={after.DecodedBitmapCacheCount} " +
            $"decodedBitmapBeforeMB={before.DecodedBitmapEstimatedBytes / (1024.0 * 1024):F1} " +
            $"decodedBitmapAfterMB={after.DecodedBitmapEstimatedBytes / (1024.0 * 1024):F1} " +
            $"quickCaptureDetailDecodesBefore={before.QuickCaptureDetailImageDecodeCount} " +
            $"quickCaptureDetailDecodesAfter={after.QuickCaptureDetailImageDecodeCount} " +
            $"quickCaptureDetailBeforeMB={before.QuickCaptureDetailImageEstimatedBytes / (1024.0 * 1024):F1} " +
            $"quickCaptureDetailAfterMB={after.QuickCaptureDetailImageEstimatedBytes / (1024.0 * 1024):F1} " +
            $"glanceCompactDecodesBefore={before.GlanceCompactBackgroundDecodeCount} " +
            $"glanceCompactDecodesAfter={after.GlanceCompactBackgroundDecodeCount} " +
            $"glanceCompactBeforeMB={before.GlanceCompactBackgroundEstimatedBytes / (1024.0 * 1024):F1} " +
            $"glanceCompactAfterMB={after.GlanceCompactBackgroundEstimatedBytes / (1024.0 * 1024):F1} " +
            $"shellKindCacheBefore={before.ShellKindCacheCount} " +
            $"shellKindCacheAfter={after.ShellKindCacheCount} " +
            $"shortcutCacheBefore={before.ShortcutMetadataCacheCount} " +
            $"shortcutCacheAfter={after.ShortcutMetadataCacheCount} " +
            $"releasedSearchIconEntries={state.ReleasedSearchIconEntries} " +
            $"releasedShellKindEntries={state.ReleasedShellKindEntries} " +
            $"releasedShortcutMetadataEntries={state.ReleasedShortcutMetadataEntries} " +
            $"releasedThumbs={state.ReleasedThumbnailEntries} " +
            $"releasedBitmaps={state.ReleasedDecodedBitmapEntries} " +
            $"releasedIconBytes={state.ReleasedIconByteEntries} " +
            $"releasedEstimatedMB={state.ReleasedEstimatedBytes / (1024.0 * 1024):F1} " +
            $"releasedCachedContents={state.ReleasedCachedContentCount} " +
            $"glanceStoresBefore={before.GlanceStoreCount} " +
            $"glanceStoresAfter={after.GlanceStoreCount} " +
            $"weatherGatesBefore={before.WeatherPathGateCount} " +
            $"weatherGatesAfter={after.WeatherPathGateCount} " +
            $"maintainedHosts={state.MaintainedContentHostCount} " +
            $"loadedWidgets={after.Visibility.LoadedWindowCount} " +
            $"logicalVisibleWidgets={after.Visibility.LogicalVisibleCount} " +
            $"nativeVisibleWidgets={after.Visibility.NativeVisibleCount} " +
            $"interactionActive={activity.IsWidgetInteractionActive} " +
            $"settingsOpen={activity.IsSettingsOpen} " +
            $"onboardingOpen={activity.IsOnboardingOpen} " +
            $"searchVisible={activity.IsSearchPopupVisible} " +
            $"desktopOrganizationOpen={activity.IsDesktopOrganizationOpen} " +
            $"deskBoxForegroundVisible={activity.IsDeskBoxForeground} " +
            $"pointerOverDeskBox={activity.IsPointerOverDeskBox}";
        Log($"[Memory] Background cleanup outcome {details}");
        PerformanceLogger.Mark("BackgroundMemoryCleanupOutcome", details);
    }

    internal static void ScheduleBackgroundMemoryCleanup(
        string reason = "unspecified")
    {
        if (IsShuttingDown) return;
        CancelBackgroundMemoryCleanupDelay();
        App app = Current;
        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings);
        int generation = Interlocked.Increment(
            ref s_backgroundMemoryCleanupGeneration);
        long scheduledTimestamp = Stopwatch.GetTimestamp();
        MemoryCleanupDiagnosticSnapshot before =
            CaptureMemoryCleanupDiagnosticSnapshot();
        int softDelaySeconds = performance.HiddenCacheCleanupDelaySeconds;
        int deepDelaySeconds = performance.HiddenDeepCleanupDelaySeconds;
        string cleanupScope = performance.HiddenCacheCleanupScope;

        if (softDelaySeconds == PerformanceSettingsPolicy.CleanupNever &&
            deepDelaySeconds == PerformanceSettingsPolicy.CleanupNever)
        {
            Log(
                $"[Memory] Background cleanup disabled generation={generation} " +
                $"mode={performance.Mode} reason={reason}");
            PerformanceLogger.Mark(
                "BackgroundMemoryCleanupDisabled",
                $"generation={generation} mode={performance.Mode} reason={reason}");
            LogBackgroundMemoryCleanupOutcome(
                generation,
                reason,
                performance.Mode,
                softDelaySeconds,
                deepDelaySeconds,
                scheduledTimestamp,
                before,
                new BackgroundMemoryCleanupOutcomeState
                {
                    Status = "disabled",
                    Detail = "all-stages-disabled",
                    CleanupScope = cleanupScope
                });
            return;
        }

        if (!app.CanArmBackgroundMemoryCleanup(out string blockReason))
        {
            LogVerbose(
                $"[Memory] Background cleanup not armed generation={generation} " +
                $"reason={reason} blockedBy={blockReason}");
            PerformanceLogger.Mark(
                "BackgroundMemoryCleanupNotArmed",
                $"generation={generation} reason={reason} blockedBy={blockReason}");
            LogBackgroundMemoryCleanupOutcome(
                generation,
                reason,
                performance.Mode,
                softDelaySeconds,
                deepDelaySeconds,
                scheduledTimestamp,
                before,
                new BackgroundMemoryCleanupOutcomeState
                {
                    Status = "not-armed",
                    Detail = blockReason,
                    CleanupScope = cleanupScope
                });
            return;
        }

        string scheduleDetails =
            $"generation={generation} reason={reason} mode={performance.Mode} " +
            $"softDelaySeconds={softDelaySeconds} " +
            $"deepDelaySeconds={deepDelaySeconds} " +
            $"cleanupScope={cleanupScope} " +
            $"policy=conditional-deep-finalizer,no-working-set-trim,no-view-rebuild";
        Log($"[Memory] Background cleanup scheduled {scheduleDetails}");
        PerformanceLogger.Mark(
            "BackgroundMemoryCleanupScheduled",
            scheduleDetails);

        var cancellationSource = new CancellationTokenSource();
        CancellationTokenSource? replacedSource = Interlocked.Exchange(
            ref s_backgroundMemoryCleanupCancellationSource,
            cancellationSource);
        if (replacedSource is not null)
        {
            try
            {
                replacedSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var dispatcherQueue = UiDispatcherQueue;
        bool enqueued = dispatcherQueue?.TryEnqueue(() =>
            SafeFireAndForget(() =>
                app.RunBackgroundMemoryCleanupScheduleAsync(
                    generation,
                    reason,
                    performance.Mode,
                    scheduledTimestamp,
                    softDelaySeconds,
                    deepDelaySeconds,
                    cleanupScope,
                    before,
                    cancellationSource))) == true;
        if (!enqueued)
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref s_backgroundMemoryCleanupCancellationSource,
                        null,
                        cancellationSource),
                    cancellationSource))
            {
                cancellationSource.Cancel();
            }

            cancellationSource.Dispose();
            Log(
                $"[Memory] Background cleanup scheduling failed " +
                $"generation={generation} reason={reason} " +
                $"error=dispatcher-unavailable");
            LogBackgroundMemoryCleanupOutcome(
                generation,
                reason,
                performance.Mode,
                softDelaySeconds,
                deepDelaySeconds,
                scheduledTimestamp,
                before,
                new BackgroundMemoryCleanupOutcomeState
                {
                    Status = "dispatch-failed",
                    Detail = "dispatcher-unavailable",
                    CleanupScope = cleanupScope
                });
        }
    }

    private async Task RunBackgroundMemoryCleanupScheduleAsync(
        int generation,
        string triggerReason,
        string mode,
        long scheduledTimestamp,
        int softDelaySeconds,
        int deepDelaySeconds,
        string cleanupScope,
        MemoryCleanupDiagnosticSnapshot before,
        CancellationTokenSource cancellationSource)
    {
        var state = new BackgroundMemoryCleanupOutcomeState
        {
            CleanupScope = cleanupScope
        };
        CancellationToken cancellationToken = cancellationSource.Token;
        string? loggedWaitReason = null;
        int activityBackoffAttempt = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation !=
                    Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
                {
                    state.Status = "cancelled";
                    state.Detail = "generation-changed";
                    PerformanceLogger.Mark(
                        "BackgroundMemoryCleanupCancelled",
                        $"generation={generation} reason=generation-changed");
                    return;
                }

                TimeSpan hiddenDuration =
                    Stopwatch.GetElapsedTime(scheduledTimestamp);
                TimeSpan? nextDelay =
                    MemoryCleanupPolicy.GetDelayUntilNextBackgroundStage(
                        hiddenDuration,
                        softDelaySeconds,
                        deepDelaySeconds,
                        state.CompletedStages);
                if (nextDelay is null)
                {
                    state.Status = "completed";
                    state.Detail = "all-configured-stages-completed";
                    PerformanceLogger.Mark(
                        "BackgroundMemoryCleanupScheduleCompleted",
                        $"generation={generation} " +
                        $"stages={state.CompletedStages}");
                    return;
                }

                if (nextDelay.Value > TimeSpan.Zero)
                {
                    await Task.Delay(nextDelay.Value, cancellationToken);
                    continue;
                }

                BackgroundMemoryCleanupStage dueStages =
                    MemoryCleanupPolicy.GetDueBackgroundStages(
                        hiddenDuration,
                        softDelaySeconds,
                        deepDelaySeconds,
                        state.CompletedStages);
                if (dueStages == BackgroundMemoryCleanupStage.None)
                {
                    await Task.Yield();
                    continue;
                }

                if (!CanRunBackgroundMemoryCleanup())
                {
                    string waitReason =
                        $"activity:{DescribeBackgroundMemoryCleanupBlockReason()}";
                    state.ActivityRetryCount++;
                    activityBackoffAttempt++;
                    TimeSpan retryDelay =
                        MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                            activityBackoffAttempt,
                            BackgroundMemoryCleanupRetryDelaySeconds,
                            BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                    state.Detail = waitReason;
                    if (!string.Equals(
                            loggedWaitReason,
                            waitReason,
                            StringComparison.Ordinal))
                    {
                        loggedWaitReason = waitReason;
                        Log(
                            $"[Memory] Background cleanup waiting " +
                            $"generation={generation} stages={dueStages} " +
                            $"reason={waitReason} " +
                            $"retrySeconds={retryDelay.TotalSeconds:F0}");
                    }

                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                activityBackoffAttempt = 0;

                if (dueStages.HasFlag(
                        BackgroundMemoryCleanupStage.SoftCache))
                {
                    PerformanceLogger.Mark(
                        "BackgroundMemorySoftCleanupTriggered",
                        $"generation={generation}");
                    bool softCleanupCompleted =
                        await RunBackgroundSoftMemoryCleanupAsync(
                            generation,
                            softDelaySeconds,
                            cleanupScope,
                            state);
                    if (!softCleanupCompleted)
                    {
                        state.ActivityRetryCount++;
                        activityBackoffAttempt++;
                        TimeSpan retryDelay =
                            MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                activityBackoffAttempt,
                                BackgroundMemoryCleanupRetryDelaySeconds,
                                BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                        await Task.Delay(retryDelay, cancellationToken);
                        continue;
                    }

                    state.CompletedStages |=
                        BackgroundMemoryCleanupStage.SoftCache;
                    activityBackoffAttempt = 0;
                    loggedWaitReason = null;
                }

                if (generation !=
                    Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
                {
                    state.Status = "cancelled";
                    state.Detail = "generation-changed-after-soft-cleanup";
                    return;
                }

                if (dueStages.HasFlag(
                        BackgroundMemoryCleanupStage.DeepFinalizerCollection))
                {
                    // Warm retention deliberately stops at cache eviction. A
                    // full collection is reserved for the explicit
                    // all-recreatable scope, where the user has accepted that
                    // disconnected native graphs may be finalized.
                    if (!string.Equals(
                            cleanupScope,
                            PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                            StringComparison.Ordinal))
                    {
                        state.DeepReclaimResult = "skipped-scope";
                        state.CompletedStages |=
                            BackgroundMemoryCleanupStage.DeepFinalizerCollection;
                    }
                    else
                    {
                        if (!CanRunBackgroundMemoryCleanup())
                        {
                            state.ActivityRetryCount++;
                            activityBackoffAttempt++;
                            TimeSpan retryDelay =
                                MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                    activityBackoffAttempt,
                                    BackgroundMemoryCleanupRetryDelaySeconds,
                                    BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                            state.Detail =
                                $"activity:{DescribeBackgroundMemoryCleanupBlockReason()}";
                            await Task.Delay(retryDelay, cancellationToken);
                            continue;
                        }

                        PerformanceLogger.Mark(
                            "BackgroundMemoryDeepReclaimTriggered",
                            $"generation={generation}");
                        bool deepReclaimCompleted =
                            await RunBackgroundDeepMemoryCleanupAsync(
                                generation,
                                triggerReason,
                                state);
                        if (!deepReclaimCompleted)
                        {
                            state.ActivityRetryCount++;
                            activityBackoffAttempt++;
                            TimeSpan retryDelay =
                                MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                    activityBackoffAttempt,
                                    BackgroundMemoryCleanupRetryDelaySeconds,
                                    BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                            await Task.Delay(retryDelay, cancellationToken);
                            continue;
                        }

                        state.CompletedStages |=
                            BackgroundMemoryCleanupStage.DeepFinalizerCollection;
                    }

                    activityBackoffAttempt = 0;
                    loggedWaitReason = null;
                }

                if (dueStages.HasFlag(
                        BackgroundMemoryCleanupStage.LongHiddenMaintenance))
                {
                    if (!CanRunBackgroundMemoryCleanup())
                    {
                        state.ActivityRetryCount++;
                        activityBackoffAttempt++;
                        TimeSpan retryDelay =
                            MemoryCleanupPolicy.GetCappedBackgroundRetryDelay(
                                activityBackoffAttempt,
                                BackgroundMemoryCleanupRetryDelaySeconds,
                                BackgroundMemoryCleanupMaximumRetryDelaySeconds);
                        await Task.Delay(retryDelay, cancellationToken);
                        continue;
                    }

                    PerformanceLogger.Mark(
                        "BackgroundMemoryLongHiddenMaintenanceTriggered",
                        $"generation={generation}");
                    LongHiddenWidgetMaintenanceResult maintenanceResult =
                        WidgetManager?.RunLongHiddenNoRebuildMaintenance() ??
                        default;
                    state.MaintainedContentHostCount +=
                        maintenanceResult.ContentHostCount;
                    int releasedCachedContents = 0;
                    if (string.Equals(
                            cleanupScope,
                            PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                            StringComparison.Ordinal))
                    {
                        LongHiddenWidgetResourceReleaseResult releaseResult =
                            WidgetManager?.ReleaseLongHiddenInactiveContent() ??
                            default;
                        releasedCachedContents = releaseResult.CachedContentCount;
                        state.ReleasedCachedContentCount += releasedCachedContents;
                    }
                    string maintenanceDetails =
                        $"generation={generation} " +
                        $"hosts={maintenanceResult.ContentHostCount} " +
                        $"releasedCachedContents={releasedCachedContents} " +
                        $"fullViewRebuilds=0 warmViewsPreserved=true";
                    Log(
                        $"[Memory] Long-hidden no-rebuild maintenance completed " +
                        maintenanceDetails);
                    PerformanceLogger.Mark(
                        "LongHiddenWidgetNoRebuildMaintenanceCompleted",
                        maintenanceDetails);
                    state.CompletedStages |=
                        BackgroundMemoryCleanupStage.LongHiddenMaintenance;
                    activityBackoffAttempt = 0;
                    loggedWaitReason = null;
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            state.Status = "cancelled";
            state.Detail = "cancellation-requested";
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Detail = ex.GetType().Name;
            Log(
                $"[Memory] Background cleanup coordinator failed " +
                $"generation={generation}: {ex}");
        }
        finally
        {
            Interlocked.CompareExchange(
                ref s_backgroundMemoryCleanupCancellationSource,
                null,
                cancellationSource);
            try
            {
                LogBackgroundMemoryCleanupOutcome(
                    generation,
                    triggerReason,
                    mode,
                    softDelaySeconds,
                    deepDelaySeconds,
                    scheduledTimestamp,
                    before,
                    state);
            }
            catch (Exception ex)
            {
                Log(
                    $"[Memory] Background cleanup outcome logging failed " +
                    $"generation={generation}: {ex.Message}");
            }

            cancellationSource.Dispose();
        }
    }

    private bool CanArmBackgroundMemoryCleanup(out string blockReason)
    {
        if (WidgetManager is null)
        {
            blockReason = "widget-manager-unavailable";
            return false;
        }

        WidgetMemoryVisibilitySnapshot visibility =
            WidgetManager.CaptureMemoryCleanupVisibilitySnapshot();
        if (visibility.HasNativeVisibleWidgets)
        {
            blockReason =
                $"widgets-visible-native-{visibility.NativeVisibleCount}";
            return false;
        }

        if (_settingsWindow is { IsVisibleToUser: true })
        {
            blockReason = "settings-open";
            return false;
        }

        if (_onboardingWindow is not null)
        {
            blockReason = "onboarding-open";
            return false;
        }

        if (_desktopOrganizationWindow is not null)
        {
            blockReason = "desktop-organization-open";
            return false;
        }

        if (_searchPopupWindow?.IsPopupVisible == true)
        {
            blockReason = "search-popup-visible";
            return false;
        }

        blockReason = string.Empty;
        return true;
    }

    private bool CanRunBackgroundMemoryCleanup()
    {
        if (!CanArmBackgroundMemoryCleanup(out _))
        {
            return false;
        }

        // The scheduler may wake up after the user has moved back over a
        // hidden DeskBox HWND. Recheck the complete idle contract immediately
        // before touching caches or running the finalizer collection.
        MemoryCleanupActivitySnapshot activity =
            CaptureMemoryCleanupActivity();
        return MemoryCleanupPolicy.IsDeepCleanupCandidate(activity);
    }

    private string DescribeBackgroundMemoryCleanupBlockReason()
    {
        if (!CanArmBackgroundMemoryCleanup(out string blockReason))
        {
            return blockReason;
        }

        MemoryCleanupActivitySnapshot activity =
            CaptureMemoryCleanupActivity();
        if (activity.IsWidgetInteractionActive)
        {
            return "widget-interaction";
        }

        if (activity.IsDeskBoxForeground)
        {
            return "deskbox-foreground";
        }

        if (activity.IsPointerOverDeskBox)
        {
            return "pointer-over-deskbox";
        }

        return "unknown";
    }

    private Task<bool> RunBackgroundSoftMemoryCleanupAsync(
        int generation,
        int hiddenDelaySeconds,
        string cleanupScope,
        BackgroundMemoryCleanupOutcomeState state)
    {
        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            return Task.FromResult(false);
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long privateBytesBefore = process.PrivateMemorySize64;
        long managedHeapBefore = GC.GetGCMemoryInfo().HeapSizeBytes;

        Localized.PruneDeadTargets();
        IconHelper.IdleIconCacheReleaseResult cacheRelease =
            string.Equals(
                cleanupScope,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
                StringComparison.Ordinal)
                ? IconHelper.ReleaseIdleCaches(
                    TimeSpan.FromSeconds(Math.Max(1, hiddenDelaySeconds)))
                : IconHelper.ReleaseHiddenCaches();
        int releasedSearchIconEntries = 0;
        int releasedShellKindEntries = 0;
        int releasedShortcutMetadataEntries = 0;
        if (string.Equals(
                cleanupScope,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                StringComparison.Ordinal))
        {
            releasedSearchIconEntries = _fileMetaService?.ReleaseHiddenCaches() ?? 0;
            releasedShellKindEntries = FileService.ReleaseHiddenShellKindCache();
            releasedShortcutMetadataEntries =
                ShortcutHelper.ReleaseHiddenMetadataCache();
        }

        bool releasedAnything =
            cacheRelease.ReleasedAnything ||
            releasedSearchIconEntries > 0 ||
            releasedShellKindEntries > 0 ||
            releasedShortcutMetadataEntries > 0;
        state.SoftCleanupResult = releasedAnything ? "released" : "no-op";
        state.ReleasedSearchIconEntries += releasedSearchIconEntries;
        state.ReleasedShellKindEntries += releasedShellKindEntries;
        state.ReleasedShortcutMetadataEntries += releasedShortcutMetadataEntries;
        state.ReleasedThumbnailEntries += cacheRelease.ReleasedThumbnails;
        state.ReleasedDecodedBitmapEntries +=
            cacheRelease.ReleasedDecodedBitmaps;
        state.ReleasedIconByteEntries += cacheRelease.ReleasedIconByteEntries;
        state.ReleasedEstimatedBytes += cacheRelease.ReleasedEstimatedBytes;

        if (generation != Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            LogVerbose(
                "[Memory] Background soft cleanup completed while UI activity resumed");
        }

        process.Refresh();
        long managedHeapAfter = GC.GetGCMemoryInfo().HeapSizeBytes;
        Log(
            $"[Memory] Background soft cleanup completed hiddenSeconds={hiddenDelaySeconds} " +
            $"workingSetBeforeMB={workingSetBefore / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={process.WorkingSet64 / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={privateBytesBefore / (1024.0 * 1024):F1} " +
            $"privateAfterMB={process.PrivateMemorySize64 / (1024.0 * 1024):F1} " +
            $"managedHeapBeforeMB={managedHeapBefore / (1024.0 * 1024):F1} " +
            $"managedHeapAfterMB={managedHeapAfter / (1024.0 * 1024):F1} " +
            $"cleanupScope={cleanupScope} result={state.SoftCleanupResult} " +
            $"forcedCollection=false trimmed=false warmCachePreserved=true " +
            $"releasedThumbs={cacheRelease.ReleasedThumbnails} " +
            $"releasedBitmaps={cacheRelease.ReleasedDecodedBitmaps} " +
            $"releasedIconBytes={cacheRelease.ReleasedIconByteEntries} " +
            $"releasedSearchIconEntries={releasedSearchIconEntries} " +
            $"releasedShellKindEntries={releasedShellKindEntries} " +
            $"releasedShortcutMetadataEntries={releasedShortcutMetadataEntries} " +
            $"releasedEstimatedMB={cacheRelease.ReleasedEstimatedBytes / (1024.0 * 1024):F1}");
        return Task.FromResult(true);
    }

    private async Task<bool> RunBackgroundDeepMemoryCleanupAsync(
        int generation,
        string triggerReason,
        BackgroundMemoryCleanupOutcomeState state)
    {
        if (generation !=
                Volatile.Read(ref s_backgroundMemoryCleanupGeneration) ||
            !CanRunBackgroundMemoryCleanup())
        {
            return false;
        }

        MemoryCleanupDiagnosticSnapshot before =
            CaptureMemoryCleanupDiagnosticSnapshot();
        MemoryReclaimResult reclaimResult = await Task.Run(
            () => MemoryReclaimer.TryCollect(
                $"background:{triggerReason}"));
        state.DeepReclaimResult = reclaimResult.Status.ToString();
        state.DeepReclaimExecuted = reclaimResult.Executed;
        state.DeepReclaimDurationMilliseconds =
            reclaimResult.DurationMilliseconds;
        state.DeepReclaimReleasedHeapBytes =
            reclaimResult.ReleasedHeapBytes;

        // The pre-1.4.5 idle trim: page everything out once the user is fully
        // away. This stage only runs after every widget is hidden and all
        // activity gates cleared, so no visible surface can jitter from the
        // back-faults. Advancing the epoch lets widgets re-prime warmed layout
        // state lazily when they come back.
        bool workingSetTrimmed = false;
        if (reclaimResult.Executed &&
            SettingsService.Settings.IdleWorkingSetTrimEnabled &&
            !(SettingsService.Settings.ImmediateHiddenWorkingSetTrimEnabled &&
              _immediateHiddenWorkingSetTrimTracker.TrimmedCurrentHiddenSession))
        {
            workingSetTrimmed = Win32Helper.TrimWorkingSet();
            CompleteWorkingSetTrim(workingSetTrimmed, triggerReason);
        }

        MemoryCleanupDiagnosticSnapshot after =
            CaptureMemoryCleanupDiagnosticSnapshot();
        Log(
            $"[Memory] Deep finalizer cleanup completed " +
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"collections={reclaimResult.CollectionsBefore}->" +
            $"{reclaimResult.CollectionsAfter} " +
            $"gcHeapBeforeMB={before.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"gcHeapAfterMB={after.ManagedHeapBytes / (1024.0 * 1024):F1} " +
            $"privateBeforeMB={before.PrivateBytes / (1024.0 * 1024):F1} " +
            $"privateAfterMB={after.PrivateBytes / (1024.0 * 1024):F1} " +
            $"workingSetBeforeMB={before.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={after.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"reclaimPrivateBeforeMB={reclaimResult.PrivateBeforeBytes / (1024.0 * 1024):F1} " +
            $"reclaimPrivateAfterMB={reclaimResult.PrivateAfterBytes / (1024.0 * 1024):F1} " +
            $"reason={triggerReason} " +
            $"workingSetTrimmed={workingSetTrimmed} fullViewRebuilds=0");
        PerformanceLogger.Mark(
            "BackgroundMemoryDeepReclaimCompleted",
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"releasedPrivateMB={reclaimResult.ReleasedPrivateBytes / (1024.0 * 1024):F1} " +
            $"reason={triggerReason}");

        // A cooldown or in-progress veto must not consume the deep stage: the
        // scheduler used to treat it as completed, so the finalizer collection
        // never actually ran for that hidden session. Returning false makes
        // the pipeline back off and retry until the cooldown window elapses.
        return reclaimResult.Status is not (MemoryReclaimStatus.SkippedCooldown
            or MemoryReclaimStatus.SkippedInProgress);
    }

    private bool CanRunDeepMemoryCleanup(out string blockReason)
    {
        MemoryCleanupActivitySnapshot activity =
            CaptureMemoryCleanupActivity();
        if (!MemoryCleanupPolicy.IsDeepCleanupCandidate(activity))
        {
            if (activity.IsSettingsOpen)
            {
                blockReason = "settings-open";
            }
            else if (activity.IsOnboardingOpen)
            {
                blockReason = "onboarding-open";
            }
            else if (activity.IsSearchPopupVisible)
            {
                blockReason = "search-popup-visible";
            }
            else if (activity.IsDesktopOrganizationOpen)
            {
                blockReason = "desktop-organization-open";
            }
            else if (activity.IsWidgetInteractionActive)
            {
                blockReason = "widget-interaction";
            }
            else if (activity.IsDeskBoxForeground)
            {
                blockReason = "deskbox-foreground";
            }
            else if (activity.IsPointerOverDeskBox)
            {
                blockReason = "pointer-over-deskbox";
            }
            else
            {
                blockReason = "activity";
            }

            return false;
        }

        blockReason = string.Empty;
        return true;
    }

    private async Task RunHeavyOperationDeepMemoryCleanupAsync(
        int generation,
        string reason,
        int? requiredBackgroundGeneration)
    {
        if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
        {
            return;
        }

        if (requiredBackgroundGeneration is int requiredGeneration &&
            requiredGeneration !=
                Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
        {
            PerformanceLogger.Mark(
                "DeepMemoryReclaimSkipped",
                $"reason=background-generation-changed trigger={reason}");
            return;
        }

        if (!CanRunDeepMemoryCleanup(out string blockReason))
        {
            LogVerbose(
                $"[Memory] Deep finalizer cleanup deferred " +
                $"reason={reason} blockedBy={blockReason}");
            PerformanceLogger.Mark(
                "DeepMemoryReclaimDeferred",
                $"reason={reason} blockedBy={blockReason}");
            return;
        }

        MemoryCleanupDiagnosticSnapshot before =
            CaptureMemoryCleanupDiagnosticSnapshot();
        MemoryReclaimResult reclaimResult = await Task.Run(
            () => MemoryReclaimer.TryCollect($"heavy-operation:{reason}"));
        if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
        {
            return;
        }

        MemoryCleanupDiagnosticSnapshot after =
            CaptureMemoryCleanupDiagnosticSnapshot();
        Log(
            $"[Memory] Heavy-operation deep cleanup completed " +
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"collections={reclaimResult.CollectionsBefore}->" +
            $"{reclaimResult.CollectionsAfter} " +
            $"privateBeforeMB={before.PrivateBytes / (1024.0 * 1024):F1} " +
            $"privateAfterMB={after.PrivateBytes / (1024.0 * 1024):F1} " +
            $"workingSetBeforeMB={before.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"workingSetAfterMB={after.WorkingSetBytes / (1024.0 * 1024):F1} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"releasedPrivateMB={reclaimResult.ReleasedPrivateBytes / (1024.0 * 1024):F1} " +
            $"reason={reason} workingSetTrimmed=false fullViewRebuilds=0");
        PerformanceLogger.Mark(
            "HeavyOperationDeepMemoryCleanupCompleted",
            $"status={reclaimResult.Status} " +
            $"durationMs={reclaimResult.DurationMilliseconds} " +
            $"releasedHeapMB={reclaimResult.ReleasedHeapBytes / (1024.0 * 1024):F1} " +
            $"releasedPrivateMB={reclaimResult.ReleasedPrivateBytes / (1024.0 * 1024):F1} " +
            $"reason={reason}");
    }

    internal static void ScheduleLightMemoryCleanup(
        bool completedHeavyOperation = false,
        int? requiredBackgroundGeneration = null)
    {
        if (IsShuttingDown) return;
        int generation = Interlocked.Increment(ref s_lightMemoryCleanupGeneration);
        App app = Current;
        var dispatcherQueue = App.UiDispatcherQueue;
        bool enqueued = dispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(2000);
                if (generation != Volatile.Read(ref s_lightMemoryCleanupGeneration))
                {
                    return;
                }

                if (requiredBackgroundGeneration is int requiredGeneration &&
                    requiredGeneration !=
                        Volatile.Read(ref s_backgroundMemoryCleanupGeneration))
                {
                    PerformanceLogger.Mark(
                        "BackgroundMemoryCleanupCancelled",
                        "reason=widgets-restored");
                    return;
                }

                if (completedHeavyOperation)
                {
                    await app.RunHeavyOperationDeepMemoryCleanupAsync(
                        generation,
                        reason: "completed-heavy-operation",
                        requiredBackgroundGeneration: requiredBackgroundGeneration);
                }

                Localized.PruneDeadTargets();
                PerformanceLogger.SampleMemory("light-cleanup-completed");
                LogVerbose(
                    "[Memory] Delayed maintenance completed " +
                    $"deepReclaimRequested={completedHeavyOperation} " +
                    "workingSetTrimmed=false heapCompaction=false");
            }
            catch (Exception ex)
            {
                Log($"[Memory] Delayed cleanup failed: {ex}");
            }
        }) == true;
        if (!enqueued)
        {
            Log(
                $"[Memory] Delayed cleanup scheduling failed " +
                $"generation={generation} error=dispatcher-unavailable");
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
        try
        {
            await ShutdownCoreAsync();
        }
        catch (Exception ex)
        {
            Log($"[Shutdown] Teardown failed: {ex}");
        }
        finally
        {
            // Teardown must never leave a tray-less process holding the
            // single-instance mutex, so the exit is unconditional.
            // Keep the tray's dispatcher host alive until the asynchronous
            // shutdown sequence and all of its continuations have completed.
            try
            {
                _trayWindow?.Close();
                _trayWindow = null;
            }
            catch (Exception ex) { Log($"[Shutdown] Tray window close failed: {ex}"); }
            finally
            {
                DrainLogQueue();
                Exit();
            }
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Interlocked.Exchange(ref s_shutdownRequested, 1);
        await _shutdownSequence.RunAsync(
            ShutdownStep.Sync("backup-subscriptions", () =>
            {
                SettingsService.SettingsChanged -= OnBackupSettingsChanged;
                CloudBackupService.BackupRunCompleted -= OnCloudBackupRunCompleted;
                DataBackupService.AutomaticSnapshotFallbackDetected -= OnAutomaticBackupFallbackDetected;
            }),
            new("backup-runtime", () => _backupRuntime?.StopAsync() ?? Task.CompletedTask),
            new("todo-settings", () => _todoSettings?.StopAsync() ?? Task.CompletedTask),
            ShutdownStep.Sync("memory-maintenance", () =>
            {
                StopVisibleIdleMemoryMaintenance();
                StopQuiescenceWorkingSetTrim();
                CancelBackgroundMemoryCleanup();
                Interlocked.Increment(ref s_lightMemoryCleanupGeneration);
            }),
            // Observers stop before windows, so topology callbacks cannot
            // access partially closed surfaces. Each owner has its own step.
            ShutdownStep.Sync("display-watcher", () => { _displayAreaWatcher?.Dispose(); _displayAreaWatcher = null; }),
            ShutdownStep.Sync("display-transition", () => { _displayTopologyTransitionCoordinator?.Dispose(); _displayTopologyTransitionCoordinator = null; }),
            ShutdownStep.Sync("lifecycle-watcher", () => { _lifecycleRecoveryWatcher?.Dispose(); _lifecycleRecoveryWatcher = null; }),
            ShutdownStep.Sync("hook-watchdog", () => { _hookHealthWatchdog?.Dispose(); _hookHealthWatchdog = null; }),
            ShutdownStep.Sync("diagnostics", () => { _diagnosticsService?.Dispose(); _diagnosticsService = null; }),
            new("quick-capture", () => _quickCaptureSettings?.StopAsync() ??
                _quickCaptureClipboardRuntime?.StopAsync() ?? Task.CompletedTask),
            ShutdownStep.Sync("desktop-organization-watcher", () =>
            {
                if (DesktopAutoOrganizationWatcher is not null)
                {
                    DesktopAutoOrganizationWatcher.ItemOrganized -= ShowDesktopAutoOrganizationNotification;
                    DesktopAutoOrganizationWatcher.Dispose();
                    DesktopAutoOrganizationWatcher = null;
                }
            }),
            ShutdownStep.Sync("desktop-activation", () => { DesktopDoubleClickActivationService?.Dispose(); DesktopDoubleClickActivationService = null; }),
            new("todo-reminders", async () => { if (_todoReminderRuntime is not null) await _todoReminderRuntime.DisposeAsync(); }),
            ShutdownStep.Sync("notifications", () => { _nativeNotificationService?.Dispose(); _nativeNotificationService = null; }),
            new("search-settings", () => _searchSettings?.StopAsync() ?? Task.CompletedTask),
            ShutdownStep.Sync("search-runtime", DisposeSearchServices),
            // Hooks must be removed while their owning tray window still exists.
            ShutdownStep.Sync("global-hotkey", () => { GlobalHotkeyService?.Dispose(); GlobalHotkeyService = null; }),
            ShutdownStep.Sync("widgets", () => WidgetManager?.CloseAll()),
            ShutdownStep.Sync("desktop-organization-window", () => { _desktopOrganizationWindow?.CloseForShutdown(); _desktopOrganizationWindow = null; }),
            ShutdownStep.Sync("settings-window", () => { _settingsWindow?.CloseForShutdown(); _settingsWindow = null; }),
            ShutdownStep.Sync("backup-settings", () => _backupSettings?.Dispose()),
            ShutdownStep.Sync("onboarding-window", () => { _onboardingWindow?.Close(); _onboardingWindow = null; }),
            // Closing a surface may commit its final edit/order. Flush after
            // those consumers are closed, while persistence services still live.
            new("settings-flush", async () =>
            {
                if (!await SettingsService.FlushPendingSaveAsync(notifySubscribers: false))
                    throw new IOException("Final settings flush failed.");
            }),
            ShutdownStep.Sync("app-subscriptions", () =>
            {
                SettingsService.PersistenceFailed -= OnSettingsPersistenceFailed;
                SettingsService.SettingsChanged -= OnMaterialCapabilitySettingsChanged;
                LocalizationService.LanguageChanged -= OnLanguageChanged;
            }),
            ShutdownStep.Sync("tray-icon", () => { _trayIcon?.Dispose(); _trayIcon = null; }),
            ShutdownStep.Sync("activation-registration", () => { _activationRegistration?.Unregister(null); _activationRegistration = null; }),
            ShutdownStep.Sync("activation-event", () => { _activationEvent?.Dispose(); _activationEvent = null; }),
            // The currently registered disposables are synchronous; container
            // disposal stays on the UI thread for ThemeService's WinRT objects.
            ShutdownStep.Sync("service-container", () => Services.Dispose()),
            ShutdownStep.Sync("log-drain", DrainLogQueue),
            ShutdownStep.Sync("single-instance", () =>
            {
                try { _singleInstanceMutex?.ReleaseMutex(); }
                catch (ApplicationException) { }
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }));
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"Unhandled exception: {e.Exception}");
        // Handled first: the runtime must not tear the process down while the
        // fatal path reports the failure to the user.
        e.Handled = true;

        if (!IsStartupLifelineEstablished)
        {
            // Before the tray exists the process has nothing to fall back on,
            // and swallowing the exception would leave it holding the
            // single-instance mutex without any UI.
            FailStartup("unhandled exception before the startup lifeline was established", e.Exception);
        }
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
                    LocalizationService,
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

            _searchSettings?.OnRuntimeChanged();

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
        _searchSettings?.OnRuntimeStopping();
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
        _searchSettings?.OnRuntimeChanged();
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

    private IReadOnlyList<IntPtr> GetRaisedBandAuxiliaryWindowHandles()
    {
        List<IntPtr> handles = new(3);
        if (_settingsWindow is { IsVisibleToUser: true } settings)
        {
            handles.Add(WindowNative.GetWindowHandle(settings));
        }

        if (_searchPopupWindow is { IsPopupVisible: true } popup)
        {
            handles.Add(popup.WindowHandle);
        }

        if (_desktopOrganizationWindow is { } organize)
        {
            handles.Add(organize.WindowHandle);
        }

        return handles;
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
            CancelBackgroundMemoryCleanup();
        };
        popup.PopupHidden += (_, _) =>
        {
            ScheduleBackgroundMemoryCleanup("search-popup-hidden");
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_searchPopupWindow, popup))
            {
                _searchPopupWindow = null;
                Log("[Search] Popup shell discarded; next open will recreate it");
            }

            _fileMetaService?.Dispose();
            _fileMetaService = null;
            PerformanceLogger.SampleMemory("search-popup-closed");
            // Closing a transient popup already detaches its visual tree. Do not
            // promote that normal release into a compacting GC while visible
            // widgets are still warm.
            ScheduleLightMemoryCleanup();
            ScheduleBackgroundMemoryCleanup("search-popup-closed");
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
