using Windows.Graphics;

namespace DeskBox.Services;

internal readonly record struct SearchPopupMaterialResolution(
    double OuterCornerRadius,
    double InputCapsuleCornerRadius,
    double ResultRowCornerRadius,
    bool UseTransparentTintFallback,
    double EffectiveAcrylicSurfaceOpacity,
    Windows.UI.Color SurfacePlateColor);

internal readonly record struct SearchPopupInputWellVisuals(
    Windows.UI.Color BackgroundColor,
    Windows.UI.Color BorderColor,
    bool ShowHeaderBottomDivider = false);

internal readonly record struct SearchPopupLayoutState(
    bool HasQuery,
    bool IsSearching,
    int ResultCount,
    bool ShowFilterBar,
    bool ShowSortHeader,
    bool ShowEverythingBanner,
    bool ShowRecommendations,
    int RecommendationEntryCount,
    bool ShowAllHiddenRecommendationsHint,
    bool ShowNoResults,
    bool ShowFooter,
    bool ShowDetailsPane = false);

internal readonly record struct SearchPopupHeightSpringStep(
    double Height,
    double Velocity,
    bool IsSettled);

/// <summary>
/// Pure geometry and material policy for the standalone search popup:
/// - Always uses a harmonious 16px outer corner radius (12px input capsule, 10px result cards).
/// - Follows the configured DeskBox material, and when opacity is set to fully transparent
///   (or near-transparent &lt; 0.25), supplies a theme-aware dark/light tinted base for legibility.
/// - Anchors the top edge and dynamically computes downward window height up to a scrollable cap.
/// </summary>
internal static class SearchPopupSurfacePolicy
{
    public const double OuterCornerRadius = 16.0;
    public const double InputCapsuleCornerRadius = 12.0;
    public const double ResultRowCornerRadius = 10.0;

    public const int DefaultPopupWidthLogical = 660;
    public const int ExpandedWithDetailsWidthLogical = 780;
    public const int CapsuleOnlyHeightLogical = 56;
    public const int EverythingBannerHeightLogical = 56;
    public const int FilterBarHeightLogical = 36;
    public const int SortHeaderHeightLogical = 30;
    public const int ResultRowStrideLogical = 46;
    public const int ResultsVerticalPaddingLogical = 12;
    public const int DetailsPaneMinBodyHeightLogical = 286;
    public const int FooterHeightLogical = 42;
    public const int NoResultsBodyHeightLogical = 136;
    public const int RecommendationColumnsEstimate = 6;
    public const int RecommendationRowStrideLogical = 98;
    public const int RecommendationVerticalPaddingLogical = 18;
    public const int MaxPopupHeightLogicalCap = 540;
    public const double MaxWorkAreaHeightFraction = 0.62;
    public const double DefaultTopAnchorFraction = 0.22;
    public const double TransparentTintThreshold = 0.25;

    /// <summary>
    /// Resolves the inner search input header visuals:
    /// Uses a full-bleed single-layer surface (no nested inner dark box) in both collapsed
    /// and expanded states, showing a 1px full-width bottom divider only when expanded.
    /// </summary>
    public static SearchPopupInputWellVisuals ResolveInputWellVisuals(bool isExpanded, bool isDark)
    {
        _ = isDark;
        var transparent = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
        return new SearchPopupInputWellVisuals(
            BackgroundColor: transparent,
            BorderColor: transparent,
            ShowHeaderBottomDivider: isExpanded);
    }

    public static SearchPopupMaterialResolution ResolveMaterial(
        string resolvedMaterialType,
        double widgetOpacity,
        double widgetMaterialIntensity,
        bool isDark,
        Windows.UI.Color accentColor)
    {
        double clampedOpacity = double.IsFinite(widgetOpacity)
            ? Math.Clamp(widgetOpacity, 0.0, 1.0)
            : 0.85;
        double clampedIntensity = double.IsFinite(widgetMaterialIntensity)
            ? Math.Clamp(widgetMaterialIntensity, 0.0, 1.0)
            : 0.5;
        bool useTransparentFallback = clampedOpacity < TransparentTintThreshold;

        if (string.Equals(
                resolvedMaterialType,
                SettingsService.WidgetMaterialTypeSolid,
                StringComparison.OrdinalIgnoreCase))
        {
            Windows.UI.Color baseRgb = BuildThemeBaseColor(isDark, accentColor, clampedIntensity);
            // When user sets "fully transparent" (< 0.25) on Solid, provide a rich theme-aware
            // dark/light translucent tint (~86%-88%) so floating search text stays crisp;
            // otherwise scale smoothly with the user's configured opacity.
            double alphaRatio = useTransparentFallback
                ? (isDark ? 0.86 : 0.89)
                : Lerp(0.78, 1.0, (clampedOpacity - TransparentTintThreshold) / (1.0 - TransparentTintThreshold));
            byte alpha = (byte)Math.Clamp(Math.Round(alphaRatio * 255.0), 1, 255);
            return new SearchPopupMaterialResolution(
                OuterCornerRadius: OuterCornerRadius,
                InputCapsuleCornerRadius: InputCapsuleCornerRadius,
                ResultRowCornerRadius: ResultRowCornerRadius,
                UseTransparentTintFallback: useTransparentFallback,
                EffectiveAcrylicSurfaceOpacity: clampedOpacity,
                SurfacePlateColor: Windows.UI.Color.FromArgb(alpha, baseRgb.R, baseRgb.G, baseRgb.B));
        }

        if (SettingsService.IsAcrylicMaterial(resolvedMaterialType))
        {
            double effectiveAcrylicOpacity = useTransparentFallback
                ? (isDark ? 0.62 : 0.68)
                : clampedOpacity;
            Windows.UI.Color plateColor = useTransparentFallback
                ? BuildTranslucentUnderlayPlate(isDark, accentColor, clampedIntensity)
                : Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00);
            return new SearchPopupMaterialResolution(
                OuterCornerRadius: OuterCornerRadius,
                InputCapsuleCornerRadius: InputCapsuleCornerRadius,
                ResultRowCornerRadius: ResultRowCornerRadius,
                UseTransparentTintFallback: useTransparentFallback,
                EffectiveAcrylicSurfaceOpacity: effectiveAcrylicOpacity,
                SurfacePlateColor: plateColor);
        }

        // Mica / MicaAlt: native DWM controller already supplies theme-aware base tint;
        // keep the hit-test plate at 1/255 alpha unless transparent fallback wants subtle depth.
        return new SearchPopupMaterialResolution(
            OuterCornerRadius: OuterCornerRadius,
            InputCapsuleCornerRadius: InputCapsuleCornerRadius,
            ResultRowCornerRadius: ResultRowCornerRadius,
            UseTransparentTintFallback: false,
            EffectiveAcrylicSurfaceOpacity: clampedOpacity,
            SurfacePlateColor: Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00));
    }

    public static int ComputeDesiredLogicalHeight(
        SearchPopupLayoutState state,
        int workAreaLogicalHeight)
    {
        int maxLogicalHeight = ComputeMaxLogicalHeight(workAreaLogicalHeight);
        int height = CapsuleOnlyHeightLogical;

        if (state.ShowEverythingBanner)
        {
            height += EverythingBannerHeightLogical;
        }

        if (!state.HasQuery)
        {
            if (state.ShowRecommendations)
            {
                if (state.RecommendationEntryCount > 0)
                {
                    int rows = Math.Clamp(
                        (state.RecommendationEntryCount + RecommendationColumnsEstimate - 1) /
                        RecommendationColumnsEstimate,
                        1,
                        3);
                    height += RecommendationVerticalPaddingLogical + (rows * RecommendationRowStrideLogical);
                }
                else if (state.ShowAllHiddenRecommendationsHint)
                {
                    height += 120;
                }

                if (state.ShowFooter)
                {
                    height += FooterHeightLogical;
                }
            }

            return Math.Clamp(height, CapsuleOnlyHeightLogical, maxLogicalHeight);
        }

        if (state.ShowFilterBar)
        {
            height += FilterBarHeightLogical;
        }

        if (state.ShowSortHeader)
        {
            height += SortHeaderHeightLogical;
        }

        if (state.ResultCount > 0)
        {
            int bodyHeight = ResultsVerticalPaddingLogical + (state.ResultCount * ResultRowStrideLogical);
            if (state.ShowDetailsPane)
            {
                bodyHeight = Math.Max(bodyHeight, DetailsPaneMinBodyHeightLogical);
            }

            height += bodyHeight;
            if (state.ShowFooter)
            {
                height += FooterHeightLogical;
            }
        }
        else if (state.ShowNoResults)
        {
            height += NoResultsBodyHeightLogical;
            if (state.ShowFooter)
            {
                height += FooterHeightLogical;
            }
        }

        return Math.Clamp(height, CapsuleOnlyHeightLogical, maxLogicalHeight);
    }

    public static int ComputeMaxLogicalHeight(int workAreaLogicalHeight)
    {
        if (workAreaLogicalHeight <= 0)
        {
            return MaxPopupHeightLogicalCap;
        }

        int workBound = (int)Math.Round(workAreaLogicalHeight * MaxWorkAreaHeightFraction);
        return Math.Max(CapsuleOnlyHeightLogical + 120, Math.Min(MaxPopupHeightLogicalCap, workBound));
    }

    public static RectInt32 ResolveAnchoredWindowBounds(
        RectInt32 workAreaPhysical,
        double dpiScale,
        int desiredLogicalHeight,
        int? customXPhysical,
        int? customYPhysical,
        int? minBottomHeightPhysical = null,
        int desiredLogicalWidth = DefaultPopupWidthLogical)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        int effectiveLogicalWidth = desiredLogicalWidth > 0 ? desiredLogicalWidth : DefaultPopupWidthLogical;
        int widthPhysical = Math.Min(
            Math.Max(320, workAreaPhysical.Width - 24),
            ToPhysicalPixels(effectiveLogicalWidth, scale));
        int heightPhysical = Math.Min(
            Math.Max(ToPhysicalPixels(CapsuleOnlyHeightLogical, scale), workAreaPhysical.Height - 24),
            ToPhysicalPixels(desiredLogicalHeight, scale));
        if (minBottomHeightPhysical is int overrideMinHeight && overrideMinHeight > heightPhysical)
        {
            heightPhysical = Math.Min(workAreaPhysical.Height - 24, overrideMinHeight);
        }

        int x;
        int y;
        if (customXPhysical is int savedX &&
            customYPhysical is int savedY &&
            IntersectsAnchorWithWorkArea(savedX, savedY, widthPhysical, workAreaPhysical))
        {
            x = Math.Clamp(
                savedX,
                workAreaPhysical.X + 8,
                Math.Max(workAreaPhysical.X + 8, workAreaPhysical.X + workAreaPhysical.Width - widthPhysical - 8));
            y = Math.Clamp(
                savedY,
                workAreaPhysical.Y + 8,
                Math.Max(
                    workAreaPhysical.Y + 8,
                    workAreaPhysical.Y + workAreaPhysical.Height - ToPhysicalPixels(CapsuleOnlyHeightLogical, scale) - 8));
        }
        else
        {
            x = workAreaPhysical.X + ((workAreaPhysical.Width - widthPhysical) / 2);
            y = workAreaPhysical.Y + (int)Math.Round(workAreaPhysical.Height * DefaultTopAnchorFraction);
        }

        // Keep top edge anchored as height grows downward; only cap height if near bottom of monitor.
        int availableDownwardHeight = (workAreaPhysical.Y + workAreaPhysical.Height - 8) - y;
        if (availableDownwardHeight >= ToPhysicalPixels(CapsuleOnlyHeightLogical, scale) &&
            heightPhysical > availableDownwardHeight)
        {
            heightPhysical = availableDownwardHeight;
        }

        return new RectInt32(x, y, widthPhysical, heightPhysical);
    }

    public const int SpringHeadroomLogical = 10;

    /// <summary>
    /// Advances a velocity-preserving, gently under-damped spring for smooth, organic
    /// height transitions during search typing. Preserves momentum across mid-flight
    /// target updates and settles with a subtle cushion.
    /// </summary>
    public static SearchPopupHeightSpringStep StepHeightSpring(
        double currentHeight,
        double currentVelocity,
        double targetHeight,
        double deltaSeconds,
        bool expandingDirection)
    {
        if (!double.IsFinite(currentHeight))
        {
            return new SearchPopupHeightSpringStep(targetHeight, 0, IsSettled: true);
        }

        if (!double.IsFinite(currentVelocity))
        {
            currentVelocity = 0;
        }

        double dt = Math.Clamp(deltaSeconds, 0.001, 0.050);

        // Gently under-damped parameters:
        // Expanding: omega0 = 22.0 rad/s, zeta = 0.72 (~3.8% soft elastic cushion, ~280ms settle).
        // Shrinking: omega0 = 21.0 rad/s, zeta = 0.74 (~3.1% soft deceleration cushion, ~290ms settle).
        double omega0 = expandingDirection ? 22.0 : 21.0;
        double zeta = expandingDirection ? 0.72 : 0.74;
        double stiffness = omega0 * omega0;
        double damping = 2.0 * zeta * omega0;

        // Sub-step at <= 4 ms for unconditional numerical stability regardless of UI frame jitter.
        int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double subDt = dt / steps;
        double x = currentHeight;
        double v = currentVelocity;

        for (int i = 0; i < steps; i++)
        {
            double accel = (stiffness * (targetHeight - x)) - (damping * v);
            v += accel * subDt;
            x += v * subDt;
        }

        // Bound maximum elastic excursion so large jumps never overshoot excessively or dip below capsule height.
        double maxOvershootAboveTarget = 7.0;
        double maxUndershootBelowTarget = 5.0;
        if (expandingDirection && x > targetHeight + maxOvershootAboveTarget)
        {
            x = targetHeight + maxOvershootAboveTarget;
            if (v > 0)
            {
                v *= 0.35;
            }
        }
        else if (!expandingDirection && x < targetHeight - maxUndershootBelowTarget)
        {
            x = targetHeight - maxUndershootBelowTarget;
            if (v < 0)
            {
                v *= 0.35;
            }
        }

        if (x < CapsuleOnlyHeightLogical)
        {
            x = CapsuleOnlyHeightLogical;
            if (v < 0)
            {
                v = 0;
            }
        }

        if (Math.Abs(targetHeight - x) <= 0.35 && Math.Abs(v) <= 3.0)
        {
            return new SearchPopupHeightSpringStep(targetHeight, 0, IsSettled: true);
        }

        return new SearchPopupHeightSpringStep(x, v, IsSettled: false);
    }

    public static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        double normalized = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(1, (int)Math.Round(logicalPixels * normalized, MidpointRounding.AwayFromZero));
    }

    private static bool IntersectsAnchorWithWorkArea(
        int x,
        int y,
        int width,
        RectInt32 work) =>
        x < work.X + work.Width &&
        x + width > work.X &&
        y >= work.Y - 16 &&
        y < work.Y + work.Height - 32;

    private static Windows.UI.Color BuildThemeBaseColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double intensity)
    {
        var baseColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x1D, 0x20, 0x26)
            : Windows.UI.Color.FromArgb(0xFF, 0xF9, 0xFA, 0xFD);
        return BlendRgb(baseColor, accentColor, (isDark ? 0.06 : 0.05) * intensity);
    }

    private static Windows.UI.Color BuildTranslucentUnderlayPlate(
        bool isDark,
        Windows.UI.Color accentColor,
        double intensity)
    {
        Windows.UI.Color rgb = BuildThemeBaseColor(isDark, accentColor, intensity);
        byte alpha = isDark ? (byte)0x5C : (byte)0x68;
        return Windows.UI.Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }

    private static Windows.UI.Color BlendRgb(
        Windows.UI.Color from,
        Windows.UI.Color to,
        double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0.0, 1.0));
}
