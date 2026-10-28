using System.Security.Cryptography;
using System.Text;

namespace DeskBox.Services;

public sealed class DeskBoxDataPathService
{
    public const string DevelopmentRootEnvironmentVariable = "DESKBOX_DEV_DATA_ROOT";
    public const string AotPreviewRootEnvironmentVariable = "DESKBOX_AOT_PREVIEW_DATA_ROOT";
    private const string ProductionInstanceScope = "7F3A9B2E";

    public static DeskBoxDataPathService Current { get; } = new(ResolveConfiguredRoot());

    public DeskBoxDataPathService(string? rootPath = null)
    {
        string productionRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox"));
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
    public string DataDirectory => Path.Combine(RootPath, "data");
    public string UpdatesDirectory => Path.Combine(RootPath, "updates");
    // Recovery snapshots intentionally live beside, rather than inside, the
    // app-data root. Direct install: the directory really is
    // %LOCALAPPDATA%\DeskBox-Recovery and survives a normal uninstall.
    // Store (MSIX): AppData write virtualization redirects the write into
    // Packages\<PFN>\LocalCache, so uninstalling the package deletes the
    // recovery copy with it; an in-place update over the same
    // PackageFamilyName keeps LocalCache and therefore the snapshots.
    // Guaranteeing uninstall survival on the store channel as well would
    // require declaring desktop6:FileSystemWriteVirtualization=disabled —
    // a restricted capability that is effectively unavailable to third-party
    // apps (store-lifecycle audit 2026-10-07, option D); the accepted
    // alternative is the in-product disclosure on the backup settings page.
    public string RecoveryDirectory => IsDevelopmentRoot
        ? $"{RootPath}-Recovery"
        : Path.Combine(
            Path.GetDirectoryName(RootPath) ?? RootPath,
            "DeskBox-Recovery");
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
