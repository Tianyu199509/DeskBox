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
        }
    }

    private readonly Dictionary<string, CustomWidgetNodeSpec> _popoverSpecsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeakReference<FrameworkElement>> _popoverAnchorsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenPopoverEntry> _openPopoversById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MarkdownDocumentView> _activeMarkdownViews = [];
    private WeakReference<FrameworkElement>? _lastRootElementRef;

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
            foreach (OpenPopoverEntry entry in _openPopoversById.Values)
            {
                entry.ReleaseScopedResources();
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

    internal bool TryResolveExplicitBackgroundBrush(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        out Brush? brush)
    {
        if (spec.LinearGradient is { Stops.Count: > 0 })
        {
            brush = BuildLinearGradientBrush(spec.LinearGradient, ctx);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(spec.Background))
        {
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(spec.Background, ctx).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                if (TryParseRawColor(resolved, out Color rawColor))
                {
                    brush = GetOrCreateSolidBrush(rawColor);
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
                brush = TryParseRawColor(resolved, out Color raw)
                    ? GetOrCreateSolidBrush(raw)
                    : ResolveForegroundBrush(resolved);
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.Color) &&
            !string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase))
        {
            string resolvedColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx).Trim();
            if (TryParseRawColor(resolvedColor, out Color rawFromColor))
            {
                brush = GetOrCreateSolidBrush(rawFromColor);
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
                brush = TryParseRawColor(resolved, out Color raw)
                    ? GetOrCreateSolidBrush(raw)
                    : ResolveSemanticBrush(resolved);
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
            return GetOrCreateSolidBrush(raw);
        }

        return resolved.ToLowerInvariant() switch
        {
            "primary" or "foreground" => ResolveForegroundBrush("default"),
            "secondary" => ResolveForegroundBrush("secondary"),
            "muted" => ResolveForegroundBrush("muted"),
            _ => ResolveSemanticBrush(resolved)
        };
    }

    internal static FontWeight? TryResolveFontWeight(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "thin" or "100" => FontWeights.Thin,
            "extralight" or "ultralight" or "200" => FontWeights.ExtraLight,
            "light" or "300" => FontWeights.Light,
            "normal" or "regular" or "400" => FontWeights.Normal,
            "medium" or "500" => FontWeights.Medium,
            "semibold" or "demibold" or "600" => FontWeights.SemiBold,
            "bold" or "700" => FontWeights.Bold,
            "extrabold" or "ultrabold" or "800" => FontWeights.ExtraBold,
            "black" or "heavy" or "900" => FontWeights.Black,
            _ => null
        };
    }

    private void ApplyRawTextTypographyOverrides(
        TextBlock tb,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (spec.FontSize.HasValue && spec.FontSize.Value > 0)
        {
            tb.FontSize = Math.Clamp(spec.FontSize.Value, 8.0, 96.0);
        }

        if (TryResolveFontWeight(spec.FontWeight) is { } fw)
        {
            tb.FontWeight = fw;
        }

        if (!string.IsNullOrWhiteSpace(spec.FontFamily))
        {
            tb.FontFamily = new FontFamily(spec.FontFamily.Trim());
        }

        if (spec.Strikethrough)
        {
            tb.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        }

        if (TryResolveExplicitTextBrush(spec, ctx, out Brush? explicitFg) && explicitFg is not null)
        {
            tb.Foreground = explicitFg;
            WidgetTextShadow.SetCast(tb, false);
        }
    }

    private FrameworkElement ApplyContainerVisualOverrides(
        FrameworkElement element,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        bool hasBg = TryResolveExplicitBackgroundBrush(spec, ctx, out Brush? bgBrush);
        bool hasBorderColor = TryResolveExplicitBorderBrush(spec, ctx, out Brush? borderBrush);
        bool hasBorderWidth = !string.IsNullOrWhiteSpace(spec.BorderWidth);
        bool hasCornerRadius = spec.CornerRadius.HasValue;

        if (!hasBg && !hasBorderColor && !hasBorderWidth && !hasCornerRadius)
        {
            return element;
        }

        Thickness borderThickness = hasBorderWidth
            ? ParseThickness(spec.BorderWidth)
            : (hasBorderColor ? new Thickness(1) : new Thickness(0));
        CornerRadius radius = new(Math.Max(0, spec.CornerRadius ?? 0));

        if (element is Border border)
        {
            if (hasBg && bgBrush is not null) border.Background = bgBrush;
            if (hasBorderColor && borderBrush is not null) border.BorderBrush = borderBrush;
            if (hasBorderWidth || hasBorderColor) border.BorderThickness = borderThickness;
            if (hasCornerRadius) border.CornerRadius = radius;
            return border;
        }

        if (element is StackPanel sp)
        {
            if (hasBg && bgBrush is not null) sp.Background = bgBrush;
            if (hasBorderColor && borderBrush is not null) sp.BorderBrush = borderBrush;
            if (hasBorderWidth || hasBorderColor) sp.BorderThickness = borderThickness;
            if (hasCornerRadius) sp.CornerRadius = radius;
            return sp;
        }

        if (element is Grid grid)
        {
            if (hasBg && bgBrush is not null) grid.Background = bgBrush;
            if (hasBorderColor && borderBrush is not null) grid.BorderBrush = borderBrush;
            if (hasBorderWidth || hasBorderColor) grid.BorderThickness = borderThickness;
            if (hasCornerRadius) grid.CornerRadius = radius;
            return grid;
        }

        var wrapper = new Border
        {
            Child = element
        };
        if (hasBg && bgBrush is not null) wrapper.Background = bgBrush;
        if (hasBorderColor && borderBrush is not null) wrapper.BorderBrush = borderBrush;
        if (hasBorderWidth || hasBorderColor) wrapper.BorderThickness = borderThickness;
        if (hasCornerRadius) wrapper.CornerRadius = radius;
        return wrapper;
    }

    private void ApplyRawButtonVisualOverrides(
        Button btn,
        TextBlock? labelTb,
        FrameworkElement? iconEl,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (spec.CornerRadius.HasValue)
        {
            btn.CornerRadius = new CornerRadius(Math.Max(0, spec.CornerRadius.Value));
        }

        if (spec.FontSize.HasValue && spec.FontSize.Value > 0)
        {
            double fs = Math.Clamp(spec.FontSize.Value, 8.0, 64.0);
            btn.FontSize = fs;
            if (labelTb is not null) labelTb.FontSize = fs;
        }

        if (TryResolveFontWeight(spec.FontWeight) is { } fw)
        {
            btn.FontWeight = fw;
            if (labelTb is not null) labelTb.FontWeight = fw;
        }

        if (!string.IsNullOrWhiteSpace(spec.FontFamily))
        {
            var ff = new FontFamily(spec.FontFamily.Trim());
            btn.FontFamily = ff;
            if (labelTb is not null) labelTb.FontFamily = ff;
        }

        bool hasCustomBg = TryResolveExplicitBackgroundBrush(spec, ctx, out Brush? bgBrush);
        bool hasCustomBorderColor = TryResolveExplicitBorderBrush(spec, ctx, out Brush? borderBrush);
        bool hasCustomBorderWidth = !string.IsNullOrWhiteSpace(spec.BorderWidth);
        if (hasCustomBg || hasCustomBorderColor || hasCustomBorderWidth)
        {
            if (hasCustomBg && bgBrush is not null)
            {
                btn.Background = bgBrush;
            }
            if (hasCustomBorderColor && borderBrush is not null)
            {
                btn.BorderBrush = borderBrush;
            }
            if (hasCustomBorderWidth || hasCustomBorderColor)
            {
                btn.BorderThickness = hasCustomBorderWidth
                    ? ParseThickness(spec.BorderWidth)
                    : new Thickness(1);
            }

            Brush effectiveBg = btn.Background ?? SubtleHoverBrush;
            Brush effectiveBorder = btn.BorderBrush ?? GetOrCreateSolidBrush(Colors.Transparent);
            ApplyButtonStateOverrides(btn, effectiveBg, effectiveBg, effectiveBorder, effectiveBorder);
        }

        if (TryResolveExplicitTextBrush(spec, ctx, out Brush? fgBrush) && fgBrush is not null)
        {
            btn.Foreground = fgBrush;
            if (labelTb is not null)
            {
                labelTb.Foreground = fgBrush;
                WidgetTextShadow.SetCast(labelTb, false);
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

        if (!string.IsNullOrWhiteSpace(id))
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
                triggerBtn.HorizontalAlignment = spec.HorizontalAlignment.Trim().ToLowerInvariant() switch
                {
                    "left" => HorizontalAlignment.Left,
                    "center" => HorizontalAlignment.Center,
                    "right" => HorizontalAlignment.Right,
                    "stretch" => HorizontalAlignment.Stretch,
                    _ => triggerBtn.HorizontalAlignment
                };
            }
            if (!string.IsNullOrWhiteSpace(spec.VerticalAlignment))
            {
                triggerBtn.VerticalAlignment = spec.VerticalAlignment.Trim().ToLowerInvariant() switch
                {
                    "top" => VerticalAlignment.Top,
                    "center" => VerticalAlignment.Center,
                    "bottom" => VerticalAlignment.Bottom,
                    "stretch" => VerticalAlignment.Stretch,
                    _ => triggerBtn.VerticalAlignment
                };
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
        string normalizedMode = (mode ?? "toggle").Trim().ToLowerInvariant();

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

        FrameworkElement? anchor = explicitAnchor;
        if (anchor is null &&
            _popoverAnchorsById.TryGetValue(id, out WeakReference<FrameworkElement>? anchorRef) &&
            anchorRef.TryGetTarget(out FrameworkElement? trackedAnchor) &&
            trackedAnchor.XamlRoot is not null)
        {
            anchor = trackedAnchor;
        }

        if (anchor is null &&
            _lastRootElementRef is not null &&
            _lastRootElementRef.TryGetTarget(out FrameworkElement? rootFallback) &&
            rootFallback.XamlRoot is not null)
        {
            anchor = rootFallback;
        }

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
            string.IsNullOrWhiteSpace(title) ? _viewModel.Config.Name : title);

        var bodyHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var entry = new OpenPopoverEntry(popoverWindow, bodyHost, popoverSpec);
        bodyHost.Content = BuildPopoverBodyContent(popoverSpec, popoverWindow, anchor, entry);
        _openPopoversById[id] = entry;
        popoverWindow.PopoverClosed += (_, _) =>
        {
            entry.ReleaseScopedResources();
            _openPopoversById.Remove(id);
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

    internal void RefreshOpenPopovers()
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

                FrameworkElement? anchor = null;
                if (_popoverAnchorsById.TryGetValue(id, out WeakReference<FrameworkElement>? anchorRef))
                {
                    anchorRef.TryGetTarget(out anchor);
                }

                entry.ReleaseScopedResources();
                entry.BodyHost.Content = BuildPopoverBodyContent(entry.Spec, entry.Window, anchor, entry);
            }
            catch
            {
            }
        }
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
            Spacing = Math.Max(4, popoverSpec.Spacing)
        };

        if (!string.IsNullOrWhiteSpace(popoverSpec.Padding))
        {
            root.Padding = ParseThickness(popoverSpec.Padding);
        }

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
        }

        int metricStart = _liveMetricUpdaters.Count;
        int mdStart = _activeMarkdownViews.Count;
        foreach (CustomWidgetNodeSpec child in popoverSpec.Children)
        {
            root.Children.Add(BuildNode(child, ctx));
        }

        if (ownerEntry is not null)
        {
            int metricCount = _liveMetricUpdaters.Count - metricStart;
            if (metricCount > 0)
            {
                ownerEntry.MetricUpdaters.AddRange(_liveMetricUpdaters.GetRange(metricStart, metricCount));
                _liveMetricUpdaters.RemoveRange(metricStart, metricCount);
            }

            int mdCount = _activeMarkdownViews.Count - mdStart;
            if (mdCount > 0)
            {
                ownerEntry.MarkdownViews.AddRange(_activeMarkdownViews.GetRange(mdStart, mdCount));
                _activeMarkdownViews.RemoveRange(mdStart, mdCount);
            }
        }

        return ApplyContainerVisualOverrides(root, popoverSpec, ctx);
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
            FontSize = spec.FontSize is > 0 ? Math.Clamp(spec.FontSize.Value, 10, 32) : BodyFontSize,
            Foreground = TryResolveExplicitTextBrush(spec, ctx, out Brush? explicitFg) && explicitFg is not null
                ? explicitFg
                : ResolveForegroundBrush(spec.Color),
            Markdown = ResolveCurrentMarkdown(ctx)
        };

        if (spec.MaxHeight.HasValue && spec.MaxHeight.Value > 0)
        {
            mdView.MaxHeight = spec.MaxHeight.Value;
        }

        if (interactiveTasks)
        {
            CustomWidgetEvaluationContext capturedCtx = ctx;
            mdView.TaskToggleRequested += async (_, e) =>
            {
                string currentMd = mdView.Markdown ?? string.Empty;
                if (s_sharedMarkdownService.TryToggleTask(currentMd, e.TaskIndex, out string updatedMd))
                {
                    mdView.Markdown = updatedMd;
                    CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(updatedMd);
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("state.set", Key: bindKey, Value: "$value")
                    ], evCtx);
                    if (spec.OnChange.Count > 0)
                    {
                        await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
                    }
                }
            };
        }

        if (ContainsSystemMetricsToken(rawTemplate) || ContainsSystemMetricsToken(bindKey))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                string nextMd = ResolveCurrentMarkdown(liveCtx);
                if (!string.Equals(mdView.Markdown, nextMd, StringComparison.Ordinal))
                {
                    mdView.Markdown = nextMd;
                }
            });
        }

        _activeMarkdownViews.Add(mdView);
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
            return BuildEmptyState(
                spec.EmptyState ?? new CustomWidgetEmptyStateSpec(
                    "\uE80A",
                    Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                    string.Empty),
                ctx);
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

        static TextAlignment ParseColumnTextAlignment(string? align) =>
            (align ?? "left").Trim().ToLowerInvariant() switch
            {
                "right" or "end" => TextAlignment.Right,
                "center" or "middle" => TextAlignment.Center,
                _ => TextAlignment.Left
            };

        static HorizontalAlignment ParseColumnHorizontalAlignment(string? align) =>
            (align ?? "left").Trim().ToLowerInvariant() switch
            {
                "right" or "end" => HorizontalAlignment.Right,
                "center" or "middle" => HorizontalAlignment.Center,
                _ => HorizontalAlignment.Left
            };

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
                        FontSize = Math.Max(11.5, (spec.FontSize ?? BodyFontSize) - 0.5),
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
                    rowBorder.Tapped += async (_, e) =>
                    {
                        e.Handled = true;
                        await _viewModel.ExecuteActionsAsync(spec.OnRowClick, capturedRowCtx);
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
                labelRow.Children.Add(new TextBlock
                {
                    Text = headerLabel,
                    FontSize = CaptionFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ResolveForegroundBrush("secondary"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
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

        RenderSortedRows();

        UIElement bodyElement = spec.MaxHeight is > 0 || spec.Height is > 0
            ? new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = spec.MaxHeight ?? spec.Height ?? double.PositiveInfinity,
                Content = rowsHost
            }
            : rowsHost;
        tableStack.Children.Add(bodyElement);

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(0, spec.CornerRadius ?? RadiusMedium)),
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

    // =========================================================================
    // DonutChart & Multi-Series Chart (Module 1)
    // =========================================================================

    private sealed record DonutSliceData(string Label, double Value, Brush Brush);

    private FrameworkElement BuildDonutChart(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double ringDiameter = Math.Clamp(spec.Size ?? spec.Height ?? spec.Width ?? 128, 64, 280);
        double strokeThickness = Math.Clamp(Math.Round(ringDiameter * 0.16), 8, 32);
        double radius = (ringDiameter - strokeThickness) / 2.0;
        double cx = ringDiameter / 2.0;
        double cy = ringDiameter / 2.0;

        var ringHost = new Grid
        {
            Width = ringDiameter,
            Height = ringDiameter,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slicesCanvas = new Canvas
        {
            Width = ringDiameter,
            Height = ringDiameter
        };

        var trackRing = new WinUIPath
        {
            Width = ringDiameter,
            Height = ringDiameter,
            StrokeThickness = strokeThickness,
            Stroke = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)),
            Data = new EllipseGeometry
            {
                Center = new Point(cx, cy),
                RadiusX = radius,
                RadiusY = radius
            }
        };
        ringHost.Children.Add(trackRing);
        ringHost.Children.Add(slicesCanvas);

        var centerStack = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = Math.Max(36, ringDiameter - (strokeThickness * 2.4))
        };
        var centerValTb = new TextBlock
        {
            FontFamily = DisplayFontFamily,
            FontSize = Math.Clamp(ringDiameter * 0.16, 13, 24),
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(centerValTb);
        var centerTitleTb = new TextBlock
        {
            FontSize = CaptionFontSize - 0.5,
            Foreground = ResolveForegroundBrush("muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        centerStack.Children.Add(centerValTb);
        centerStack.Children.Add(centerTitleTb);
        ringHost.Children.Add(centerStack);

        var legendWrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 10,
            VerticalSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        List<DonutSliceData> ExtractSlices(CustomWidgetEvaluationContext evalCtx)
        {
            var list = new List<DonutSliceData>();
            IReadOnlyList<CustomWidgetRecord> records = ResolveNodeCollectionRecords(spec, evalCtx, out _);
            if (records.Count > 0)
            {
                string labelField = !string.IsNullOrWhiteSpace(spec.TitleField) ? spec.TitleField! : "label";
                string valField = !string.IsNullOrWhiteSpace(spec.ValueField) ? spec.ValueField! : "value";
                string colorField = !string.IsNullOrWhiteSpace(spec.ColorField) ? spec.ColorField! : "color";

                for (int i = 0; i < records.Count; i++)
                {
                    CustomWidgetRecord rec = records[i];
                    string rawVal = rec.GetField(valField);
                    if (string.IsNullOrWhiteSpace(rawVal)) rawVal = rec.GetField("count");
                    if (!double.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
                    {
                        continue;
                    }

                    string lbl = rec.GetField(labelField);
                    if (string.IsNullOrWhiteSpace(lbl)) lbl = rec.GetField("title");
                    if (string.IsNullOrWhiteSpace(lbl)) lbl = rec.GetField("name");
                    if (string.IsNullOrWhiteSpace(lbl)) lbl = $"#{i + 1}";

                    string cToken = rec.GetField(colorField);
                    string fallbackTone = DefaultChartSeriesPalette[i % DefaultChartSeriesPalette.Length];
                    Brush sliceBrush = ResolveAnyColorBrush(cToken, evalCtx, fallbackTone);
                    list.Add(new DonutSliceData(lbl, v, sliceBrush));
                }
            }
            else if (spec.Series.Count > 0)
            {
                for (int i = 0; i < spec.Series.Count; i++)
                {
                    CustomWidgetChartSeriesSpec s = spec.Series[i];
                    double v = s.Values is { Count: > 0 }
                        ? s.Values.Sum()
                        : (CustomWidgetExpressionEvaluator.EvaluateDouble(s.ValueField, evalCtx) ?? 0);
                    if (v <= 0) continue;
                    string fallbackTone = DefaultChartSeriesPalette[i % DefaultChartSeriesPalette.Length];
                    list.Add(new DonutSliceData(
                        CustomWidgetExpressionEvaluator.Interpolate(s.Name, evalCtx),
                        v,
                        ResolveAnyColorBrush(s.Color, evalCtx, fallbackTone)));
                }
            }

            return list;
        }

        string? lastSlicesSignature = null;
        void RenderDonut(CustomWidgetEvaluationContext evalCtx)
        {
            List<DonutSliceData> slices = ExtractSlices(evalCtx);
            double total = slices.Sum(static s => s.Value);

            string resolvedCenterVal = !string.IsNullOrWhiteSpace(spec.CenterValue)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.CenterValue, evalCtx)
                : (total > 0 ? total.ToString("0.#", CultureInfo.InvariantCulture) : "0");
            string resolvedCenterTitle = !string.IsNullOrWhiteSpace(spec.CenterTitle)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.CenterTitle, evalCtx)
                : (!string.IsNullOrWhiteSpace(spec.Title) ? CustomWidgetExpressionEvaluator.Interpolate(spec.Title, evalCtx) : string.Empty);

            centerValTb.Text = resolvedCenterVal;
            centerTitleTb.Text = resolvedCenterTitle;
            centerTitleTb.Visibility = string.IsNullOrWhiteSpace(resolvedCenterTitle) ? Visibility.Collapsed : Visibility.Visible;

            var sigBuilder = new System.Text.StringBuilder(slices.Count * 24);
            foreach (DonutSliceData s in slices)
            {
                Color c = s.Brush is SolidColorBrush scb ? scb.Color : Colors.Transparent;
                sigBuilder.Append(s.Label).Append('|')
                    .Append(s.Value.ToString("G9", CultureInfo.InvariantCulture)).Append('|')
                    .Append(c.A).Append(',').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B).Append(';');
            }

            string nextSignature = sigBuilder.ToString();
            if (string.Equals(lastSlicesSignature, nextSignature, StringComparison.Ordinal))
            {
                return;
            }

            lastSlicesSignature = nextSignature;
            slicesCanvas.Children.Clear();
            legendWrap.Children.Clear();

            if (total <= 0 || slices.Count == 0)
            {
                return;
            }

            double gapDeg = slices.Count > 1 ? 2.4 : 0.0;
            double cursorDeg = -90.0;
            foreach (DonutSliceData slice in slices)
            {
                double sweepDeg = (slice.Value / total) * 360.0;
                double drawSweep = Math.Max(0.8, sweepDeg - gapDeg);
                double startDeg = cursorDeg + (gapDeg * 0.5);
                cursorDeg += sweepDeg;

                WinUIPath segPath;
                if (slices.Count == 1 || drawSweep >= 359.5)
                {
                    segPath = new WinUIPath
                    {
                        Width = ringDiameter,
                        Height = ringDiameter,
                        StrokeThickness = strokeThickness,
                        Stroke = slice.Brush,
                        Data = new EllipseGeometry
                        {
                            Center = new Point(cx, cy),
                            RadiusX = radius,
                            RadiusY = radius
                        }
                    };
                }
                else
                {
                    double startRad = startDeg * Math.PI / 180.0;
                    double endRad = (startDeg + drawSweep) * Math.PI / 180.0;
                    var startPt = new Point(cx + (radius * Math.Cos(startRad)), cy + (radius * Math.Sin(startRad)));
                    var endPt = new Point(cx + (radius * Math.Cos(endRad)), cy + (radius * Math.Sin(endRad)));

                    var fig = new PathFigure
                    {
                        StartPoint = startPt,
                        IsClosed = false,
                        IsFilled = false
                    };
                    fig.Segments.Add(new ArcSegment
                    {
                        Point = endPt,
                        Size = new Size(radius, radius),
                        IsLargeArc = drawSweep > 180.0,
                        SweepDirection = SweepDirection.Clockwise
                    });
                    var geom = new PathGeometry();
                    geom.Figures.Add(fig);

                    segPath = new WinUIPath
                    {
                        Width = ringDiameter,
                        Height = ringDiameter,
                        StrokeThickness = strokeThickness,
                        StrokeStartLineCap = PenLineCap.Flat,
                        StrokeEndLineCap = PenLineCap.Flat,
                        Stroke = slice.Brush,
                        Data = geom
                    };
                }

                double pct = (slice.Value / total) * 100.0;
                ToolTipService.SetToolTip(segPath, $"{slice.Label}: {slice.Value:0.##} ({pct:0.#}%)");
                slicesCanvas.Children.Add(segPath);

                if (spec.ShowLegend)
                {
                    var itemRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    itemRow.Children.Add(new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(4),
                        Background = slice.Brush,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    itemRow.Children.Add(new TextBlock
                    {
                        Text = $"{slice.Label} ({pct:0}%)",
                        FontSize = CaptionFontSize - 0.5,
                        Foreground = ResolveForegroundBrush("secondary"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    legendWrap.Children.Add(itemRow);
                }
            }
        }

        RenderDonut(ctx);
        if (ContainsSystemMetricsToken(spec.Items ?? spec.Collection) ||
            ContainsSystemMetricsToken(spec.CenterValue) ||
            ContainsSystemMetricsToken(spec.CenterTitle) ||
            spec.Series.Any(static s =>
                ContainsSystemMetricsToken(s.ValueField) ||
                ContainsSystemMetricsToken(s.Name) ||
                ContainsSystemMetricsToken(s.Color)))
        {
            _liveMetricUpdaters.Add(RenderDonut);
        }

        bool isHorizontal = string.Equals(spec.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
        var layout = new StackPanel
        {
            Orientation = isHorizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        layout.Children.Add(ringHost);
        if (spec.ShowLegend)
        {
            legendWrap.VerticalAlignment = VerticalAlignment.Center;
            layout.Children.Add(legendWrap);
        }

        return ApplyContainerVisualOverrides(layout, spec, ctx);
    }

    private sealed record ResolvedChartSeries(
        string Name,
        string Kind,
        Brush StrokeBrush,
        Color SolidColor,
        IReadOnlyList<double> Points);

    private FrameworkElement BuildChart(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double chartHeight = Math.Clamp(spec.Height ?? 148, 64, 420);
        string defaultKind = (spec.ChartKind ?? "line").Trim().ToLowerInvariant();

        var rootStack = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleTb = new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx),
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("secondary")
            };
            Grid.SetColumn(titleTb, 0);
            titleRow.Children.Add(titleTb);

            if (!string.IsNullOrWhiteSpace(spec.Subtitle))
            {
                var subTb = new TextBlock
                {
                    Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, ctx),
                    FontSize = CaptionFontSize,
                    Foreground = ResolveForegroundBrush("muted")
                };
                Grid.SetColumn(subTb, 1);
                titleRow.Children.Add(subTb);
            }
            rootStack.Children.Add(titleRow);
        }

        var plotHost = new Grid
        {
            Height = chartHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var plotCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        plotHost.Children.Add(plotCanvas);
        rootStack.Children.Add(plotHost);

        var xLabelsGrid = new Grid
        {
            Margin = new Thickness(0, -2, 0, 0)
        };
        xLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        xLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        xLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var xStartTb = new TextBlock { FontSize = 10.5, Foreground = ResolveForegroundBrush("muted"), HorizontalAlignment = HorizontalAlignment.Left };
        var xMidTb = new TextBlock { FontSize = 10.5, Foreground = ResolveForegroundBrush("muted"), HorizontalAlignment = HorizontalAlignment.Center };
        var xEndTb = new TextBlock { FontSize = 10.5, Foreground = ResolveForegroundBrush("muted"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(xStartTb, 0);
        Grid.SetColumn(xMidTb, 1);
        Grid.SetColumn(xEndTb, 2);
        xLabelsGrid.Children.Add(xStartTb);
        xLabelsGrid.Children.Add(xMidTb);
        xLabelsGrid.Children.Add(xEndTb);
        rootStack.Children.Add(xLabelsGrid);

        var legendRow = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 12,
            VerticalSpacing = 4,
            Visibility = spec.ShowLegend ? Visibility.Visible : Visibility.Collapsed
        };
        rootStack.Children.Add(legendRow);

        (List<ResolvedChartSeries> SeriesList, List<string> Categories) ExtractChartModel(CustomWidgetEvaluationContext evalCtx)
        {
            var categories = new List<string>();
            var resolvedSeries = new List<ResolvedChartSeries>();
            IReadOnlyList<CustomWidgetRecord> records = ResolveNodeCollectionRecords(spec, evalCtx, out _);

            string xField = !string.IsNullOrWhiteSpace(spec.XField) ? spec.XField! : "label";
            if (records.Count > 0)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    CustomWidgetRecord r = records[i];
                    string cat = r.GetField(xField);
                    if (string.IsNullOrWhiteSpace(cat)) cat = r.GetField("date");
                    if (string.IsNullOrWhiteSpace(cat)) cat = r.GetField("name");
                    if (string.IsNullOrWhiteSpace(cat)) cat = r.GetField("x");
                    categories.Add(string.IsNullOrWhiteSpace(cat) ? $"{i + 1}" : cat);
                }
            }

            IReadOnlyList<CustomWidgetChartSeriesSpec> seriesSpecs = spec.Series;
            if (seriesSpecs.Count == 0 && records.Count > 0)
            {
                string vf = !string.IsNullOrWhiteSpace(spec.ValueField) ? spec.ValueField! : "value";
                seriesSpecs = [new CustomWidgetChartSeriesSpec(spec.Title ?? vf, vf, spec.Color, defaultKind)];
            }
            else if (seriesSpecs.Count == 0 && !string.IsNullOrWhiteSpace(spec.Sparkline))
            {
                IReadOnlyList<double> hist = CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, evalCtx);
                seriesSpecs = [new CustomWidgetChartSeriesSpec(spec.Title ?? "Series", "value", spec.Color, defaultKind, hist)];
            }

            for (int si = 0; si < seriesSpecs.Count; si++)
            {
                CustomWidgetChartSeriesSpec sSpec = seriesSpecs[si];
                string fallbackTone = DefaultChartSeriesPalette[si % DefaultChartSeriesPalette.Length];
                Brush sBrush = ResolveAnyColorBrush(sSpec.Color, evalCtx, fallbackTone);
                Color solid = sBrush is SolidColorBrush scb
                    ? scb.Color
                    : ResolveSemanticBrush(fallbackTone).Color;

                var pts = new List<double>();
                if (sSpec.Values is { Count: > 0 })
                {
                    pts.AddRange(sSpec.Values);
                }
                else if (records.Count > 0)
                {
                    string vf = !string.IsNullOrWhiteSpace(sSpec.ValueField) ? sSpec.ValueField : "value";
                    foreach (CustomWidgetRecord r in records)
                    {
                        double.TryParse(r.GetField(vf), NumberStyles.Float, CultureInfo.InvariantCulture, out double dv);
                        pts.Add(dv);
                    }
                }

                string sKind = !string.IsNullOrWhiteSpace(sSpec.Kind)
                    ? sSpec.Kind.Trim().ToLowerInvariant()
                    : (defaultKind == "line" && spec.ShowArea && seriesSpecs.Count == 1 && string.IsNullOrWhiteSpace(spec.ChartKind)
                        ? "area"
                        : defaultKind);

                resolvedSeries.Add(new ResolvedChartSeries(
                    CustomWidgetExpressionEvaluator.Interpolate(sSpec.Name, evalCtx),
                    sKind,
                    sBrush,
                    solid,
                    pts));
            }

            return (resolvedSeries, categories);
        }

        string? lastPlotSignature = null;
        void RenderPlot(CustomWidgetEvaluationContext evalCtx)
        {
            double width = plotHost.ActualWidth > 16 ? plotHost.ActualWidth : Math.Max(180, (spec.Width ?? 280) - 24);
            double height = plotHost.ActualHeight > 16 ? plotHost.ActualHeight : chartHeight;
            const double padTop = 8;
            const double padBottom = 6;
            double usableH = Math.Max(16, height - padTop - padBottom);

            (List<ResolvedChartSeries> seriesList, List<string> categories) = ExtractChartModel(evalCtx);
            var sigBuilder = new System.Text.StringBuilder(128);
            sigBuilder.Append(Math.Round(width, 1).ToString(CultureInfo.InvariantCulture)).Append('x')
                .Append(Math.Round(height, 1).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(IsDark ? 'D' : 'L').Append('|');
            foreach (string cat in categories)
            {
                sigBuilder.Append(cat).Append(',');
            }
            sigBuilder.Append('|');
            foreach (ResolvedChartSeries s in seriesList)
            {
                sigBuilder.Append(s.Name).Append(':').Append(s.Kind).Append(':')
                    .Append(s.SolidColor.A).Append('.').Append(s.SolidColor.R).Append('.')
                    .Append(s.SolidColor.G).Append('.').Append(s.SolidColor.B).Append('[');
                for (int pi = 0; pi < s.Points.Count; pi++)
                {
                    if (pi > 0) sigBuilder.Append(',');
                    sigBuilder.Append(s.Points[pi].ToString("G9", CultureInfo.InvariantCulture));
                }
                sigBuilder.Append("];");
            }

            string nextSignature = sigBuilder.ToString();
            if (string.Equals(lastPlotSignature, nextSignature, StringComparison.Ordinal))
            {
                return;
            }

            lastPlotSignature = nextSignature;
            plotCanvas.Children.Clear();
            legendRow.Children.Clear();
            int maxPoints = seriesList.Count > 0 ? seriesList.Max(static s => s.Points.Count) : 0;

            if (categories.Count > 0)
            {
                xStartTb.Text = categories[0];
                xMidTb.Text = categories.Count >= 3 ? categories[categories.Count / 2] : string.Empty;
                xEndTb.Text = categories.Count >= 2 ? categories[^1] : string.Empty;
                xLabelsGrid.Visibility = Visibility.Visible;
            }
            else
            {
                xLabelsGrid.Visibility = Visibility.Collapsed;
            }

            if (spec.ShowGrid)
            {
                Brush gridBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0));
                for (int g = 0; g <= 3; g++)
                {
                    double gy = padTop + (usableH * g / 3.0);
                    plotCanvas.Children.Add(new Line
                    {
                        X1 = 0,
                        Y1 = gy,
                        X2 = width,
                        Y2 = gy,
                        Stroke = gridBrush,
                        StrokeThickness = 1
                    });
                }
            }

            if (maxPoints == 0 || seriesList.Count == 0)
            {
                return;
            }

            double minVal = 0;
            double maxVal = 1;
            bool firstVal = true;
            foreach (ResolvedChartSeries s in seriesList)
            {
                foreach (double v in s.Points)
                {
                    if (firstVal)
                    {
                        minVal = Math.Min(0, v);
                        maxVal = v;
                        firstVal = false;
                    }
                    else
                    {
                        if (v < minVal) minVal = v;
                        if (v > maxVal) maxVal = v;
                    }
                }
            }

            if (Math.Abs(maxVal - minVal) < 0.0001)
            {
                maxVal = minVal + 1.0;
            }

            double MapY(double v) =>
                padTop + usableH - (((v - minVal) / (maxVal - minVal)) * usableH);

            double baselineY = MapY(Math.Clamp(0, minVal, maxVal));

            List<ResolvedChartSeries> barSeries = seriesList.Where(static s => s.Kind == "bar").ToList();
            int barSeriesCount = Math.Max(1, barSeries.Count);

            for (int si = 0; si < seriesList.Count; si++)
            {
                ResolvedChartSeries s = seriesList[si];
                if (s.Points.Count == 0) continue;

                if (s.Kind == "bar")
                {
                    int barIdx = barSeries.IndexOf(s);
                    double slotWidth = width / Math.Max(1, maxPoints);
                    double groupWidth = slotWidth * 0.72;
                    double singleBarWidth = Math.Max(3.0, (groupWidth / barSeriesCount) - (barSeriesCount > 1 ? 2 : 0));

                    for (int i = 0; i < s.Points.Count; i++)
                    {
                        double v = s.Points[i];
                        double yVal = MapY(v);
                        double top = Math.Min(yVal, baselineY);
                        double barH = Math.Max(2.0, Math.Abs(baselineY - yVal));
                        double groupLeft = (i * slotWidth) + ((slotWidth - groupWidth) * 0.5);
                        double barLeft = groupLeft + (barIdx * (singleBarWidth + 2));

                        var rect = new Rectangle
                        {
                            Width = singleBarWidth,
                            Height = barH,
                            RadiusX = Math.Min(3, singleBarWidth * 0.4),
                            RadiusY = Math.Min(3, singleBarWidth * 0.4),
                            Fill = s.StrokeBrush
                        };
                        string catLabel = i < categories.Count ? categories[i] : $"#{i + 1}";
                        ToolTipService.SetToolTip(rect, $"{s.Name} · {catLabel}: {v:0.##}{spec.Unit}");
                        Canvas.SetLeft(rect, barLeft);
                        Canvas.SetTop(rect, top);
                        plotCanvas.Children.Add(rect);
                    }
                }
                else
                {
                    int count = s.Points.Count;
                    double stepX = count > 1 ? width / (count - 1) : width;
                    var linePts = new PointCollection();
                    var areaPts = new PointCollection { new Point(0, baselineY) };

                    for (int i = 0; i < count; i++)
                    {
                        double x = count > 1 ? i * stepX : width * 0.5;
                        double y = MapY(s.Points[i]);
                        var pt = new Point(x, y);
                        linePts.Add(pt);
                        areaPts.Add(pt);
                    }

                    if (count == 1)
                    {
                        linePts.Insert(0, new Point(0, linePts[0].Y));
                        linePts[1] = new Point(width, linePts[0].Y);
                        areaPts.Clear();
                        areaPts.Add(new Point(0, baselineY));
                        areaPts.Add(new Point(0, linePts[0].Y));
                        areaPts.Add(new Point(width, linePts[0].Y));
                    }
                    areaPts.Add(new Point(width, baselineY));

                    if (s.Kind == "area")
                    {
                        var areaGrad = new LinearGradientBrush
                        {
                            StartPoint = new Point(0, 0),
                            EndPoint = new Point(0, 1)
                        };
                        areaGrad.GradientStops.Add(new GradientStop
                        {
                            Color = Color.FromArgb((byte)(IsDark ? 56 : 40), s.SolidColor.R, s.SolidColor.G, s.SolidColor.B),
                            Offset = 0
                        });
                        areaGrad.GradientStops.Add(new GradientStop
                        {
                            Color = Color.FromArgb(0, s.SolidColor.R, s.SolidColor.G, s.SolidColor.B),
                            Offset = 1
                        });
                        plotCanvas.Children.Add(new Polygon
                        {
                            Points = areaPts,
                            Fill = areaGrad
                        });
                    }

                    plotCanvas.Children.Add(new Polyline
                    {
                        Points = linePts,
                        Stroke = s.StrokeBrush,
                        StrokeThickness = 2.0,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    });

                    if (count <= 24)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            double x = count > 1 ? i * stepX : width * 0.5;
                            double y = MapY(s.Points[i]);
                            var dot = new Ellipse
                            {
                                Width = 6,
                                Height = 6,
                                Fill = s.StrokeBrush
                            };
                            string catLabel = i < categories.Count ? categories[i] : $"#{i + 1}";
                            ToolTipService.SetToolTip(dot, $"{s.Name} · {catLabel}: {s.Points[i]:0.##}{spec.Unit}");
                            Canvas.SetLeft(dot, x - 3);
                            Canvas.SetTop(dot, y - 3);
                            plotCanvas.Children.Add(dot);
                        }
                    }
                }

                if (spec.ShowLegend && (seriesList.Count > 1 || !string.IsNullOrWhiteSpace(s.Name)))
                {
                    var legItem = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5
                    };
                    legItem.Children.Add(new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(s.Kind == "bar" ? 2 : 4),
                        Background = s.StrokeBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    legItem.Children.Add(new TextBlock
                    {
                        Text = s.Name,
                        FontSize = CaptionFontSize - 0.5,
                        Foreground = ResolveForegroundBrush("secondary"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    legendRow.Children.Add(legItem);
                }
            }

            legendRow.Visibility = spec.ShowLegend && legendRow.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        RenderPlot(ctx);
        plotHost.SizeChanged += (_, _) => RenderPlot(_viewModel.CreateEvaluationContext());
        if (ContainsSystemMetricsToken(spec.Items ?? spec.Collection) ||
            ContainsSystemMetricsToken(spec.Sparkline) ||
            spec.Series.Any(static s =>
                ContainsSystemMetricsToken(s.ValueField) ||
                ContainsSystemMetricsToken(s.Name) ||
                ContainsSystemMetricsToken(s.Color)))
        {
            _liveMetricUpdaters.Add(RenderPlot);
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(0, spec.CornerRadius ?? RadiusMedium)),
            Padding = ParseThickness(spec.Padding ?? "12,10,12,10"),
            Background = ResolveCardBackground("default"),
            BorderThickness = CardBorderThickness,
            BorderBrush = CardStrokeBrush,
            Child = rootStack
        };

        return ApplyContainerVisualOverrides(card, spec, ctx);
    }

    // =========================================================================
    // Declarative Vector Canvas (Module 2)
    // =========================================================================

    private FrameworkElement BuildCanvas(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        (double vbMinX, double vbMinY, double vbWidth, double vbHeight) = ParseCanvasViewBox(spec.ViewBox, spec.Width, spec.Height);

        var drawingCanvas = new Canvas
        {
            Width = vbWidth,
            Height = vbHeight,
            Background = GetOrCreateSolidBrush(Colors.Transparent)
        };

        string? lastCanvasSignature = null;
        void PopulateCanvasShapes(CustomWidgetEvaluationContext evalCtx)
        {
            string nextSignature = BuildCanvasRenderSignature(spec, evalCtx);
            if (string.Equals(lastCanvasSignature, nextSignature, StringComparison.Ordinal))
            {
                return;
            }

            lastCanvasSignature = nextSignature;
            drawingCanvas.Children.Clear();
            IReadOnlyList<CustomWidgetCanvasElementSpec> shapes = ResolveCanvasElements(spec, evalCtx);

            foreach (CustomWidgetCanvasElementSpec el in shapes)
            {
                UIElement? built = BuildSingleCanvasShape(el, spec, evalCtx);
                if (built is not null)
                {
                    drawingCanvas.Children.Add(built);
                }
            }
        }

        PopulateCanvasShapes(ctx);

        if (ContainsLiveCanvasTokens(spec))
        {
            _liveMetricUpdaters.Add(PopulateCanvasShapes);
        }

        FrameworkElement visualStage;
        if (!string.IsNullOrWhiteSpace(spec.ViewBox))
        {
            if (Math.Abs(vbMinX) > 0.01 || Math.Abs(vbMinY) > 0.01)
            {
                drawingCanvas.RenderTransform = new TranslateTransform { X = -vbMinX, Y = -vbMinY };
            }

            visualStage = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Width = spec.Width ?? double.NaN,
                Height = spec.Height ?? 180,
                Child = drawingCanvas
            };
        }
        else
        {
            if (spec.Width.HasValue) drawingCanvas.Width = spec.Width.Value;
            if (spec.Height.HasValue) drawingCanvas.Height = spec.Height.Value;
            visualStage = drawingCanvas;
        }

        var hostBorder = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(0, spec.CornerRadius ?? 0)),
            Background = GetOrCreateSolidBrush(Colors.Transparent),
            Clip = new RectangleGeometry(),
            Child = visualStage
        };
        hostBorder.SizeChanged += (_, e) =>
        {
            if (hostBorder.Clip is RectangleGeometry clipRect)
            {
                clipRect.Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
            }
        };

        if (spec.OnClick.Count > 0 && !_isNodeSelectionEnabled)
        {
            hostBorder.Tapped += async (_, e) =>
            {
                if (e.Handled)
                {
                    return;
                }

                Point pt = e.GetPosition(drawingCanvas);
                e.Handled = true;
                CustomWidgetEvaluationContext evCtx = _viewModel.CreateEvaluationContext()
                    .WithPointerEvent(spec.Id, Math.Round(pt.X, 1), Math.Round(pt.Y, 1));
                await _viewModel.ExecuteActionsAsync(spec.OnClick, evCtx);
            };
        }

        return ApplyContainerVisualOverrides(hostBorder, spec, ctx);
    }

    private static (double MinX, double MinY, double Width, double Height) ParseCanvasViewBox(
        string? rawViewBox,
        double? fallbackW,
        double? fallbackH)
    {
        if (!string.IsNullOrWhiteSpace(rawViewBox))
        {
            string[] parts = rawViewBox.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double mx) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double my) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double vw) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double vh) &&
                vw > 0 && vh > 0)
            {
                return (mx, my, vw, vh);
            }
        }

        return (0, 0, Math.Max(16, fallbackW ?? 260), Math.Max(16, fallbackH ?? 160));
    }

    private static bool ContainsLiveCanvasTokens(CustomWidgetNodeSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.ElementsSource))
        {
            return true;
        }

        foreach (CustomWidgetCanvasElementSpec el in spec.Elements)
        {
            if (ContainsSystemMetricsToken(el.X) ||
                ContainsSystemMetricsToken(el.Y) ||
                ContainsSystemMetricsToken(el.X2) ||
                ContainsSystemMetricsToken(el.Y2) ||
                ContainsSystemMetricsToken(el.Width) ||
                ContainsSystemMetricsToken(el.Height) ||
                ContainsSystemMetricsToken(el.Radius) ||
                ContainsSystemMetricsToken(el.Data) ||
                ContainsSystemMetricsToken(el.Points) ||
                ContainsSystemMetricsToken(el.Text) ||
                ContainsSystemMetricsToken(el.Fill) ||
                ContainsSystemMetricsToken(el.Stroke))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildCanvasRenderSignature(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        var sb = new System.Text.StringBuilder(256);
        if (!string.IsNullOrWhiteSpace(spec.ElementsSource))
        {
            string expr = spec.ElementsSource.Trim();
            if (expr.StartsWith("${", StringComparison.Ordinal) && expr.EndsWith('}'))
            {
                expr = expr[2..^1].Trim();
            }

            if (expr.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase))
            {
                string k = expr["$computed.".Length..];
                if (ctx.Computed.TryGetValue(k, out JsonElement compEl))
                {
                    sb.Append(compEl.ValueKind == JsonValueKind.String ? compEl.GetString() : compEl.GetRawText());
                }
            }
            else if (expr.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
            {
                string k = expr["computed.".Length..];
                if (ctx.Computed.TryGetValue(k, out JsonElement compEl))
                {
                    sb.Append(compEl.ValueKind == JsonValueKind.String ? compEl.GetString() : compEl.GetRawText());
                }
            }
            else
            {
                sb.Append(CustomWidgetExpressionEvaluator.EvaluateExpression(expr, ctx));
            }
        }

        if (spec.Elements.Count > 0)
        {
            sb.Append("|static:");
            foreach (CustomWidgetCanvasElementSpec el in spec.Elements)
            {
                sb.Append(el.Type).Append(':')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.X, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Y, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.X2, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Y2, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Width, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Height, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Radius, ctx)).Append(',')
                    .Append(el.Rotation).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Data, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Points, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Text, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Fill, ctx)).Append(',')
                    .Append(CustomWidgetExpressionEvaluator.Interpolate(el.Stroke, ctx)).Append(';');
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyList<CustomWidgetCanvasElementSpec> ResolveCanvasElements(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(spec.ElementsSource))
        {
            string expr = spec.ElementsSource.Trim();
            if (expr.StartsWith("${", StringComparison.Ordinal) && expr.EndsWith('}'))
            {
                expr = expr[2..^1].Trim();
            }

            string? rawJson = null;
            if (expr.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase))
            {
                string k = expr["$computed.".Length..];
                if (ctx.Computed.TryGetValue(k, out JsonElement compEl))
                {
                    rawJson = compEl.ValueKind == JsonValueKind.String ? compEl.GetString() : compEl.GetRawText();
                }
            }
            else if (expr.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
            {
                string k = expr["computed.".Length..];
                if (ctx.Computed.TryGetValue(k, out JsonElement compEl))
                {
                    rawJson = compEl.ValueKind == JsonValueKind.String ? compEl.GetString() : compEl.GetRawText();
                }
            }
            else
            {
                rawJson = CustomWidgetExpressionEvaluator.EvaluateExpression(expr, ctx);
            }

            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.TrimStart().StartsWith('['))
            {
                List<CustomWidgetCanvasElementSpec> dynamicShapes = ParseDynamicCanvasElementsJson(rawJson);
                if (spec.Elements.Count == 0)
                {
                    return dynamicShapes;
                }

                var combined = new List<CustomWidgetCanvasElementSpec>(spec.Elements.Count + dynamicShapes.Count);
                combined.AddRange(spec.Elements);
                combined.AddRange(dynamicShapes);
                return combined;
            }
        }

        return spec.Elements;
    }

    private static List<CustomWidgetCanvasElementSpec> ParseDynamicCanvasElementsJson(string rawJson)
    {
        var list = new List<CustomWidgetCanvasElementSpec>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            static string? ReadScalar(JsonElement obj, string prop)
            {
                if (!obj.TryGetProperty(prop, out JsonElement p)) return null;
                return p.ValueKind switch
                {
                    JsonValueKind.String => p.GetString(),
                    JsonValueKind.Number => p.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
            }

            static double ReadDouble(JsonElement obj, string prop, double fallback)
            {
                if (!obj.TryGetProperty(prop, out JsonElement p)) return fallback;
                if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d)) return d;
                if (p.ValueKind == JsonValueKind.String &&
                    double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd)) return sd;
                return fallback;
            }

            int idx = 0;
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string type = (ReadScalar(item, "type") ?? ReadScalar(item, "kind") ?? "rect").Trim();
                if (string.IsNullOrWhiteSpace(type)) continue;
                bool isLine = string.Equals(type, "line", StringComparison.OrdinalIgnoreCase);
                string? cxVal = ReadScalar(item, "cx");
                string? cyVal = ReadScalar(item, "cy");
                string? rotExpr = ReadScalar(item, "rotation") ?? ReadScalar(item, "rotate") ?? ReadScalar(item, "angle");

                list.Add(new CustomWidgetCanvasElementSpec(
                    Type: type,
                    Id: ReadScalar(item, "id"),
                    X: ReadScalar(item, "x") ?? (isLine ? ReadScalar(item, "x1") : (cxVal ?? ReadScalar(item, "x1"))),
                    Y: ReadScalar(item, "y") ?? (isLine ? ReadScalar(item, "y1") : (cyVal ?? ReadScalar(item, "y1"))),
                    X2: ReadScalar(item, "x2"),
                    Y2: ReadScalar(item, "y2"),
                    Width: ReadScalar(item, "width") ?? ReadScalar(item, "w"),
                    Height: ReadScalar(item, "height") ?? ReadScalar(item, "h"),
                    Radius: ReadScalar(item, "r") ?? ReadScalar(item, "radius"),
                    Rx: ReadScalar(item, "rx") ?? ReadScalar(item, "cornerRadius"),
                    Ry: ReadScalar(item, "ry"),
                    Data: ReadScalar(item, "d") ?? ReadScalar(item, "data"),
                    Points: ReadScalar(item, "points"),
                    Text: ReadScalar(item, "text") ?? ReadScalar(item, "value"),
                    Fill: ReadScalar(item, "fill") ?? ReadScalar(item, "color"),
                    Stroke: ReadScalar(item, "stroke"),
                    StrokeWidth: Math.Clamp(ReadDouble(item, "strokeWidth", 1.5), 0, 64),
                    Opacity: Math.Clamp(ReadDouble(item, "opacity", 1.0), 0, 1.0),
                    FontSize: Math.Clamp(ReadDouble(item, "fontSize", 12), 6, 120),
                    Rotation: ReadDouble(item, "rotation", ReadDouble(item, "rotate", ReadDouble(item, "angle", 0))),
                    Align: ReadScalar(item, "align") ?? ReadScalar(item, "textAnchor"),
                    Cx: cxVal,
                    Cy: cyVal,
                    FontWeight: ReadScalar(item, "fontWeight") ?? ReadScalar(item, "weight"),
                    RotationExpression: rotExpr));

                idx++;
                if (idx >= 512) break;
            }
        }
        catch
        {
        }

        return list;
    }

    private UIElement? BuildSingleCanvasShape(
        CustomWidgetCanvasElementSpec el,
        CustomWidgetNodeSpec canvasSpec,
        CustomWidgetEvaluationContext ctx)
    {
        static double EvalCoord(string? expr, CustomWidgetEvaluationContext evalCtx, double fallback = 0) =>
            string.IsNullOrWhiteSpace(expr)
                ? fallback
                : (CustomWidgetExpressionEvaluator.EvaluateDouble(expr, evalCtx) ?? fallback);

        Brush? fillBrush = !string.IsNullOrWhiteSpace(el.Fill)
            ? ResolveAnyColorBrush(el.Fill, ctx, "accent")
            : null;
        Brush? strokeBrush = !string.IsNullOrWhiteSpace(el.Stroke)
            ? ResolveAnyColorBrush(el.Stroke, ctx, "accent")
            : null;

        string kind = el.Type.Trim().ToLowerInvariant();
        FrameworkElement? element = null;

        switch (kind)
        {
            case "rect" or "rectangle":
            {
                double x = EvalCoord(el.X, ctx);
                double y = EvalCoord(el.Y, ctx);
                double w = Math.Max(0, EvalCoord(el.Width, ctx, 20));
                double h = Math.Max(0, EvalCoord(el.Height, ctx, 20));
                double rx = Math.Max(0, EvalCoord(el.Rx, ctx, 0));
                double ry = Math.Max(0, EvalCoord(el.Ry, ctx, rx));
                var rect = new Rectangle
                {
                    Width = w,
                    Height = h,
                    RadiusX = rx,
                    RadiusY = ry,
                    Fill = fillBrush,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeBrush is not null ? el.StrokeWidth : 0
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                element = rect;
                break;
            }

            case "circle" or "ellipse":
            {
                double cx = EvalCoord(el.X, ctx);
                double cy = EvalCoord(el.Y, ctx);
                double r = Math.Max(0, EvalCoord(el.Radius, ctx, 8));
                double rx = kind == "ellipse" ? Math.Max(0, EvalCoord(el.Rx ?? el.Width, ctx, r)) : r;
                double ry = kind == "ellipse" ? Math.Max(0, EvalCoord(el.Ry ?? el.Height, ctx, r)) : r;
                var ellipse = new Ellipse
                {
                    Width = rx * 2,
                    Height = ry * 2,
                    Fill = fillBrush,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeBrush is not null ? el.StrokeWidth : 0
                };
                Canvas.SetLeft(ellipse, cx - rx);
                Canvas.SetTop(ellipse, cy - ry);
                element = ellipse;
                break;
            }

            case "line":
            {
                var line = new Line
                {
                    X1 = EvalCoord(el.X, ctx),
                    Y1 = EvalCoord(el.Y, ctx),
                    X2 = EvalCoord(el.X2, ctx),
                    Y2 = EvalCoord(el.Y2, ctx),
                    Stroke = strokeBrush ?? fillBrush ?? ResolveForegroundBrush("default"),
                    StrokeThickness = Math.Max(0.5, el.StrokeWidth),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                element = line;
                break;
            }

            case "polyline" or "polygon":
            {
                string rawPts = CustomWidgetExpressionEvaluator.Interpolate(el.Points, ctx);
                PointCollection pts = ParsePointList(rawPts);
                if (pts.Count == 0) return null;

                if (kind == "polygon")
                {
                    element = new Polygon
                    {
                        Points = pts,
                        Fill = fillBrush,
                        Stroke = strokeBrush,
                        StrokeThickness = strokeBrush is not null ? el.StrokeWidth : 0,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                }
                else
                {
                    element = new Polyline
                    {
                        Points = pts,
                        Fill = fillBrush,
                        Stroke = strokeBrush ?? fillBrush ?? ResolveSemanticBrush("accent"),
                        StrokeThickness = Math.Max(0.5, el.StrokeWidth),
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                }
                break;
            }

            case "path":
            {
                string rawD = CustomWidgetExpressionEvaluator.Interpolate(el.Data, ctx);
                PathGeometry? geom = ParseSvgPathGeometry(rawD);
                if (geom is null) return null;
                var path = new WinUIPath
                {
                    Data = geom,
                    Fill = fillBrush,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeBrush is not null ? el.StrokeWidth : 0,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                double ox = EvalCoord(el.X, ctx, 0);
                double oy = EvalCoord(el.Y, ctx, 0);
                if (Math.Abs(ox) > 0.001) Canvas.SetLeft(path, ox);
                if (Math.Abs(oy) > 0.001) Canvas.SetTop(path, oy);
                element = path;
                break;
            }

            case "text":
            {
                double x = EvalCoord(el.X, ctx);
                double y = EvalCoord(el.Y, ctx);
                string txt = CustomWidgetExpressionEvaluator.Interpolate(el.Text, ctx);
                var tb = new TextBlock
                {
                    Text = txt,
                    FontSize = el.FontSize,
                    Foreground = fillBrush ?? strokeBrush ?? ResolveForegroundBrush("default")
                };
                if (TryResolveFontWeight(el.FontWeight) is { } tbFw)
                {
                    tb.FontWeight = tbFw;
                }
                WidgetTextShadow.SetCast(tb, false);

                string align = (el.Align ?? "left").Trim().ToLowerInvariant();
                if (align is "center" or "middle" or "right" or "end")
                {
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double dx = align is "center" or "middle"
                        ? -tb.DesiredSize.Width * 0.5
                        : -tb.DesiredSize.Width;
                    Canvas.SetLeft(tb, x + dx);
                    Canvas.SetTop(tb, y - (tb.DesiredSize.Height * 0.5));
                }
                else
                {
                    Canvas.SetLeft(tb, x);
                    Canvas.SetTop(tb, y);
                }
                element = tb;
                break;
            }
        }

        if (element is null)
        {
            return null;
        }

        element.Opacity = Math.Clamp(el.Opacity, 0.0, 1.0);
        double rotAngle = !string.IsNullOrWhiteSpace(el.RotationExpression)
            ? EvalCoord(el.RotationExpression, ctx, el.Rotation)
            : el.Rotation;
        if (Math.Abs(rotAngle) > 0.01)
        {
            if (!string.IsNullOrWhiteSpace(el.Cx) || !string.IsNullOrWhiteSpace(el.Cy))
            {
                double pivotX = EvalCoord(el.Cx, ctx);
                double pivotY = EvalCoord(el.Cy, ctx);
                double leftOffset = element is (Line or Polygon or Polyline) ? 0.0 : Canvas.GetLeft(element);
                double topOffset = element is (Line or Polygon or Polyline) ? 0.0 : Canvas.GetTop(element);
                if (double.IsNaN(leftOffset)) leftOffset = 0.0;
                if (double.IsNaN(topOffset)) topOffset = 0.0;
                element.RenderTransformOrigin = new Point(0, 0);
                element.RenderTransform = new RotateTransform
                {
                    Angle = rotAngle,
                    CenterX = pivotX - leftOffset,
                    CenterY = pivotY - topOffset
                };
            }
            else if (element is Line ln)
            {
                element.RenderTransformOrigin = new Point(0, 0);
                element.RenderTransform = new RotateTransform
                {
                    Angle = rotAngle,
                    CenterX = (ln.X1 + ln.X2) * 0.5,
                    CenterY = (ln.Y1 + ln.Y2) * 0.5
                };
            }
            else
            {
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                element.RenderTransform = new RotateTransform { Angle = rotAngle };
            }
        }

        if (canvasSpec.OnItemClick.Count > 0 && !_isNodeSelectionEnabled && !string.IsNullOrWhiteSpace(el.Id))
        {
            string capturedId = CustomWidgetExpressionEvaluator.Interpolate(el.Id, ctx);
            element.Tapped += async (sender, e) =>
            {
                e.Handled = true;
                Point pt = sender is UIElement ui && VisualTreeHelper.GetParent(ui) is UIElement parentCanvas
                    ? e.GetPosition(parentCanvas)
                    : new Point(0, 0);
                CustomWidgetEvaluationContext evCtx = _viewModel.CreateEvaluationContext()
                    .WithPointerEvent(capturedId, Math.Round(pt.X, 1), Math.Round(pt.Y, 1));
                await _viewModel.ExecuteActionsAsync(canvasSpec.OnItemClick, evCtx);
            };
        }

        return element;
    }

    private static PointCollection ParsePointList(string? raw)
    {
        var pts = new PointCollection();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return pts;
        }

        string[] tokens = raw.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
                double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
            {
                pts.Add(new Point(px, py));
            }
        }
        return pts;
    }

    internal static PathGeometry? ParseSvgPathGeometry(string? pathData)
    {
        if (string.IsNullOrWhiteSpace(pathData))
        {
            return null;
        }

        var geometry = new PathGeometry();
        PathFigure? currentFigure = null;
        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        double lastCtrlX = 0, lastCtrlY = 0;
        char lastCmd = '\0';

        int idx = 0;
        int len = pathData.Length;

        void SkipSeparators()
        {
            while (idx < len && (char.IsWhiteSpace(pathData[idx]) || pathData[idx] == ','))
            {
                idx++;
            }
        }

        bool TryReadNumber(out double val)
        {
            SkipSeparators();
            if (idx >= len)
            {
                val = 0;
                return false;
            }

            int start = idx;
            if (pathData[idx] is '+' or '-')
            {
                idx++;
            }

            bool hasDigits = false;
            while (idx < len && char.IsDigit(pathData[idx]))
            {
                idx++;
                hasDigits = true;
            }

            if (idx < len && pathData[idx] == '.')
            {
                idx++;
                while (idx < len && char.IsDigit(pathData[idx]))
                {
                    idx++;
                    hasDigits = true;
                }
            }

            if (!hasDigits)
            {
                idx = start;
                val = 0;
                return false;
            }

            if (idx < len && pathData[idx] is 'e' or 'E')
            {
                int expStart = idx++;
                if (idx < len && pathData[idx] is '+' or '-') idx++;
                bool expDigits = false;
                while (idx < len && char.IsDigit(pathData[idx]))
                {
                    idx++;
                    expDigits = true;
                }
                if (!expDigits) idx = expStart;
            }

            return double.TryParse(
                pathData.AsSpan(start, idx - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out val);
        }

        bool TryReadFlag(out bool flag)
        {
            SkipSeparators();
            if (idx < len && pathData[idx] is '0' or '1')
            {
                flag = pathData[idx] == '1';
                idx++;
                return true;
            }
            flag = false;
            return false;
        }

        PathFigure EnsureFigure()
        {
            if (currentFigure is null)
            {
                currentFigure = new PathFigure
                {
                    StartPoint = new Point(curX, curY),
                    IsFilled = true,
                    IsClosed = false
                };
                geometry.Figures.Add(currentFigure);
            }
            return currentFigure;
        }

        char cmd = '\0';
        while (true)
        {
            SkipSeparators();
            if (idx >= len)
            {
                break;
            }

            char ch = pathData[idx];
            if (char.IsLetter(ch))
            {
                cmd = ch;
                idx++;
            }
            else if (cmd == '\0')
            {
                break;
            }

            bool isRel = char.IsLower(cmd);
            char upper = char.ToUpperInvariant(cmd);

            switch (upper)
            {
                case 'M':
                {
                    if (!TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    curX = isRel ? curX + x : x;
                    curY = isRel ? curY + y : y;
                    startX = curX;
                    startY = curY;
                    currentFigure = new PathFigure
                    {
                        StartPoint = new Point(curX, curY),
                        IsFilled = true,
                        IsClosed = false
                    };
                    geometry.Figures.Add(currentFigure);
                    cmd = isRel ? 'l' : 'L';
                    break;
                }

                case 'L':
                {
                    if (!TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    curX = isRel ? curX + x : x;
                    curY = isRel ? curY + y : y;
                    EnsureFigure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    break;
                }

                case 'H':
                {
                    if (!TryReadNumber(out double x)) return geometry;
                    curX = isRel ? curX + x : x;
                    EnsureFigure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    break;
                }

                case 'V':
                {
                    if (!TryReadNumber(out double y)) return geometry;
                    curY = isRel ? curY + y : y;
                    EnsureFigure().Segments.Add(new LineSegment { Point = new Point(curX, curY) });
                    break;
                }

                case 'C':
                {
                    if (!TryReadNumber(out double x1) || !TryReadNumber(out double y1) ||
                        !TryReadNumber(out double x2) || !TryReadNumber(out double y2) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    lastCtrlX = ox + x2;
                    lastCtrlY = oy + y2;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new BezierSegment
                    {
                        Point1 = new Point(ox + x1, oy + y1),
                        Point2 = new Point(lastCtrlX, lastCtrlY),
                        Point3 = new Point(curX, curY)
                    });
                    break;
                }

                case 'S':
                {
                    if (!TryReadNumber(out double x2) || !TryReadNumber(out double y2) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    double rx1 = lastCmd is 'C' or 'c' or 'S' or 's' ? (2 * curX) - lastCtrlX : curX;
                    double ry1 = lastCmd is 'C' or 'c' or 'S' or 's' ? (2 * curY) - lastCtrlY : curY;
                    lastCtrlX = ox + x2;
                    lastCtrlY = oy + y2;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new BezierSegment
                    {
                        Point1 = new Point(rx1, ry1),
                        Point2 = new Point(lastCtrlX, lastCtrlY),
                        Point3 = new Point(curX, curY)
                    });
                    break;
                }

                case 'Q':
                {
                    if (!TryReadNumber(out double x1) || !TryReadNumber(out double y1) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    lastCtrlX = ox + x1;
                    lastCtrlY = oy + y1;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = new Point(lastCtrlX, lastCtrlY),
                        Point2 = new Point(curX, curY)
                    });
                    break;
                }

                case 'T':
                {
                    if (!TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    double ox = isRel ? curX : 0;
                    double oy = isRel ? curY : 0;
                    lastCtrlX = lastCmd is 'Q' or 'q' or 'T' or 't' ? (2 * curX) - lastCtrlX : curX;
                    lastCtrlY = lastCmd is 'Q' or 'q' or 'T' or 't' ? (2 * curY) - lastCtrlY : curY;
                    curX = ox + x;
                    curY = oy + y;
                    EnsureFigure().Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = new Point(lastCtrlX, lastCtrlY),
                        Point2 = new Point(curX, curY)
                    });
                    break;
                }

                case 'A':
                {
                    if (!TryReadNumber(out double rx) || !TryReadNumber(out double ry) ||
                        !TryReadNumber(out double angle) ||
                        !TryReadFlag(out bool largeArc) || !TryReadFlag(out bool sweep) ||
                        !TryReadNumber(out double x) || !TryReadNumber(out double y)) return geometry;
                    curX = isRel ? curX + x : x;
                    curY = isRel ? curY + y : y;
                    EnsureFigure().Segments.Add(new ArcSegment
                    {
                        Size = new Size(Math.Abs(rx), Math.Abs(ry)),
                        RotationAngle = angle,
                        IsLargeArc = largeArc,
                        SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                        Point = new Point(curX, curY)
                    });
                    break;
                }

                case 'Z':
                {
                    if (currentFigure is not null)
                    {
                        currentFigure.IsClosed = true;
                    }
                    curX = startX;
                    curY = startY;
                    cmd = '\0';
                    break;
                }

                default:
                    return geometry.Figures.Count > 0 ? geometry : null;
            }

            lastCmd = ch;
        }

        return geometry.Figures.Count > 0 ? geometry : null;
    }
}
