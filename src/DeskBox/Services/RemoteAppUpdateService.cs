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
    private readonly bool _useInProcessStoreService;
    private IAppUpdateService? _storeUpdateService;

    private SettingsIpcUpdateState? _cachedState;

    public RemoteAppUpdateService(
        SettingsIpcClient client,
        IAppUpdateService? inProcessStoreUpdate = null)
    {
        _client = client;
        _useInProcessStoreService =
            inProcessStoreUpdate is not null ||
            AppDistributionService.Current.IsMicrosoftStore ||
            AppDistributionService.Current.IsPackaged;
        if (inProcessStoreUpdate is not null)
        {
            _storeUpdateService = AttachStoreService(inProcessStoreUpdate);
        }

        _client.EventReceived += OnEventReceived;
        if (!_useInProcessStoreService)
        {
            _ = PrimeStateAsync();
        }
    }

    private IAppUpdateService GetStoreService() =>
        _storeUpdateService ??= AttachStoreService(new StoreAppUpdateService());

    private IAppUpdateService AttachStoreService(IAppUpdateService service)
    {
        service.CheckCompleted += result => CheckCompleted?.Invoke(result);
        service.DownloadProgressChanged += progress => DownloadProgressChanged?.Invoke(progress);
        service.DownloadCompleted += result => DownloadCompleted?.Invoke(result);
        return service;
    }

    private async Task PrimeStateAsync()
    {
        try
        {
            (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.UpdateState)
                .ConfigureAwait(false);
            if (ok &&
                _cachedState is null &&
                SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcUpdateState) is { } state)
            {
                _cachedState = state;
            }
        }
        catch
        {
            // Best-effort warm-up; synchronous ReadState() remains the fallback.
        }
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

    public AppUpdateCheckResult? LastCheckResult => _useInProcessStoreService
        ? GetStoreService().LastCheckResult
        : ReadState().CheckResultJson is { } json
            ? SettingsIpcPayload.Decode(json, SettingsIpcJsonContext.Default.AppUpdateCheckResult)
            : null;

    public DateTime? LastCheckTimeUtc
    {
        get
        {
            if (_useInProcessStoreService)
            {
                return GetStoreService().LastCheckTimeUtc;
            }

            long ticks = ReadState().LastCheckTicks;
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    public string ManifestUrl => _useInProcessStoreService
        ? GetStoreService().ManifestUrl
        : ReadState().ManifestUrl;

    public AppUpdateDeliveryKind DeliveryKind => _useInProcessStoreService
        ? GetStoreService().DeliveryKind
        : (AppUpdateDeliveryKind)ReadState().DeliveryKind;

    public string? LastDownloadedInstallerPath => _useInProcessStoreService
        ? GetStoreService().LastDownloadedInstallerPath
        : ReadState().LastDownloadedInstallerPath;

    public bool IsDownloadInProgress => _useInProcessStoreService
        ? GetStoreService().IsDownloadInProgress
        : ReadState().DownloadInProgress;

    public Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        _useInProcessStoreService
            ? GetStoreService().CheckForUpdatesAsync(cancellationToken)
            : CheckCoreAsync(null, cancellationToken);

    public Task<AppUpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default) =>
        _useInProcessStoreService
            ? GetStoreService().CheckForUpdatesAsync(currentVersion, cancellationToken)
            : CheckCoreAsync(currentVersion, cancellationToken);

    private async Task<AppUpdateCheckResult> CheckCoreAsync(string? currentVersion, CancellationToken cancellationToken)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.UpdateCheck,
            SettingsIpcPayload.Encode(
                new SettingsIpcUpdateCheck(currentVersion),
                SettingsIpcJsonContext.Default.SettingsIpcUpdateCheck),
            cancellationToken);
        _cachedState = null;
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
        if (_useInProcessStoreService)
        {
            // StoreContext.RequestDownloadAndInstallStorePackageUpdatesAsync
            // requires an in-process foreground HWND via IInitializeWithWindow;
            // run directly inside the settings-host MSIX process so it binds to
            // the live SettingsWindow handle, reuses the check's pending updates,
            // and streams real progress.
            _cachedState = null;
            return await GetStoreService().DownloadUpdateAsync(
                manifest,
                progress,
                cancellationToken);
        }

        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.UpdateDownload,
            SettingsIpcPayload.Encode(manifest, SettingsIpcJsonContext.Default.AppUpdateManifest),
            cancellationToken);
        _cachedState = null;
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppUpdateDownloadResult)
                is not { } result)
        {
            throw new InvalidOperationException(error ?? "update download failed");
        }

        return result;
    }

    // update.clear-installer / update.prune-stale wires removed (deep
    // review R13-2, D19 audit): no host caller ever existed — the main
    // process calls its own service directly, and the interface members
    // went with the wires (the concrete service keeps the methods).

    public AppUpdateInstallResult StartInstallerHelper(
        string installerPath,
        bool silent = true,
        string? appCultureName = null)
    {
        (bool ok, string? payload, string? error) = SettingsIpcShell.RequestSyncSafe(
            _client,
            SettingsIpcMethods.UpdateStartInstaller,
            SettingsIpcPayload.Encode(
                new SettingsIpcStartInstaller(installerPath, silent, appCultureName),
                SettingsIpcJsonContext.Default.SettingsIpcStartInstaller));
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

        (bool ok, string? payload, _) = SettingsIpcShell.RequestSyncSafe(_client, SettingsIpcMethods.UpdateState);
        _cachedState = SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcUpdateState)
            ?? new SettingsIpcUpdateState(null, 0, string.Empty, 0, null, false);
        return _cachedState;
    }
}
