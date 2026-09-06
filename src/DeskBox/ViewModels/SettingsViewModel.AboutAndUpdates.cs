using System.Reflection;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0] ??
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ??
        "1.0.2";
    public string DistributionChannelText => _localizationService.T(IsStoreUpdateDelivery
        ? "Settings.About.Channel.Store"
        : "Settings.About.Channel.Direct");
    public string OpenSourceRepositoryUrl => RepositoryUrl;
    public string OfficialWebsiteDisplayText => OfficialWebsiteUrl.Replace("https://", string.Empty).TrimEnd('/');
    public string OfficialWebsiteLink => OfficialWebsiteUrl;
    public string MicrosoftStoreLink => MicrosoftStoreUrl;
    public string MicrosoftStoreAppLink => MicrosoftStoreAppUrl;
    public string FeedbackEmailAddress => FeedbackEmail;
    public string FeedbackEmailLink => $"mailto:{FeedbackEmail}";
    public string DomesticMirrorDownloadUrl => AppUpdateService.DefaultManualDownloadUrl;
    public Visibility StoreSupportCardVisibility => IsDirectInstallerUpdateDelivery ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Donation QR assets ship only in Direct builds; Store policy forbids
    /// payment QR codes in the package, so every surface that shows one must
    /// collapse under the Store channel.
    /// </summary>
    public Visibility DonationQrCodeVisibility => IsDirectInstallerUpdateDelivery ? Visibility.Visible : Visibility.Collapsed;
    public string OpenSourceRepositoryDisplayText =>
        _localizationService.Format(
            "Settings.About.Developer",
            RepositoryUrl.Replace("https://", string.Empty).Replace("http://", string.Empty).TrimEnd('/'));
    public string AvailableUpdateReleaseNotesUrl => _availableUpdateManifest?.ReleaseNotesUrl ?? string.Empty;
    /// <summary>
    /// Latest manifest returned by a successful check. This is intentionally
    /// separate from the available-update manifest so release notes remain
    /// accessible when the current build is already up to date.
    /// </summary>
    public AppUpdateManifest? LatestUpdateManifest => _availableUpdateManifest ?? _latestUpdateManifest;
    public bool CanViewReleaseNotes => LatestUpdateManifest?.HasReleaseNotesOrUrl == true;
    public Visibility ReleaseNotesButtonVisibility =>
        CanViewReleaseNotes ? Visibility.Visible : Visibility.Collapsed;
    public string ViewReleaseNotesButtonText => _localizationService.T("Settings.Update.ViewReleaseNotes");
    public string ManualUpdateDownloadUrl => GetManualUpdateDownloadUrl(_availableUpdateManifest);
    public Visibility UpdateAutoCheckVisibility => Visibility.Visible;
    public Visibility UpdateProgressVisibility => IsDownloadingUpdate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateProgressTextVisibility => IsDownloadingUpdate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateReleaseNotesVisibility =>
        string.IsNullOrWhiteSpace(AvailableUpdateReleaseNotesUrl) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ManualUpdateFallbackVisibility => CanOpenManualUpdateDownload ? Visibility.Visible : Visibility.Collapsed;
    // Aliases for XAML binding compatibility
    public Visibility UpdateFallbackVisibility => ManualUpdateFallbackVisibility;
    public bool CanOpenUpdateFallback => CanOpenManualUpdateDownload;
    public Visibility InstallUpdateButtonVisibility => IsDirectInstallerUpdateDelivery ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateReminderBadgeVisibility =>
        _availableUpdateManifest is not null ? Visibility.Visible : Visibility.Collapsed;
    public bool CanCheckForUpdates => !IsCheckingForUpdates && !IsDownloadingUpdate;
    public bool CanDownloadUpdate => _availableUpdateManifest is not null && !IsCheckingForUpdates && !IsDownloadingUpdate;
    public bool CanOpenManualUpdateDownload =>
        IsDirectInstallerUpdateDelivery &&
        !string.IsNullOrWhiteSpace(ManualUpdateDownloadUrl) &&
        (_showManualUpdateFallback || HasManifestManualDownloadUrl(_availableUpdateManifest));
    public bool CanInstallUpdate =>
        IsDirectInstallerUpdateDelivery &&
        !IsCheckingForUpdates &&
        !IsDownloadingUpdate &&
        !string.IsNullOrWhiteSpace(_downloadedUpdateInstallerPath) &&
        File.Exists(_downloadedUpdateInstallerPath);
    public string UpdateDownloadActionText => _localizationService.T(IsStoreUpdateDelivery
        ? "Settings.Update.StoreInstall"
        : "Settings.Update.Download");
    public string UpdateFallbackActionText => _localizationService.T("Settings.Update.ManualDownload");
    public bool UpdateProgressIsIndeterminate => IsDownloadingUpdate && _updateTotalBytes is not > 0;
    public string UpdateProgressText => _updateTotalBytes is > 0
        ? $"{FormatByteSize(_updateBytesReceived)} / {FormatByteSize(_updateTotalBytes.Value)} · {Math.Clamp(UpdateProgressValue, 0, 100):0}%"
        : FormatByteSize(_updateBytesReceived);

    // One-click update properties
    public string UpdateSummaryText =>
        GetUpdateSummaryPreview(_availableUpdateManifest?.GetLocalizedSummary(_localizationService.CurrentCultureName));
    public Visibility UpdateSummaryVisibility =>
        _availableUpdateManifest is not null &&
        !IsDownloadingUpdate &&
        !_lastUpdateDownloadFailed &&
        !string.IsNullOrWhiteSpace(UpdateSummaryText)
            ? Visibility.Visible : Visibility.Collapsed;
    public bool HasUpdateAvailable => _availableUpdateManifest is not null;
    public bool IsUpdateDownloaded =>
        !string.IsNullOrWhiteSpace(_downloadedUpdateInstallerPath) &&
        File.Exists(_downloadedUpdateInstallerPath);
    public Visibility UpdateCardVisibility =>
        _availableUpdateManifest is not null || _showManualUpdateFallback
            ? Visibility.Visible : Visibility.Collapsed;
    public string OneClickActionButtonText
    {
        get
        {
            if (IsCheckingForUpdates)
                return _localizationService.T("Settings.Update.Status.Checking");
            if (IsDownloadingUpdate)
                return _localizationService.Format("Settings.Update.Status.Downloading", _availableUpdateManifest?.Version ?? "");
            if (IsUpdateDownloaded)
                return _localizationService.T("Settings.Update.OneClick.Install");
            if (_lastUpdateDownloadFailed)
                return _localizationService.T("Settings.Update.OneClick.Retry");
            if (_availableUpdateManifest is not null)
                return _localizationService.Format("Settings.Update.OneClick.UpdateTo", _availableUpdateManifest.Version);
            return _localizationService.T("Settings.Update.Check");
        }
    }
    public bool IsOneClickActionEnabled => !IsCheckingForUpdates && !IsDownloadingUpdate;

    private bool IsStoreUpdateDelivery => _appUpdateService.DeliveryKind == AppUpdateDeliveryKind.MicrosoftStore;
    private bool IsDirectInstallerUpdateDelivery => _appUpdateService.DeliveryKind == AppUpdateDeliveryKind.DirectInstaller;

    public void RefreshCachedUpdateState()
    {
        ApplyCachedUpdateResult();
    }

    /// <summary>
    /// One-click update action: check → download → ready to install.
    /// The caller (XAML click handler) is responsible for showing the
    /// install confirmation dialog when <see cref="IsUpdateDownloaded"/> becomes true.
    /// </summary>
    public async Task<AppUpdateDownloadResult?> OneClickUpdateActionAsync()
    {
        if (IsCheckingForUpdates || IsDownloadingUpdate)
        {
            return null;
        }

        // If already downloaded, the caller should handle install confirmation.
        if (IsUpdateDownloaded)
        {
            return null;
        }

        // If update is available but not yet downloaded, start downloading.
        if (_availableUpdateManifest is not null)
        {
            return await DownloadAvailableUpdateAsync();
        }

        // Otherwise, check for updates first.
        await CheckForUpdatesAsync();

        // If check found an update, auto-start download.
        if (_availableUpdateManifest is not null && !IsUpdateDownloaded)
        {
            return await DownloadAvailableUpdateAsync();
        }

        return null;
    }

    public async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates || IsDownloadingUpdate)
        {
            return;
        }

        _updateOperationCts?.Cancel();
        _updateOperationCts = new CancellationTokenSource();
        IsCheckingForUpdates = true;
        UpdateStatusText = _localizationService.T("Settings.Update.Status.Checking");
        UpdateDetailText = _localizationService.T("Settings.Update.Detail.Checking");
        NotifyUpdateActionPropertiesChanged();

        try
        {
            var result = await _appUpdateService.CheckForUpdatesAsync(AppVersion, _updateOperationCts.Token);
            _settingsService.Settings.LastUpdateCheckAt = DateTimeOffset.Now;
            _settingsService.SaveDebounced(notifySubscribers: false);
            ApplyUpdateCheckResult(result);
        }
        finally
        {
            IsCheckingForUpdates = false;
            NotifyUpdateActionPropertiesChanged();
        }
    }

    public async Task<AppUpdateDownloadResult?> DownloadAvailableUpdateAsync()
    {
        if (_availableUpdateManifest is null || IsCheckingForUpdates || IsDownloadingUpdate)
        {
            return null;
        }

        _updateOperationCts?.Cancel();
        _updateOperationCts = new CancellationTokenSource();
        _downloadedUpdateInstallerPath = null;
        _lastUpdateDownloadFailed = false;
        IsDownloadingUpdate = true;
        UpdateProgressValue = 0;
        _updateBytesReceived = 0;
        _updateTotalBytes = null;
        UpdateStatusText = IsStoreUpdateDelivery
            ? _localizationService.T("Settings.Update.Status.StoreInstalling")
            : _localizationService.Format("Settings.Update.Status.Downloading", _availableUpdateManifest.Version);
        UpdateDetailText = IsStoreUpdateDelivery
            ? _localizationService.T("Settings.Update.Detail.StoreInstalling")
            : _localizationService.T("Settings.Update.Detail.Downloading");
        NotifyUpdateActionPropertiesChanged();

        var progress = new Progress<AppUpdateDownloadProgress>(downloadProgress =>
        {
            _updateBytesReceived = downloadProgress.BytesReceived;
            _updateTotalBytes = downloadProgress.TotalBytes;
            UpdateProgressValue = downloadProgress.Percent;
            OnPropertyChanged(nameof(UpdateProgressText));
            OnPropertyChanged(nameof(UpdateProgressIsIndeterminate));
        });

        try
        {
            var result = await _appUpdateService.DownloadUpdateAsync(_availableUpdateManifest, progress, _updateOperationCts.Token);
            if (result.Success && IsStoreUpdateDelivery)
            {
                _availableUpdateManifest = null;
                _downloadedUpdateInstallerPath = null;
                _showManualUpdateFallback = false;
                UpdateProgressValue = 100;
                UpdateStatusText = _localizationService.T("Settings.Update.Status.StoreInstallComplete");
                UpdateDetailText = _localizationService.T("Settings.Update.Detail.StoreInstallComplete");
                return result;
            }

            if (result.Success && !string.IsNullOrWhiteSpace(result.FilePath))
            {
                _downloadedUpdateInstallerPath = result.FilePath;
                _showManualUpdateFallback = false;
                UpdateProgressValue = 100;
                UpdateStatusText = _localizationService.Format("Settings.Update.Status.Downloaded", _availableUpdateManifest.Version);
                UpdateDetailText = _localizationService.T("Settings.Update.Detail.Downloaded");
                return result;
            }

            ApplyDownloadFailure(result);
            return result;
        }
        finally
        {
            IsDownloadingUpdate = false;
            NotifyUpdateActionPropertiesChanged();
        }
    }

    public AppUpdateInstallResult StartDownloadedUpdateInstall()
    {
        if (!CanInstallUpdate || string.IsNullOrWhiteSpace(_downloadedUpdateInstallerPath))
        {
            return AppUpdateInstallResult.Failed(_localizationService.T("Settings.Update.Detail.DownloadMissing"));
        }

        var result = _appUpdateService.StartInstallerHelper(_downloadedUpdateInstallerPath);
        if (result.Success)
        {
            UpdateStatusText = _localizationService.T("Settings.Update.Status.Installing");
            UpdateDetailText = _localizationService.T("Settings.Update.Detail.Installing");
            NotifyUpdateActionPropertiesChanged();
        }

        return result;
    }

    private void ApplyCachedUpdateResult()
    {
        if (_appUpdateService.LastCheckResult is { } result)
        {
            ApplyUpdateCheckResult(result);
        }
    }

    private void ApplyUpdateCheckResult(AppUpdateCheckResult result)
    {
        if (result.Manifest is not null && AppUpdateService.IsManifestUsable(result.Manifest))
        {
            _latestUpdateManifest = result.Manifest;
        }

        if (result.IsUpdateAvailable && result.Manifest is not null)
        {
            _availableUpdateManifest = result.Manifest;
            _downloadedUpdateInstallerPath = null;
            _showManualUpdateFallback = false;
            _lastUpdateDownloadFailed = false;
            UpdateStatusText = IsStoreUpdateDelivery
                ? _localizationService.T("Settings.Update.Status.StoreAvailable")
                : _localizationService.Format("Settings.Update.Status.Available", result.Manifest.Version);
            UpdateDetailText = _localizationService.T(IsStoreUpdateDelivery
                ? "Settings.Update.Detail.StoreAvailable"
                : "Settings.Update.Detail.Available");
        }
        else if (result.Status == AppUpdateCheckStatus.UpToDate)
        {
            _availableUpdateManifest = null;
            _downloadedUpdateInstallerPath = null;
            _showManualUpdateFallback = false;
            _lastUpdateDownloadFailed = false;
            UpdateStatusText = _localizationService.T("Settings.Update.Status.UpToDate");
            UpdateDetailText = BuildUpToDateDetailText(result);
        }
        else if (result.Status == AppUpdateCheckStatus.InvalidManifest)
        {
            _availableUpdateManifest = null;
            _downloadedUpdateInstallerPath = null;
            _showManualUpdateFallback = IsDirectInstallerUpdateDelivery;
            _lastUpdateDownloadFailed = false;
            UpdateStatusText = _localizationService.T("Settings.Update.Status.Failed");
            UpdateDetailText = _localizationService.T("Settings.Update.Detail.InvalidManifest");
        }
        else
        {
            _availableUpdateManifest = null;
            _downloadedUpdateInstallerPath = null;
            _showManualUpdateFallback = IsDirectInstallerUpdateDelivery;
            _lastUpdateDownloadFailed = false;
            UpdateStatusText = _localizationService.T("Settings.Update.Status.Failed");
            UpdateDetailText = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? _localizationService.T("Settings.Update.Detail.Failed")
                : GetFriendlyUpdateErrorText(result.ErrorMessage);
        }

        NotifyUpdateActionPropertiesChanged();
    }

    private void ApplyDownloadFailure(AppUpdateDownloadResult result)
    {
        _showManualUpdateFallback = IsDirectInstallerUpdateDelivery;
        _lastUpdateDownloadFailed = result.FailureKind != AppUpdateDownloadFailureKind.Cancelled;
        UpdateStatusText = _localizationService.T("Settings.Update.Status.Failed");
        UpdateDetailText = result.FailureKind switch
        {
            AppUpdateDownloadFailureKind.Cancelled => _localizationService.T("Settings.Update.Detail.DownloadCancelled"),
            AppUpdateDownloadFailureKind.HashMissing => _localizationService.T("Settings.Update.Detail.HashMissing"),
            AppUpdateDownloadFailureKind.HashMismatch => _localizationService.T("Settings.Update.Detail.HashMismatch"),
            AppUpdateDownloadFailureKind.InvalidManifest => _localizationService.T("Settings.Update.Detail.InvalidManifest"),
            _ when !string.IsNullOrWhiteSpace(result.ErrorMessage) =>
                GetFriendlyUpdateErrorText(result.ErrorMessage),
            _ => _localizationService.T("Settings.Update.Detail.Failed")
        };
    }

    private string GetFriendlyUpdateErrorText(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return _localizationService.T("Settings.Update.Detail.Failed");
        }

        if (errorMessage.Contains("STORE_NOT_PACKAGED", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.StoreNotPackaged");
        }

        if (errorMessage.Contains("STORE_CANCELED", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.StoreCanceled");
        }

        if (errorMessage.Contains("STORE_INSTALL_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.StoreInstallFailed");
        }

        if (errorMessage.Contains("STORE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.StoreUnavailable");
        }

        if (errorMessage.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.ManifestNotFound");
        }

        if (errorMessage.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.AccessDenied");
        }

        if (errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("TaskCanceledException", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.Timeout");
        }

        if (errorMessage.Contains("NameResolution", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("remote name could not be resolved", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("无法解析", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("网络", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Settings.Update.Detail.NetworkUnavailable");
        }

        return _localizationService.T("Settings.Update.Detail.Failed");
    }

    internal static string GetManualUpdateDownloadUrl(AppUpdateManifest? manifest)
    {
        // This action is labelled "Official download". Older manifests may
        // carry a direct cloud-drive link; let the website offer those mirrors.
        return AppUpdateService.DefaultManualDownloadUrl;
    }

    internal static string GetUpdateSummaryPreview(string? summary)
    {
        return string.Join(" ", SimpleMarkdownRenderer.Parse(summary)
            .Select(block => string.Concat(block.Inlines.Select(inline => inline.Text)))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static bool HasManifestManualDownloadUrl(AppUpdateManifest? manifest)
    {
        return AppUpdateManifest.IsSafeWebUrl(manifest?.ManualDownloadUrl) ||
            AppUpdateManifest.IsSafeWebUrl(manifest?.MirrorUrl);
    }

    private static string FormatByteSize(long bytes)
    {
        const double KiloByte = 1024d;
        const double MegaByte = KiloByte * 1024d;
        const double GigaByte = MegaByte * 1024d;

        return bytes switch
        {
            >= (long)GigaByte => $"{bytes / GigaByte:0.0} GB",
            >= (long)MegaByte => $"{bytes / MegaByte:0.0} MB",
            >= (long)KiloByte => $"{bytes / KiloByte:0.0} KB",
            _ => $"{Math.Max(0, bytes)} B"
        };
    }

    private void NotifyUpdateActionPropertiesChanged()
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanInstallUpdate));
        OnPropertyChanged(nameof(InstallUpdateButtonVisibility));
        OnPropertyChanged(nameof(UpdateDownloadActionText));
        OnPropertyChanged(nameof(UpdateProgressVisibility));
        OnPropertyChanged(nameof(UpdateProgressTextVisibility));
        OnPropertyChanged(nameof(UpdateProgressIsIndeterminate));
        OnPropertyChanged(nameof(UpdateReleaseNotesVisibility));
        OnPropertyChanged(nameof(AvailableUpdateReleaseNotesUrl));
        OnPropertyChanged(nameof(LatestUpdateManifest));
        OnPropertyChanged(nameof(CanViewReleaseNotes));
        OnPropertyChanged(nameof(ReleaseNotesButtonVisibility));
        OnPropertyChanged(nameof(ViewReleaseNotesButtonText));
        OnPropertyChanged(nameof(ManualUpdateDownloadUrl));
        OnPropertyChanged(nameof(CanOpenManualUpdateDownload));
        OnPropertyChanged(nameof(ManualUpdateFallbackVisibility));
        OnPropertyChanged(nameof(CanOpenUpdateFallback));
        OnPropertyChanged(nameof(UpdateFallbackVisibility));
        OnPropertyChanged(nameof(UpdateReminderBadgeVisibility));
        OnPropertyChanged(nameof(UpdateProgressText));
        // One-click update properties
        OnPropertyChanged(nameof(UpdateSummaryText));
        OnPropertyChanged(nameof(UpdateSummaryVisibility));
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(IsUpdateDownloaded));
        OnPropertyChanged(nameof(UpdateCardVisibility));
        OnPropertyChanged(nameof(OneClickActionButtonText));
        OnPropertyChanged(nameof(IsOneClickActionEnabled));
    }

    private string GetReadyUpdateDetailText()
    {
        return _localizationService.T(IsStoreUpdateDelivery
            ? "Settings.Update.Detail.StoreReady"
            : "Settings.Update.Detail.Ready");
    }

    /// <summary>
    /// Builds the detail line shown under "当前已是最新版本". Replaces the
    /// old redundant "暂时没有发现可用的新版本" with metadata that actually
    /// carries information: current version, last check time, distribution channel.
    /// </summary>
    private string BuildUpToDateDetailText(AppUpdateCheckResult result)
    {
        string version = string.IsNullOrWhiteSpace(result.CurrentVersion) ? AppVersion : result.CurrentVersion;
        string checkedAt = FormatCheckTime(_appUpdateService.LastCheckTimeUtc);
        string channel = DistributionChannelText;
        return _localizationService.Format("Settings.Update.Detail.CurrentVersion", version, checkedAt, channel);
    }

    private string FormatCheckTime(DateTime? utc)
    {
        if (utc is null)
        {
            return "—";
        }

        DateTime local = utc.Value.ToLocalTime();
        DateTime now = DateTime.Now;
        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm");
        }

        if (local.Year == now.Year)
        {
            return local.ToString("MM-dd HH:mm");
        }

        return local.ToString("yyyy-MM-dd HH:mm");
    }
}
