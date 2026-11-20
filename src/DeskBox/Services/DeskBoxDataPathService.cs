using System.Security.Cryptography;
using System.Text;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class DeskBoxDataPathService
{
    public const string DevelopmentRootEnvironmentVariable = "DESKBOX_DEV_DATA_ROOT";
    public const string AotPreviewRootEnvironmentVariable = "DESKBOX_AOT_PREVIEW_DATA_ROOT";
    internal const string StoreDurableSubdirectoryName = ".appdata";
    internal const string StoreDurableRecoverySubdirectoryName = ".appdata-recovery";
    private const string ProductionInstanceScope = "7F3A9B2E";

    private static readonly object s_currentGate = new();
    private static string? s_testRootOverride;
    private static DeskBoxDataPathService? s_current;

    private readonly string _recoveryDirectory;
    private readonly string _legacyLocalAppDataRoot;
    private readonly string _legacyRecoveryRoot;
    private readonly bool _isMicrosoftStore;

    /// <summary>
    /// Test seam: isolates <see cref="Current"/> under a throwaway root in both
    /// Debug and Release test hosts without opening environment-variable
    /// overrides in shipped Release binaries.
    /// </summary>
    internal static string? TestRootOverride
    {
        get => Volatile.Read(ref s_testRootOverride);
        set
        {
            lock (s_currentGate)
            {
                Volatile.Write(ref s_testRootOverride, value);
                Volatile.Write(ref s_current, null);
            }
        }
    }

    public static DeskBoxDataPathService Current
    {
        get
        {
            DeskBoxDataPathService? existing = Volatile.Read(ref s_current);
            if (existing is not null)
            {
                return existing;
            }

            lock (s_currentGate)
            {
                existing = s_current;
                if (existing is null)
                {
                    existing = new(ResolveConfiguredRoot(), deferStoreMigration: true);
                    Volatile.Write(ref s_current, existing);
                }

                return existing;
            }
        }
    }

    public DeskBoxDataPathService(string? rootPath = null)
        : this(rootPath, deferStoreMigration: false)
    {
    }

    private DeskBoxDataPathService(string? rootPath, bool deferStoreMigration)
        : this(
            rootPath,
            AppDistributionService.Current.IsMicrosoftStore,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            deferStoreMigration)
    {
    }

    internal DeskBoxDataPathService(
        string? rootPath,
        bool isMicrosoftStore,
        string localAppDataPath,
        string userProfilePath,
        bool deferStoreMigration = false)
    {
        _isMicrosoftStore = isMicrosoftStore;
        string legacyLocalAppDataRoot = Path.GetFullPath(Path.Combine(
            localAppDataPath,
            "DeskBox"));
        _legacyLocalAppDataRoot = legacyLocalAppDataRoot;
        _legacyRecoveryRoot = Path.Combine(
            Path.GetDirectoryName(legacyLocalAppDataRoot) ?? legacyLocalAppDataRoot,
            "DeskBox-Recovery");
        string storeDurableRoot = Path.GetFullPath(Path.Combine(
            userProfilePath,
            "DeskBox",
            StoreDurableSubdirectoryName));
        string productionRoot = isMicrosoftStore && !string.IsNullOrWhiteSpace(userProfilePath)
            ? storeDurableRoot
            : legacyLocalAppDataRoot;
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? productionRoot
            : Path.GetFullPath(rootPath.Trim());
        IsDevelopmentRoot = !string.Equals(
            RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            productionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        InstanceScope = IsDevelopmentRoot
            ? CreateInstanceScope(RootPath)
            : ProductionInstanceScope;
        int sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        SettingsIpcPipeName =
            $"DeskBox_Settings_Ipc_{InstanceScope}_S{sessionId}";
        StudioIpcPipeName =
            $"DeskBox_Studio_Ipc_{InstanceScope}_S{sessionId}";
        AgentBridgePipeName =
            $"DeskBox_Agent_Ipc_{InstanceScope}_S{sessionId}";
        PrimaryOwnerMappingName =
            $"DeskBox_PrimaryOwner_{InstanceScope}_S{sessionId}";

        if (IsDevelopmentRoot)
        {
            _recoveryDirectory = $"{RootPath}-Recovery";
        }
        else if (isMicrosoftStore && !string.IsNullOrWhiteSpace(userProfilePath))
        {
            _recoveryDirectory = Path.GetFullPath(Path.Combine(
                userProfilePath,
                "DeskBox",
                StoreDurableRecoverySubdirectoryName));
            if (!deferStoreMigration)
            {
                EnsureStoreDurableRootInitialized();
            }
        }
        else
        {
            _recoveryDirectory = Path.Combine(
                Path.GetDirectoryName(RootPath) ?? RootPath,
                "DeskBox-Recovery");
        }
    }

    internal string LegacyLocalAppDataRoot => _legacyLocalAppDataRoot;
    internal string LegacyRecoveryRoot => _legacyRecoveryRoot;
    internal bool IsMicrosoftStoreRoot => _isMicrosoftStore;

    internal void EnsureStoreDurableRootInitialized()
    {
        if (IsDevelopmentRoot || !_isMicrosoftStore)
        {
            return;
        }

        StoreUserAssetMigrationFileOps.EnsureStoreDurableRootInitialized(
            _legacyLocalAppDataRoot,
            RootPath,
            _legacyRecoveryRoot,
            _recoveryDirectory);
    }

    public string RootPath { get; }
    public bool IsDevelopmentRoot { get; }
    public string InstanceScope { get; }
    public string ActivationEventName => $"DeskBox_Activate_Event_{InstanceScope}";
    public string SingleInstanceMutexName => $"DeskBox_SingleInstance_Mutex_{InstanceScope}";
    public string PrimaryOwnerMappingName { get; }
    public string BuildGracefulExitEventName(int processId) => $"DeskBox_GracefulExit_{InstanceScope}_{processId}";
    /// <summary>Duplex pipe carrying settings-host IPC (batch 3b of the
    /// settings-process split); same user only, per instance scope.
    /// Named pipes live in a MACHINE-GLOBAL namespace (unlike the mutex and
    /// activation event above, whose kernel objects default to the
    /// session-local namespace): without a session discriminator, a DeskBox
    /// instance left running in a disconnected fast-user-switch session held
    /// the single pipe instance and the next user's settings host could
    /// never connect — settings silently unopenable for that whole session
    /// (batch R9).</summary>
    public string SettingsIpcPipeName { get; }
    public string StudioIpcPipeName { get; }
    public string AgentBridgePipeName { get; }
    public string DataDirectory => Path.Combine(RootPath, "data");
    public string UpdatesDirectory => Path.Combine(RootPath, "updates");
    // Recovery snapshots intentionally live beside, rather than inside, the
    // app-data root. Direct install uses %LOCALAPPDATA%\DeskBox-Recovery;
    // Store (MSIX) uses %USERPROFILE%\DeskBox\.appdata-recovery alongside
    // %USERPROFILE%\DeskBox\.appdata so MSIX LocalCache resets/reinstalls
    // never wipe automatic snapshots or user-owned attachments.
    public string RecoveryDirectory => _recoveryDirectory;
    public string LogFilePath => Path.Combine(RootPath, "DeskBox.log");

    /// <summary>
    /// The settings-host process's own log (batch 3c-3b): same directory and
    /// queue/rotation machinery as the main log, different file, so the two
    /// processes never append the same file concurrently.
    /// </summary>
    public string SettingsHostLogFilePath => Path.Combine(RootPath, "DeskBox.Settings.log");
    public string StudioHostLogFilePath => Path.Combine(RootPath, "DeskBox.Studio.log");

    private static string? ResolveConfiguredRoot()
    {
        string? testOverride = Volatile.Read(ref s_testRootOverride);
        if (!string.IsNullOrWhiteSpace(testOverride))
        {
            return testOverride;
        }

#if DEBUG
        return Environment.GetEnvironmentVariable(DevelopmentRootEnvironmentVariable);
#elif DESKBOX_AOT_SMOKE_HARNESS || DESKBOX_PGO_INSTRUMENTATION
        return Environment.GetEnvironmentVariable(AotPreviewRootEnvironmentVariable);
#elif DESKBOX_NATIVE_AOT && !DESKBOX_STORE
        // Only uninstalled NativeAOT staging binaries (such as Stage 7C1's pre-installer
        // smoke gate under .artifacts\aot-retail\...\publish) may redirect data roots.
        // Any installed Direct instance (ProgramFiles, LocalAppData\Programs, or a custom
        // Inno Setup directory carrying unins000.exe/unins000.dat) ignores env overrides.
        string? candidate = Environment.GetEnvironmentVariable(AotPreviewRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            string trimmedCandidate = candidate.Trim();
            if (trimmedCandidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                trimmedCandidate.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                return null;
            }

            string baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            if (File.Exists(Path.Combine(baseDir, "unins000.exe")) ||
                File.Exists(Path.Combine(baseDir, "unins000.dat")))
            {
                return null;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs");
            if ((!string.IsNullOrEmpty(programFiles) && IsSameOrSubPath(baseDir, programFiles)) ||
                (!string.IsNullOrEmpty(programFilesX86) && IsSameOrSubPath(baseDir, programFilesX86)) ||
                (!string.IsNullOrEmpty(localPrograms) && IsSameOrSubPath(baseDir, localPrograms)))
            {
                return null;
            }

            string fullCandidate = Path.GetFullPath(trimmedCandidate);
            if (fullCandidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                HasReparsePointInChain(fullCandidate))
            {
                return null;
            }

            return fullCandidate;
        }
        catch
        {
            return null;
        }
#else
        return null;
#endif
    }

#if DESKBOX_NATIVE_AOT && !DESKBOX_STORE && !DEBUG && !DESKBOX_AOT_SMOKE_HARNESS && !DESKBOX_PGO_INSTRUMENTATION
    private static bool IsSameOrSubPath(string candidatePath, string rootPath)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointInChain(string fullPath)
    {
        for (DirectoryInfo? current = new(fullPath); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
#endif

    private static string CreateInstanceScope(string rootPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
