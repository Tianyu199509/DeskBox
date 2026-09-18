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
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Timer-tick entry point: uploads only when configured and the interval
    /// has elapsed. Never throws — a transient network failure must not take
    /// down the timer path.
    /// </summary>
    internal async Task RunScheduledIfDueAsync(CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = _options;
        if (!options.IsConfigured)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - options.LastSuccessUtc < TimeSpan.FromMinutes(options.IntervalMinutes))
        {
            return;
        }

        try
        {
            await RunBackupNowAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[CloudBackup] Scheduled upload failed: {ex}");
        }
    }

    /// <summary>
    /// Uploads one scoped snapshot now. Throws on failure — the manual path
    /// (PR-3 "backup now") surfaces the error to the user.
    /// </summary>
    internal async Task<CloudBackupRunResult> RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = _options;
        if (!options.IsConfigured)
        {
            return CloudBackupRunResult.NotConfigured;
        }

        await _gate.WaitAsync(cancellationToken);
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

            string remoteFilePath = $"{options.RemotePath}/{BuildRemoteSnapshotName(localArchivePath)}";
            await using (FileStream content = File.OpenRead(localArchivePath))
            {
                await transport.UploadAsync(remoteFilePath, content, cancellationToken);
            }

            int pruned = await ApplyRetentionAsync(transport, options, cancellationToken);
            await MarkSuccessAsync();
            App.Log($"[CloudBackup] Uploaded '{remoteFilePath}' (pruned {pruned} old snapshots).");
            return new CloudBackupRunResult(Uploaded: true, remoteFilePath, pruned);
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                TryDeleteDirectory(stagingDirectory);
            }

            _gate.Release();
        }
    }

    /// <summary>Verifies the configured endpoint; for the PR-3 "test connection" button.</summary>
    internal async Task ProbeConnectionAsync(CancellationToken cancellationToken = default)
    {
        CloudBackupOptions options = _options;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("Cloud backup is not configured.");
        }

        ICloudBackupTransport transport = await CreateConfiguredTransportAsync(options, cancellationToken);
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
            .Where(e => !e.IsCollection && IsSnapshotName(e.Name))
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
        if (string.IsNullOrWhiteSpace(remoteFileName) ||
            remoteFileName.Contains('/') ||
            remoteFileName.Contains('\\') ||
            remoteFileName.Contains(".."))
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
    /// Remote name = local archive name + short device suffix, so two
    /// devices backing up in the same minute can never overwrite each
    /// other's snapshot.
    /// </summary>
    private static string BuildRemoteSnapshotName(string localArchivePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(localArchivePath);
        string deviceSuffix = DeviceIdentity.Id is { Length: >= 8 } id ? id[..8] : "nodevice";
        return $"{baseName}-{deviceSuffix}{SnapshotFileExtension}";
    }

    internal static bool IsSnapshotName(string name) =>
        name.StartsWith(SnapshotFilePrefix, StringComparison.Ordinal) &&
        name.EndsWith(SnapshotFileExtension, StringComparison.OrdinalIgnoreCase);

    private async Task<int> ApplyRetentionAsync(
        ICloudBackupTransport transport,
        CloudBackupOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudBackupRemoteEntry> entries = await transport.ListAsync(options.RemotePath, cancellationToken);
        List<CloudBackupRemoteEntry> snapshots = entries
            .Where(e => !e.IsCollection && IsSnapshotName(e.Name))
            .OrderByDescending(e => e.Name, StringComparer.Ordinal)
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

    private async Task MarkSuccessAsync()
    {
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
