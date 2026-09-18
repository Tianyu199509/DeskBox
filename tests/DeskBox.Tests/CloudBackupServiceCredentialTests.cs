using DeskBox.Models;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Service-level credential helpers and policy normalizers.
/// The secret key is scoped by provider+host+username so an account or
/// endpoint change never silently reuses a stale credential.
/// </summary>
public sealed class CloudBackupServiceCredentialTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), $"deskbox-cloudcred-{Guid.NewGuid():N}");
    private readonly string _appDataRoot;

    public CloudBackupServiceCredentialTests()
    {
        _appDataRoot = Path.Combine(_tempRoot, "appdata");
        Directory.CreateDirectory(_appDataRoot);
    }

    [Fact]
    public async Task SaveCredential_StoresUnderScopedKey()
    {
        var store = new InMemoryCredentialStore();
        CloudBackupService service = CreateService(store, out _);
        service.UpdateOptions(MakeOptions());

        await service.SaveCredentialAsync("s3cret-token");

        Assert.Equal(
            "s3cret-token",
            await store.GetSecretAsync("webdav:alice@dav.example.com"));
    }

    [Fact]
    public async Task SaveCredential_RejectsBlankSecret()
    {
        var store = new InMemoryCredentialStore();
        CloudBackupService service = CreateService(store, out _);
        service.UpdateOptions(MakeOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveCredentialAsync("   "));
    }

    [Fact]
    public async Task HasCredential_FalseWhenNotConfigured()
    {
        var store = new InMemoryCredentialStore();
        await store.SetSecretAsync("webdav:alice@dav.example.com", "s3cret");
        CloudBackupService service = CreateService(store, out _);
        service.UpdateOptions(MakeOptions(provider: CloudBackupSettingsPolicy.ProviderNone));

        Assert.False(await service.HasCredentialAsync());
    }

    [Fact]
    public async Task HasCredential_TrueAfterSave()
    {
        var store = new InMemoryCredentialStore();
        CloudBackupService service = CreateService(store, out _);
        service.UpdateOptions(MakeOptions());

        Assert.False(await service.HasCredentialAsync());
        await service.SaveCredentialAsync("s3cret");
        Assert.True(await service.HasCredentialAsync());
    }

    [Fact]
    public async Task HasCredential_FalseForDifferentAccountOrHost()
    {
        var store = new InMemoryCredentialStore();
        CloudBackupService service = CreateService(store, out _);
        service.UpdateOptions(MakeOptions());
        await service.SaveCredentialAsync("s3cret");

        // Same provider+host, different username → different key.
        service.UpdateOptions(MakeOptions(username: "bob"));
        Assert.False(await service.HasCredentialAsync());

        // Same username, different host → different key.
        service.UpdateOptions(MakeOptions(serverUrl: "https://other.example.com/dav/"));
        Assert.False(await service.HasCredentialAsync());

        // Original account still resolves.
        service.UpdateOptions(MakeOptions());
        Assert.True(await service.HasCredentialAsync());
    }

    [Fact]
    public void NormalizeIntervalMinutes_KeepsPresetValuesAndDefaultsOtherwise()
    {
        foreach (int preset in CloudBackupSettingsPolicy.SupportedIntervalMinutes)
        {
            Assert.Equal(preset, CloudBackupSettingsPolicy.NormalizeIntervalMinutes(preset));
        }

        Assert.Equal(
            CloudBackupSettingsPolicy.DefaultIntervalMinutes,
            CloudBackupSettingsPolicy.NormalizeIntervalMinutes(61));
        Assert.Equal(
            CloudBackupSettingsPolicy.DefaultIntervalMinutes,
            CloudBackupSettingsPolicy.NormalizeIntervalMinutes(-5));
    }

    [Fact]
    public void NormalizeRetentionCount_KeepsPresetValuesAndDefaultsOtherwise()
    {
        foreach (int preset in CloudBackupSettingsPolicy.SupportedRetentionCounts)
        {
            Assert.Equal(preset, CloudBackupSettingsPolicy.NormalizeRetentionCount(preset));
        }

        Assert.Equal(
            CloudBackupSettingsPolicy.DefaultRetentionCount,
            CloudBackupSettingsPolicy.NormalizeRetentionCount(4));
        Assert.Equal(
            CloudBackupSettingsPolicy.DefaultRetentionCount,
            CloudBackupSettingsPolicy.NormalizeRetentionCount(0));
    }

    [Fact]
    public void GetOptions_ClampsOutOfRangeValues_AndDefaultsRemotePath()
    {
        var settings = new AppSettings();
        settings.CloudBackup.CloudBackupProvider = "  WEBDAV ";
        settings.CloudBackup.CloudBackupServerUrl = "  https://dav.example.com/dav/  ";
        settings.CloudBackup.CloudBackupRemotePath = "   ";
        settings.CloudBackup.CloudBackupRetentionCount = 0;
        settings.CloudBackup.CloudBackupIntervalMinutes = 1;
        settings.CloudBackup.CloudBackupTodoDataEnabled = true;

        CloudBackupOptions options = CloudBackupSettingsPolicy.GetOptions(settings);

        Assert.Equal("webdav", options.Provider);
        Assert.Equal("https://dav.example.com/dav/", options.ServerUrl);
        Assert.Equal("DeskBox/backups", options.RemotePath);
        Assert.Equal(1, options.RetentionCount);
        Assert.Equal(5, options.IntervalMinutes);

        settings.CloudBackup.CloudBackupRetentionCount = 500;
        settings.CloudBackup.CloudBackupIntervalMinutes = 999_999_999;
        settings.CloudBackup.CloudBackupRemotePath = " /nested/dir/ ";

        options = CloudBackupSettingsPolicy.GetOptions(settings);

        Assert.Equal(50, options.RetentionCount);
        Assert.Equal(30 * 24 * 60, options.IntervalMinutes);
        Assert.Equal("nested/dir", options.RemotePath);
    }

    private static CloudBackupOptions MakeOptions(
        string provider = CloudBackupSettingsPolicy.ProviderWebDav,
        string serverUrl = "https://dav.example.com/dav/",
        string username = "alice") =>
        new(
            Provider: provider,
            ServerUrl: serverUrl,
            RemotePath: "DeskBox/backups",
            Username: username,
            Scope: CloudBackupDomain.TodoData,
            RetentionCount: CloudBackupSettingsPolicy.DefaultRetentionCount,
            IntervalMinutes: CloudBackupSettingsPolicy.DefaultIntervalMinutes,
            LastSuccessUtc: DateTimeOffset.MinValue);

    private CloudBackupService CreateService(
        InMemoryCredentialStore store,
        out SettingsService settings)
    {
        var backup = new DeskBoxDataBackupService(_appDataRoot);
        settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        return new CloudBackupService(
            backup, settings, store,
            (options, secret) => throw new NotSupportedException(
                "Transport must not be created in credential tests."));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(key, out string? value) ? value : null);

        public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }
    }
}
