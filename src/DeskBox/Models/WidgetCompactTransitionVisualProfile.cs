namespace DeskBox.Models;

public sealed record WidgetCompactTransitionVisualProfile(
    int DurationMilliseconds,
    double BodyRevealStart,
    double BodyHideEnd,
    double IdentityTranslation,
    bool IsAnimated,
    int EasingPower)
{
    public const double CloseDurationFactor = 0.78;
    public const double MinTurnDurationFactor = 0.40;
    public const double ShrunkScale = 0.96;
    public const double CloseSurfaceDelay = 0.10;

    public static WidgetCompactTransitionVisualProfile Resolve(
        string? preset,
        int customDurationMilliseconds,
        bool animationsEnabled)
    {
        if (!animationsEnabled ||
            string.Equals(preset, "None", StringComparison.OrdinalIgnoreCase))
        {
            return new(0, 0, 0, 0, false, 1);
        }

        return preset?.Trim().ToLowerInvariant() switch
        {
            "slow" => new(360, 0.34, 0.58, 4, true, 3),
            "snappy" => new(160, 0.22, 0.48, 3, true, 5),
            "custom" => new(
                Math.Clamp(customDurationMilliseconds, 120, 400),
                0.28,
                0.54,
                4,
                true,
                3),
            _ => new(
                Math.Clamp(customDurationMilliseconds, 120, 400),
                0.28,
                0.54,
                4,
                true,
                3)
        };
    }

    public int ResolveEffectiveDurationMilliseconds(
        bool collapsing,
        double remainingShare = 1.0)
    {
        if (!IsAnimated || DurationMilliseconds <= 0)
        {
            return 0;
        }

        double baseDuration = collapsing
            ? DurationMilliseconds * CloseDurationFactor
            : DurationMilliseconds;
        double turnFactor = Math.Clamp(remainingShare, MinTurnDurationFactor, 1.0);
        return Math.Max(70, (int)Math.Round(baseDuration * turnFactor));
    }

    public double EaseProgress(double progress)
    {
        double value = Math.Clamp(progress, 0, 1);
        if (!IsAnimated)
        {
            return value <= 0 ? 0 : 1;
        }

        if (EasingPower == 3)
        {
            return SoftOut(value);
        }

        return 1 - Math.Pow(1 - value, Math.Max(1, EasingPower));
    }

    public double EaseSurfaceProgress(
        bool collapsing,
        double progress,
        bool redirectedMidFlight = false)
    {
        double value = Math.Clamp(progress, 0, 1);
        if (!IsAnimated)
        {
            return value <= 0 ? 0 : 1;
        }

        if (!collapsing)
        {
            return EaseProgress(value);
        }

        double startDelay = redirectedMidFlight ? 0 : CloseSurfaceDelay;
        double surfaceTrack = Track(value, startDelay, 1.0);
        return EasingPower > 3
            ? 1 - Math.Pow(1 - surfaceTrack, EasingPower)
            : SoftDrawer(surfaceTrack);
    }

    public (double CompactOpacity, double ExpandedOpacity) GetOpacity(
        bool collapsing,
        double progress)
    {
        if (!IsAnimated)
        {
            return collapsing ? (1, 0) : (0, 1);
        }

        double value = Math.Clamp(progress, 0, 1);
        if (collapsing)
        {
            double handoff = Math.Clamp(BodyHideEnd, 0.36, 0.62);
            double expanded = 1 - SmoothStep(Math.Clamp(value / handoff, 0, 1));
            double compact = SmoothStep(Math.Clamp(
                (value - handoff) / (1 - handoff),
                0,
                1));
            return (compact, expanded);
        }

        double expandHandoff = Math.Clamp(BodyRevealStart, 0.22, 0.42);
        double compactOut = 1 - SmoothStep(Math.Clamp(
            value / expandHandoff,
            0,
            1));
        double expandedIn = SmoothStep(Math.Clamp(
            (value - expandHandoff) / (1 - expandHandoff),
            0,
            1));
        return (compactOut, expandedIn);
    }

    public double GetLiveContentOpacity(bool collapsing, double progress)
    {
        if (!IsAnimated)
        {
            return collapsing ? 0 : 1;
        }

        double value = Math.Clamp(progress, 0, 1);
        if (collapsing)
        {
            return 1 - SmoothStep(Track(value, 0.15, 0.64));
        }

        // Keep the real expanded tree visible while the HWND grows so newly
        // revealed pixels always show live content instead of bare backdrop.
        return 1;
    }

    public double GetCompactSurfaceOpacity(bool collapsing, double progress)
    {
        if (!IsAnimated)
        {
            return collapsing ? 1 : 0;
        }

        double value = Math.Clamp(progress, 0, 1);
        if (collapsing)
        {
            // Keep the styled capsule background available throughout the handoff.
            // Identity and text use separate delayed stages below.
            return 1;
        }

        return 1 - OutCubic(Track(value, 0.0, 0.24));
    }

    public double GetCompactIdentityOpacity(bool collapsing, double progress)
    {
        if (!collapsing || !IsAnimated)
        {
            return 1;
        }

        double value = Math.Clamp(progress, 0, 1);
        return SoftOut(Track(value, 0.50, 0.82));
    }

    public double GetCompactTextOpacity(bool collapsing, double progress)
    {
        if (!collapsing || !IsAnimated)
        {
            return 1;
        }

        double value = Math.Clamp(progress, 0, 1);
        return SoftOut(Track(value, 0.68, 0.96));
    }

    public double GetLiveContentScale(bool collapsing, double progress)
    {
        if (!IsAnimated)
        {
            return 1;
        }

        double value = Math.Clamp(progress, 0, 1);
        if (collapsing)
        {
            return Lerp(1.0, ShrunkScale, OutCubic(Track(value, 0.0, 0.60)));
        }

        return Lerp(ShrunkScale, 1.0, SoftOut(Track(value, 0.0, 0.72)));
    }

    public (double ScaleX, double ScaleY) GetLiveContentScaleVector(
        bool collapsing,
        double progress,
        bool hasHorizontalMorph)
    {
        double scale = GetLiveContentScale(collapsing, progress);
        return hasHorizontalMorph ? (scale, scale) : (1.0, scale);
    }

    public double GetCompactContentScale(bool collapsing, double progress)
    {
        if (!IsAnimated)
        {
            return 1;
        }

        double value = Math.Clamp(progress, 0, 1);
        if (collapsing)
        {
            return Lerp(ShrunkScale, 1.0, SoftOut(Track(value, 0.48, 0.96)));
        }

        return Lerp(1.0, ShrunkScale, OutCubic(Track(value, 0.0, 0.24)));
    }

    public (double ScaleX, double ScaleY) GetCompactContentScaleVector(
        bool collapsing,
        double progress,
        bool hasHorizontalMorph)
    {
        if (!hasHorizontalMorph)
        {
            return (1.0, 1.0);
        }

        double scale = GetCompactContentScale(collapsing, progress);
        return (scale, scale);
    }

    public double GetLiveContentTranslationY(
        bool collapsing,
        double progress,
        bool anchorsBottom = false)
    {
        if (!IsAnimated)
        {
            return 0;
        }

        double value = Math.Clamp(progress, 0, 1);
        double directionSign = anchorsBottom ? 1.0 : -1.0;
        if (collapsing)
        {
            return directionSign * IdentityTranslation *
                (1 - GetLiveContentOpacity(collapsing: true, value));
        }

        return directionSign * IdentityTranslation *
            (1 - SoftOut(Track(value, 0.0, 0.70)));
    }

    private static double Track(double value, double start, double end) =>
        end <= start
            ? (value >= end ? 1 : 0)
            : Math.Clamp((value - start) / (end - start), 0, 1);

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0, 1));

    private static double OutCubic(double value)
    {
        double inv = 1 - Math.Clamp(value, 0, 1);
        return 1 - (inv * inv * inv);
    }

    private static double SoftOut(double value) =>
        CubicBezier(0.22, 0.80, 0.28, 1.0, value);

    private static double SoftDrawer(double value) =>
        CubicBezier(0.32, 0.72, 0.0, 1.0, value);

    private static double CubicBezier(
        double x1,
        double y1,
        double x2,
        double y2,
        double x)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        double t = x;
        for (int i = 0; i < 5; i++)
        {
            double slope = BezierDerivative(x1, x2, t);
            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            double currentX = BezierSample(x1, x2, t) - x;
            t = Math.Clamp(t - (currentX / slope), 0, 1);
        }

        return BezierSample(y1, y2, t);
    }

    private static double BezierSample(double p1, double p2, double t)
    {
        double inv = 1 - t;
        return (3 * inv * inv * t * p1) + (3 * inv * t * t * p2) + (t * t * t);
    }

    private static double BezierDerivative(double p1, double p2, double t)
    {
        double inv = 1 - t;
        return (3 * inv * inv * p1) + (6 * inv * t * (p2 - p1)) + (3 * t * t * (1 - p2));
    }

    private static double SmoothStep(double value) =>
        value * value * (3 - (2 * value));
}
