using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Orchestrates cloud backups (roadmap §10): builds a domain-scoped archive
/// via <see cref="DeskBoxDataBackupService.ExportScopedBackupAsync"/>,
/// resolves the provider secret from <see cref="ICredentialStore"/>, moves
/// the zip through <see cref="ICloudBackupTransport"/>, then applies remote
/// retention. Scheduled runs ride the existing 1-minute backup timer; the
/// service itself is transport-agnostic so the official cloud later plugs
/// in as another <see cref="ICloudBackupTransport"/> implementation.
/// </summary>
internal sealed class CloudBackupService
{
    internal const string SnapshotFilePrefix = "DeskBox-CloudBackup-";
    internal const string SnapshotFileExtension = ".zip";

    private readonly DeskBoxDataBackupService _backupService;
    private readonly SettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly Func<CloudBackupOptions, string?, ICloudBackupTransport> _transportFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CloudBackupOptions _options = CloudBackupSettingsPolicy.GetOptions(new AppSettings());
    private bool _optionsInitialized;

    internal CloudBackupService(
        DeskBoxDataBackupService backupService,
        SettingsService settingsService,
        ICredentialStore credentialStore,
        Func<CloudBackupOptions, string?, ICloudBackupTransport>? transportFactory = null)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _transportFactory = transportFactory ?? CreateTransport;
    }

    internal CloudBackupOptions Options => _options;

    internal void UpdateOptions(CloudBackupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A success recorded against a different destination must not
        // suppress the first backup to the NEW destination: switching
        // provider/endpoint/folder/account/scope invalidates LastSuccess.
        // The comparison runs regardless of IsConfigured — a configured →
        // unconfigured → configured detour must not smuggle the old
        // success through either. Only a real destination switch counts:
        // the first options push after startup keeps the persisted value,
        // and an already-empty timestamp needs no invalidation write.
        if (_optionsInitialized &&
            DestinationIdentityChanged(_options, options) &&
            options.LastSuccessUtc != DateTimeOffset.MinValue)
        {
            options = options with { LastSuccessUtc = DateTimeOffset.MinValue };
            _settingsService.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks = 0;
            _settingsService.SaveDebounced();
            App.Log("[CloudBackup] Backup destination changed; last-success invalidated.");
        }

        _options = options;
        _optionsInitialized = true;
    }

    /// <summary>Fields defining which backup destination a success belongs to.</summary>
    private static bool DestinationIdentityChanged(CloudBackupOptions previous, CloudBackupOptions next) =>
        !string.Equals(previous.Provider, next.Provider, StringComparison.Ordinal) ||
        !string.Equals(previous.ServerUrl, next.ServerUrl, StringComparison.Ordinal) ||
        !string.Equals(previous.RemotePath, next.RemotePath, StringComparison.Ordinal) ||
        !string.Equals(previous.Username, next.Username, StringComparison.Ordinal) ||
        previous.Scope != next.Scope;

    /// <summary>
    /// Timer-tick entry point: uploads only when configured and the interval
    /// has elapsed. Never throws — a transient network failure must not take
    /// down the timer path.
    /// </summary>
    internal async Task RunScheduledIfDueAsync(CancellationToken cancellationToken = default)
    {
        // Skip rather than queue: a manual run or a still-uploading
        // scheduled run must not pile up a second upload behind it —
        // especially one holding options the user already changed.
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            CloudBackupOptions options = _options;   // re-read inside the gate
            if (!options.IsConfigured)
            {
                return;
            }

            if (DateTimeOffset.UtcNow - options.LastSuccessUtc < TimeSpan.FromMinutes(options.IntervalMinutes))
            {
                return;
            }

            await RunBackupCoreAsync(options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[CloudBackup] Scheduled upload failed: {ex}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Uploads one scoped snapshot now. Throws on failure — the manual path
    /// (PR-3 "backup now") surfaces the error to the user.
    /// </summary>
    internal async Task<CloudBackupRunResult> RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CloudBackupOptions options = _options;   // fresh read inside the gate
            if (!options.IsConfigured)
            {
                return CloudBackupRunResult.NotConfigured;
            }

            return await RunBackupCoreAsync(options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gate-held upload body shared by the scheduled and manual paths.</summary>
    private async Task<CloudBackupRunResult> RunBackupCoreAsync(
        CloudBackupOptions options,
        CancellationToken cancellationToken)
    {
        string? stagingDirectory = null;
        try
        {
            ICloudBackupTransport transport = await CreateConfiguredTransportAsync(options, cancellationToken);
            await transport.EnsureDirectoryAsync(options.RemotePath, cancellationToken);

            stagingDirectory = Path.Combine(
                Path.GetTempPath(),
                $"deskbox-cloud-upload-{Guid.NewGuid():N}");
            string localArchivePath = await _backupService.ExportScopedBackupAsync(
                stagingDirectory,
                options.Scope,
                ct => Task.FromResult<byte[]?>(
                    WidgetStyleBackupProjection.Serialize(_settingsService.Settings)),
                cancellationToken);

            string remoteFilePath = $"{options.RemotePath}/{BuildRemoteSnapshotName()}";
            await using (FileStream content = File.OpenRead(localArchivePath))
            {
                await transport.UploadAsync(remoteFilePath, content, cancellationToken);
            }

            int pruned = await ApplyRetentionAsync(transport, options, cancellationToken);
            await MarkSuccessAsync(options);
            App.Log($"[CloudBackup] Uploaded '{remoteFilePath}' (pruned {pruned} old snapshots).");
            return new CloudBackupRunResult(Uploaded: true, remoteFilePath, pruned);
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }
    }

    /// <summary>
    /// Stores the provider secret for the currently configured account in
    /// the OS credential store. The key is scoped by provider+origin+username,
    /// so changing the endpoint or account writes a fresh entry rather than
    /// silently reusing the old one — and stale keys under the same provider
    /// are removed so secrets never linger in the vault.
    /// </summary>
    internal async Task SaveCredentialAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        string key = CloudBackupSettingsPolicy.CredentialKey(_options);
        await _credentialStore.SetSecretAsync(key, secret, cancellationToken);

        string prefix = $"{_options.Provider}:";
        foreach (string stale in await _credentialStore.ListKeysAsync(cancellationToken))
        {
            if (stale.StartsWith(prefix, StringComparison.Ordinal) &&
                !string.Equals(stale, key, StringComparison.Ordinal))
            {
                await _credentialStore.RemoveSecretAsync(stale, cancellationToken);
            }
        }
    }

    /// <summary>Whether a secret already exists for the configured account.</summary>
    internal async Task<bool> HasCredentialAsync(CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = _options;
        if (!options.IsConfigured)
        {
            return false;
        }

        return await _credentialStore.GetSecretAsync(
            CloudBackupSettingsPolicy.CredentialKey(options),
            cancellationToken) is not null;
    }

    /// <summary>
    /// Verifies the configured endpoint; for the PR-3 "test connection"
    /// button. <paramref name="secretOverride"/> lets the caller probe with
    /// a just-typed password before it is saved to the vault.
    /// </summary>
    internal async Task ProbeConnectionAsync(
        string? secretOverride = null,
        CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = _options;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Cloud backup is not configured.");
        }

        ICloudBackupTransport transport = string.IsNullOrEmpty(secretOverride)
            ? await CreateConfiguredTransportAsync(options, cancellationToken)
            : _transportFactory(options, secretOverride);
        await transport.ProbeAsync(cancellationToken);
    }

    /// <summary>Remote snapshot inventory for the PR-3 restore picker, newest first.</summary>
    internal async Task<IReadOnlyList<CloudBackupRemoteEntry>> ListRemoteSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = _options;
        if (!options.IsConfigured)
        {
            return Array.Empty<CloudBackupRemoteEntry>();
        }

        ICloudBackupTransport transport = await CreateConfiguredTransportAsync(options, cancellationToken);
        IReadOnlyList<CloudBackupRemoteEntry> entries = await transport.ListAsync(options.RemotePath, cancellationToken);
        return entries
            .Where(e => !e.IsCollection && IsSafeSnapshotBasename(e.Name))
            .OrderByDescending(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Downloads one remote snapshot into a local directory. The returned
    /// file feeds straight into
    /// <see cref="DeskBoxDataBackupService.PrepareScopedRestoreAsync"/>.
    /// </summary>
    internal async Task<string> DownloadSnapshotAsync(
        string remoteFileName,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = _options;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Cloud backup is not configured.");
        }

        // Basename only — never let a remote-supplied name escape the
        // destination directory or reach outside RemotePath.
        if (!IsSafeSnapshotBasename(remoteFileName))
        {
            throw new ArgumentException("Invalid remote snapshot name.", nameof(remoteFileName));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ICloudBackupTransport transport = await CreateConfiguredTransportAsync(options, cancellationToken);
        Directory.CreateDirectory(destinationDirectory);
        string destinationPath = Path.Combine(destinationDirectory, remoteFileName);
        await using (FileStream destination = File.Create(destinationPath))
        {
            await transport.DownloadAsync($"{options.RemotePath}/{remoteFileName}", destination, cancellationToken);
        }

        return destinationPath;
    }

    private async Task<ICloudBackupTransport> CreateConfiguredTransportAsync(
        CloudBackupOptions options,
        CancellationToken cancellationToken)
    {
        string? secret = await _credentialStore.GetSecretAsync(
            CloudBackupSettingsPolicy.CredentialKey(options),
            cancellationToken);
        return _transportFactory(options, secret);
    }

    private static ICloudBackupTransport CreateTransport(CloudBackupOptions options, string? secret) =>
        options.Provider switch
        {
            CloudBackupSettingsPolicy.ProviderWebDav => new WebDavBackupTransport(
                new WebDavBackupTransport.Options(
                    new Uri(options.ServerUrl),
                    options.Username,
                    secret)),
            _ => throw new NotSupportedException(
                $"Cloud backup provider '{options.Provider}' is not supported.")
        };

    /// <summary>
    /// Remote name = UTC timestamp + short device suffix, so two devices
    /// backing up in the same minute can never overwrite each other's
    /// snapshot — and cross-device ordering never depends on a local clock
    /// (legacy local-time names are still parsed on read).
    /// </summary>
    private static string BuildRemoteSnapshotName()
    {
        string deviceSuffix = DeviceIdentity.Id is { Length: >= 8 } id ? id[..8] : "nodevice";
        return $"{SnapshotFilePrefix}{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{deviceSuffix}{SnapshotFileExtension}";
    }

    internal static bool IsSnapshotName(string name) =>
        name.StartsWith(SnapshotFilePrefix, StringComparison.Ordinal) &&
        name.EndsWith(SnapshotFileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A snapshot name that is also safe to join into a remote path —
    /// server-supplied names must never reach Delete/Download carrying
    /// separators or traversal segments.
    /// </summary>
    internal static bool IsSafeSnapshotBasename(string name) =>
        !string.IsNullOrEmpty(name) &&
        IsSnapshotName(name) &&
        !name.Contains('/') &&
        !name.Contains('\\') &&
        !name.Contains("..");

    /// <summary>
    /// Creation timestamp embedded in a remote snapshot name. New format:
    /// <c>DeskBox-CloudBackup-20260918T124500Z-&lt;device8&gt;.zip</c> (UTC).
    /// Legacy names embed local time (<c>20260918-210000</c>) and parse as
    /// UTC on a best-effort basis.
    /// </summary>
    internal static DateTimeOffset? ParseSnapshotTimestamp(string name)
    {
        if (!IsSnapshotName(name))
        {
            return null;
        }

        string stem = name[..^SnapshotFileExtension.Length];
        string[] parts = stem.Split('-');
        if (parts.Length >= 4 &&
            DateTimeOffset.TryParseExact(
                parts[^2],
                "yyyyMMdd'T'HHmmss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset utc))
        {
            return utc;
        }

        if (parts.Length >= 5 &&
            DateTimeOffset.TryParseExact(
                $"{parts[^3]}-{parts[^2]}",
                "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset legacy))
        {
            return legacy;
        }

        // Pre-suffix legacy names end directly in the timestamp.
        if (parts.Length >= 4 &&
            DateTimeOffset.TryParseExact(
                $"{parts[^2]}-{parts[^1]}",
                "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset unsuffixed))
        {
            return unsuffixed;
        }

        return null;
    }

    private async Task<int> ApplyRetentionAsync(
        ICloudBackupTransport transport,
        CloudBackupOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudBackupRemoteEntry> entries = await transport.ListAsync(options.RemotePath, cancellationToken);
        List<CloudBackupRemoteEntry> snapshots = entries
            .Where(e => !e.IsCollection && IsSafeSnapshotBasename(e.Name))
            // Server-reported LastModified first, embedded timestamp as
            // fallback — never raw name order, which legacy local-time
            // names skew across devices.
            .OrderByDescending(e => e.LastModified ??
                                    ParseSnapshotTimestamp(e.Name) ??
                                    DateTimeOffset.MinValue)
            .ToList();

        int pruned = 0;
        foreach (CloudBackupRemoteEntry stale in snapshots.Skip(options.RetentionCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await transport.DeleteAsync($"{options.RemotePath}/{stale.Name}", cancellationToken);
            pruned++;
        }

        return pruned;
    }

    private async Task MarkSuccessAsync(CloudBackupOptions completedOptions)
    {
        // The success belongs to the destination this run actually used.
        // If the user reconfigured mid-upload, stamping it onto the NEW
        // options would mark a destination that has never been backed up —
        // false assurance that delays its first real backup by a full
        // interval. Upload already succeeded; just skip the stamp.
        if (!_options.IsConfigured ||
            DestinationIdentityChanged(_options, completedOptions))
        {
            App.Log("[CloudBackup] Upload succeeded against superseded options; last-success not stamped.");
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _options = _options with { LastSuccessUtc = now };
        _settingsService.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks = now.UtcTicks;
        try
        {
            await _settingsService.SaveAsync(notifySubscribers: false);
        }
        catch (Exception ex)
        {
            App.Log($"[CloudBackup] Failed to persist last-success timestamp: {ex}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort — temp staging must not fail the backup.
        }
    }
}

/// <summary>Outcome of <see cref="CloudBackupService.RunBackupNowAsync"/>.</summary>
internal sealed record CloudBackupRunResult(bool Uploaded, string? RemoteFilePath, int PrunedCount)
{
    internal static readonly CloudBackupRunResult NotConfigured = new(false, null, 0);
}
