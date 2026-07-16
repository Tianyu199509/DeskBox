using DeskBox.Models;

namespace DeskBox.Services;

public enum WidgetCollapseBehavior
{
    System,
    Expanded,
    Click,
    Smart
}

public static class WidgetCollapseBehaviorNames
{
    public const string MetadataKey = "CollapseBehavior";
    public const string System = nameof(WidgetCollapseBehavior.System);
    public const string Expanded = nameof(WidgetCollapseBehavior.Expanded);
    public const string Click = nameof(WidgetCollapseBehavior.Click);
    public const string Smart = nameof(WidgetCollapseBehavior.Smart);

    public static WidgetCollapseBehavior Normalize(
        string? value,
        WidgetCollapseBehavior fallback = WidgetCollapseBehavior.Click,
        bool allowSystem = false)
    {
        if (string.Equals(value, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCollapseBehavior.Click;
        }

        if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCollapseBehavior.Smart;
        }

        if (Enum.TryParse(value, ignoreCase: true, out WidgetCollapseBehavior behavior) &&
            Enum.IsDefined(behavior) &&
            (allowSystem || behavior != WidgetCollapseBehavior.System))
        {
            return behavior;
        }

        return fallback;
    }

    public static string ToSettingValue(WidgetCollapseBehavior behavior)
    {
        return behavior switch
        {
            WidgetCollapseBehavior.Expanded => Expanded,
            WidgetCollapseBehavior.Smart => Smart,
            WidgetCollapseBehavior.System => System,
            _ => Click
        };
    }

    public static WidgetCollapseBehavior GetOverride(WidgetConfig config)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(MetadataKey, out string? value))
        {
            return WidgetCollapseBehavior.System;
        }

        return Normalize(value, WidgetCollapseBehavior.System, allowSystem: true);
    }

    public static WidgetCollapseBehavior Resolve(WidgetConfig config, string? globalValue)
    {
        WidgetCollapseBehavior overrideBehavior = GetOverride(config);
        return overrideBehavior == WidgetCollapseBehavior.System
            ? Normalize(globalValue)
            : overrideBehavior;
    }

    public static void SetOverride(WidgetConfig config, WidgetCollapseBehavior behavior)
    {
        config.Metadata ??= [];
        if (behavior == WidgetCollapseBehavior.System)
        {
            config.Metadata.Remove(MetadataKey);
            return;
        }

        config.Metadata[MetadataKey] = ToSettingValue(behavior);
    }
}
