using System.Globalization;
using System.Text.Json;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using WinUIShape = Microsoft.UI.Xaml.Shapes.Shape;
using WinUIPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private static readonly string[] DefaultChartSeriesPalette =
    [
        "accent", "emerald", "violet", "coral", "cyan", "warning", "danger"
    ];

    private static readonly MarkdownDocumentService s_sharedMarkdownService = new();

    private sealed class OpenPopoverEntry(
        DeskBox.Views.WidgetEditorPopoverWindow window,
        ContentControl bodyHost,
        CustomWidgetNodeSpec spec)
    {
        public DeskBox.Views.WidgetEditorPopoverWindow Window { get; } = window;
        public ContentControl BodyHost { get; } = bodyHost;
        public CustomWidgetNodeSpec Spec { get; } = spec;
        public List<Action<CustomWidgetEvaluationContext>> MetricUpdaters { get; } = [];
        public List<MarkdownDocumentView> MarkdownViews { get; } = [];
        public List<ManagedMediaPlayerSlot> MediaPlayers { get; } = [];
        public List<CustomWidgetRecycleBinCard> RecycleBinCards { get; } = [];

        public void ReleaseScopedResources()
        {
            MetricUpdaters.Clear();
            foreach (MarkdownDocumentView md in MarkdownViews)
            {
                try
                {
                    md.ReleaseVisualHandlers();
                }
                catch
                {
                }
            }
            MarkdownViews.Clear();

            foreach (ManagedMediaPlayerSlot slot in MediaPlayers)
            {
                DisposeMediaPlayerSlot(slot);
            }
            MediaPlayers.Clear();

            foreach (CustomWidgetRecycleBinCard card in RecycleBinCards)
            {
                try
                {
                    card.Dispose();
                }
                catch
                {
                }
            }
            RecycleBinCards.Clear();
        }
    }

    private readonly Dictionary<string, CustomWidgetNodeSpec> _popoverSpecsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeakReference<FrameworkElement>> _popoverAnchorsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenPopoverEntry> _openPopoversById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MarkdownDocumentView> _activeMarkdownViews = [];
    private OpenPopoverEntry? _currentPopoverBuildOwner;
    private WeakReference<FrameworkElement>? _lastRootElementRef;

    private void RegisterMarkdownView(MarkdownDocumentView mdView)
    {
        if (_currentPopoverBuildOwner is { } popoverOwner)
        {
            popoverOwner.MarkdownViews.Add(mdView);
        }
        else
        {
            _activeMarkdownViews.Add(mdView);
        }
    }

    private void ForgetMarkdownView(MarkdownDocumentView mdView)
    {
        try
        {
            mdView.ReleaseVisualHandlers();
        }
        catch
        {
        }

        _activeMarkdownViews.RemoveAll(view => ReferenceEquals(view, mdView));
        foreach (OpenPopoverEntry entry in _openPopoversById.Values)
        {
            entry.MarkdownViews.RemoveAll(view => ReferenceEquals(view, mdView));
        }
    }

    private void ReleasePopoverEntry(OpenPopoverEntry entry)
    {
        ForgetRetainedSubtree(entry.BodyHost);
        entry.ReleaseScopedResources();
    }

    private void ReleaseMarkdownViews()
    {
        if (_activeMarkdownViews.Count == 0)
        {
            return;
        }

        foreach (MarkdownDocumentView md in _activeMarkdownViews)
        {
            try
            {
                md.ReleaseVisualHandlers();
            }
            catch
            {
            }
        }
        _activeMarkdownViews.Clear();
    }

    private void ResetExtendedVisualState(bool releasePopovers)
    {
        ReleaseMarkdownViews();
        _popoverSpecsById.Clear();
        _popoverAnchorsById.Clear();
        if (releasePopovers)
        {
            foreach (OpenPopoverEntry entry in _openPopoversById.Values.ToArray())
            {
                ReleasePopoverEntry(entry);
            }
            _openPopoversById.Clear();
        }
    }

    private void IndexManifestPopovers()
    {
        foreach (CustomWidgetNodeSpec pop in _viewModel.Manifest.Popovers)
        {
            string? id = !string.IsNullOrWhiteSpace(pop.PopoverId) ? pop.PopoverId : pop.Id;
            if (!string.IsNullOrWhiteSpace(id))
            {
                _popoverSpecsById[id.Trim()] = pop;
            }
        }
    }

    internal static bool TryParseRawColor(string? raw, out Color color)
    {
        color = Color.FromArgb(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string s = raw.Trim();
        if (string.Equals(s, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = Color.FromArgb(0, 0, 0, 0);
            return true;
        }

        if (s.StartsWith('#'))
        {
            ReadOnlySpan<char> hex = s.AsSpan(1);
            if (hex.Length == 3 &&
                TryParseHexNibble(hex[0], out byte r3) &&
                TryParseHexNibble(hex[1], out byte g3) &&
                TryParseHexNibble(hex[2], out byte b3))
            {
                color = Color.FromArgb(255, (byte)(r3 * 17), (byte)(g3 * 17), (byte)(b3 * 17));
                return true;
            }

            if (hex.Length == 4 &&
                TryParseHexNibble(hex[0], out byte a4) &&
                TryParseHexNibble(hex[1], out byte r4) &&
                TryParseHexNibble(hex[2], out byte g4) &&
                TryParseHexNibble(hex[3], out byte b4))
            {
                color = Color.FromArgb((byte)(a4 * 17), (byte)(r4 * 17), (byte)(g4 * 17), (byte)(b4 * 17));
                return true;
            }

            if (hex.Length == 6 &&
                byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r6) &&
                byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g6) &&
                byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b6))
            {
                color = Color.FromArgb(255, r6, g6, b6);
                return true;
            }

            if (hex.Length == 8 &&
                byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte a8) &&
                byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r8) &&
                byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g8) &&
                byte.TryParse(hex.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b8))
            {
                color = Color.FromArgb(a8, r8, g8, b8);
                return true;
            }

            return false;
        }

        if ((s.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) ||
             s.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase)) &&
            s.EndsWith(')'))
        {
            int openIdx = s.IndexOf('(');
            string inner = s[(openIdx + 1)..^1];
            string[] parts = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is 3 or 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rf) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double gf) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double bf))
            {
                byte alpha = 255;
                if (parts.Length == 4 &&
                    double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double af))
                {
                    alpha = af <= 1.0
                        ? (byte)Math.Clamp(Math.Round(af * 255.0), 0, 255)
                        : (byte)Math.Clamp(Math.Round(af), 0, 255);
                }

                color = Color.FromArgb(
                    alpha,
                    (byte)Math.Clamp(Math.Round(rf), 0, 255),
                    (byte)Math.Clamp(Math.Round(gf), 0, 255),
                    (byte)Math.Clamp(Math.Round(bf), 0, 255));
                return true;
            }
        }

        return false;
    }

    private static bool TryParseHexNibble(char c, out byte value)
    {
        if (c is >= '0' and <= '9')
        {
            value = (byte)(c - '0');
            return true;
        }
        if (c is >= 'a' and <= 'f')
        {
            value = (byte)(10 + (c - 'a'));
            return true;
        }
        if (c is >= 'A' and <= 'F')
        {
            value = (byte)(10 + (c - 'A'));
            return true;
        }
        value = 0;
        return false;
    }

    internal Brush BuildLinearGradientBrush(
        CustomWidgetLinearGradientSpec gradientSpec,
        CustomWidgetEvaluationContext ctx)
    {
        double angleRad = (gradientSpec.Angle - 90.0) * Math.PI / 180.0;
        double dx = Math.Cos(angleRad);
        double dy = Math.Sin(angleRad);
        double scale = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (scale > 0.0001)
        {
            dx /= scale;
            dy /= scale;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5 - (dx * 0.5), 0.5 - (dy * 0.5)),
            EndPoint = new Point(0.5 + (dx * 0.5), 0.5 + (dy * 0.5))
        };

        foreach (CustomWidgetGradientStopSpec stop in gradientSpec.Stops)
        {
            string resolvedStopColor = CustomWidgetExpressionEvaluator.Interpolate(stop.Color, ctx).Trim();
            Color parsed = TryParseRawColor(resolvedStopColor, out Color raw)
                ? raw
                : (ResolveSemanticBrush(resolvedStopColor).Color);
            brush.GradientStops.Add(new GradientStop
            {
                Offset = Math.Clamp(stop.Offset, 0.0, 1.0),
                Color = parsed
            });
        }

        return brush;
    }

    internal static int ColorChroma(Color c) =>
        Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    internal static double RelativeLuminance(Color c) =>
        (0.2126 * (c.R / 255.0)) + (0.7152 * (c.G / 255.0)) + (0.0722 * (c.B / 255.0));

    internal static Color EnsureChromaticTextLegibilityForTheme(Color c, bool isDark)
    {
        double lum = RelativeLuminance(c);
        if (!isDark)
        {
            if (lum > 0.58)
            {
                return Color.FromArgb(
                    c.A,
                    (byte)Math.Round(c.R * 0.55),
                    (byte)Math.Round(c.G * 0.55),
                    (byte)Math.Round(c.B * 0.55));
            }
            return c;
        }

        if (lum < 0.30)
        {
            return Color.FromArgb(
                c.A,
                (byte)Math.Min(255, Math.Round(c.R + ((255 - c.R) * 0.45))),
                (byte)Math.Min(255, Math.Round(c.G + ((255 - c.G) * 0.45))),
                (byte)Math.Min(255, Math.Round(c.B + ((255 - c.B) * 0.45))));
        }

        return c;
    }

    internal static (bool UsesThemeSemantic, string ThemeSemantic, Color ColorValue) EvaluateSafeTextColorToken(
        string rawColorToken,
        bool isDark,
        string fallbackSemantic = "default")
    {
        string resolved = rawColorToken?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(resolved))
        {
            return (true, fallbackSemantic, default);
        }

        if (TryParseRawColor(resolved, out Color parsed))
        {
            if (parsed.A == 0)
            {
                return (false, string.Empty, Colors.Transparent);
            }

            if (ColorChroma(parsed) <= 48)
            {
                double lum = RelativeLuminance(parsed);
                bool isMidNeutral = (lum is >= 0.25 and <= 0.72) || parsed.A < 210;
                return (true, isMidNeutral ? "secondary" : "default", default);
            }

            return (false, string.Empty, EnsureChromaticTextLegibilityForTheme(parsed, isDark));
        }

        return (true, resolved, default);
    }

    internal static bool ShouldStripContainerRawBackground(string? resolvedBackground, out bool isExplicitTransparent)
    {
        isExplicitTransparent = false;
        if (string.IsNullOrWhiteSpace(resolvedBackground))
        {
            return false;
        }

        if (TryParseRawColor(resolvedBackground.Trim(), out Color rawColor))
        {
            if (rawColor.A == 0)
            {
                isExplicitTransparent = true;
                return false;
            }

            return true;
        }

        return false;
    }

    internal Brush ResolveSafeTextColorBrush(string rawColorToken, string fallbackSemantic = "default")
    {
        (bool usesThemeSemantic, string themeSemantic, Color colorValue) =
            EvaluateSafeTextColorToken(rawColorToken, IsDark, fallbackSemantic);
        return usesThemeSemantic
            ? ResolveForegroundBrush(themeSemantic)
            : GetOrCreateSolidBrush(colorValue);
    }

    internal double SnapLegacyFontSizeToHostTier(double rawFontSize) =>
        rawFontSize switch
        {
            <= 11.0 => CaptionFontSize,
            <= 12.5 => BodyFontSize,
            <= 15.0 => SubtitleFontSize,
            <= 22.0 => MetricFontSize,
            _ => HeroFontSize
        };

    internal static bool IsMicroIndicatorSpec(CustomWidgetNodeSpec spec) =>
        spec.Children.Count == 0 &&
        spec.ItemTemplate is null &&
        spec.Width is > 0 and <= 32 &&
        spec.Height is > 0 and <= 32;

    internal bool TryResolveExplicitBackgroundBrush(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        out Brush? brush)
    {
        if (!string.IsNullOrWhiteSpace(spec.Background))
        {
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(spec.Background, ctx).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                if (IsMicroIndicatorSpec(spec) && TryParseRawColor(resolved, out Color microColor))
                {
                    brush = GetOrCreateSolidBrush(microColor);
                    return true;
                }

                if (ShouldStripContainerRawBackground(resolved, out bool isExplicitTransparent))
                {
                    // Host-unified surfaces: ignore raw opaque/hex container backgrounds so Mica/Acrylic glass shows through.
                    brush = null;
                    return false;
                }

                if (isExplicitTransparent)
                {
                    brush = GetOrCreateSolidBrush(Colors.Transparent);
                    return true;
                }

                brush = ResolveCardBackground(resolved);
                return true;
            }
        }

        brush = null;
        return false;
    }

    internal bool TryResolveExplicitTextBrush(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        out Brush? brush)
    {
        if (!string.IsNullOrWhiteSpace(spec.TextColor))
        {
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(spec.TextColor, ctx).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                brush = ResolveSafeTextColorBrush(resolved);
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.Color) &&
            !string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase))
        {
            string resolvedColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx).Trim();
            if (TryParseRawColor(resolvedColor, out _))
            {
                brush = ResolveSafeTextColorBrush(resolvedColor);
                return true;
            }
        }

        brush = null;
        return false;
    }

    internal bool TryResolveExplicitBorderBrush(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        out Brush? brush)
    {
        if (!string.IsNullOrWhiteSpace(spec.BorderColor))
        {
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(spec.BorderColor, ctx).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                if (TryParseRawColor(resolved, out Color raw))
                {
                    if (raw.A == 0)
                    {
                        brush = GetOrCreateSolidBrush(Colors.Transparent);
                        return true;
                    }

                    if (ColorChroma(raw) <= 48)
                    {
                        brush = CardStrokeBrush;
                        return true;
                    }

                    byte clampedAlpha = Math.Min(raw.A, (byte)96);
                    brush = GetOrCreateSolidBrush(Color.FromArgb(clampedAlpha, raw.R, raw.G, raw.B));
                    return true;
                }

                brush = ResolveSemanticBrush(resolved);
                return true;
            }
        }

        brush = null;
        return false;
    }

    internal Brush ResolveAnyColorBrush(
        string? rawToken,
        CustomWidgetEvaluationContext ctx,
        string fallbackSemantic = "accent")
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return ResolveSemanticBrush(fallbackSemantic);
        }

        string resolved = CustomWidgetExpressionEvaluator.Interpolate(rawToken, ctx).Trim();
        if (string.IsNullOrWhiteSpace(resolved) ||
            string.Equals(resolved, "default", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSemanticBrush(fallbackSemantic);
        }

        if (TryParseRawColor(resolved, out Color raw))
        {
            if (raw.A == 0)
            {
                return GetOrCreateSolidBrush(Colors.Transparent);
            }

            if (ColorChroma(raw) <= 48)
            {
                double lum = RelativeLuminance(raw);
                bool isMidNeutral = (lum is >= 0.25 and <= 0.72) || raw.A < 210;
                return ResolveForegroundBrush(isMidNeutral ? "secondary" : "default");
            }

            return GetOrCreateSolidBrush(EnsureChromaticTextLegibilityForTheme(raw, IsDark));
        }

        ReadOnlySpan<char> tok = resolved.AsSpan().Trim();
        if (tok.Equals("primary", StringComparison.OrdinalIgnoreCase) ||
            tok.Equals("foreground", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveForegroundBrush("default");
        }
        if (tok.Equals("secondary", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveForegroundBrush("secondary");
        }
        if (tok.Equals("muted", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveForegroundBrush("muted");
        }

        return ResolveSemanticBrush(resolved);
    }

    internal static FontWeight? TryResolveFontWeight(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        ReadOnlySpan<char> w = raw.AsSpan().Trim();
        if (w.Equals("thin", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("100")) return FontWeights.Thin;
        if (w.Equals("extralight", StringComparison.OrdinalIgnoreCase) || w.Equals("ultralight", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("200")) return FontWeights.ExtraLight;
        if (w.Equals("light", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("300")) return FontWeights.Light;
        if (w.Equals("normal", StringComparison.OrdinalIgnoreCase) || w.Equals("regular", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("400")) return FontWeights.Normal;
        if (w.Equals("medium", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("500")) return FontWeights.Medium;
        if (w.Equals("semibold", StringComparison.OrdinalIgnoreCase) || w.Equals("demibold", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("600")) return FontWeights.SemiBold;
        if (w.Equals("bold", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("700")) return FontWeights.Bold;
        if (w.Equals("extrabold", StringComparison.OrdinalIgnoreCase) || w.Equals("ultrabold", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("800")) return FontWeights.ExtraBold;
        if (w.Equals("black", StringComparison.OrdinalIgnoreCase) || w.Equals("heavy", StringComparison.OrdinalIgnoreCase) || w.SequenceEqual("900")) return FontWeights.Black;
        return null;
    }

    private void ApplyRawTextTypographyOverrides(
        TextBlock tb,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (spec.FontSize.HasValue && spec.FontSize.Value > 0)
        {
            tb.FontSize = SnapLegacyFontSizeToHostTier(spec.FontSize.Value);
        }

        if (TryResolveFontWeight(spec.FontWeight) is { } fw)
        {
            tb.FontWeight = fw;
        }

        if (!string.IsNullOrWhiteSpace(spec.FontFamily))
        {
            string requested = spec.FontFamily.Trim();
            if (requested.Contains("mono", StringComparison.OrdinalIgnoreCase) ||
                requested.Contains("consolas", StringComparison.OrdinalIgnoreCase) ||
                requested.Contains("cascadia", StringComparison.OrdinalIgnoreCase))
            {
                tb.FontFamily = MonoFontFamily;
            }
        }

        if (spec.Strikethrough)
        {
            tb.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        }

        if (TryResolveExplicitTextBrush(spec, ctx, out Brush? explicitFg) && explicitFg is not null)
        {
            tb.Foreground = explicitFg;
        }
    }

    private FrameworkElement ApplyContainerVisualOverrides(
        FrameworkElement element,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        bool hasBg = TryResolveExplicitBackgroundBrush(spec, ctx, out _);
        bool hasBorderColor = TryResolveExplicitBorderBrush(spec, ctx, out _);
        bool hasBorderWidth = !string.IsNullOrWhiteSpace(spec.BorderWidth);
        bool hasDynamicVisuals = ContainsDynamicToken(spec.Background) ||
                                 ContainsDynamicToken(spec.BorderColor) ||
                                 ContainsDynamicToken(spec.BorderWidth);
        bool isMicroIndicator = IsMicroIndicatorSpec(spec);

        if (!hasBg && !hasBorderColor && !hasBorderWidth && !hasDynamicVisuals)
        {
            return element;
        }

        FrameworkElement target = element is Border or StackPanel or Grid
            ? element
            : new Border { Child = element };

        CornerRadius? microRadius = isMicroIndicator && spec.CornerRadius.HasValue
            ? new CornerRadius(Math.Max(0, spec.CornerRadius.Value))
            : null;

        void ApplyVisuals(CustomWidgetEvaluationContext liveCtx)
        {
            bool liveHasBg = TryResolveExplicitBackgroundBrush(spec, liveCtx, out Brush? liveBg);
            bool liveHasBorder = TryResolveExplicitBorderBrush(spec, liveCtx, out Brush? liveBorder);
            Thickness liveThickness = hasBorderWidth
                ? ParseThickness(spec.BorderWidth)
                : (liveHasBorder ? new Thickness(1) : new Thickness(0));

            if (target is Border border)
            {
                if (liveHasBg && liveBg is not null) border.Background = liveBg;
                if (liveHasBorder && liveBorder is not null) border.BorderBrush = liveBorder;
                if (hasBorderWidth || liveHasBorder) border.BorderThickness = liveThickness;
                if (microRadius.HasValue)
                {
                    border.CornerRadius = microRadius.Value;
                }
                else if (border.CornerRadius == new CornerRadius(0) && (liveHasBg || liveHasBorder || hasBorderWidth))
                {
                    border.CornerRadius = CornerMedium;
                }
            }
            else if (target is StackPanel sp)
            {
                if (liveHasBg && liveBg is not null) sp.Background = liveBg;
                if (liveHasBorder && liveBorder is not null) sp.BorderBrush = liveBorder;
                if (hasBorderWidth || liveHasBorder) sp.BorderThickness = liveThickness;
                if (microRadius.HasValue)
                {
                    sp.CornerRadius = microRadius.Value;
                }
                else if (sp.CornerRadius == new CornerRadius(0) && (liveHasBg || liveHasBorder || hasBorderWidth))
                {
                    sp.CornerRadius = CornerMedium;
                }
            }
            else if (target is Grid grid)
            {
                if (liveHasBg && liveBg is not null) grid.Background = liveBg;
                if (liveHasBorder && liveBorder is not null) grid.BorderBrush = liveBorder;
                if (hasBorderWidth || liveHasBorder) grid.BorderThickness = liveThickness;
                if (microRadius.HasValue)
                {
                    grid.CornerRadius = microRadius.Value;
                }
                else if (grid.CornerRadius == new CornerRadius(0) && (liveHasBg || liveHasBorder || hasBorderWidth))
                {
                    grid.CornerRadius = CornerMedium;
                }
            }
        }

        ApplyVisuals(ctx);

        if (hasDynamicVisuals)
        {
            RegisterRetainedPatcher(ApplyVisuals);
        }

        return target;
    }

    private void ApplyRawButtonVisualOverrides(
        Button btn,
        TextBlock? labelTb,
        FrameworkElement? iconEl,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (TryResolveFontWeight(spec.FontWeight) is { } fw)
        {
            btn.FontWeight = fw;
            if (labelTb is not null) labelTb.FontWeight = fw;
        }

        // Only allow high-chroma chromatic foreground accents on text/icon; buttons stay Fluent-native in surface and radius.
        if (TryResolveExplicitTextBrush(spec, ctx, out Brush? fgBrush) &&
            fgBrush is SolidColorBrush scb &&
            ColorChroma(scb.Color) > 48)
        {
            btn.Foreground = fgBrush;
            if (labelTb is not null)
            {
                labelTb.Foreground = fgBrush;
            }
            if (iconEl is FontIcon fi)
            {
                fi.Foreground = fgBrush;
            }
            else if (iconEl is TextBlock iconTb && !CustomWidgetTemplates.IsEmojiIcon(iconTb.Text))
            {
                iconTb.Foreground = fgBrush;
            }
        }
    }

    // =========================================================================
    // Popover System (Module 1)
    // =========================================================================

    private FrameworkElement BuildPopoverDeclarationNode(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string id = (!string.IsNullOrWhiteSpace(spec.PopoverId) ? spec.PopoverId : spec.Id)?.Trim() ?? string.Empty;
        bool hasInlineTrigger = !string.IsNullOrWhiteSpace(spec.Text) || !string.IsNullOrWhiteSpace(spec.Icon);
        if (string.IsNullOrWhiteSpace(id) && hasInlineTrigger && spec.Children.Count > 0)
        {
            id = $"inline-pop-{_popoverSpecsById.Count + 1}";
        }

        if (!string.IsNullOrWhiteSpace(id) &&
            (spec.Children.Count > 0 || !_popoverSpecsById.ContainsKey(id)))
        {
            _popoverSpecsById[id] = spec;
        }

        if (hasInlineTrigger && !string.IsNullOrWhiteSpace(id))
        {
            var triggerSpec = new CustomWidgetNodeSpec
            {
                Type = "Button",
                Text = spec.Text,
                Icon = spec.Icon,
                Variant = spec.Variant,
                Color = spec.Color,
                Tooltip = spec.Tooltip,
                Popover = id,
                OnClick = spec.OnClick,
                FontSize = spec.FontSize,
                FontWeight = spec.FontWeight,
                FontFamily = spec.FontFamily,
                TextColor = spec.TextColor,
                CornerRadius = spec.CornerRadius
            };
            FrameworkElement triggerBtn = BuildButton(triggerSpec, ctx);
            if (!string.IsNullOrWhiteSpace(spec.Margin))
            {
                triggerBtn.Margin = ParseThickness(spec.Margin);
            }
            if (!string.IsNullOrWhiteSpace(spec.HorizontalAlignment))
            {
                triggerBtn.HorizontalAlignment = ParseHorizontalAlignment(spec.HorizontalAlignment, triggerBtn.HorizontalAlignment);
            }
            if (!string.IsNullOrWhiteSpace(spec.VerticalAlignment))
            {
                triggerBtn.VerticalAlignment = ParseVerticalAlignment(spec.VerticalAlignment, triggerBtn.VerticalAlignment);
            }
            _popoverAnchorsById[id] = new WeakReference<FrameworkElement>(triggerBtn);
            return triggerBtn;
        }

        return new Grid
        {
            Width = 0,
            Height = 0,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
    }

    private void RegisterPopoverTriggerAnchor(CustomWidgetNodeSpec spec, FrameworkElement element, CustomWidgetEvaluationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(spec.Popover))
        {
            string popId = CustomWidgetExpressionEvaluator.Interpolate(spec.Popover, ctx).Trim();
            if (!string.IsNullOrWhiteSpace(popId))
            {
                _popoverAnchorsById[popId] = new WeakReference<FrameworkElement>(element);
            }
        }

        foreach (CustomWidgetActionSpec action in spec.OnClick)
        {
            if ((string.Equals(action.Type, "popover.open", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action.Type, "popover.toggle", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(action.PopoverId ?? action.TargetId ?? action.Key))
            {
                string popId = CustomWidgetExpressionEvaluator.Interpolate(
                    action.PopoverId ?? action.TargetId ?? action.Key,
                    ctx).Trim();
                if (!string.IsNullOrWhiteSpace(popId))
                {
                    _popoverAnchorsById[popId] = new WeakReference<FrameworkElement>(element);
                }
            }
        }
    }

    internal void HandlePopoverCommand(
        string mode,
        string popoverId,
        FrameworkElement? explicitAnchor = null)
    {
        if (string.IsNullOrWhiteSpace(popoverId))
        {
            return;
        }

        string id = popoverId.Trim();
        _popoverCommandSequence++;
        _lastCommandedPopoverId = id;
        string normalizedMode = (mode ?? "toggle").Trim().ToLowerInvariant();

        if (InterceptPopoversForStudioPreview &&
            !string.Equals(id, "__secrets", StringComparison.OrdinalIgnoreCase) &&
            !id.StartsWith("__ai_credentials", StringComparison.OrdinalIgnoreCase))
        {
            StudioPopoverPreviewRequested?.Invoke(normalizedMode, id);
            return;
        }

        if (string.Equals(normalizedMode, "close", StringComparison.Ordinal))
        {
            if (_openPopoversById.Remove(id, out OpenPopoverEntry? openEntry))
            {
                openEntry.Window.ClosePopover(restoreOwnerForeground: true);
            }
            else if (string.Equals(id, "*", StringComparison.Ordinal) ||
                     string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
            {
                CloseOpenPopovers();
                _openPopoversById.Clear();
            }
            return;
        }

        if (_openPopoversById.TryGetValue(id, out OpenPopoverEntry? existing))
        {
            if (string.Equals(normalizedMode, "toggle", StringComparison.Ordinal))
            {
                _openPopoversById.Remove(id);
                existing.Window.ClosePopover(restoreOwnerForeground: true);
            }
            return;
        }

        FrameworkElement? ResolveTargetAnchor()
        {
            if (explicitAnchor is not null && explicitAnchor.XamlRoot is not null)
            {
                return explicitAnchor;
            }
            if (_popoverAnchorsById.TryGetValue(id, out WeakReference<FrameworkElement>? aRef) &&
                aRef.TryGetTarget(out FrameworkElement? tracked) &&
                tracked.XamlRoot is not null)
            {
                return tracked;
            }
            if (_lastRootElementRef is not null &&
                _lastRootElementRef.TryGetTarget(out FrameworkElement? rootEl) &&
                rootEl.XamlRoot is not null)
            {
                return rootEl;
            }
            return explicitAnchor;
        }

        if (string.Equals(id, "__secrets", StringComparison.OrdinalIgnoreCase))
        {
            if (ResolveTargetAnchor() is { } secretAnchor)
            {
                if (_viewModel.SecretSettingDefinitions.Count > 0)
                {
                    ShowSecretSettingsPopover(secretAnchor, _viewModel);
                }
                else if (_viewModel.Manifest.Sources.Any(static s => string.Equals(s.Type, "aiMonitor", StringComparison.OrdinalIgnoreCase)))
                {
                    ShowAiCredentialsFlyout(secretAnchor, initialProviderId: null);
                }
            }
            return;
        }

        if (id.StartsWith("__ai_credentials", StringComparison.OrdinalIgnoreCase))
        {
            if (ResolveTargetAnchor() is { } aiAnchor)
            {
                int colon = id.IndexOf(':');
                string? targetProvider = colon >= 0 && colon + 1 < id.Length
                    ? id[(colon + 1)..].Trim()
                    : null;
                ShowAiCredentialsFlyout(aiAnchor, targetProvider);
            }
            return;
        }

        if (!_popoverSpecsById.TryGetValue(id, out CustomWidgetNodeSpec? popoverSpec))
        {
            CustomWidgetNodeSpec? fromManifest = _viewModel.Manifest.Popovers.FirstOrDefault(
                p => string.Equals(p.PopoverId ?? p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (fromManifest is null)
            {
                return;
            }
            popoverSpec = fromManifest;
            _popoverSpecsById[id] = popoverSpec;
        }

        FrameworkElement? anchor = ResolveTargetAnchor();
        if (anchor is null)
        {
            return;
        }

        IntPtr ownerHwnd = DeskBox.Views.WidgetEditorPopoverWindow.ResolveOwnerWindowHandle(anchor);
        string logicalKey = $"custom-popover:{_viewModel.Config.Id}:{id}";
        if (string.Equals(normalizedMode, "toggle", StringComparison.Ordinal) &&
            DeskBox.Views.WidgetEditorPopoverWindow.TryConsumeToggleOrCloseExisting(ownerHwnd, logicalKey, anchor))
        {
            return;
        }

        CustomWidgetEvaluationContext ctx = _viewModel.CreateEvaluationContext();
        string title = CustomWidgetExpressionEvaluator.Interpolate(popoverSpec.Title, ctx).Trim();
        var popoverWindow = new DeskBox.Views.WidgetEditorPopoverWindow(
            ownerHwnd,
            string.IsNullOrWhiteSpace(title) ? _viewModel.Config.Name : title,
            cornerPreference: EffectiveAppearance.CornerPreference);

        var bodyHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var entry = new OpenPopoverEntry(popoverWindow, bodyHost, popoverSpec);
        try
        {
            bodyHost.Content = BuildPopoverBodyContent(popoverSpec, popoverWindow, anchor, entry);
            if (_openPopoversById.TryGetValue(id, out OpenPopoverEntry? previousEntry) &&
                !ReferenceEquals(previousEntry, entry))
            {
                ReleasePopoverEntry(previousEntry);
            }

            _openPopoversById[id] = entry;
            popoverWindow.PopoverClosed += (_, _) =>
            {
                ReleasePopoverEntry(entry);
                if (_openPopoversById.TryGetValue(id, out OpenPopoverEntry? currentEntry) &&
                    ReferenceEquals(currentEntry, entry))
                {
                    _openPopoversById.Remove(id);
                }
            };

            DeskBox.Views.WidgetPopoverPlacement placement = (popoverSpec.Placement ?? "bottom").Trim().ToLowerInvariant() switch
            {
                "side" or "left" or "right" => DeskBox.Views.WidgetPopoverPlacement.Side,
                "bottomright" or "bottom-right" => DeskBox.Views.WidgetPopoverPlacement.BottomRight,
                _ => DeskBox.Views.WidgetPopoverPlacement.Bottom
            };

            double desiredWidth = popoverSpec.Width ?? 300;
            double minW = Math.Clamp(popoverSpec.MinWidth ?? Math.Min(240, desiredWidth), 180, 560);
            double maxW = Math.Clamp(popoverSpec.MaxWidth ?? Math.Max(desiredWidth + 24, 340), minW, 640);

            ShowTrackedPopoverAt(
                popoverWindow,
                anchor,
                bodyHost,
                placement,
                minWidthDip: minW,
                maxWidthDip: maxW,
                logScope: "CustomWidgetPopover",
                logicalKey: logicalKey);
        }
        catch
        {
            ReleasePopoverEntry(entry);
            if (_openPopoversById.TryGetValue(id, out OpenPopoverEntry? currentEntry) &&
                ReferenceEquals(currentEntry, entry))
            {
                _openPopoversById.Remove(id);
            }

            try { popoverWindow.ClosePopover(restoreOwnerForeground: false); } catch { }
            throw;
        }
    }

    private void PatchOpenPopovers(CustomWidgetEvaluationContext liveCtx, bool rebuildEmptyBodies)
    {
        if (_openPopoversById.Count == 0 ||
            _activeOpenFlyouts.Count > 0 ||
            _activeInteractions.Count > 0)
        {
            return;
        }

        foreach ((string id, OpenPopoverEntry entry) in _openPopoversById.ToArray())
        {
            try
            {
                if (entry.BodyHost.XamlRoot is { } xr &&
                    (FocusManager.GetFocusedElement(xr) is (TextBox or PasswordBox or AutoSuggestBox or NumberBox) ||
                     VisualTreeHelper.GetOpenPopupsForXamlRoot(xr).Count > 0))
                {
                    continue;
                }

                if (entry.MetricUpdaters.Count > 0)
                {
                    OpenPopoverEntry? prevOwner = _currentPopoverBuildOwner;
                    _currentPopoverBuildOwner = entry;
                    try
                    {
                        foreach (Action<CustomWidgetEvaluationContext> updater in entry.MetricUpdaters.ToArray())
                        {
                            updater(liveCtx);
                        }
                    }
                    finally
                    {
                        _currentPopoverBuildOwner = prevOwner;
                    }

                    entry.Window.RefreshBoundsFromContent();
                    continue;
                }

                if (!rebuildEmptyBodies || entry.BodyHost.Content is not null)
                {
                    continue;
                }

                FrameworkElement? anchor = null;
                if (_popoverAnchorsById.TryGetValue(id, out WeakReference<FrameworkElement>? anchorRef))
                {
                    anchorRef.TryGetTarget(out anchor);
                }

                ReleasePopoverEntry(entry);
                entry.BodyHost.Content = BuildPopoverBodyContent(entry.Spec, entry.Window, anchor, entry);
            }
            catch
            {
            }
        }
    }

    internal void RefreshOpenPopovers()
    {
        if (_openPopoversById.Count == 0 ||
            _activeOpenFlyouts.Count > 0 ||
            _activeInteractions.Count > 0)
        {
            return;
        }

        PatchOpenPopovers(_viewModel.CreateEvaluationContext(), rebuildEmptyBodies: true);
    }

    private FrameworkElement BuildPopoverBodyContent(
        CustomWidgetNodeSpec popoverSpec,
        DeskBox.Views.WidgetEditorPopoverWindow popoverWindow,
        FrameworkElement? anchor,
        OpenPopoverEntry? ownerEntry = null)
    {
        CustomWidgetEvaluationContext ctx = _viewModel.CreateEvaluationContext();
        double targetWidth = Math.Clamp(popoverSpec.Width ?? 286, 180, 580);
        var root = new StackPanel
        {
            Width = targetWidth,
            Spacing = Math.Max(6, popoverSpec.Spacing)
        };

        string title = CustomWidgetExpressionEvaluator.Interpolate(popoverSpec.Title, ctx).Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            var headerGrid = new Grid { ColumnSpacing = 8 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleTb = new TextBlock
            {
                Text = title,
                FontSize = SubtitleFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("default"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(titleTb, 0);
            headerGrid.Children.Add(titleTb);

            Button closeBtn = DeskBox.Views.WidgetEditorPopoverWindow.CreateHeaderCloseButton(
                Tr("Common.Close", ctx.CultureName),
                () => popoverWindow.ClosePopover(restoreOwnerForeground: true),
                anchor);
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);
            root.Children.Add(headerGrid);
            root.Children.Add(new Border
            {
                Height = 1,
                Background = CardStrokeBrush,
                Opacity = 0.65,
                Margin = new Thickness(0, 1, 0, 2)
            });
        }

        int metricStart = _liveMetricUpdaters.Count;
        OpenPopoverEntry? prevOwner = _currentPopoverBuildOwner;
        _currentPopoverBuildOwner = ownerEntry;
        List<Action<CustomWidgetEvaluationContext>> scopedRetainedPatchers;
        try
        {
            scopedRetainedPatchers = CaptureScopedPatchers(() =>
            {
                foreach (CustomWidgetNodeSpec child in popoverSpec.Children)
                {
                    root.Children.Add(BuildNode(child, ctx));
                }
            });
        }
        finally
        {
            _currentPopoverBuildOwner = prevOwner;
        }

        if (ownerEntry is not null)
        {
            if (scopedRetainedPatchers.Count > 0)
            {
                ownerEntry.MetricUpdaters.AddRange(scopedRetainedPatchers);
            }

            int metricCount = _liveMetricUpdaters.Count - metricStart;
            if (metricCount > 0)
            {
                ownerEntry.MetricUpdaters.AddRange(_liveMetricUpdaters.GetRange(metricStart, metricCount));
                _liveMetricUpdaters.RemoveRange(metricStart, metricCount);
            }
        }

        return ApplyContainerVisualOverrides(root, popoverSpec, ctx);
    }

    private bool TryResolveActivePopoverSpec(
        out CustomWidgetNodeSpec? popoverSpec,
        out string? popoverId)
    {
        popoverSpec = null;
        popoverId = ActivePopoverPreviewId;
        if (string.IsNullOrWhiteSpace(popoverId))
        {
            return false;
        }

        string trimmed = popoverId.Trim();
        if (_popoverSpecsById.TryGetValue(trimmed, out CustomWidgetNodeSpec? cached))
        {
            popoverSpec = cached;
            popoverId = !string.IsNullOrWhiteSpace(cached.PopoverId) ? cached.PopoverId!.Trim() : trimmed;
            return true;
        }

        CustomWidgetNodeSpec? fromManifest = _viewModel.Manifest.Popovers.FirstOrDefault(p =>
            string.Equals(p.PopoverId ?? p.Id, trimmed, StringComparison.OrdinalIgnoreCase));
        if (fromManifest is null)
        {
            return false;
        }

        popoverSpec = fromManifest;
        popoverId = !string.IsNullOrWhiteSpace(fromManifest.PopoverId)
            ? fromManifest.PopoverId!.Trim()
            : (!string.IsNullOrWhiteSpace(fromManifest.Id) ? fromManifest.Id!.Trim() : trimmed);
        _popoverSpecsById[popoverId] = fromManifest;
        return true;
    }

    private FrameworkElement BuildInStagePopoverCanvas(
        CustomWidgetNodeSpec popoverSpec,
        string activePopoverId,
        CustomWidgetEvaluationContext ctx)
    {
        var canvasGrid = new Grid();
        canvasGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        canvasGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        string backTip = Tr("CustomWidget.Workshop.Stage.PopoverBackToMain", ctx.CultureName)
            .TrimStart('←', '→', ' ');
        var backBtn = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = CornerSmall,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon
            {
                Glyph = "\uE72B",
                FontSize = 11.5,
                Foreground = ResolveForegroundBrush("default")
            },
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(backBtn, backTip);
        AutomationProperties.SetName(backBtn, backTip);
        backBtn.Click += (_, _) => StudioPopoverPreviewRequested?.Invoke("close", "*");

        string title = CustomWidgetExpressionEvaluator.Interpolate(popoverSpec.Title, ctx).Trim();
        var titleTb = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? activePopoverId : title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };

        var headerRow = new Grid { ColumnSpacing = 6 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.Children.Add(backBtn);
        Grid.SetColumn(titleTb, 1);
        headerRow.Children.Add(titleTb);

        var banner = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = CardStrokeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 10, 6),
            Child = headerRow
        };
        Grid.SetRow(banner, 0);
        canvasGrid.Children.Add(banner);

        var bodyStack = new StackPanel
        {
            Spacing = Math.Max(4, popoverSpec.Spacing),
            Padding = !string.IsNullOrWhiteSpace(popoverSpec.Padding)
                ? ParseThickness(popoverSpec.Padding)
                : new Thickness(12, 10, 12, 12)
        };
        foreach (CustomWidgetNodeSpec child in popoverSpec.Children)
        {
            bodyStack.Children.Add(BuildNode(child, ctx));
        }

        FrameworkElement styledBody = ApplyContainerVisualOverrides(bodyStack, popoverSpec, ctx);
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = styledBody
        };
        Grid.SetRow(scroller, 1);
        canvasGrid.Children.Add(scroller);

        return canvasGrid;
    }

    // =========================================================================
    // Markdown Component (Module 1)
    // =========================================================================

    private FrameworkElement BuildMarkdown(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawTemplate = spec.Text ?? spec.Value ?? string.Empty;

        string ResolveCurrentMarkdown(CustomWidgetEvaluationContext evalCtx)
        {
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                string bound = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, evalCtx);
                if (!string.IsNullOrEmpty(bound) || string.IsNullOrEmpty(rawTemplate))
                {
                    return bound;
                }
            }

            return CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, evalCtx);
        }

        bool useInternalScroll = spec.Height.HasValue || spec.MaxHeight.HasValue;
        bool interactiveTasks = !string.IsNullOrWhiteSpace(bindKey) && !_isNodeSelectionEnabled;

        var mdView = new MarkdownDocumentView
        {
            UseInternalScrollViewer = useInternalScroll,
            AllowRemoteImages = _viewModel.IsPermissionAllowed(CustomWidgetPermissionKind.PublicNetwork),
            AreTaskListsInteractive = interactiveTasks,
            FontSize = spec.FontSize is > 0 ? SnapLegacyFontSizeToHostTier(spec.FontSize.Value) : BodyFontSize,
            Foreground = TryResolveExplicitTextBrush(spec, ctx, out Brush? explicitFg) && explicitFg is not null
                ? explicitFg
                : ResolveForegroundBrush(spec.Color),
            Markdown = ResolveCurrentMarkdown(ctx)
        };

        if (spec.MaxHeight.HasValue && spec.MaxHeight.Value > 0)
        {
            mdView.MaxHeight = spec.MaxHeight.Value;
        }

        CustomWidgetEvaluationContext capturedCtx = ctx;
        if (interactiveTasks)
        {
            mdView.TaskToggleRequested += (_, e) => FireAndForgetSafeUi(async () =>
            {
                string currentMd = mdView.Markdown ?? string.Empty;
                if (s_sharedMarkdownService.TryToggleTask(currentMd, e.TaskIndex, out string updatedMd))
                {
                    mdView.Markdown = updatedMd;
                    CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(updatedMd);
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec(ResolveBindSetActionType(bindKey), Key: bindKey, Value: "$value")
                    ], evCtx);
                    if (spec.OnChange.Count > 0)
                    {
                        await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
                    }
                }
            });
        }

        if (interactiveTasks || ContainsDynamicToken(rawTemplate) || !string.IsNullOrWhiteSpace(bindKey))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                capturedCtx = liveCtx;
                string nextMd = ResolveCurrentMarkdown(liveCtx);
                if (!string.Equals(mdView.Markdown, nextMd, StringComparison.Ordinal))
                {
                    mdView.Markdown = nextMd;
                }
            });
        }

        RegisterMarkdownView(mdView);
        return ApplyContainerVisualOverrides(mdView, spec, ctx);
    }

    // =========================================================================
    // Table Component (Module 1)
    // =========================================================================

    private static IReadOnlyList<CustomWidgetRecord> ResolveNodeCollectionRecords(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        out string resolvedCollectionName)
    {
        resolvedCollectionName = string.Empty;
        string rawBinding = spec.Items ?? spec.Collection ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawBinding))
        {
            return [];
        }

        if (TryResolveComputedRecords(rawBinding, ctx, out IReadOnlyList<CustomWidgetRecord> computedRecords))
        {
            return computedRecords;
        }

        string colName = CustomWidgetExpressionEvaluator.Interpolate(rawBinding, ctx).Trim();
        if (colName.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
        {
            colName = colName["collections.".Length..];
            int dot = colName.IndexOf('.');
            if (dot > 0) colName = colName[..dot];
        }

        resolvedCollectionName = colName;
        return ctx.Collections.TryGetValue(colName, out List<CustomWidgetRecord>? list) ? list : [];
    }

    private FrameworkElement BuildTable(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        IReadOnlyList<CustomWidgetRecord> sourceRecords = ResolveNodeCollectionRecords(spec, ctx, out _);
        var filtered = new List<(CustomWidgetRecord Record, int Index)>();
        for (int i = 0; i < sourceRecords.Count; i++)
        {
            CustomWidgetRecord rec = sourceRecords[i];
            CustomWidgetEvaluationContext itemCtx = ctx.WithItem(rec, i);
            if (!string.IsNullOrWhiteSpace(spec.Filter) &&
                !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Filter, itemCtx))
            {
                continue;
            }
            filtered.Add((rec, i));
        }

        IReadOnlyList<CustomWidgetTableColumnSpec> columns = spec.TableColumns;
        if (columns.Count == 0 && filtered.Count > 0)
        {
            columns = filtered[0].Record.Fields.Keys
                .Take(5)
                .Select(static k => new CustomWidgetTableColumnSpec(k, k))
                .ToArray();
        }

        if (columns.Count == 0)
        {
            CustomWidgetEmptyStateSpec emptySpec = spec.EmptyState ?? new CustomWidgetEmptyStateSpec(
                "\uE80A",
                Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                string.Empty);
            var deferredHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            deferredHost.Children.Add(BuildEmptyState(emptySpec, ctx));

            List<Action<CustomWidgetEvaluationContext>>? mountedTablePatchers = null;
            RegisterRetainedPatcher(liveCtx =>
            {
                if (mountedTablePatchers is not null)
                {
                    for (int i = 0; i < mountedTablePatchers.Count; i++)
                    {
                        mountedTablePatchers[i](liveCtx);
                    }
                    return;
                }

                IReadOnlyList<CustomWidgetRecord> liveRecords = ResolveNodeCollectionRecords(spec, liveCtx, out _);
                CustomWidgetRecord? firstMatching = null;
                for (int i = 0; i < liveRecords.Count; i++)
                {
                    CustomWidgetRecord rec = liveRecords[i];
                    CustomWidgetEvaluationContext itemCtx = liveCtx.WithItem(rec, i);
                    if (!string.IsNullOrWhiteSpace(spec.Filter) &&
                        !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Filter, itemCtx))
                    {
                        continue;
                    }
                    if (rec.Fields.Count > 0)
                    {
                        firstMatching = rec;
                        break;
                    }
                }

                if (firstMatching is null)
                {
                    return;
                }

                CustomWidgetTableColumnSpec[] inferredCols = firstMatching.Fields.Keys
                    .Take(5)
                    .Select(static k => new CustomWidgetTableColumnSpec(k, k))
                    .ToArray();
                CustomWidgetNodeSpec resolvedSpec = spec with { TableColumns = inferredCols };
                FrameworkElement? builtTable = null;
                mountedTablePatchers = CaptureScopedPatchers(() =>
                {
                    builtTable = BuildTable(resolvedSpec, liveCtx);
                });
                deferredHost.Children.Clear();
                if (builtTable is not null)
                {
                    deferredHost.Children.Add(builtTable);
                }
            });

            return deferredHost;
        }

        string sortStateKey = $"__tableSort.{spec.Id ?? spec.NodePath}";
        string initialSortRaw = ctx.State.TryGetValue(sortStateKey, out string? savedSort) ? savedSort : string.Empty;
        string sortField = string.Empty;
        bool sortDesc = false;
        if (!string.IsNullOrWhiteSpace(initialSortRaw))
        {
            int colon = initialSortRaw.IndexOf(':');
            if (colon > 0)
            {
                sortField = initialSortRaw[..colon];
                sortDesc = string.Equals(initialSortRaw[(colon + 1)..], "desc", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                sortField = initialSortRaw;
            }
        }

        var tableStack = new StackPanel { Spacing = 0 };
        var rowsHost = new StackPanel { Spacing = 0 };
        var headerIndicators = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        static TextAlignment ParseColumnTextAlignment(string? align)
        {
            ReadOnlySpan<char> a = (align ?? "left").AsSpan().Trim();
            if (a.Equals("right", StringComparison.OrdinalIgnoreCase) || a.Equals("end", StringComparison.OrdinalIgnoreCase))
                return TextAlignment.Right;
            if (a.Equals("center", StringComparison.OrdinalIgnoreCase) || a.Equals("middle", StringComparison.OrdinalIgnoreCase))
                return TextAlignment.Center;
            return TextAlignment.Left;
        }

        static HorizontalAlignment ParseColumnHorizontalAlignment(string? align) =>
            ParseHorizontalAlignment(align, HorizontalAlignment.Left);

        void ApplyColumnDefinitions(Grid rowGrid)
        {
            foreach (CustomWidgetTableColumnSpec col in columns)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = ParseGridLength(string.IsNullOrWhiteSpace(col.Width) ? "*" : col.Width)
                });
            }
        }

        void RenderSortedRows()
        {
            rowsHost.Children.Clear();
            IEnumerable<(CustomWidgetRecord Record, int Index)> ordered = filtered;
            if (!string.IsNullOrWhiteSpace(sortField))
            {
                ordered = sortDesc
                    ? filtered.OrderByDescending(x => x.Record.GetField(sortField), NaturalTableValueComparer.Instance)
                    : filtered.OrderBy(x => x.Record.GetField(sortField), NaturalTableValueComparer.Instance);
            }

            foreach ((string colField, TextBlock indicatorTb) in headerIndicators)
            {
                if (string.Equals(colField, sortField, StringComparison.OrdinalIgnoreCase))
                {
                    indicatorTb.Text = sortDesc ? " \u25BE" : " \u25B4";
                    indicatorTb.Opacity = 0.9;
                }
                else
                {
                    indicatorTb.Text = string.Empty;
                    indicatorTb.Opacity = 0;
                }
            }

            int maxRows = spec.MaxLines > 0 ? spec.MaxLines : 200;
            int visualIdx = 0;
            foreach ((CustomWidgetRecord rec, int origIndex) in ordered.Take(maxRows))
            {
                CustomWidgetEvaluationContext rowCtx = ctx.WithItem(rec, origIndex);
                var rGrid = new Grid
                {
                    ColumnSpacing = 8,
                    Padding = new Thickness(10, 6, 10, 6)
                };
                ApplyColumnDefinitions(rGrid);

                for (int c = 0; c < columns.Count; c++)
                {
                    CustomWidgetTableColumnSpec col = columns[c];
                    string cellText = !string.IsNullOrWhiteSpace(col.Format)
                        ? CustomWidgetExpressionEvaluator.Interpolate(col.Format, rowCtx)
                        : rec.GetField(col.Field);

                    Brush cellFg = !string.IsNullOrWhiteSpace(col.Color)
                        ? ResolveAnyColorBrush(col.Color, rowCtx, "default")
                        : ResolveForegroundBrush("default");

                    var cellTb = new TextBlock
                    {
                        Text = cellText,
                        FontSize = spec.FontSize is > 0
                            ? SnapLegacyFontSizeToHostTier(spec.FontSize.Value)
                            : Math.Max(10.0, BodyFontSize - 0.5),
                        Foreground = cellFg,
                        TextAlignment = ParseColumnTextAlignment(col.Align),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1
                    };
                    if (string.Equals(col.Align, "right", StringComparison.OrdinalIgnoreCase))
                    {
                        SetTabularNumerals(cellTb);
                    }
                    Grid.SetColumn(cellTb, c);
                    rGrid.Children.Add(cellTb);
                }

                Brush stripeBg = spec.Striped && (visualIdx % 2 == 1)
                    ? GetOrCreateSolidBrush(IsDark ? Color.FromArgb(12, 255, 255, 255) : Color.FromArgb(9, 0, 0, 0))
                    : GetOrCreateSolidBrush(Colors.Transparent);

                var rowBorder = new Border
                {
                    Background = stripeBg,
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(0, visualIdx == 0 && !spec.ShowHeader ? 0 : 1, 0, 0),
                    Child = rGrid
                };

                if (spec.OnRowClick.Count > 0 && !_isNodeSelectionEnabled)
                {
                    Brush hoverBg = SubtleHoverBrush;
                    Brush restBg = stripeBg;
                    CustomWidgetEvaluationContext capturedRowCtx = rowCtx;
                    rowBorder.PointerEntered += (_, _) => rowBorder.Background = hoverBg;
                    rowBorder.PointerExited += (_, _) => rowBorder.Background = restBg;
                    rowBorder.Tapped += (_, e) =>
                    {
                        e.Handled = true;
                        FireAndForgetSafeUi(() => _viewModel.ExecuteActionsAsync(spec.OnRowClick, capturedRowCtx));
                    };
                }

                rowsHost.Children.Add(rowBorder);
                visualIdx++;
            }

            if (visualIdx == 0)
            {
                rowsHost.Children.Add(new TextBlock
                {
                    Text = spec.EmptyState?.Title is { Length: > 0 } et
                        ? CustomWidgetExpressionEvaluator.Interpolate(et, ctx)
                        : Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                    FontSize = CaptionFontSize,
                    Foreground = ResolveForegroundBrush("muted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(8, 12, 8, 12)
                });
            }
        }

        var headerTitleBlocks = new List<(CustomWidgetTableColumnSpec Col, TextBlock Tb)>(columns.Count);
        if (spec.ShowHeader)
        {
            var headerGrid = new Grid
            {
                ColumnSpacing = 8,
                Padding = new Thickness(10, 6, 10, 6),
                Background = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0))
            };
            ApplyColumnDefinitions(headerGrid);

            for (int c = 0; c < columns.Count; c++)
            {
                CustomWidgetTableColumnSpec col = columns[c];
                string headerLabel = CustomWidgetExpressionEvaluator.Interpolate(col.Header, ctx);
                var indicatorTb = new TextBlock
                {
                    FontSize = CaptionFontSize - 1,
                    Foreground = ResolveForegroundBrush("accent"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerIndicators[col.Field] = indicatorTb;

                var labelRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    HorizontalAlignment = ParseColumnHorizontalAlignment(col.Align),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var headerTitleTb = new TextBlock
                {
                    Text = headerLabel,
                    FontSize = CaptionFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ResolveForegroundBrush("secondary"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                headerTitleBlocks.Add((col, headerTitleTb));
                labelRow.Children.Add(headerTitleTb);
                labelRow.Children.Add(indicatorTb);

                var cellHost = new Border
                {
                    Background = GetOrCreateSolidBrush(Colors.Transparent),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = labelRow
                };

                if (spec.Sortable && !_isNodeSelectionEnabled && !string.IsNullOrWhiteSpace(col.Field))
                {
                    string capturedField = col.Field;
                    cellHost.Tapped += (_, e) =>
                    {
                        e.Handled = true;
                        if (string.Equals(sortField, capturedField, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!sortDesc)
                            {
                                sortDesc = true;
                            }
                            else
                            {
                                sortField = string.Empty;
                                sortDesc = false;
                            }
                        }
                        else
                        {
                            sortField = capturedField;
                            sortDesc = false;
                        }

                        string encoded = string.IsNullOrEmpty(sortField)
                            ? string.Empty
                            : $"{sortField}:{(sortDesc ? "desc" : "asc")}";
                        _viewModel.SetStateValueWithoutRebuild(sortStateKey, encoded);
                        RenderSortedRows();
                    };
                }

                Grid.SetColumn(cellHost, c);
                headerGrid.Children.Add(cellHost);
            }

            tableStack.Children.Add(headerGrid);
        }

        CustomWidgetEvaluationContext latestTableCtx = ctx;
        string? lastTableSignature = null;
        void RefreshTableFromContext(CustomWidgetEvaluationContext evalCtx, bool force)
        {
            latestTableCtx = evalCtx;
            for (int h = 0; h < headerTitleBlocks.Count; h++)
            {
                (CustomWidgetTableColumnSpec hCol, TextBlock hTb) = headerTitleBlocks[h];
                string nextHeader = CustomWidgetExpressionEvaluator.Interpolate(hCol.Header, evalCtx);
                if (!string.Equals(hTb.Text, nextHeader, StringComparison.Ordinal))
                {
                    hTb.Text = nextHeader;
                }
            }

            IReadOnlyList<CustomWidgetRecord> liveRecords = ResolveNodeCollectionRecords(spec, evalCtx, out _);
            var nextFiltered = new List<(CustomWidgetRecord Record, int Index)>(liveRecords.Count);
            var sig = new System.Text.StringBuilder(liveRecords.Count * 24 + 16);
            sig.Append(evalCtx.CultureName).Append('|').Append(sortField).Append(':').Append(sortDesc ? 'D' : 'A').Append('|');

            for (int i = 0; i < liveRecords.Count; i++)
            {
                CustomWidgetRecord rec = liveRecords[i];
                CustomWidgetEvaluationContext itemCtx = evalCtx.WithItem(rec, i);
                if (!string.IsNullOrWhiteSpace(spec.Filter) &&
                    !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Filter, itemCtx))
                {
                    continue;
                }
                nextFiltered.Add((rec, i));
                sig.Append(rec.Id).Append(':');
                for (int c = 0; c < columns.Count; c++)
                {
                    CustomWidgetTableColumnSpec col = columns[c];
                    string cell = !string.IsNullOrWhiteSpace(col.Format)
                        ? CustomWidgetExpressionEvaluator.Interpolate(col.Format, itemCtx)
                        : rec.GetField(col.Field);
                    sig.Append(cell).Append('|');
                }
                sig.Append(';');
            }

            string nextSig = sig.ToString();
            if (!force && string.Equals(lastTableSignature, nextSig, StringComparison.Ordinal))
            {
                return;
            }

            lastTableSignature = nextSig;
            filtered.Clear();
            filtered.AddRange(nextFiltered);
            ctx = latestTableCtx;
            RenderSortedRows();
        }

        RefreshTableFromContext(ctx, force: true);
        RegisterRetainedPatcher(liveCtx => RefreshTableFromContext(liveCtx, force: false));

        UIElement bodyElement = spec.MaxHeight is > 0 || spec.Height is > 0
            ? new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = spec.MaxHeight ?? spec.Height ?? double.PositiveInfinity,
                Content = WrapInShadowHostGrid(rowsHost)
            }
            : rowsHost;
        tableStack.Children.Add(bodyElement);

        var outerBorder = new Border
        {
            CornerRadius = CornerMedium,
            Background = ResolveCardBackground("default"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            Child = tableStack
        };

        return ApplyContainerVisualOverrides(outerBorder, spec, ctx);
    }

    private sealed class NaturalTableValueComparer : IComparer<string>
    {
        public static readonly NaturalTableValueComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            string sx = (x ?? string.Empty).Trim();
            string sy = (y ?? string.Empty).Trim();
            if (TryExtractNumericPrefix(sx, out double nx) && TryExtractNumericPrefix(sy, out double ny))
            {
                int cmp = nx.CompareTo(ny);
                if (cmp != 0) return cmp;
            }
            return string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryExtractNumericPrefix(string s, out double num)
        {
            string cleaned = s.TrimStart('$', '¥', '€', '£', '+', ' ').TrimEnd('%', ' ');
            return double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out num);
        }
    }
}
