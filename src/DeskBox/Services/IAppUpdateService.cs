using DeskBox.Models;

namespace DeskBox.Services;

public enum AppUpdateDeliveryKind
{
    DirectInstaller,
    MicrosoftStore
}

public interface IAppUpdateService
{
    AppUpdateCheckResult? LastCheckResult { get; }
    DateTime? LastCheckTimeUtc { get; }
    event Action<AppUpdateCheckResult>? CheckCompleted;
    string ManifestUrl { get; }
    AppUpdateDeliveryKind DeliveryKind { get; }

    /// <summary>
    /// Installer of the most recent successful download, owned by the
    /// service so a background-initiated download (auto-download) is visible
    /// to every surface that asks, not just the window that started it.
    /// Invalidated by a check that offers a different version.
    /// </summary>
    string? LastDownloadedInstallerPath { get; }

    /// <summary>True while any caller's download is transferring.</summary>
    bool IsDownloadInProgress { get; }

    /// <summary>Progress of whichever download is in flight (any initiator).</summary>
    event Action<AppUpdateDownloadProgress>? DownloadProgressChanged;

    /// <summary>Final result of the most recent download attempt (any initiator).</summary>
    event Action<AppUpdateDownloadResult>? DownloadCompleted;

    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<AppUpdateDownloadResult> DownloadUpdateAsync(
        AppUpdateManifest manifest,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    void ClearLastDownloadedInstaller();

    /// <summary>
    /// Reclaims installer directories and logs left behind by updates that
    /// completed in a previous process (the direct service's download state
    /// is per-session). No-op on channels that never download installers.
    /// </summary>
    void PruneStaleInstallerDirectories();

    AppUpdateInstallResult StartInstallerHelper(
        string installerPath,
        bool silent = true,
        string? appCultureName = null);
}
