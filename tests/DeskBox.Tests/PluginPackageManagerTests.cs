using DeskBox.Services.Plugins;

namespace DeskBox.Tests;

/// <summary>
/// B1b: the untrusted-package pipeline - stage, verify (with input
/// budgets), content-addressed immutable commit, InstalledPackageHandle,
/// publisher pin + version monotonicity, and the grant store. All tests
/// run against isolated temp roots via the internal constructors.
/// </summary>
public sealed class PluginPackageManagerTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N")))
        .FullName;

    private string PluginsRoot => Path.Combine(_tempRoot, "plugins");

    [Fact]
    public void Install_CommitsContentAddressedImmutableCopy()
    {
        var manager = new PluginPackageManager(PluginsRoot);
        string source = TestPaths.FromRepository("spikes/github-stats-live");

        PluginInstallResult result = manager.Install(source);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures));
        Assert.NotNull(result.Package);
        Assert.Equal("com.github.stats.live", result.Package!.PackageId);
        Assert.Equal("0.1.0", result.Package.Version);
        Assert.Equal("none", result.Package.Runtime);
        Assert.Contains(result.Package.Permissions, p => p.Id == "network.fetch");
        Assert.Contains(result.Package.Permissions, p => p.Id == "shell.open");
        Assert.True(result.Package.DataSources.ContainsKey("github-repo"));
        Assert.True(result.Package.Actions.ContainsKey("open-repo"));

        // Content-addressed directory named by the content hash prefix.
        string expectedRoot = Path.Combine(PluginsRoot, "com.github.stats.live");
        Assert.True(Directory.Exists(expectedRoot));
        string versionDirectory = Directory.GetDirectories(expectedRoot).Single();
        Assert.StartsWith(result.Package.ContentHash[..16], Path.GetFileName(versionDirectory), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(versionDirectory, "manifest.json")));

        // Immutable commit: payload files are read-only.
        FileAttributes attributes = File.GetAttributes(Path.Combine(versionDirectory, "manifest.json"));
        Assert.True((attributes & FileAttributes.ReadOnly) != 0);

        // Registry has the handle.
        InstalledPackageRecord? record = manager.Find("com.github.stats.live");
        Assert.NotNull(record);
        Assert.Equal(result.Package.ContentHash, record!.ContentHash);
        Assert.Equal(result.Package.PublisherFingerprint, record.PublisherFingerprint);
    }

    [Fact]
    public void ReinstallSameContent_IsIdempotent()
    {
        var manager = new PluginPackageManager(PluginsRoot);
        string source = TestPaths.FromRepository("spikes/github-stats-live");

        Assert.True(manager.Install(source).Succeeded);
        Assert.True(manager.Install(source).Succeeded);

        Assert.Single(manager.GetInstalled());
    }

    [Fact]
    public void Uninstall_RemovesFilesRegistryAndGrants()
    {
        var manager = new PluginPackageManager(PluginsRoot);
        var grants = new PluginGrantStore(PluginsRoot);
        Assert.True(manager.Install(TestPaths.FromRepository("spikes/github-stats-live")).Succeeded);
        grants.SetGrants("com.github.stats.live", "network.fetch", ["api.github.com"]);

        Assert.True(manager.Uninstall("com.github.stats.live"));

        Assert.Empty(manager.GetInstalled());
        Assert.False(Directory.Exists(Path.Combine(PluginsRoot, "com.github.stats.live")));
        Assert.Empty(grants.GetGrants("com.github.stats.live"));
        Assert.False(manager.Uninstall("com.github.stats.live"));
    }

    [Fact]
    public void Update_WithDifferentPublisher_IsRejected()
    {
        var manager = new PluginPackageManager(PluginsRoot);
        Assert.True(manager.Install(TestPaths.FromRepository("spikes/github-stats-live")).Succeeded);

        // Simulate the package having been pinned to a DIFFERENT publisher
        // (in production this happens when the index is tampered with and
        // the update presents a different, validly-signed key).
        RewriteRegistryPublisher(PluginsRoot, "com.github.stats.live", new string('c', 64));

        PluginInstallResult result = manager.Install(TestPaths.FromRepository("spikes/github-stats-live"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.Contains("publisher takeover blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_WithLowerVersionContent_IsRejected()
    {
        var manager = new PluginPackageManager(PluginsRoot);
        Assert.True(manager.Install(TestPaths.FromRepository("spikes/github-stats-live")).Succeeded);

        // Same publisher, but the recorded version is newer: the incoming
        // 0.1.0 content must be rejected (downgrade protection). Registry
        // rewrite simulates a previously-installed 9.9.9.
        RewriteRegistryVersion(PluginsRoot, "com.github.stats.live", "9.9.9");

        PluginInstallResult result = manager.Install(TestPaths.FromRepository("spikes/github-stats-live"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.Contains("must not decrease", StringComparison.Ordinal));
    }

    private static void RewriteRegistryPublisher(string pluginsRoot, string packageId, string newFingerprint)
    {
        string registryPath = Path.Combine(pluginsRoot, "installed.json");
        string json = File.ReadAllText(registryPath);
        File.WriteAllText(registryPath, json.Replace(
            "\"publisherFingerprint\": \"1bc4f2db8438d2fd296bd48074088ccc726c265712125abbae975063ba719ea4\"",
            $"\"publisherFingerprint\": \"{newFingerprint}\""));
    }

    private static void RewriteRegistryVersion(string pluginsRoot, string packageId, string newVersion)
    {
        string registryPath = Path.Combine(pluginsRoot, "installed.json");
        string json = File.ReadAllText(registryPath);
        File.WriteAllText(registryPath, json.Replace(
            "\"version\": \"0.1.0\"",
            $"\"version\": \"{newVersion}\""));
    }

    [Fact]
    public void Install_InvalidPackage_FailsWithoutSideEffects()
    {
        var manager = new PluginPackageManager(PluginsRoot);
        string tampered = CopyPackage("spikes/github-stats");
        File.AppendAllText(Path.Combine(tampered, "files", "icon.svg"), "<!--tamper-->");

        PluginInstallResult result = manager.Install(tampered);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Failures);
        Assert.Empty(manager.GetInstalled());
        Assert.False(Directory.Exists(Path.Combine(PluginsRoot, "com.github.stats")));
    }

    [Fact]
    public void Verify_InputBudgets_RejectHugeManifestWithoutReadingItAll()
    {
        string package = Path.Combine(_tempRoot, "huge-manifest");
        Directory.CreateDirectory(package);
        File.WriteAllText(
            Path.Combine(package, "manifest.json"),
            "{\"padding\":\"" + new string('x', 300_000) + "\"}");

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(
            package,
            PluginPackageVerificationPolicy.Development,
            new PluginVerificationLimits(MaxManifestBytes: 100_000));

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f =>
            f.Contains("exceeds the 100000-byte input budget", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_FileCountBudget_IsEnforced()
    {
        string package = CopyPackage("spikes/github-stats");
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(package, $"extra-{i}.txt"), "x");
        }

        PluginPackageVerifier.VerificationResult result = PluginPackageVerifier.Verify(
            package,
            PluginPackageVerificationPolicy.Development,
            new PluginVerificationLimits(MaxFileCount: 4));

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, f => f.Contains("file-count budget", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamingFileHash_MatchesBufferedHash()
    {
        string path = Path.Combine(_tempRoot, "hash-sample.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray());

        using FileStream stream = File.OpenRead(path);
        string buffered = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();

        Assert.Equal(buffered, PluginPackageVerifier.Sha256HexFile(path));
    }

    [Fact]
    public void GrantStore_RoundTripsAndFailsClosed()
    {
        var grants = new PluginGrantStore(PluginsRoot);

        Assert.Empty(grants.GetGrants("com.example.x"));

        grants.SetGrants("com.example.x", "network.fetch", ["API.GitHub.com", "api.github.com", "example.org"]);
        grants.SetGrants("com.example.x", "shell.open", ["github.com"]);

        IReadOnlyDictionary<string, IReadOnlyList<string>> stored = grants.GetGrants("com.example.x");
        Assert.Equal(["api.github.com", "example.org"], stored["network.fetch"].OrderBy(h => h));
        Assert.Equal(["github.com"], stored["shell.open"]);

        // A corrupt grants file fails closed (no grants), not throws.
        File.WriteAllText(
            Path.Combine(PluginsRoot, "grants.json"),
            "{ not valid json");
        var reloaded = new PluginGrantStore(PluginsRoot);
        Assert.Empty(reloaded.GetGrants("com.example.x"));
    }

    private string CopyPackage(string relativePackagePath)
    {
        string source = TestPaths.FromRepository(relativePackagePath);
        string destination = Path.Combine(_tempRoot, "src-" + Path.GetFileName(relativePackagePath) + "-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                foreach (string file in Directory.GetFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for files briefly held by antivirus.
        }
    }
}
