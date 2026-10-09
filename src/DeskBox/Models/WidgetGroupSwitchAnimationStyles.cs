namespace DeskBox.Models;

/// <summary>
/// Canonical member-switch transition choices for widget groups. The
/// persisted vocabulary is Auto/Vertical/Horizontal/Fade/None; Auto defers
/// the axis to the group's resolved navigation style at switch time.
/// </summary>
public static class WidgetGroupSwitchAnimationStyles
{
    public const string Auto = "Auto";
    public const string Vertical = "Vertical";
    public const string Horizontal = "Horizontal";
    public const string Fade = "Fade";
    public const string None = "None";

    public static string Normalize(string? value)
    {
        return value switch
        {
            Auto => Auto,
            Vertical => Vertical,
            Horizontal => Horizontal,
            Fade => Fade,
            None => None,
            // Invalid/corrupt values fall back to the fresh-install default,
            // matching what a fresh install would get.
            _ => Auto
        };
    }
}

/// <summary>
/// Runtime transition effect after the setting and the group's navigation
/// style resolve: the two slide axes, a pure cross fade, and the suppressed
/// (instant swap) effect.
/// </summary>
public enum WidgetGroupSwitchAnimationEffect
{
    Vertical,
    Horizontal,
    CrossFade,
    Suppress
}

/// <summary>
/// Resolves the persisted animation preference into the runtime effect.
/// Pure so the shell, the title switcher, and tests share one mapping: flat
/// tab strips are lateral navigation and switch horizontally, the collapsed
/// stack keeps the vertical push, and a missing presentation stays vertical
/// so non-group content swaps keep their current motion.
/// </summary>
public static class WidgetGroupSwitchAnimationPolicy
{
    public static WidgetGroupSwitchAnimationEffect ResolveEffect(
        string? settingValue,
        string? navigationStyle)
    {
        return WidgetGroupSwitchAnimationStyles.Normalize(settingValue) switch
        {
            WidgetGroupSwitchAnimationStyles.Vertical =>
                WidgetGroupSwitchAnimationEffect.Vertical,
            WidgetGroupSwitchAnimationStyles.Horizontal =>
                WidgetGroupSwitchAnimationEffect.Horizontal,
            WidgetGroupSwitchAnimationStyles.Fade =>
                WidgetGroupSwitchAnimationEffect.CrossFade,
            WidgetGroupSwitchAnimationStyles.None =>
                WidgetGroupSwitchAnimationEffect.Suppress,
            _ => ResolveAutoEffect(navigationStyle)
        };
    }

    private static WidgetGroupSwitchAnimationEffect ResolveAutoEffect(
        string? navigationStyle)
    {
        string normalized = WidgetGroupNavigationStyles.Normalize(
            navigationStyle is null
                ? WidgetGroupNavigationStyles.Stack
                : navigationStyle,
            allowFollowDefault: false);
        return normalized == WidgetGroupNavigationStyles.Tabs
            ? WidgetGroupSwitchAnimationEffect.Horizontal
            : WidgetGroupSwitchAnimationEffect.Vertical;
    }
}
