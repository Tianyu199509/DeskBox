namespace DeskBox.Models;

public static class WidgetGroupNavigationStyles
{
    public const string FollowDefault = "FollowDefault";
    public const string Tabs = "Tabs";
    public const string EqualTabs = "EqualTabs";
    public const string Stack = "Stack";
    private const string LegacyAuto = "Auto";

    public static bool UsesTabStrip(string? value) =>
        value is Tabs or EqualTabs;

    public static bool UsesEqualWidthTabs(string? value) =>
        value == EqualTabs;

    public static string Normalize(string? value, bool allowFollowDefault)
    {
        return value switch
        {
            FollowDefault when allowFollowDefault => FollowDefault,
            Tabs => Tabs,
            EqualTabs => EqualTabs,
            Stack => Stack,
            // "Auto" was the pre-Tabs name for the stacked look — keep its
            // meaning, it is not an invalid value.
            LegacyAuto => Stack,
            // Invalid/corrupt values fall back to the current default style,
            // matching what a fresh install would get.
            _ => Tabs
        };
    }

    public static string Resolve(string? groupValue, string? defaultValue)
    {
        string normalized = Normalize(groupValue, allowFollowDefault: true);
        return normalized == FollowDefault
            ? Normalize(defaultValue, allowFollowDefault: false)
            : normalized;
    }
}
