using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Binds one built-in widget kind to its official native package: which
/// package id to install/activate, which contribution serves the content,
/// and how legacy data hands off. Registration happens once at app startup.
/// </summary>
internal sealed record OfficialPackageBinding(
    WidgetKind Kind,
    string PackageId,
    string ContributionId,
    ILegacyInstanceMigration? Migration);

/// <summary>
/// The official-package manifest: kind → binding. The generic pilot and
/// data-sync machinery resolve everything feature-specific through this
/// registry, so adding the second package is a registration, not a new
/// branch in the plugin layer.
/// </summary>
internal static class PackageBindingRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<WidgetKind, OfficialPackageBinding> ByKind = [];
    private static readonly Dictionary<string, OfficialPackageBinding> ByPackageId = new(StringComparer.Ordinal);

    public static void Register(OfficialPackageBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (Gate)
        {
            ByKind[binding.Kind] = binding;
            ByPackageId[binding.PackageId] = binding;
        }
    }

    public static OfficialPackageBinding? TryGetByKind(WidgetKind kind)
    {
        lock (Gate)
        {
            return ByKind.TryGetValue(kind, out OfficialPackageBinding? binding) ? binding : null;
        }
    }

    public static OfficialPackageBinding? TryGetByPackageId(string packageId)
    {
        lock (Gate)
        {
            return ByPackageId.TryGetValue(packageId, out OfficialPackageBinding? binding) ? binding : null;
        }
    }
}

/// <summary>
/// Live instance ownership: instanceId → the owning feature's migration
/// adapter, populated at native create and cleared at destroy. The
/// write-through callback resolves the adapter DIRECTLY — no intermediate
/// ByPackageId lookup — so a package with multiple contributions routes
/// correctly (audit round 21 §11: Package ≠ Widget).
/// </summary>
internal static class PackageInstanceRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ILegacyInstanceMigration> MigrationByInstance = new(StringComparer.Ordinal);

    public static void Register(ILegacyInstanceMigration migration, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        lock (Gate)
        {
            MigrationByInstance[instanceId] = migration;
        }
    }

    public static void Unregister(string instanceId)
    {
        lock (Gate)
        {
            MigrationByInstance.Remove(instanceId);
        }
    }

    public static ILegacyInstanceMigration? TryResolveMigration(string instanceId)
    {
        lock (Gate)
        {
            return MigrationByInstance.TryGetValue(instanceId, out var migration) ? migration : null;
        }
    }
}
