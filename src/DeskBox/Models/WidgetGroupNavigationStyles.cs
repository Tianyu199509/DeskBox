namespace DeskBox.Models;

public static class WidgetGroupNavigationStyles
{
    public const string FollowDefault = "FollowDefault";
    public const string Tabs = "Tabs";
    public const string Stack = "Stack";
    private const string LegacyAuto = "Auto";

    public static string Normalize(string? value, bool allowFollowDefault)
    {
        return value switch
        {
            FollowDefault when allowFollowDefault => FollowDefault,
            Tabs => Tabs,
            Stack => Stack,
            LegacyAuto => Stack,
            _ => Stack
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
