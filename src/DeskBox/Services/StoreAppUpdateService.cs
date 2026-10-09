using System.Runtime.InteropServices;
using DeskBox.Models;
using Windows.Services.Store;
using WinRT.Interop;

namespace DeskBox.Services;

public sealed class StoreAppUpdateService : IAppUpdateService
{
    public const string StoreUpdateUri = "ms-windows-store://downloadsandupdates";
    private const string StoreNotPackagedError = "STORE_NOT_PACKAGED";
    private const string StoreCanceledError = "STORE_CANCELED";
    private const string StoreUnavailableError = "STORE_UNAVAILABLE";
    private const string StoreInstallFailedError = "STORE_INSTALL_FAILED";

    private IReadOnlyList<StorePackageUpdate> _pendingUpdates = [];

    public AppUpdateCheckResult? LastCheckResult { get; private set; }
    public DateTime? LastCheckTimeUtc { get; private set; }
    public event Action<AppUpdateCheckResult>? CheckCompleted;
    public string ManifestUrl => string.Empty;
    public AppUpdateDeliveryKind DeliveryKind => AppUpdateDeliveryKind.MicrosoftStore;

    public Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        return CheckForUpdatesAsync(AppUpdateService.GetCurrentAppVersion(), cancellationToken);
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!AppDistributionService.Current.IsPackaged)
        {
            return SetLastCheckResult(new AppUpdateCheckResult(
                AppUpdateCheckStatus.Failed,
                currentVersion,
                errorMessage: StoreNotPackagedError));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storeContext = GetStoreContext();
            var updates = await storeContext.GetAppAndOptionalStorePackageUpdatesAsync()
                .AsTask(cancellationToken);

            _pendingUpdates = updates?.ToList() ?? [];
            if (_pendingUpdates.Count == 0)
            {
                return SetLastCheckResult(new AppUpdateCheckResult(
                    AppUpdateCheckStatus.UpToDate,
                    currentVersion));
            }

            var manifest = new AppUpdateManifest
            {
                SchemaVersion = 1,
                Channel = "store",
                Version = GetDisplayVersion(_pendingUpdates),
                DownloadUrl = StoreUpdateUri,
                Summary =
                {
                    ["zh-CN"] = "Microsoft Store 中有可用更新，可由商店完成下载和安装。",
                    ["zh-TW"] = "Microsoft Store 中有可用更新，可由商店完成下載與安裝。",
                    ["en-US"] = "An update is available in Microsoft Store. The Store will handle download and installation.",
                    ["hi-IN"] = "Microsoft Store में अपडेट उपलब्ध है। Microsoft Store इसे डाउनलोड और इंस्टॉल करेगा।",
                    ["es-ES"] = "Hay una actualización disponible en Microsoft Store. Microsoft Store se encargará de descargarla e instalarla.",
                    ["fr-FR"] = "Une mise à jour est disponible dans le Microsoft Store. Le Microsoft Store se chargera du téléchargement et de l’installation.",
                    ["ar-SA"] = "يتوفر تحديث في Microsoft Store. سيتولى المتجر تنزيله وتثبيته.",
                    ["bn-BD"] = "Microsoft Store-এ একটি আপডেট পাওয়া যাচ্ছে। Microsoft Store এটি ডাউনলোড ও ইনস্টল করবে।",
                    ["ru-RU"] = "В Microsoft Store доступно обновление. Microsoft Store выполнит загрузку и установку.",
                    ["ja-JP"] = "Microsoft Store でアップデートが利用可能です。ストアがダウンロードとインストールを行います。",
                    ["de-DE"] = "Ein Update ist im Microsoft Store verfügbar. Der Store übernimmt Download und Installation.",
                    ["pt-BR"] = "Há uma atualização disponível na Microsoft Store. A Store fará o download e a instalação."
                }
            };

            return SetLastCheckResult(new AppUpdateCheckResult(
                AppUpdateCheckStatus.UpdateAvailable,
                currentVersion,
                manifest));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TaskCanceledException)
        {
            return SetLastCheckResult(new AppUpdateCheckResult(
                AppUpdateCheckStatus.Failed,
                currentVersion,
                errorMessage: $"{StoreUnavailableError}:{ex.Message}"));
        }
    }

    public async Task<AppUpdateDownloadResult> DownloadUpdateAsync(
        AppUpdateManifest manifest,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!AppDistributionService.Current.IsPackaged)
        {
            return AppUpdateDownloadResult.Failed(
                AppUpdateDownloadFailureKind.Network,
                StoreNotPackagedError);
        }

        try
        {
            if (_pendingUpdates.Count == 0)
            {
                var checkResult = await CheckForUpdatesAsync(cancellationToken);
                if (!checkResult.IsUpdateAvailable || _pendingUpdates.Count == 0)
                {
                    return AppUpdateDownloadResult.Failed(
                        AppUpdateDownloadFailureKind.InvalidManifest,
                        StoreUnavailableError);
                }
            }

            var storeContext = GetStoreContext();
            var result = await storeContext
                .RequestDownloadAndInstallStorePackageUpdatesAsync(_pendingUpdates)
                .AsTask(
                    cancellationToken,
                    new Progress<StorePackageUpdateStatus>(status =>
                    {
                        double normalizedProgress = Math.Clamp(status.PackageDownloadProgress, 0, 1);
                        progress?.Report(new AppUpdateDownloadProgress(
                            (long)Math.Round(normalizedProgress * 100d, MidpointRounding.AwayFromZero),
                            100));
                    }));

            string overallState = result.OverallState.ToString();
            if (string.Equals(overallState, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _pendingUpdates = [];
                progress?.Report(new AppUpdateDownloadProgress(100, 100));
                return AppUpdateDownloadResult.Completed(StoreUpdateUri);
            }

            if (string.Equals(overallState, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                return AppUpdateDownloadResult.Failed(
                    AppUpdateDownloadFailureKind.Network,
                    StoreCanceledError);
            }

            return AppUpdateDownloadResult.Failed(
                AppUpdateDownloadFailureKind.Network,
                $"{StoreInstallFailedError}:{overallState}");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or TaskCanceledException)
        {
            return AppUpdateDownloadResult.Failed(
                AppUpdateDownloadFailureKind.Network,
                $"{StoreUnavailableError}:{ex.Message}");
        }
    }

    public AppUpdateInstallResult StartInstallerHelper(string installerPath, bool silent = true)
    {
        return AppUpdateInstallResult.Failed(StoreInstallFailedError);
    }

    private AppUpdateCheckResult SetLastCheckResult(AppUpdateCheckResult result)
    {
        LastCheckResult = result;
        LastCheckTimeUtc = DateTime.UtcNow;
        CheckCompleted?.Invoke(result);
        return result;
    }

    private static StoreContext GetStoreContext()
    {
        var storeContext = StoreContext.GetDefault();
        IntPtr windowHandle = GetSettingsWindowHandle();
        if (windowHandle != IntPtr.Zero)
        {
            InitializeWithWindow.Initialize(storeContext, windowHandle);
        }

        return storeContext;
    }

    private static IntPtr GetSettingsWindowHandle()
    {
        try
        {
            return global::DeskBox.App.Current.SettingsWindowInstance is
                { IsVisibleToUser: true } window
                ? WindowNative.GetWindowHandle(window)
                : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string GetDisplayVersion(IReadOnlyList<StorePackageUpdate> updates)
    {
        foreach (var update in updates)
        {
            try
            {
                var version = update.Package.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
            }
        }

        return "Microsoft Store";
    }
}
