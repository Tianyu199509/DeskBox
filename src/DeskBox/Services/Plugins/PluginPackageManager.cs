using System.Text.Json;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Takes a bounded snapshot before verification, commits it to a unique version
/// directory, then atomically switches the registry. ReadOnly is accidental-write
/// protection, not an OS sandbox. Find revalidates content before returning a record.
/// </summary>
public sealed class PluginPackageManager
{
    private readonly string _pluginsRoot;
    private readonly object _lock;
    private readonly HashSet<string> _trustedPublishers;

    // No production publisher key has been provisioned yet. An empty trust set
    // refuses Store installs; the public spike key must never become a trust root.
    public PluginPackageManager() : this(
        Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"), []) { }

    internal PluginPackageManager(string pluginsRoot, IEnumerable<string>? trustedPublishers = null)
    {
        _pluginsRoot = Path.GetFullPath(pluginsRoot);
        Directory.CreateDirectory(_pluginsRoot);
        PluginPackageStorage.RejectReparsePoint(_pluginsRoot);
        _lock = PluginPackageStorage.Gate(_pluginsRoot);
        _trustedPublishers = new HashSet<string>(trustedPublishers ?? [], StringComparer.Ordinal);
    }

    private string RegistryPath => Path.Combine(_pluginsRoot, "installed.json");

    public PluginInstallResult Install(
        string sourceDirectory,
        PluginPackageVerificationPolicy policy = PluginPackageVerificationPolicy.Store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        lock (_lock)
        {
            string? staging = null;
            string? uncommitted = null;
            try
            {
                // A damaged existing registry must not erase identity/version pins.
                List<InstalledPackageRecord> registry = LoadRegistry();
                string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
                if (_pluginsRoot.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    _pluginsRoot.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return PluginInstallResult.Failed(["source must not contain the install store"]);

                string stagingRoot = Path.Combine(_pluginsRoot, ".staging");
                Directory.CreateDirectory(stagingRoot);
                PluginPackageStorage.RejectReparsePoint(stagingRoot);
                staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                PluginPackageStorage.CopySnapshot(source, staging, PluginVerificationLimits.Default);

                var verification = PluginPackageVerifier.Verify(staging, policy);
                if (!verification.IsValid || verification.ManifestJson is null)
                    return PluginInstallResult.Failed(verification.Failures);
                using JsonDocument document = JsonDocument.Parse(verification.ManifestJson);
                VerifiedPluginPackage? package = BuildVerifiedModel(document.RootElement,
                    PluginPackageVerifier.Sha256HexFile(Path.Combine(staging, "package.integrity")));
                if (package is null)
                    return PluginInstallResult.Failed(["failed to build the typed verified model"]);

                if (policy == PluginPackageVerificationPolicy.Store && !_trustedPublishers.Contains(package.PublisherFingerprint))
                    return PluginInstallResult.Failed(["publisher is not trusted by the host; a valid signature alone is insufficient"]);
                if (!IsCompatible(package))
                    return PluginInstallResult.Failed(["package hostApi range does not include host API 1.0.0"]);

                InstalledPackageRecord? existing = registry.FirstOrDefault(r => r.PackageId == package.PackageId);
                if (existing is not null)
                {
                    if (existing.PublisherFingerprint != package.PublisherFingerprint)
                        return PluginInstallResult.Failed(["publisher takeover blocked: package is pinned to another publisher"]);
                    int comparison = Version.Parse(package.Version).CompareTo(Version.Parse(existing.Version));
                    if (comparison < 0)
                        return PluginInstallResult.Failed(["update rejected: version must not decrease"]);
                    if (existing.ContentHash == package.ContentHash && IsRecordIntact(existing))
                        return PluginInstallResult.Ok(package, ResolveInstallPath(existing));
                    if (comparison == 0 && existing.ContentHash != package.ContentHash)
                        return PluginInstallResult.Failed(["update rejected: version must strictly increase for new content"]);
                }

                string packageRoot = PluginPackageStorage.UnderRoot(_pluginsRoot, MakeSafeDirectoryName(package.PackageId));
                Directory.CreateDirectory(packageRoot);
                PluginPackageStorage.RejectReparsePoint(packageRoot);
                // A repair gets a fresh location too, so a locked/corrupt installation
                // is never removed before a working replacement is registered.
                string installDirectory = Path.Combine(packageRoot, package.ContentHash + "-" + Guid.NewGuid().ToString("N"));
                Directory.Move(staging, installDirectory);
                staging = null;
                uncommitted = installDirectory;
                foreach (string file in PluginPackageVerifier.WalkPackageTree(installDirectory).Files)
                    File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

                registry.RemoveAll(r => r.PackageId == package.PackageId);
                registry.Add(new InstalledPackageRecord(package.PackageId, package.Version,
                    package.PublisherFingerprint, package.Runtime, package.ContentHash,
                    Path.GetRelativePath(_pluginsRoot, installDirectory).Replace('\\', '/'),
                    DateTimeOffset.UtcNow, policy == PluginPackageVerificationPolicy.Development));
                SaveRegistry(registry);
                uncommitted = null;
                return PluginInstallResult.Ok(package, installDirectory);
            }
            catch (Exception error) when (IsStorageFailure(error))
            {
                return PluginInstallResult.Failed([error.Message]);
            }
            finally
            {
                CleanupUncommitted(staging);
                CleanupUncommitted(uncommitted);
            }
        }
    }

    public PluginInstalledState GetInstalledState()
    {
        lock (_lock)
        {
            try { return new(true, LoadRegistry(), []); }
            catch (Exception error) when (IsStorageFailure(error)) { return new(false, [], [error.Message]); }
        }
    }

    public IReadOnlyList<InstalledPackageRecord> GetInstalled() => GetInstalledState().Packages;

    /// <summary>Missing, corrupt or incompatible content cannot be activated.</summary>
    public InstalledPackageRecord? Find(string packageId)
    {
        lock (_lock)
        {
            try
            {
                InstalledPackageRecord? record = LoadRegistry().FirstOrDefault(r => r.PackageId == packageId);
                return record is not null && IsRecordIntact(record) ? record : null;
            }
            catch (Exception error) when (IsStorageFailure(error)) { return null; }        }
    }

    /// <summary>
    /// Batch C1: structurally paired handle for the native runtime. The record
    /// and its resolved install root can only be combined here; the runtime
    /// manager never accepts a separable package + path pair. Refuses every
    /// record until the package-format freeze introduces runtime:native.
    /// </summary>
    public NativeInstalledPackageHandle? TryCreateNativeHandle(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        // Read the registry without full verification (single-verify pattern,
        // audit round 14). Publisher trust and identity binding are checked
        // AFTER verification below (audit round 15 P0).
        InstalledPackageRecord? record = null;
        lock (_lock)
        {
            try
            {
                record = LoadRegistry().FirstOrDefault(r => r.PackageId == packageId);
            }
            catch (Exception error) when (IsStorageFailure(error)) { return null; }
        }
        if (record is null || record.Runtime != NativeWidgetRuntimeManager.NativeRuntimeType) return null;
        if (record.IsDevelopment &&
            Environment.GetEnvironmentVariable("DESKBOX_ALLOW_UNTRUSTED_NATIVE_DEV") != "1")
        {
            return null;
        }
        // Publisher authorization (audit round 15 P0: in-process full-trust
        // native code must never be activated on signature validity alone).
        if (!record.IsDevelopment && !_trustedPublishers.Contains(record.PublisherFingerprint)) return null;
        string installRoot = ResolveInstallPath(record);
        // Single verification pass with the ACTUAL content hash from disk.
        PluginPackageVerifier.VerificationResult verification = PluginPackageVerifier.Verify(installRoot, PluginPackageVerificationPolicy.Store);
        if (!verification.IsValid) return null;
        try
        {
            // Use the actual verified content hash, not the registry value
            // (audit round 15 P0: registry hash may be stale or tampered).
            string actualContentHash = PluginPackageVerifier.Sha256HexFile(
                Path.Combine(installRoot, "package.integrity"));
            using var document = System.Text.Json.JsonDocument.Parse(
                System.IO.File.ReadAllText(Path.Combine(installRoot, "manifest.json")));
            VerifiedPluginPackage? verified = BuildVerifiedModel(document.RootElement, actualContentHash);
            if (verified is null) return null;
            // Identity binding: the verified package must match the registry
            // record on all identity dimensions (audit round 15 P0).
            if (!string.Equals(verified.PackageId, record.PackageId, StringComparison.Ordinal) ||
                !string.Equals(verified.PublisherFingerprint, record.PublisherFingerprint, StringComparison.Ordinal) ||
                !string.Equals(verified.Version, record.Version, StringComparison.Ordinal) ||
                !string.Equals(verified.Runtime, record.Runtime, StringComparison.Ordinal) ||
                !string.Equals(verified.ContentHash, record.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            // Compatibility gate: architecture must match the running host process.
            if (verified.EntryArchitecture is { } arch)
            {
                string hostArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "x64",
                    System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                    _ => "unsupported",
                };
                if (!string.Equals(arch, hostArch, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
            return new NativeInstalledPackageHandle(record, verified, installRoot);
        }
        catch
        {
            return null;
        }
    }

    public bool Uninstall(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        lock (_lock)
        {
            try
            {
                List<InstalledPackageRecord> registry = LoadRegistry();
                if (!registry.Any(r => r.PackageId == packageId)) return false;
                // Only the package's binary subtree is removed, including old versions.
                // If a file is locked, report incomplete and retain the registry entry.
                PluginPackageStorage.DeleteDirectory(_pluginsRoot,
                    PluginPackageStorage.UnderRoot(_pluginsRoot, MakeSafeDirectoryName(packageId)));
                new PluginGrantStore(_pluginsRoot).RemovePackage(packageId);
                registry.RemoveAll(r => r.PackageId == packageId);
                SaveRegistry(registry);
                return true;
            }
            catch (Exception error) when (IsStorageFailure(error)) { return false; }
        }
    }

    private bool IsRecordIntact(InstalledPackageRecord record)
    {
        var policy = record.IsDevelopment ? PluginPackageVerificationPolicy.Development : PluginPackageVerificationPolicy.Store;
        if (!record.IsDevelopment && !_trustedPublishers.Contains(record.PublisherFingerprint)) return false;
        string path = ResolveInstallPath(record);
        var verification = PluginPackageVerifier.Verify(path, policy);
        if (!verification.IsValid || verification.ManifestJson is null) return false;
        using JsonDocument document = JsonDocument.Parse(verification.ManifestJson);
        VerifiedPluginPackage? package = BuildVerifiedModel(document.RootElement,
            PluginPackageVerifier.Sha256HexFile(Path.Combine(path, "package.integrity")));
        return package is not null && IsCompatible(package) && package.PackageId == record.PackageId &&
            package.PublisherFingerprint == record.PublisherFingerprint && package.ContentHash == record.ContentHash &&
            package.Version == record.Version && package.Runtime == record.Runtime;
    }

    private static bool IsCompatible(VerifiedPluginPackage package) =>
        Version.TryParse(package.HostApiMinimum, out Version? minimum) &&
        Version.TryParse(package.HostApiMaximum, out Version? maximum) &&
        minimum <= new Version(1, 0, 0) && maximum >= new Version(1, 0, 0);

    private string ResolveInstallPath(InstalledPackageRecord record) =>
        PluginPackageStorage.UnderRoot(_pluginsRoot, record.InstallRelativePath);

    private void CleanupUncommitted(string? path)
    {
        if (path is null) return;
        try { PluginPackageStorage.DeleteDirectory(_pluginsRoot, path); }
        catch (Exception error) when (IsStorageFailure(error)) { /* Orphaned content is never registered or activated. */ }
    }

    private static bool IsStorageFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException;

    // ---------- typed model construction (single parse, downstream contract) ----------
    internal static VerifiedPluginPackage? BuildVerifiedModel(JsonElement root, string? verifiedContentHash = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        try
        {
            var permissions = new List<PluginRequestedPermission>();
            if (root.TryGetProperty("permissions", out JsonElement permissionsElement))
            {
                foreach (JsonElement permission in permissionsElement.EnumerateArray())
                {
                    permissions.Add(new PluginRequestedPermission(
                        permission.GetProperty("id").GetString()!,
                        permission.TryGetProperty("scope", out JsonElement scope) &&
                        scope.TryGetProperty("allow", out JsonElement allow) &&
                        allow.ValueKind == JsonValueKind.Array
                            ? allow.EnumerateArray().Select(entry => entry.GetString()!).ToList()
                            : [],
                        !permission.TryGetProperty("required", out JsonElement required) || required.GetBoolean()));
                }
            }

            var dataSources = new Dictionary<string, VerifiedDataSource>();
            if (root.TryGetProperty("dataSources", out JsonElement sourcesElement))
            {
                foreach (JsonProperty source in sourcesElement.EnumerateObject())
                {
                    dataSources[source.Name] = new VerifiedDataSource(
                        source.Value.GetProperty("url").GetString()!,
                        source.Value.GetProperty("refreshSeconds").GetInt32());
                }
            }

            var actions = new Dictionary<string, VerifiedAction>();
            if (root.TryGetProperty("actions", out JsonElement actionsElement))
            {
                foreach (JsonProperty action in actionsElement.EnumerateObject())
                {
                    actions[action.Name] = new VerifiedAction(
                        action.Value.GetProperty("type").GetString()!,
                        action.Value.GetProperty("url").GetString()!);
                }
            }

            var contributions = new List<VerifiedContribution>();
            foreach (JsonElement contribution in root.GetProperty("contributions").EnumerateArray())
            {
                var payloadFields = new Dictionary<string, string>();
                if (contribution.TryGetProperty("payload", out JsonElement payload))
                {
                    foreach (JsonProperty field in payload.EnumerateObject())
                    {
                        if (field.Value.ValueKind == JsonValueKind.String)
                        {
                            payloadFields[field.Name] = field.Value.GetString()!;
                        }
                    }
                }
                var bindings = new Dictionary<string, VerifiedBinding>();
                if (contribution.TryGetProperty("bindings", out JsonElement bindingsElement))
                {
                    foreach (JsonProperty binding in bindingsElement.EnumerateObject())
                    {
                        bindings[binding.Name] = new VerifiedBinding(
                            binding.Value.GetProperty("source").GetString()!,
                            binding.Value.GetProperty("path").GetString()!);
                    }
                }
                contributions.Add(new VerifiedContribution(
                    contribution.GetProperty("id").GetString()!,
                    contribution.GetProperty("displayName").GetString()!,
                    // Template is nullable: null for runtime:native (renders via its own DLL).
                    contribution.TryGetProperty("template", out JsonElement templateElement) ? templateElement.GetString() : null,
                    payloadFields,
                    bindings)
                {
                    Payload = contribution.TryGetProperty("payload", out JsonElement structuredPayload) ? structuredPayload.Clone() : default,
                    DefaultSize = contribution.TryGetProperty("defaultSize", out JsonElement size)
                        ? new VerifiedWidgetSize(size.GetProperty("width").GetInt32(), size.GetProperty("height").GetInt32()) : null,
                    ActivationEvents = contribution.TryGetProperty("activationEvents", out JsonElement activation)
                        ? activation.EnumerateArray().Select(e => e.GetString()!).ToArray() : [],
                    UnavailableFallback = contribution.TryGetProperty("fallback", out JsonElement fallbackElement) && fallbackElement.ValueKind == JsonValueKind.Object
                        ? new VerifiedContributionFallback(
                            fallbackElement.TryGetProperty("template", out JsonElement fbTemplate) ? fbTemplate.GetString() ?? "status" : "status",
                            fallbackElement.TryGetProperty("message", out JsonElement fbMessage) ? fbMessage.GetString() : null)
                        : null,
                });
            }

            return new VerifiedPluginPackage
            {
                PackageId = root.GetProperty("id").GetString()!,
                Version = root.GetProperty("version").GetString()!,
                PublisherFingerprint = root.GetProperty("publisher").GetString()!,
                Runtime = root.GetProperty("runtime").GetString()!,
                ContentHash = verifiedContentHash ?? root.GetProperty("signature").GetProperty("contentHash").GetString()!,
                HostApiMinimum = root.GetProperty("hostApi").GetProperty("min").GetString()!,
                HostApiMaximum = root.GetProperty("hostApi").GetProperty("max").GetString()!,
                Fallback = root.TryGetProperty("fallback", out JsonElement fallback) ? fallback.Clone() : default,
                DataSchema = root.TryGetProperty("data", out JsonElement data) ? data.Clone() : default,
                ManifestRelativePath = "manifest.json",
                Permissions = permissions,
                Contributions = contributions,
                DataSources = dataSources,
                Actions = actions,
                EntryMain = root.TryGetProperty("entry", out JsonElement entry) &&
                            entry.TryGetProperty("main", out JsonElement main) &&
                            main.ValueKind == JsonValueKind.String
                    ? main.GetString()
                    : null,
                EntryArchitecture = root.TryGetProperty("entry", out JsonElement entryArch) &&
                            entryArch.TryGetProperty("architecture", out JsonElement arch) &&
                            arch.ValueKind == JsonValueKind.String
                    ? arch.GetString()
                    : null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string MakeSafeDirectoryName(string packageId)
    {
        if (string.IsNullOrEmpty(packageId) || !char.IsAsciiLetterOrDigit(packageId[0]) ||
            packageId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-') ||
            packageId.Contains("..", StringComparison.Ordinal) || packageId.EndsWith('.'))
            throw new InvalidDataException("unsafe package id");
        return packageId;
    }

    private List<InstalledPackageRecord> LoadRegistry()
    {
        if (!File.Exists(RegistryPath)) return [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(PluginPackageStorage.ReadText(RegistryPath, 4 * 1024 * 1024));
            var records = new List<InstalledPackageRecord>();
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                var record = new InstalledPackageRecord(
                    item.GetProperty("packageId").GetString()!, item.GetProperty("version").GetString()!,
                    item.GetProperty("publisherFingerprint").GetString()!, item.GetProperty("runtime").GetString()!,
                    item.GetProperty("contentHash").GetString()!, item.GetProperty("installRelativePath").GetString()!,
                    item.GetProperty("installedAtUtc").GetDateTimeOffset(),
                    item.TryGetProperty("isDevelopment", out JsonElement development) && development.GetBoolean());
                MakeSafeDirectoryName(record.PackageId);
                bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
                if (!PluginPackageVerifier.IsValidVersion(record.Version) ||
                    !IsHash(record.PublisherFingerprint) || !IsHash(record.ContentHash) ||
                    record.Runtime is not ("none" or "wasm" or "process" or "native") ||
                    string.IsNullOrEmpty(record.InstallRelativePath) ||
                    PluginPackageVerifier.PackagePathViolation(record.InstallRelativePath) is not null)
                    throw new InvalidDataException("invalid installed package fields");
                string[] parts = record.InstallRelativePath.Split('/');
                if (parts.Length != 2 || parts[0] != record.PackageId ||
                    !parts[1].StartsWith(record.ContentHash[..16], StringComparison.Ordinal) ||
                    records.Any(r => r.PackageId == record.PackageId))
                    throw new InvalidDataException("invalid or duplicate installed package path");
                ResolveInstallPath(record);
                records.Add(record);
            }
            return records;
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            throw new InvalidDataException("installed package registry is damaged; installation is blocked until it is recovered", error);
        }
    }

    private void SaveRegistry(List<InstalledPackageRecord> registry)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (InstalledPackageRecord record in registry.OrderBy(r => r.PackageId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("packageId", record.PackageId);
                writer.WriteString("version", record.Version);
                writer.WriteString("publisherFingerprint", record.PublisherFingerprint);
                writer.WriteString("runtime", record.Runtime);
                writer.WriteString("contentHash", record.ContentHash);
                writer.WriteString("installRelativePath", record.InstallRelativePath);
                writer.WriteString("installedAtUtc", record.InstalledAtUtc);
                writer.WriteBoolean("isDevelopment", record.IsDevelopment);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        PluginPackageStorage.WriteAtomically(RegistryPath, output.ToArray());
    }
}

public sealed record PluginInstalledState(bool IsHealthy, IReadOnlyList<InstalledPackageRecord> Packages, IReadOnlyList<string> Failures);

public sealed record InstalledPackageRecord(
    string PackageId, string Version, string PublisherFingerprint, string Runtime,
    string ContentHash, string InstallRelativePath, DateTimeOffset InstalledAtUtc, bool IsDevelopment = false);

public sealed record PluginInstallResult(
    bool Succeeded, VerifiedPluginPackage? Package, string? InstallDirectory, IReadOnlyList<string> Failures)
{
    public static PluginInstallResult Ok(VerifiedPluginPackage package, string installDirectory) => new(true, package, installDirectory, []);
    public static PluginInstallResult Failed(IReadOnlyList<string> failures) => new(false, null, null, failures);
}
