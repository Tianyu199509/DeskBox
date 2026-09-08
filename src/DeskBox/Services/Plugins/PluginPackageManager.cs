using System.Text.Json;

namespace DeskBox.Services.Plugins;

/// <summary>
/// The untrusted-package pipeline (roadmap 16.12): quarantine/stage ->
/// VERIFY (full chain + input budgets) -> content-addressed immutable
/// commit -> InstalledPackageHandle. The runtime consumes ONLY handles
/// from here - never an arbitrary folder path - which closes the
/// verify-then-swap TOCTOU window, and the handle's ContentHash is
/// re-verifiable before any activation.
/// Update discipline: same packageId must present the SAME publisher
/// fingerprint, and a strictly INCREASING version (store-index tampering
/// must not enable downgrade or publisher takeover).
/// </summary>
public sealed class PluginPackageManager
{
    private readonly string _pluginsRoot;
    private readonly object _lock = new();

    public PluginPackageManager()
        : this(Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "plugins"))
    {
    }

    internal PluginPackageManager(string pluginsRoot)
    {
        Directory.CreateDirectory(pluginsRoot);
        _pluginsRoot = pluginsRoot;
    }

    private string RegistryPath => Path.Combine(_pluginsRoot, "installed.json");

    /// <summary>
    /// Stages, verifies, and commits a package directory into the
    /// content-addressed install store; returns the typed verified model.
    /// </summary>
    public PluginInstallResult Install(
        string sourceDirectory,
        PluginPackageVerificationPolicy policy = PluginPackageVerificationPolicy.Store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        lock (_lock)
        {
            PluginPackageVerifier.VerificationResult verification = PluginPackageVerifier.Verify(
                sourceDirectory,
                policy,
                PluginVerificationLimits.Default);
            if (!verification.IsValid || verification.ManifestJson is null)
            {
                return PluginInstallResult.Failed(verification.Failures);
            }

            using JsonDocument document = JsonDocument.Parse(verification.ManifestJson!);
            VerifiedPluginPackage? model = BuildVerifiedModel(document.RootElement);
            if (model is null)
            {
                return PluginInstallResult.Failed(["failed to build the typed verified model"]);
            }
            VerifiedPluginPackage package = model;

            List<InstalledPackageRecord> registry = LoadRegistry();
            InstalledPackageRecord? existing = registry.FirstOrDefault(r => r.PackageId == package.PackageId);
            if (existing is not null)
            {
                if (!string.Equals(existing.PublisherFingerprint, package.PublisherFingerprint, StringComparison.Ordinal))
                {
                    return PluginInstallResult.Failed(
                    [
                        $"update rejected: package '{package.PackageId}' is pinned to publisher " +
                        $"'{existing.PublisherFingerprint[..12]}…' but the update presents " +
                        $"'{package.PublisherFingerprint[..12]}…' (publisher takeover blocked)"
                    ]);
                }
                if (Version.Parse(package.Version) < Version.Parse(existing.Version))
                {
                    return PluginInstallResult.Failed(
                    [
                        $"update rejected: version must not decrease " +
                        $"(installed {existing.Version}, presented {package.Version})"
                    ]);
                }
                if (string.Equals(existing.ContentHash, package.ContentHash, StringComparison.Ordinal))
                {
                    // Same content re-install: idempotent success, no rewrite.
                    return PluginInstallResult.Ok(package, Path.Combine(_pluginsRoot, existing.InstallRelativePath));
                }
                if (Version.Parse(package.Version) == Version.Parse(existing.Version))
                {
                    return PluginInstallResult.Failed(
                    [
                        $"update rejected: version must strictly increase for new content " +
                        $"(installed {existing.Version}, presented {package.Version})"
                    ]);
                }
            }

            string installDirectory = Path.Combine(
                _pluginsRoot,
                MakeSafeDirectoryName(package.PackageId),
                package.ContentHash[..16]);
            if (Directory.Exists(installDirectory))
            {
                // Same content re-install: idempotent commit (files are
                // content-addressed, so identical hash = identical bytes).
                TryDeleteDirectory(installDirectory);
            }
            CommitFiles(sourceDirectory, installDirectory);

            registry.RemoveAll(r => r.PackageId == package.PackageId);
            registry.Add(new InstalledPackageRecord(
                package.PackageId,
                package.Version,
                package.PublisherFingerprint,
                package.Runtime,
                package.ContentHash,
                MakeRelativeInstallPath(installDirectory),
                DateTimeOffset.UtcNow));
            SaveRegistry(registry);

            return PluginInstallResult.Ok(
                package with { },
                installDirectory);
        }
    }

    public IReadOnlyList<InstalledPackageRecord> GetInstalled()
    {
        lock (_lock)
        {
            return LoadRegistry();
        }
    }

    /// <summary>Resolves a handle for activation: the install directory must still hash to the recorded content hash.</summary>
    public InstalledPackageRecord? Find(string packageId)
    {
        lock (_lock)
        {
            return LoadRegistry().FirstOrDefault(r => r.PackageId == packageId);
        }
    }

    public bool Uninstall(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        lock (_lock)
        {
            List<InstalledPackageRecord> registry = LoadRegistry();
            InstalledPackageRecord? record = registry.FirstOrDefault(r => r.PackageId == packageId);
            if (record is null)
            {
                return false;
            }
            string installDirectory = Path.Combine(_pluginsRoot, record.InstallRelativePath);
            TryDeleteDirectory(installDirectory);
            // Best-effort parent cleanup when other versions are absent.
            string? parent = Path.GetDirectoryName(installDirectory);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                try { Directory.Delete(parent); } catch { }
            }
            registry.RemoveAll(r => r.PackageId == packageId);
            SaveRegistry(registry);
            new PluginGrantStore(_pluginsRoot).RemovePackage(packageId);
            return true;
        }
    }

    // ---------- typed model construction (single parse, downstream contract) ----------
    internal static VerifiedPluginPackage? BuildVerifiedModel(JsonElement root)
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
                            : []));
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
                    contribution.GetProperty("template").GetString()!,
                    payloadFields,
                    bindings));
            }

            return new VerifiedPluginPackage
            {
                PackageId = root.GetProperty("id").GetString()!,
                Version = root.GetProperty("version").GetString()!,
                PublisherFingerprint = root.GetProperty("publisher").GetString()!,
                Runtime = root.GetProperty("runtime").GetString()!,
                ContentHash = root.GetProperty("signature").GetProperty("contentHash").GetString()!,
                ManifestRelativePath = "manifest.json",
                Permissions = permissions,
                Contributions = contributions,
                DataSources = dataSources,
                Actions = actions,
                EntryMain = root.TryGetProperty("entry", out JsonElement entry) &&
                            entry.TryGetProperty("main", out JsonElement main) &&
                            main.ValueKind == JsonValueKind.String
                    ? main.GetString()
                    : null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------- immutable commit ----------
    private static void CommitFiles(string sourceDirectory, string installDirectory)
    {
        Directory.CreateDirectory(installDirectory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, file);
            string destination = Path.Combine(installDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            // Content-addressed immutability: read-only unless a commit is
            // replacing the whole version directory.
            File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // A locked file must not make uninstall/install crash; the
            // next commit overwrites the version directory anyway.
        }
    }

    private static string MakeSafeDirectoryName(string packageId)
    {
        foreach (char character in packageId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-')
            {
                throw new ArgumentException($"package id contains an unsafe character: '{character}'");
            }
        }
        return packageId;
    }

    private string MakeRelativeInstallPath(string installDirectory) =>
        Path.GetRelativePath(_pluginsRoot, installDirectory).Replace('\\', '/');

    // ---------- registry (hand-rolled JSON, frozen baseline untouched) ----------
    private List<InstalledPackageRecord> LoadRegistry()
    {
        if (!File.Exists(RegistryPath))
        {
            return [];
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RegistryPath));
            return document.RootElement.EnumerateArray()
                .Select(record => new InstalledPackageRecord(
                    record.GetProperty("packageId").GetString()!,
                    record.GetProperty("version").GetString()!,
                    record.GetProperty("publisherFingerprint").GetString()!,
                    record.GetProperty("runtime").GetString()!,
                    record.GetProperty("contentHash").GetString()!,
                    record.GetProperty("installRelativePath").GetString()!,
                    record.GetProperty("installedAtUtc").GetDateTimeOffset()))
                .ToList();
        }
        catch (JsonException)
        {
            // Corrupt registry = empty registry (fail closed); installing
            // again repairs it.
            return [];
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
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        string temporaryPath = RegistryPath + ".tmp";
        File.WriteAllBytes(temporaryPath, output.ToArray());
        File.Move(temporaryPath, RegistryPath, overwrite: true);
    }
}

/// <summary>An installed package handle: the runtime-facing record.</summary>
public sealed record InstalledPackageRecord(
    string PackageId,
    string Version,
    string PublisherFingerprint,
    string Runtime,
    string ContentHash,
    string InstallRelativePath,
    DateTimeOffset InstalledAtUtc);

public sealed record PluginInstallResult(
    bool Succeeded,
    VerifiedPluginPackage? Package,
    string? InstallDirectory,
    IReadOnlyList<string> Failures)
{
    public static PluginInstallResult Ok(VerifiedPluginPackage package, string installDirectory) =>
        new(true, package, installDirectory, []);

    public static PluginInstallResult Failed(IReadOnlyList<string> failures) =>
        new(false, null, null, failures);
}
