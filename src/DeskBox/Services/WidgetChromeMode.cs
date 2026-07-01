using DeskBox.Models;

namespace DeskBox.Services;

public enum WidgetChromeMode
{
    System,
    Standard,
    Compact,
    Overlay,
    Hidden
}

public enum WidgetChromeCategory
{
    Interactive,
    Display
}

public static class WidgetChromeModeNames
{
    public const string MetadataKey = "ChromeMode";
    public const string System = nameof(WidgetChromeMode.System);
    public const string Standard = nameof(WidgetChromeMode.Standard);
    public const string Compact = nameof(WidgetChromeMode.Compact);
    public const string Overlay = nameof(WidgetChromeMode.Overlay);
    public const string Hidden = nameof(WidgetChromeMode.Hidden);

    public static string ToSettingValue(WidgetChromeMode mode)
    {
        return mode switch
        {
            WidgetChromeMode.Compact => Compact,
            WidgetChromeMode.Overlay => Overlay,
            WidgetChromeMode.Hidden => Hidden,
            WidgetChromeMode.System => System,
            _ => Standard
        };
    }

    public static WidgetChromeMode NormalizeMode(string? value, WidgetChromeMode fallback = WidgetChromeMode.Standard, bool allowSystem = false)
    {
        if (Enum.TryParse(value, ignoreCase: true, out WidgetChromeMode mode) &&
            Enum.IsDefined(mode) &&
            (allowSystem || mode != WidgetChromeMode.System))
        {
            return mode;
        }

        return fallback;
    }

    public static string NormalizeSettingValue(string? value, WidgetChromeMode fallback = WidgetChromeMode.Standard)
    {
        return ToSettingValue(NormalizeMode(value, fallback));
    }

    public static WidgetChromeMode GetOverrideMode(WidgetConfig config)
    {
        if (config.Metadata is null ||
            !config.Metadata.TryGetValue(MetadataKey, out string? value))
        {
            return WidgetChromeMode.System;
        }

        return NormalizeMode(value, WidgetChromeMode.System, allowSystem: true);
    }

    public static void SetOverrideMode(WidgetConfig config, WidgetChromeMode mode)
    {
        config.Metadata ??= [];

        if (mode == WidgetChromeMode.System)
        {
            config.Metadata.Remove(MetadataKey);
            return;
        }

        config.Metadata[MetadataKey] = ToSettingValue(mode);
    }
}
