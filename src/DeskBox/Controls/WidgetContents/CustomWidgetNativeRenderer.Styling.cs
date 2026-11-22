using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public readonly record struct CustomWidgetHostAppearanceProfile(
    double TextSize,
    double LayoutDensityScale,
    double HorizontalSpacingScale,
    double VerticalSpacingScale,
    string CornerPreference)
{
    public static CustomWidgetHostAppearanceProfile Default => new(
        SettingsService.DefaultTextSize,
        SettingsService.DefaultLayoutDensityScale,
        SettingsService.DefaultHorizontalSpacingScale,
        SettingsService.DefaultVerticalSpacingScale,
        SettingsService.WidgetCornerPreferenceRound);

    public static CustomWidgetHostAppearanceProfile FromSettings(AppSettings? settings)
    {
        if (settings is null)
        {
            return Default;
        }

        return new CustomWidgetHostAppearanceProfile(
            SettingsService.NormalizeTextSize(settings.WidgetShell.TextSize),
            Math.Clamp(settings.WidgetShell.LayoutDensityScale, SettingsService.MinLayoutDensityScale, SettingsService.MaxLayoutDensityScale),
            Math.Clamp(settings.WidgetShell.HorizontalSpacingScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale),
            Math.Clamp(settings.WidgetShell.VerticalSpacingScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale),
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(settings.WidgetShell.WidgetCornerPreference));
    }

    public bool ApproximatelyEquals(CustomWidgetHostAppearanceProfile other) =>
        Math.Abs(TextSize - other.TextSize) < 0.01 &&
        Math.Abs(LayoutDensityScale - other.LayoutDensityScale) < 0.001 &&
        Math.Abs(HorizontalSpacingScale - other.HorizontalSpacingScale) < 0.001 &&
        Math.Abs(VerticalSpacingScale - other.VerticalSpacingScale) < 0.001 &&
        string.Equals(CornerPreference, other.CornerPreference, StringComparison.Ordinal);

    public double DensityMultiplier => ResolveAnchoredMultiplier(
        LayoutDensityScale,
        SettingsService.DefaultLayoutDensityScale,
        minMultiplier: 0.78,
        maxMultiplier: 1.28);

    public double HorizontalSpacingMultiplier => ResolveAnchoredMultiplier(
        HorizontalSpacingScale,
        SettingsService.DefaultHorizontalSpacingScale,
        minMultiplier: 0.85,
        maxMultiplier: 1.22);

    public double VerticalSpacingMultiplier => ResolveAnchoredMultiplier(
        VerticalSpacingScale,
        SettingsService.DefaultVerticalSpacingScale,
        minMultiplier: 0.85,
        maxMultiplier: 1.22);

    public (double Small, double Medium, double Large) ResolveCornerRadii() =>
        CornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => (0.0, 0.0, 0.0),
            SettingsService.WidgetCornerPreferenceSmall => (2.0, 3.0, 4.0),
            SettingsService.WidgetCornerPreferenceLarge => (5.0, 8.0, 10.0),
            _ => (4.0, 6.0, 8.0)
        };

    private static double ResolveAnchoredMultiplier(
        double scale,
        double standardPoint,
        double minMultiplier,
        double maxMultiplier)
    {
        double clamped = Math.Clamp(scale, 0.0, 1.0);
        if (Math.Abs(clamped - standardPoint) <= 0.0005)
        {
            return 1.0;
        }

        return clamped < standardPoint
            ? minMultiplier + ((1.0 - minMultiplier) * (clamped / Math.Max(0.01, standardPoint)))
            : 1.0 + ((maxMultiplier - 1.0) * ((clamped - standardPoint) / Math.Max(0.01, 1.0 - standardPoint)));
    }
}

public sealed partial class CustomWidgetNativeRenderer
{
    private static readonly FontFamily DisplayFontFamily = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily MonoFontFamily = new("Cascadia Mono, Consolas");
    private static readonly FontFamily MonoFallbackFontFamily = new("Cascadia Mono, Consolas, Segoe UI");

    private static HorizontalAlignment ParseHorizontalAlignment(
        string? raw,
        HorizontalAlignment fallback = HorizontalAlignment.Stretch)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        ReadOnlySpan<char> token = raw.AsSpan().Trim();
        if (token.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return HorizontalAlignment.Left;
        }

        if (token.Equals("center", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("middle", StringComparison.OrdinalIgnoreCase))
        {
            return HorizontalAlignment.Center;
        }

        if (token.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            return HorizontalAlignment.Right;
        }

        if (token.Equals("stretch", StringComparison.OrdinalIgnoreCase))
        {
            return HorizontalAlignment.Stretch;
        }

        return fallback;
    }

    private static VerticalAlignment ParseVerticalAlignment(
        string? raw,
        VerticalAlignment fallback = VerticalAlignment.Stretch)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        ReadOnlySpan<char> token = raw.AsSpan().Trim();
        if (token.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            return VerticalAlignment.Top;
        }

        if (token.Equals("center", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("middle", StringComparison.OrdinalIgnoreCase))
        {
            return VerticalAlignment.Center;
        }

        if (token.Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            return VerticalAlignment.Bottom;
        }

        if (token.Equals("stretch", StringComparison.OrdinalIgnoreCase))
        {
            return VerticalAlignment.Stretch;
        }

        return fallback;
    }

    private CustomWidgetHostAppearanceProfile EffectiveAppearance
    {
        get
        {
            CustomWidgetHostAppearanceProfile profile = _appearanceProvider?.Invoke() ?? CustomWidgetHostAppearanceProfile.Default;
            if (_textSizeProvider is not null)
            {
                profile = profile with { TextSize = _textSizeProvider() };
            }

            return profile;
        }
    }

    private double RadiusSmall => EffectiveAppearance.ResolveCornerRadii().Small;
    private double RadiusMedium => EffectiveAppearance.ResolveCornerRadii().Medium;
    private double RadiusLarge => EffectiveAppearance.ResolveCornerRadii().Large;

    private CornerRadius CornerSmall => new(RadiusSmall);
    private CornerRadius CornerMedium => new(RadiusMedium);
    private CornerRadius CornerLarge => new(RadiusLarge);

    private bool IsDark => NeutralInteractionBrush.IsDarkTheme(_themeScope);

    internal double BodyFontSize =>
        SettingsService.NormalizeTextSize(EffectiveAppearance.TextSize);

    internal double CaptionFontSize =>
        Math.Max(SettingsService.MinTextSize - 1.0, BodyFontSize - 1.0);

    internal double SubtitleFontSize =>
        Math.Min(SettingsService.MaxTextSize + 1.0, BodyFontSize + 1.0);

    internal double MetricFontSize =>
        Math.Round(Math.Clamp((BodyFontSize + 4.5) * EffectiveAppearance.DensityMultiplier, 13.5, 22.0) * 2.0) / 2.0;

    internal double HeroFontSize =>
        Math.Round(Math.Clamp((BodyFontSize + 9.5) * EffectiveAppearance.DensityMultiplier, 17.0, 28.0) * 2.0) / 2.0;

    private Brush DividerBrush => GetBrush(
        "WidgetLayerStrokeBrush",
        IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));

    private Brush CardStrokeBrush => IsDark
        ? GetOrCreateSolidBrush(Colors.Transparent)
        : GetOrCreateSolidBrush(Color.FromArgb(16, 0, 0, 0));

    private Thickness CardBorderThickness => IsDark
        ? new Thickness(0)
        : new Thickness(1);

    private Brush SubtleHoverBrush => GetBrush(
        "SubtleFillColorSecondaryBrush",
        IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0));

    private Brush SubtlePressedBrush => GetBrush(
        "SubtleFillColorTertiaryBrush",
        IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0));

    private Button CreateSubtleIconButton(
        string glyph,
        double size = 26,
        double iconSize = 12,
        string? tooltip = null)
    {
        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
        var btn = new Button
        {
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            Padding = new Thickness(0),
            CornerRadius = CornerMedium,
            BorderThickness = new Thickness(0),
            BorderBrush = transparent,
            Background = transparent,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                Glyph = NormalizeFluentGlyphForOs(glyph, WindowsCompatibilityService.IsWindows11OrLater),
                FontSize = iconSize,
                Foreground = ResolveForegroundBrush("secondary")
            }
        };
        ApplyButtonStateOverrides(btn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            ToolTipService.SetToolTip(btn, tooltip);
        }

        return btn;
    }

    private Button CreateElevatedTransportButton(UIElement content, double size = 36)
    {
        Brush restBg = GetBrush(
            "ControlFillColorDefaultBrush",
            IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(182, 255, 255, 255));
        Brush hoverBg = GetBrush(
            "ControlFillColorSecondaryBrush",
            IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(215, 255, 255, 255));
        Brush pressedBg = GetBrush(
            "ControlFillColorTertiaryBrush",
            IsDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255));
        Brush restBorder = GetBrush(
            "ControlElevationBorderBrush",
            IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
        Brush hoverBorder = GetBrush(
            "ControlStrokeColorSecondaryBrush",
            IsDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0));

        var btn = new Button
        {
            Width = size,
            Height = size,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            CornerRadius = CornerLarge,
            BorderThickness = new Thickness(1),
            BorderBrush = restBorder,
            Background = restBg,
            Foreground = ResolveForegroundBrush("default"),
            UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content
        };
        ApplyButtonStateOverrides(btn, hoverBg, pressedBg, hoverBorder, restBorder);
        return btn;
    }

    private void ApplySegmentedItemState(Button btn, bool active)
    {
        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
        if (active)
        {
            Brush selectedBg = GetBrush(
                "ControlFillColorDefaultBrush",
                IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(220, 255, 255, 255));
            Brush selectedHover = GetBrush(
                "ControlFillColorSecondaryBrush",
                IsDark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(238, 255, 255, 255));
            Brush selectedPressed = GetBrush(
                "ControlFillColorTertiaryBrush",
                IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(196, 255, 255, 255));
            Brush selectedBorder = GetBrush(
                "ControlElevationBorderBrush",
                IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0));

            btn.Background = selectedBg;
            btn.BorderBrush = selectedBorder;
            btn.BorderThickness = new Thickness(1);
            btn.Foreground = ResolveForegroundBrush("default");
            btn.FontWeight = FontWeights.SemiBold;
            ApplyButtonStateOverrides(btn, selectedHover, selectedPressed, selectedBorder, selectedBorder);
        }
        else
        {
            Brush neutralHover = NeutralInteractionBrush.ResolveThemedBrush(
                NeutralInteractionBrush.FillSecondaryKey,
                _themeScope) ?? SubtleHoverBrush;
            Brush neutralPressed = NeutralInteractionBrush.ResolveThemedBrush(
                NeutralInteractionBrush.FillTertiaryKey,
                _themeScope) ?? SubtlePressedBrush;
            btn.Background = transparent;
            btn.BorderBrush = transparent;
            btn.BorderThickness = new Thickness(1);
            btn.Foreground = ResolveForegroundBrush("secondary");
            btn.FontWeight = FontWeights.Normal;
            ApplyButtonStateOverrides(btn, neutralHover, neutralPressed, transparent, transparent);
        }
    }

    private void StyleStandardCommandButton(Button btn, CornerRadius? radius = null)
    {
        Brush restBg = GetBrush(
            "ControlFillColorDefaultBrush",
            IsDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(178, 255, 255, 255));
        Brush hoverBg = GetBrush(
            "ControlFillColorSecondaryBrush",
            IsDark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(212, 255, 255, 255));
        Brush pressedBg = GetBrush(
            "ControlFillColorTertiaryBrush",
            IsDark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(148, 255, 255, 255));
        Brush border = GetBrush(
            "ControlElevationBorderBrush",
            IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0));

        btn.CornerRadius = radius ?? CornerMedium;
        btn.Background = restBg;
        btn.BorderBrush = border;
        btn.BorderThickness = new Thickness(1);
        btn.Foreground = ResolveForegroundBrush("default");
        ApplyButtonStateOverrides(btn, hoverBg, pressedBg, border, border);
    }

    private static void ApplyButtonStateOverrides(
        Button btn,
        Brush hoverBg,
        Brush pressedBg,
        Brush hoverBorder,
        Brush pressedBorder)
    {
        btn.Resources["ButtonBackgroundPointerOver"] = hoverBg;
        btn.Resources["ButtonBackgroundPressed"] = pressedBg;
        btn.Resources["ButtonBorderBrushPointerOver"] = hoverBorder;
        btn.Resources["ButtonBorderBrushPressed"] = pressedBorder;
    }

    private void ApplyTypography(TextBlock tb, string style)
    {
        ReadOnlySpan<char> s = style.AsSpan().Trim();
        if (s.Equals("caption", StringComparison.OrdinalIgnoreCase))
        {
            tb.FontSize = CaptionFontSize;
        }
        else if (s.Equals("bodystrong", StringComparison.OrdinalIgnoreCase))
        {
            tb.FontSize = BodyFontSize;
            tb.FontWeight = FontWeights.SemiBold;
        }
        else if (s.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
        {
            tb.FontSize = SubtitleFontSize;
            tb.FontWeight = FontWeights.SemiBold;
        }
        else if (s.Equals("metric", StringComparison.OrdinalIgnoreCase) ||
                 s.Equals("title", StringComparison.OrdinalIgnoreCase))
        {
            tb.FontFamily = DisplayFontFamily;
            tb.FontSize = MetricFontSize;
            tb.FontWeight = FontWeights.SemiBold;
            SetTabularNumerals(tb);
        }
        else if (s.Equals("hero", StringComparison.OrdinalIgnoreCase))
        {
            tb.FontFamily = DisplayFontFamily;
            tb.FontSize = HeroFontSize;
            tb.FontWeight = FontWeights.SemiBold;
            tb.TextLineBounds = TextLineBounds.Tight;
            SetTabularNumerals(tb);
        }
        else if (s.Equals("mono", StringComparison.OrdinalIgnoreCase))
        {
            tb.FontSize = Math.Max(10.0, BodyFontSize - 0.5);
            tb.FontFamily = MonoFontFamily;
            tb.FlowDirection = FlowDirection.LeftToRight;
            SetTabularNumerals(tb);
        }
        else
        {
            tb.FontSize = BodyFontSize;
        }
    }

    /// <summary>
    /// Resolves semantic brushes explicitly against the widget's actual theme polarity so
    /// Dark-mode text/strokes always maintain WCAG relative luminance &gt; 0.30 (preventing
    /// white LightHalo text shadows on dark glass), and Light-mode inks stay crisp and dark.
    /// </summary>
    private SolidColorBrush ResolveSemanticBrush(string semanticToken)
    {
        bool dark = IsDark;
        ReadOnlySpan<char> token = semanticToken.AsSpan().Trim();
        if (WindowsCompatibilityService.IsHighContrast)
        {
            Color hcColor;
            if (token.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("coral", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("orange", StringComparison.OrdinalIgnoreCase))
            {
                hcColor = dark ? Color.FromArgb(255, 255, 255, 0) : Color.FromArgb(255, 128, 64, 0);
            }
            else if (token.Equals("danger", StringComparison.OrdinalIgnoreCase))
            {
                hcColor = dark ? Color.FromArgb(255, 255, 128, 128) : Color.FromArgb(255, 180, 0, 0);
            }
            else
            {
                hcColor = dark ? Color.FromArgb(255, 0, 255, 255) : Color.FromArgb(255, 0, 0, 160);
            }
            return GetOrCreateSolidBrush(hcColor);
        }

        Color color;
        if (token.Equals("coral", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("orange", StringComparison.OrdinalIgnoreCase))
        {
            color = dark
                ? Color.FromArgb(255, 251, 146, 60)  // #FB923C (L = 0.42)
                : Color.FromArgb(255, 234, 88, 12);   // #EA580C (L = 0.17)
        }
        else if (token.Equals("violet", StringComparison.OrdinalIgnoreCase) ||
                 token.Equals("purple", StringComparison.OrdinalIgnoreCase))
        {
            color = dark
                ? Color.FromArgb(255, 167, 139, 250) // #A78BFA (L = 0.34)
                : Color.FromArgb(255, 109, 40, 217);  // #6D28D9 (L = 0.09)
        }
        else if (token.Equals("emerald", StringComparison.OrdinalIgnoreCase) ||
                 token.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            color = dark
                ? Color.FromArgb(255, 52, 211, 153)  // #34D399 (L = 0.55)
                : Color.FromArgb(255, 4, 120, 87);    // #047857 (L = 0.14)
        }
        else if (token.Equals("cyan", StringComparison.OrdinalIgnoreCase))
        {
            color = dark
                ? Color.FromArgb(255, 56, 189, 248)  // #38BDF8 (L = 0.44)
                : Color.FromArgb(255, 3, 105, 161);   // #0369A1 (L = 0.13)
        }
        else if (token.Equals("amber", StringComparison.OrdinalIgnoreCase))
        {
            color = dark
                ? Color.FromArgb(255, 251, 191, 36)  // #FBBF24
                : Color.FromArgb(255, 217, 119, 6);   // #D97706
        }
        else if (token.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            color = dark
                ? Color.FromArgb(255, 251, 191, 36)  // #FBBF24 (L = 0.57)
                : Color.FromArgb(255, 180, 83, 9);    // #B45309 (L = 0.12)
        }
        else if (token.Equals("danger", StringComparison.OrdinalIgnoreCase))
        {
            color = dark
                ? Color.FromArgb(255, 248, 113, 113) // #F87171 (L = 0.32)
                : Color.FromArgb(255, 220, 38, 38);   // #DC2626 (L = 0.14)
        }
        else
        {
            color = dark
                ? Color.FromArgb(255, 96, 165, 250)  // #60A5FA (L = 0.35)
                : Color.FromArgb(255, 37, 99, 235);   // #2563EB (L = 0.12)
        }
        return GetOrCreateSolidBrush(color);
    }

    private SolidColorBrush GetOrCreateSolidBrush(Color color)
    {
        if (!_solidBrushCache.TryGetValue(color, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(color);
            _solidBrushCache[color] = brush;
        }

        return brush;
    }

    private static bool IsSemanticColorToken(ReadOnlySpan<char> token) =>
        token.Equals("accent", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("coral", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("orange", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("violet", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("purple", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("emerald", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("cyan", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("amber", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("success", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("danger", StringComparison.OrdinalIgnoreCase);

    private Brush ResolveForegroundBrush(string colorToken)
    {
        ReadOnlySpan<char> token = colorToken.AsSpan().Trim();
        if (WindowsCompatibilityService.IsHighContrast)
        {
            return IsSemanticColorToken(token)
                ? ResolveSemanticBrush(colorToken)
                : GetBrush(
                    "SystemColorWindowTextColorBrush",
                    IsDark ? Colors.White : Colors.Black);
        }

        if (token.Equals("secondary", StringComparison.OrdinalIgnoreCase))
        {
            return GetBrush(
                "TextFillColorSecondaryBrush",
                IsDark ? Color.FromArgb(196, 255, 255, 255) : Color.FromArgb(160, 0, 0, 0));
        }

        if (token.Equals("muted", StringComparison.OrdinalIgnoreCase))
        {
            return GetBrush(
                "TextFillColorTertiaryBrush",
                IsDark ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(116, 0, 0, 0));
        }

        if (IsSemanticColorToken(token))
        {
            return ResolveSemanticBrush(colorToken);
        }

        return GetBrush(
            "TextFillColorPrimaryBrush",
            IsDark ? Colors.White : Color.FromArgb(228, 0, 0, 0));
    }

    private Brush ResolveCardBackground(string tone)
    {
        if (WindowsCompatibilityService.IsHighContrast)
        {
            return GetBrush(
                "SystemColorWindowColorBrush",
                IsDark ? Colors.Black : Colors.White);
        }

        ReadOnlySpan<char> t = tone.AsSpan().Trim();
        if (t.Equals("subtle", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(12, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0));
        if (t.Equals("accent", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(22, 96, 165, 250) : Color.FromArgb(22, 37, 99, 235));
        if (t.Equals("emerald", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(26, 52, 211, 153) : Color.FromArgb(26, 5, 150, 105));
        if (t.Equals("cyan", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(26, 56, 189, 248) : Color.FromArgb(26, 2, 132, 199));
        if (t.Equals("violet", StringComparison.OrdinalIgnoreCase) || t.Equals("purple", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(28, 167, 139, 250) : Color.FromArgb(24, 109, 40, 217));
        if (t.Equals("coral", StringComparison.OrdinalIgnoreCase) || t.Equals("orange", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(28, 251, 146, 60) : Color.FromArgb(26, 234, 88, 12));
        if (t.Equals("success", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(24, 52, 211, 153) : Color.FromArgb(28, 4, 120, 87));
        if (t.Equals("amber", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(26, 251, 191, 36) : Color.FromArgb(28, 217, 119, 6));
        if (t.Equals("warning", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(26, 251, 191, 36) : Color.FromArgb(32, 180, 83, 9));
        if (t.Equals("danger", StringComparison.OrdinalIgnoreCase))
            return GetOrCreateSolidBrush(IsDark ? Color.FromArgb(26, 248, 113, 113) : Color.FromArgb(28, 220, 38, 38));
        if (t.Equals("dawn", StringComparison.OrdinalIgnoreCase))
            return CreateDayPhaseGradientBrush(
                IsDark ? Color.FromArgb(34, 245, 158, 11) : Color.FromArgb(30, 217, 119, 6),
                IsDark ? Color.FromArgb(24, 251, 113, 133) : Color.FromArgb(22, 244, 63, 94));
        if (t.Equals("day", StringComparison.OrdinalIgnoreCase))
            return CreateDayPhaseGradientBrush(
                IsDark ? Color.FromArgb(30, 56, 189, 248) : Color.FromArgb(28, 14, 165, 233),
                IsDark ? Color.FromArgb(20, 45, 212, 191) : Color.FromArgb(20, 20, 184, 166));
        if (t.Equals("dusk", StringComparison.OrdinalIgnoreCase))
            return CreateDayPhaseGradientBrush(
                IsDark ? Color.FromArgb(34, 249, 115, 22) : Color.FromArgb(28, 234, 88, 12),
                IsDark ? Color.FromArgb(28, 168, 85, 247) : Color.FromArgb(24, 147, 51, 234));
        if (t.Equals("night", StringComparison.OrdinalIgnoreCase))
            return CreateDayPhaseGradientBrush(
                IsDark ? Color.FromArgb(38, 79, 70, 229) : Color.FromArgb(26, 67, 56, 202),
                IsDark ? Color.FromArgb(26, 124, 58, 237) : Color.FromArgb(20, 109, 40, 217));

        return GetBrush(
            "WidgetLayerFillSecondaryBrush",
            IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(0x70, 255, 255, 255));
    }

    private static LinearGradientBrush CreateDayPhaseGradientBrush(Color startColor, Color endColor)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = startColor, Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = endColor, Offset = 1.0 });
        return brush;
    }

    internal static string ResolveBindSetActionType(string bindKey) =>
        bindKey.StartsWith("settings.", StringComparison.OrdinalIgnoreCase) ||
        bindKey.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase)
            ? "settings.set"
            : "state.set";

    private (Brush Bg, Brush Fg) ResolveBadgeColors(string tone)
    {
        if (WindowsCompatibilityService.IsHighContrast)
        {
            Brush hcBg = GetBrush("SystemColorWindowColorBrush", IsDark ? Colors.Black : Colors.White);
            Brush hcFg = tone.Trim().ToLowerInvariant() switch
            {
                "subtle" or "muted" or "secondary" or "default" =>
                    GetBrush("SystemColorWindowTextColorBrush", IsDark ? Colors.White : Colors.Black),
                _ => ResolveSemanticBrush(tone)
            };
            return (hcBg, hcFg);
        }

        return tone.Trim().ToLowerInvariant() switch
        {
            "subtle" or "muted" or "secondary" or "default" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0)),
                ResolveForegroundBrush("secondary")),
            "success" or "emerald" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(36, 52, 211, 153) : Color.FromArgb(28, 4, 120, 87)),
                ResolveSemanticBrush("success")),
            "amber" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(38, 251, 191, 36) : Color.FromArgb(30, 217, 119, 6)),
                ResolveSemanticBrush("amber")),
            "warning" or "coral" or "orange" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(38, 251, 191, 36) : Color.FromArgb(34, 180, 83, 9)),
                ResolveSemanticBrush("warning")),
            "danger" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(38, 248, 113, 113) : Color.FromArgb(30, 220, 38, 38)),
                ResolveSemanticBrush("danger")),
            "violet" or "purple" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(36, 167, 139, 250) : Color.FromArgb(26, 109, 40, 217)),
                ResolveSemanticBrush("violet")),
            "cyan" => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(34, 56, 189, 248) : Color.FromArgb(26, 3, 105, 161)),
                ResolveSemanticBrush("cyan")),
            _ => (
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(28, 96, 165, 250) : Color.FromArgb(24, 37, 99, 235)),
                ResolveSemanticBrush("accent"))
        };
    }

    private Brush ResolveBadgePillBorderBrush(string tone)
    {
        if (WindowsCompatibilityService.IsHighContrast)
        {
            return GetBrush("SystemColorWindowTextColorBrush", IsDark ? Colors.White : Colors.Black);
        }

        return tone.Trim().ToLowerInvariant() switch
        {
            "subtle" or "muted" or "secondary" or "default" =>
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(26, 15, 23, 42)),
            "success" or "emerald" =>
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(88, 52, 211, 153) : Color.FromArgb(72, 5, 150, 105)),
            "amber" or "warning" or "coral" or "orange" =>
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(92, 251, 191, 36) : Color.FromArgb(76, 217, 119, 6)),
            "danger" =>
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(96, 248, 113, 113) : Color.FromArgb(76, 220, 38, 38)),
            "violet" or "purple" =>
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(84, 167, 139, 250) : Color.FromArgb(68, 109, 40, 217)),
            "cyan" =>
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(84, 56, 189, 248) : Color.FromArgb(68, 2, 132, 199)),
            _ =>
                GetOrCreateSolidBrush(IsDark ? Color.FromArgb(80, 96, 165, 250) : Color.FromArgb(64, 37, 99, 235))
        };
    }

    private Brush ResolveThresholdBrush(
        double value,
        double? warning,
        double? danger,
        string baseTone = "accent")
    {
        if (danger.HasValue && value >= danger.Value)
        {
            return ResolveSemanticBrush("danger");
        }
        if (warning.HasValue && value >= warning.Value)
        {
            return ResolveSemanticBrush("warning");
        }
        return ResolveSemanticBrush(
            string.IsNullOrWhiteSpace(baseTone) || string.Equals(baseTone, "default", StringComparison.OrdinalIgnoreCase)
                ? "accent"
                : baseTone);
    }

    private Brush CreateSparklineAreaBrush(
        double value,
        double? warning,
        double? danger,
        string baseTone = "accent")
    {
        Brush strokeBrush = ResolveThresholdBrush(value, warning, danger, baseTone);
        Color strokeColor = strokeBrush is SolidColorBrush scb
            ? scb.Color
            : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
        Color topColor = Color.FromArgb((byte)(IsDark ? 38 : 24), strokeColor.R, strokeColor.G, strokeColor.B);

        if (!_sparklineBrushCache.TryGetValue(topColor, out LinearGradientBrush? gradient))
        {
            gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            gradient.GradientStops.Add(new GradientStop { Color = topColor, Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, topColor.R, topColor.G, topColor.B), Offset = 1 });
            _sparklineBrushCache[topColor] = gradient;
        }

        return gradient;
    }

    private Brush GetBrush(string key, Color fallback)
    {
        try
        {
            if (NeutralInteractionBrush.ResolveThemedResource(key, _themeScope) is { } brush)
            {
                return brush;
            }
        }
        catch
        {
            // Headless unit test hosts may not initialize XAML resource dictionaries.
        }
        return GetOrCreateSolidBrush(fallback);
    }

    private static bool IsSeeThroughShadowBrush(Brush? brush) =>
        brush is null ||
        (brush is SolidColorBrush solid && ((solid.Color.A / 255.0) * solid.Opacity) <= 0.5) ||
        (brush is LinearGradientBrush lg && lg.GradientStops.All(s => ((s.Color.A / 255.0) * lg.Opacity) <= 0.5));

    internal double ResolveEffectiveSpacing(
        CustomWidgetNodeSpec spec,
        bool isHorizontal = false,
        double? defaultSpacing = null)
    {
        if (spec.HasExplicitSpacing)
        {
            return Math.Max(0.0, spec.Spacing);
        }

        double baseline = defaultSpacing ?? spec.Spacing;
        if (baseline <= 0.0)
        {
            return 0.0;
        }

        CustomWidgetHostAppearanceProfile profile = EffectiveAppearance;
        double axisMultiplier = isHorizontal
            ? profile.HorizontalSpacingMultiplier
            : profile.VerticalSpacingMultiplier;
        double combined = profile.DensityMultiplier * axisMultiplier;
        if (Math.Abs(combined - 1.0) <= 0.001)
        {
            return baseline;
        }

        return Math.Round(Math.Clamp(baseline * combined, 1.5, 28.0) * 2.0) / 2.0;
    }

    private Thickness ResolveEffectiveRootSafePadding(string? raw)
    {
        bool isFullBleed = CustomWidgetChromeCategoryClassifier.IsFullBleedRootLayout(_viewModel.Manifest);
        Thickness pad = ResolveRootSafePadding(
            raw,
            isFullBleed,
            EffectiveAppearance.DensityMultiplier);
        if (!isFullBleed &&
            pad.Top > 0 &&
            pad.Left > 0 &&
            Math.Abs(pad.Left - pad.Right) < 0.01 &&
            pad.Top < pad.Left)
        {
            pad.Top = pad.Left;
        }

        return pad;
    }

    internal static Thickness ResolveRootSafePadding(
        string? raw,
        bool isFullBleedRoot = false,
        double densityMultiplier = 1.0)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return ParseThickness(raw);
        }

        return isFullBleedRoot
            ? new Thickness(0)
            : ScaleDefaultThickness(12, 10, 12, 12, densityMultiplier);
    }

    private static Thickness ScaleDefaultThickness(
        double left,
        double top,
        double right,
        double bottom,
        double densityMultiplier)
    {
        if (Math.Abs(densityMultiplier - 1.0) <= 0.001)
        {
            return new Thickness(left, top, right, bottom);
        }

        return new Thickness(
            Math.Round(Math.Clamp(left * densityMultiplier, 4.0, 24.0)),
            Math.Round(Math.Clamp(top * densityMultiplier, 4.0, 24.0)),
            Math.Round(Math.Clamp(right * densityMultiplier, 4.0, 24.0)),
            Math.Round(Math.Clamp(bottom * densityMultiplier, 4.0, 24.0)));
    }

    private static Thickness ParseThickness(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Thickness(0);
        }

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double u))
        {
            return new Thickness(u);
        }
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return new Thickness(h, v, h, v);
        }
        if (parts.Length == 4 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double l) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
        {
            return new Thickness(l, t, r, b);
        }
        return new Thickness(0);
    }

    private static GridLength ParseGridLength(string token)
    {
        if (string.Equals(token, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return GridLength.Auto;
        }
        if (token.EndsWith('*'))
        {
            string numPart = token[..^1];
            double weight = string.IsNullOrWhiteSpace(numPart)
                ? 1
                : double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                    ? w
                    : 1;
            return new GridLength(Math.Max(0.1, weight), GridUnitType.Star);
        }
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double px))
        {
            return new GridLength(Math.Max(0, px), GridUnitType.Pixel);
        }
        return new GridLength(1, GridUnitType.Star);
    }
}
