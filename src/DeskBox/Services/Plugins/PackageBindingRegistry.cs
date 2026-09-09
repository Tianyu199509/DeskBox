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
/// Live instance ownership: instanceId → packageId, populated at native
/// create and cleared at destroy. Lets the generic HostApi write-through
/// callback route a settings patch to the owning feature's adapter without
/// the bridge knowing any feature.
/// </summary>
internal static class PackageInstanceRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> PackageIdByInstance = new(StringComparer.Ordinal);

    public static void Register(string packageId, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        lock (Gate)
        {
            PackageIdByInstance[instanceId] = packageId;
        }
    }

    public static void Unregister(string instanceId)
    {
        lock (Gate)
        {
            PackageIdByInstance.Remove(instanceId);
        }
    }

    public static string? TryResolvePackageId(string instanceId)
    {
        lock (Gate)
        {
            return PackageIdByInstance.TryGetValue(instanceId, out string? packageId) ? packageId : null;
        }
    }
}
