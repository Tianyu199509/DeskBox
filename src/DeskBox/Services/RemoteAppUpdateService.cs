using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Settings-host update service over the IPC pipe (batch 3c-3a): the whole
/// update state machine stays in the main process; the host proxies the
/// commands and mirrors the three events via pushed notifications. The
/// IProgress argument of DownloadUpdateAsync is ignored — the pushed
/// DownloadProgressChanged events carry the same ticks the view models
/// already subscribe to.
/// </summary>
public sealed class RemoteAppUpdateService : IAppUpdateService
{
    private readonly SettingsIpcClient _client;

    private SettingsIpcUpdateState? _cachedState;

    public RemoteAppUpdateService(SettingsIpcClient client)
    {
        _client = client;
        _client.EventReceived += OnEventReceived;
    }

    /// <summary>
    /// Marks the cached state dirty (batch R4): the About page reads the
    /// state properties several times per refresh — without a cache each
    /// getter was a synchronous pipe round trip on the UI thread.
    /// </summary>
    public void RefreshState() => _cachedState = null;

    public event Action<AppUpdateCheckResult>? CheckCompleted;

    public event Action<AppUpdateDownloadProgress>? DownloadProgressChanged;

    public event Action<AppUpdateDownloadResult>? DownloadCompleted;

    private void OnEventReceived(string method, string? payload)
    {
        // Any update lifecycle event invalidates the cached snapshot.
        _cachedState = null;
        switch (method)
        {
            case SettingsIpcMethods.UpdateCheckCompleted:
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateCheckResult)
                    is { } check)
                {
                    CheckCompleted?.Invoke(check);
                }

                break;
            case SettingsIpcMethods.UpdateDownloadProgress:
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateDownloadProgress)
                    is { } progress)
                {
                    DownloadProgressChanged?.Invoke(progress);
                }

                break;
            case SettingsIpcMethods.UpdateDownloadCompleted:
                if (SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateDownloadResult)
                    is { } download)
                {
                    DownloadCompleted?.Invoke(download);
                }

                break;
        }
    }

    public AppUpdateCheckResult? LastCheckResult => ReadState().CheckResultJson is { } json
        ? SettingsIpcPayload.Decode(json, SettingsIpcJsonContext.Default.AppUpdateCheckResult)
        : null;

    public DateTime? LastCheckTimeUtc
    {
        get
        {
            long ticks = ReadState().LastCheckTicks;
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    public string ManifestUrl => ReadState().ManifestUrl;

    public AppUpdateDeliveryKind DeliveryKind =>
        (AppUpdateDeliveryKind)ReadState().DeliveryKind;

    public string? LastDownloadedInstallerPath => ReadState().LastDownloadedInstallerPath;

    public bool IsDownloadInProgress => ReadState().DownloadInProgress;

    public Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        CheckCoreAsync(null, cancellationToken);

    public Task<AppUpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default) =>
        CheckCoreAsync(currentVersion, cancellationToken);

    private async Task<AppUpdateCheckResult> CheckCoreAsync(string? currentVersion, CancellationToken cancellationToken)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.UpdateCheck,
            SettingsIpcPayload.Encode(
                new SettingsIpcUpdateCheck(currentVersion),
                SettingsIpcJsonContext.Default.SettingsIpcUpdateCheck),
            cancellationToken);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateCheckResult)
                is not { } result)
        {
            throw new InvalidOperationException(error ?? "update check failed");
        }

        return result;
    }

    public async Task<AppUpdateDownloadResult> DownloadUpdateAsync(
        AppUpdateManifest manifest,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.UpdateDownload,
            SettingsIpcPayload.Encode(manifest, SettingsIpcJsonContext.Default.AppUpdateManifest),
            cancellationToken);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateDownloadResult)
                is not { } result)
        {
            throw new InvalidOperationException(error ?? "update download failed");
        }

        return result;
    }

    public void ClearLastDownloadedInstaller()
    {
        _client.RequestAsync(SettingsIpcMethods.UpdateClearInstaller)
            .GetAwaiter().GetResult();
        _cachedState = null;
    }

    public void PruneStaleInstallerDirectories()
    {
        _client.RequestAsync(SettingsIpcMethods.UpdatePruneStale)
            .GetAwaiter().GetResult();
        _cachedState = null;
    }

    public AppUpdateInstallResult StartInstallerHelper(
        string installerPath,
        bool silent = true,
        string? appCultureName = null)
    {
        (bool ok, string? payload, string? error) = _client.RequestAsync(
                SettingsIpcMethods.UpdateStartInstaller,
                SettingsIpcPayload.Encode(
                    new SettingsIpcStartInstaller(installerPath, silent, appCultureName),
                    SettingsIpcJsonContext.Default.SettingsIpcStartInstaller))
            .GetAwaiter().GetResult();
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateInstallResult)
                is not { } result)
        {
            throw new InvalidOperationException(error ?? "installer start failed");
        }

        return result;
    }

    private SettingsIpcUpdateState ReadState()
    {
        if (_cachedState is { } cached)
        {
            return cached;
        }

        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.UpdateState)
            .GetAwaiter().GetResult();
        _cachedState = SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcUpdateState)
            ?? new SettingsIpcUpdateState(null, 0, string.Empty, 0, null, false);
        return _cachedState;
    }
}
