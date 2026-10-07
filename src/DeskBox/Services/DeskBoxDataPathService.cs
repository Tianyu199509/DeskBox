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
    }

    public string RootPath { get; }
    public bool IsDevelopmentRoot { get; }
    public string InstanceScope { get; }
    public string ActivationEventName => $"DeskBox_Activate_Event_{InstanceScope}";
    public string SingleInstanceMutexName => $"DeskBox_SingleInstance_Mutex_{InstanceScope}";
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
    // require declaring desktop6:FileSystemWriteVirtualization=disabled
    // (a restricted capability needing store approval) — pending product
    // decision.
    public string RecoveryDirectory => IsDevelopmentRoot
        ? $"{RootPath}-Recovery"
        : Path.Combine(
            Path.GetDirectoryName(RootPath) ?? RootPath,
            "DeskBox-Recovery");
    public string LogFilePath => Path.Combine(RootPath, "DeskBox.log");

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
