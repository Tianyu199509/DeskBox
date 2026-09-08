namespace DeskBox.Services.Plugins;

/// <summary>
/// Installer input budgets (roadmap 16.12): the verifier OWNS its limits so
/// a hostile package cannot burn CPU/memory/disk before its signature even
/// gets checked - and callers cannot forget to pre-check.
/// </summary>
public sealed record PluginVerificationLimits(
    long MaxManifestBytes = 256 * 1024,
    long MaxIntegrityBytes = 4 * 1024 * 1024,
    int MaxFileCount = 2_000,
    long MaxSingleFileBytes = 128 * 1024 * 1024,
    long MaxTotalExpandedBytes = 512 * 1024 * 1024,
    int MaxRelativePathLength = 200,
    int MaxTreeDepth = 16,
    int MaxDirectoryCount = 2_000)
{
    public static PluginVerificationLimits Default { get; } = new();
}
