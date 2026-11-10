using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Helpers;

/// <summary>
/// The single neutral palette for transient interaction states: marquee
/// selection, drag insertion indicators, drop previews and hover/selection
/// washes. These states describe what the pointer is doing, not what the
/// content means, so they draw from the theme's monochrome fill and stroke
/// brushes instead of the accent color.
///
/// WinUI's themed tokens cannot be resolved by element theme from code, so
/// the palette is mirrored into the application theme dictionaries under the
/// <see cref="FillSecondaryKey"/> family of keys. Resolution picks the
/// dictionary by the scope element's <see cref="FrameworkElement.ActualTheme"/>,
/// or by the app's effective theme when the caller owns no tree yet; a bare
/// application-scope lookup would follow the system theme and invert the
/// colors whenever the app's theme override disagrees with it.
/// <see cref="ResolveThemedResource(string, DependencyObject?)"/> and <see cref="IsDarkTheme"/>
/// generalize the same resolution to any resource or painting decision.
///
/// The resolved color is a snapshot: it does not follow a later theme flip,
/// so callers that paint with it re-apply from their ActualThemeChanged
/// handlers (the widget surfaces and windows already do).
/// </summary>
public static class NeutralInteractionBrush
{
    public const string FillSecondaryKey = "DeskBoxNeutralFillSecondaryBrush";
    public const string FillTertiaryKey = "DeskBoxNeutralFillTertiaryBrush";
    public const string LineKey = "DeskBoxNeutralLineBrush";
    public const string TextPrimaryKey = "DeskBoxNeutralTextPrimaryBrush";

    /// <summary>Subtle wash for a surface the pointer is over.</summary>
    public static Color Fill(DependencyObject? scope) =>
        ResolveThemedBrush(FillSecondaryKey, scope)?.Color ?? Colors.Transparent;

    /// <summary>Stronger neutral tone for a 1-2px line, bar or marquee outline.</summary>
    public static Color Line(DependencyObject? scope) =>
        ResolveThemedBrush(LineKey, scope)?.Color ?? Colors.Transparent;

    /// <summary>
    /// Effective theme for painting decisions in code: the scope element's
    /// resolved theme, or the app's effective theme when the element is not
    /// in a live tree yet. Never consults <see cref="Application.RequestedTheme"/>,
    /// which stays pinned to the startup system theme.
    /// </summary>
    public static bool IsDarkTheme(DependencyObject? scope) =>
        ResolveElementTheme(scope) == ElementTheme.Dark;

    /// <summary>
    /// Resolves a themed resource — a <see cref="Brush"/>, or a
    /// <see cref="Color"/> wrapped as a brush — for the scope element's
    /// effective theme. Lookup order mirrors element-scope XAML resolution:
    /// resources scoped to the element tree first, then the Light/Dark theme
    /// dictionaries of the application and its merged dictionaries (the WinUI
    /// dictionaries holding the system tokens live in the latter, and key
    /// their dark values under "Default"). Returns null when the key is
    /// absent everywhere.
    /// </summary>
    public static Brush? ResolveThemedResource(string key, DependencyObject? scope)
    {
        if (scope is FrameworkElement element)
        {
            // A host that overrides the key in its own scope keeps that
            // override.
            for (DependencyObject? current = element;
                 current is not null;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement candidate &&
                    candidate.Resources.TryGetValue(key, out object? scoped))
                {
                    return AsBrush(scoped);
                }
            }
        }

        return ResolveThemedResource(key, ResolveElementTheme(scope));
    }

    /// <summary>
    /// Resolves a themed resource directly for an explicit <see cref="ElementTheme"/>
    /// (<see cref="ElementTheme.Light"/> or <see cref="ElementTheme.Dark"/>) without
    /// requiring an attached visual tree scope.
    /// </summary>
    public static Brush? ResolveThemedResource(string key, ElementTheme explicitTheme)
    {
        bool isDark = explicitTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => (App.Current?.ThemeService?.EffectiveTheme ?? ElementTheme.Light) == ElementTheme.Dark
        };

        if (Application.Current?.Resources is not { } appResources)
        {
            return null;
        }

        foreach (string themeKey in isDark ? DarkThemeDictionaryKeys : LightThemeDictionaryKeys)
        {
            if (TryGetThemedDictionaryValue(appResources, themeKey, key, out object? value))
            {
                return AsBrush(value);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves one of the mirrored <c>DeskBoxNeutral*</c> brushes for the
    /// scope element's effective theme. The returned instance may be shared
    /// (a host override or the theme dictionary's own brush), so callers must
    /// copy the color instead of mutating it.
    /// </summary>
    public static SolidColorBrush? ResolveThemedBrush(string key, DependencyObject? scope) =>
        ResolveThemedResource(key, scope) as SolidColorBrush;

    // XamlControlsResources carries the dark values under "Default" and has
    // no "Dark" key; app dictionaries use explicit "Dark"/"Light" keys.
    private static readonly string[] DarkThemeDictionaryKeys = ["Dark", "Default"];
    private static readonly string[] LightThemeDictionaryKeys = ["Light"];

    private static Brush? AsBrush(object? value) => value switch
    {
        Brush brush => brush,
        Color color => new SolidColorBrush(color),
        _ => null
    };

    private static ElementTheme ResolveElementTheme(DependencyObject? scope)
    {
        if (scope is FrameworkElement element)
        {
            if (element.RequestedTheme is ElementTheme.Light or ElementTheme.Dark)
            {
                return element.RequestedTheme;
            }

            if (element.ActualTheme is ElementTheme.Light or ElementTheme.Dark)
            {
                return element.ActualTheme;
            }
        }

        return App.Current?.ThemeService?.EffectiveTheme ?? ElementTheme.Light;
    }

    internal static bool TryResolveBrushBackedColorKey(string resourceKey, out string colorKey)
    {
        if (string.Equals(resourceKey, "ApplicationPageBackgroundThemeBrush", StringComparison.Ordinal))
        {
            colorKey = "SolidBackgroundFillColorBase";
            return true;
        }

        const string suffix = "Brush";
        if (!string.IsNullOrEmpty(resourceKey) &&
            resourceKey.Length > suffix.Length &&
            resourceKey.EndsWith(suffix, StringComparison.Ordinal))
        {
            colorKey = resourceKey[..^suffix.Length];
            return true;
        }

        colorKey = string.Empty;
        return false;
    }

    private static bool TryGetThemedDictionaryValue(
        ResourceDictionary dictionary,
        string themeKey,
        string resourceKey,
        out object? value)
    {
        value = null;
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out object? themedObject) &&
            themedObject is ResourceDictionary themed)
        {
            // WinUI's XamlControlsResources defines *Brush entries inside
            // unattached ResourceDictionaries with Color="{ThemeResource ...}",
            // which stay pinned to Application.RequestedTheme (startup system
            // theme) instead of themeKey. Prefer the underlying Color struct
            // token when present so Light/Dark overrides resolve accurately.
            if (TryResolveBrushBackedColorKey(resourceKey, out string colorKey) &&
                themed.TryGetValue(colorKey, out object? colorToken) &&
                colorToken is Color)
            {
                value = colorToken;
                return true;
            }

            if (string.Equals(resourceKey, "ControlElevationBorderBrush", StringComparison.Ordinal) &&
                themed.TryGetValue("ControlStrokeColorDefault", out object? strokeDefaultObj) &&
                strokeDefaultObj is Color strokeDefault)
            {
                Color strokeSecondary = themed.TryGetValue("ControlStrokeColorSecondary", out object? strokeSecObj) &&
                                        strokeSecObj is Color sc
                    ? sc
                    : strokeDefault;
                bool isLightDict = string.Equals(themeKey, "Light", StringComparison.OrdinalIgnoreCase);
                var elevationBrush = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0.5, 0.0),
                    EndPoint = new Windows.Foundation.Point(0.5, 1.0)
                };
                elevationBrush.GradientStops.Add(new GradientStop
                {
                    Offset = isLightDict ? 0.90 : 0.0,
                    Color = isLightDict ? strokeDefault : strokeSecondary
                });
                elevationBrush.GradientStops.Add(new GradientStop
                {
                    Offset = isLightDict ? 1.0 : 0.10,
                    Color = isLightDict ? strokeSecondary : strokeDefault
                });
                value = elevationBrush;
                return true;
            }

            if (themed.TryGetValue(resourceKey, out value))
            {
                return true;
            }
        }

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            if (TryGetThemedDictionaryValue(merged, themeKey, resourceKey, out value))
            {
                return true;
            }
        }

        return false;
    }
}
