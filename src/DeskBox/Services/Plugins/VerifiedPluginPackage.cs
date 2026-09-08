using System.Text.Json;

namespace DeskBox.Services.Plugins;

/// <summary>
/// The typed semantic model of a VERIFIED package (roadmap 16.12): the
/// verifier parses the manifest exactly once and everything downstream -
/// PackageManager, runtime, executor - consumes this model. Nobody
/// reparses raw manifest JSON, so interpretation drift is structurally
/// impossible.
/// </summary>
public sealed record VerifiedPluginPackage
{
    public required string PackageId { get; init; }

    public required string Version { get; init; }

    /// <summary>The lowercase-hex SHA-256 fingerprint of the raw public key - the pinning identity.</summary>
    public required string PublisherFingerprint { get; init; }

    public required string Runtime { get; init; }

    /// <summary>SHA-256 of the package.integrity bytes (lowercase hex) - the content address basis.</summary>
    public required string ContentHash { get; init; }

    public required string ManifestRelativePath { get; init; }

    public required IReadOnlyList<PluginRequestedPermission> Permissions { get; init; }

    public required IReadOnlyList<VerifiedContribution> Contributions { get; init; }

    public IReadOnlyDictionary<string, VerifiedDataSource> DataSources { get; init; } =
        new Dictionary<string, VerifiedDataSource>();

    public IReadOnlyDictionary<string, VerifiedAction> Actions { get; init; } =
        new Dictionary<string, VerifiedAction>();

    public string? EntryMain { get; init; }

    public string HostApiMinimum { get; init; } = "1.0.0";
    public string HostApiMaximum { get; init; } = "1.0.0";
    public JsonElement Fallback { get; init; }
    public JsonElement DataSchema { get; init; }
}

public sealed record PluginRequestedPermission(
    string Id,
    IReadOnlyList<string> AllowHosts,
    bool Required = true);

public sealed record VerifiedContribution(
    string Id,
    string DisplayName,
    string Template,
    IReadOnlyDictionary<string, string> PayloadStringFields,
    IReadOnlyDictionary<string, VerifiedBinding> Bindings)
{
    /// <summary>Owned structured JSON; arrays, objects and scalar types survive document disposal.</summary>
    public JsonElement Payload { get; init; }
    public VerifiedWidgetSize? DefaultSize { get; init; }
    public IReadOnlyList<string> ActivationEvents { get; init; } = [];
}

public sealed record VerifiedWidgetSize(int Width, int Height);

public sealed record VerifiedBinding(string Source, string Path);

public sealed record VerifiedDataSource(string Url, int RefreshSeconds);

public sealed record VerifiedAction(string Type, string Url);
