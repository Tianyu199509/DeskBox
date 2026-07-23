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

    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<AppUpdateDownloadResult> DownloadUpdateAsync(
        AppUpdateManifest manifest,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    AppUpdateInstallResult StartInstallerHelper(string installerPath, bool silent = true);
}
