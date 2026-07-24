using DeskBox.Models;
using System.Text.Json;

namespace DeskBox.Services;

public static class WidgetFileStackSettings
{
    public const string EnabledOverrideMetadataKey = "FileStacksEnabled";
    public const string GroupByOverrideMetadataKey = "FileStackGroupBy";
    public const string ThresholdOverrideMetadataKey = "FileStackThreshold";
    public const string OrderByOverrideMetadataKey = "FileStackOrderBy";
    public const string DisabledStacksMetadataKey = "FileStackDisabledGroups";
    public const string StackNameOverridesMetadataKey = "FileStackNameOverrides";
    public const string StackOrderMetadataKey = "FileStackGroupOrder";

    public static bool? GetEnabledOverride(WidgetConfig config)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(EnabledOverrideMetadataKey, out string? value) ||
            !bool.TryParse(value, out bool enabled))
        {
            return null;
        }

        return enabled;
    }

    public static string? GetGroupByOverride(WidgetConfig config)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(GroupByOverrideMetadataKey, out string? value))
        {
            return null;
        }

        if (!string.Equals(value, SettingsService.FileStackGroupByKind, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, SettingsService.FileStackGroupByDateAdded, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, SettingsService.FileStackGroupByDateCreated, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, SettingsService.FileStackGroupByDateModified, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, SettingsService.FileStackGroupByCustom, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string normalized = SettingsService.NormalizeFileStackGroupBy(value);
        return normalized == SettingsService.FileStackGroupByDateAdded
            ? SettingsService.FileStackGroupByKind
            : normalized;
    }

    public static int? GetThresholdOverride(WidgetConfig config)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(ThresholdOverrideMetadataKey, out string? value) ||
            !int.TryParse(value, out int threshold) ||
            SettingsService.NormalizeFileStackThreshold(threshold) != threshold)
        {
            return null;
        }

        return threshold;
    }

    public static string? GetOrderByOverride(WidgetConfig config)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(OrderByOverrideMetadataKey, out string? value))
        {
            return null;
        }

        if (!string.Equals(value, SettingsService.FileStackOrderByWidget, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, SettingsService.FileStackOrderByName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, SettingsService.FileStackOrderByDateAdded, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, SettingsService.FileStackOrderByDateModified, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return SettingsService.NormalizeFileStackOrderBy(value);
    }

    public static bool ResolveEnabled(WidgetConfig config, bool globalDefault) =>
        GetEnabledOverride(config) ?? globalDefault;

    public static string ResolveGroupBy(WidgetConfig config, string? globalDefault) =>
        GetGroupByOverride(config) ?? SettingsService.NormalizeFileStackGroupBy(globalDefault);

    public static int ResolveThreshold(WidgetConfig config, int globalDefault) =>
        GetThresholdOverride(config) ?? SettingsService.NormalizeFileStackThreshold(globalDefault);

    public static string ResolveOrderBy(WidgetConfig config, string? globalDefault) =>
        GetOrderByOverride(config) ?? SettingsService.NormalizeFileStackOrderBy(globalDefault);

    public static bool FollowsGlobalDefaults(WidgetConfig config) =>
        GetEnabledOverride(config) is null &&
        GetGroupByOverride(config) is null &&
        GetThresholdOverride(config) is null &&
        GetOrderByOverride(config) is null;

    public static void SetEnabledOverride(WidgetConfig config, bool? enabled)
    {
        config.Metadata ??= [];
        if (enabled is null)
        {
            config.Metadata.Remove(EnabledOverrideMetadataKey);
            return;
        }

        config.Metadata[EnabledOverrideMetadataKey] = enabled.Value.ToString();
    }

    public static void SetGroupByOverride(WidgetConfig config, string? groupBy)
    {
        config.Metadata ??= [];
        if (groupBy is null)
        {
            config.Metadata.Remove(GroupByOverrideMetadataKey);
            return;
        }

        string normalized = SettingsService.NormalizeFileStackGroupBy(groupBy);
        config.Metadata[GroupByOverrideMetadataKey] =
            normalized == SettingsService.FileStackGroupByDateAdded
                ? SettingsService.FileStackGroupByKind
                : normalized;
    }

    public static void SetThresholdOverride(WidgetConfig config, int? threshold)
    {
        config.Metadata ??= [];
        if (threshold is null)
        {
            config.Metadata.Remove(ThresholdOverrideMetadataKey);
            return;
        }

        config.Metadata[ThresholdOverrideMetadataKey] =
            SettingsService.NormalizeFileStackThreshold(threshold.Value).ToString();
    }

    public static void SetOrderByOverride(WidgetConfig config, string? orderBy)
    {
        config.Metadata ??= [];
        if (orderBy is null)
        {
            config.Metadata.Remove(OrderByOverrideMetadataKey);
            return;
        }

        config.Metadata[OrderByOverrideMetadataKey] =
            SettingsService.NormalizeFileStackOrderBy(orderBy);
    }

    public static void ClearOverrides(WidgetConfig config)
    {
        config.Metadata?.Remove(EnabledOverrideMetadataKey);
        config.Metadata?.Remove(GroupByOverrideMetadataKey);
        config.Metadata?.Remove(ThresholdOverrideMetadataKey);
        config.Metadata?.Remove(OrderByOverrideMetadataKey);
    }

    // ── Stack customizations (rename / unstack / manual group order) ──

    /// <summary>
    /// Group keys whose stacks the user explicitly dissolved ("don't stack this
    /// group"). Members of these groups are always projected as loose items.
    /// </summary>
    public static HashSet<string> GetDisabledStacks(WidgetConfig config) =>
        ReadStringCollection(config, DisabledStacksMetadataKey);

    public static void SetDisabledStacks(WidgetConfig config, IEnumerable<string> stackKeys) =>
        WriteStringCollection(config, DisabledStacksMetadataKey, stackKeys);

    /// <summary>User-assigned display names per stack group key.</summary>
    public static Dictionary<string, string> GetStackNameOverrides(WidgetConfig config)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(StackNameOverridesMetadataKey, out string? json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ??
                new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public static void SetStackNameOverrides(WidgetConfig config, IReadOnlyDictionary<string, string> overrides)
    {
        config.Metadata ??= [];
        if (overrides.Count == 0)
        {
            config.Metadata.Remove(StackNameOverridesMetadataKey);
            return;
        }

        config.Metadata[StackNameOverridesMetadataKey] = JsonSerializer.Serialize(overrides);
    }

    /// <summary>
    /// Manual group order (group keys in display order). Keys that no longer exist
    /// are ignored at merge time; new groups append in default order.
    /// </summary>
    public static List<string> GetStackOrder(WidgetConfig config) =>
        [.. ReadStringCollection(config, StackOrderMetadataKey)];

    public static void SetStackOrder(WidgetConfig config, IEnumerable<string>? stackKeys)
    {
        if (stackKeys is null)
        {
            config.Metadata?.Remove(StackOrderMetadataKey);
            return;
        }

        WriteStringCollection(config, StackOrderMetadataKey, stackKeys);
    }

    private static HashSet<string> ReadStringCollection(WidgetConfig config, string key)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(key, out string? json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) is { } list
                ? new HashSet<string>(list, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void WriteStringCollection(WidgetConfig config, string key, IEnumerable<string> values)
    {
        config.Metadata ??= [];
        var list = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0)
        {
            config.Metadata.Remove(key);
            return;
        }

        config.Metadata[key] = JsonSerializer.Serialize(list);
    }

    public static bool NormalizeOverrides(WidgetConfig config)
    {
        if (config.Metadata is null)
        {
            return false;
        }

        bool changed = NormalizeOverride(
            config,
            EnabledOverrideMetadataKey,
            GetEnabledOverride(config)?.ToString());
        changed |= NormalizeOverride(
            config,
            GroupByOverrideMetadataKey,
            GetGroupByOverride(config));
        changed |= NormalizeOverride(
            config,
            ThresholdOverrideMetadataKey,
            GetThresholdOverride(config)?.ToString());
        changed |= NormalizeOverride(
            config,
            OrderByOverrideMetadataKey,
            GetOrderByOverride(config));
        return changed;
    }

    private static bool NormalizeOverride(
        WidgetConfig config,
        string key,
        string? normalizedValue)
    {
        if (!config.Metadata.TryGetValue(key, out string? currentValue))
        {
            return false;
        }

        if (normalizedValue is null)
        {
            config.Metadata.Remove(key);
            return true;
        }

        if (string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
        {
            return false;
        }

        config.Metadata[key] = normalizedValue;
        return true;
    }
}
