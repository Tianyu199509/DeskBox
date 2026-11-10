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

    private readonly string _recoveryDirectory;

    public static DeskBoxDataPathService Current { get; } = new(ResolveConfiguredRoot());

    public DeskBoxDataPathService(string? rootPath = null)
        : this(
            rootPath,
            AppDistributionService.Current.IsMicrosoftStore,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal DeskBoxDataPathService(
        string? rootPath,
        bool isMicrosoftStore,
        string localAppDataPath,
        string userProfilePath)
    {
        string legacyLocalAppDataRoot = Path.GetFullPath(Path.Combine(
            localAppDataPath,
            "DeskBox"));
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
        SettingsIpcPipeName =
            $"DeskBox_Settings_Ipc_{InstanceScope}_S{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        AgentBridgePipeName =
            $"DeskBox_Agent_Ipc_{InstanceScope}_S{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

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
            string legacyRecoveryRoot = Path.Combine(
                Path.GetDirectoryName(legacyLocalAppDataRoot) ?? legacyLocalAppDataRoot,
                "DeskBox-Recovery");
            StoreUserAssetMigrationFileOps.EnsureStoreDurableRootInitialized(
                legacyLocalAppDataRoot,
                RootPath,
                legacyRecoveryRoot,
                _recoveryDirectory);
        }
        else
        {
            _recoveryDirectory = Path.Combine(
                Path.GetDirectoryName(RootPath) ?? RootPath,
                "DeskBox-Recovery");
        }
    }

    public string RootPath { get; }
    public bool IsDevelopmentRoot { get; }
    public string InstanceScope { get; }
    public string ActivationEventName => $"DeskBox_Activate_Event_{InstanceScope}";
    public string SingleInstanceMutexName => $"DeskBox_SingleInstance_Mutex_{InstanceScope}";
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

    private static string? ResolveConfiguredRoot()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(DevelopmentRootEnvironmentVariable);
#elif DESKBOX_NATIVE_AOT
        return Environment.GetEnvironmentVariable(AotPreviewRootEnvironmentVariable);
#else
        return null;
#endif
    }

    private static string CreateInstanceScope(string rootPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
