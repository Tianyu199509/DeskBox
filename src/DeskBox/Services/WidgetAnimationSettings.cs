using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record WidgetAnimationOptions(
    string Effect,
    string Speed,
    string SlideDirection,
    string EasingIntensity)
{
    public int DurationMs => WidgetAnimationSettings.GetDurationMs(Speed, isShowing: true);

    public int ShowDurationMs => WidgetAnimationSettings.GetDurationMs(Speed, isShowing: true);

    public int HideDurationMs => WidgetAnimationSettings.GetDurationMs(Speed, isShowing: false);

    public bool UsesGroupOffset => WidgetAnimationSettings.UsesGroupOffset(Effect);
}

public static class WidgetAnimationSettings
{
    public const int DefaultShowDurationMs = 210;
    public const int DefaultHideDurationMs = 150;
    public const int DefaultDurationMs = DefaultShowDurationMs;

    public const double ShortSlideDistanceDip = 36.0;
    public const double SeamFallbackSlideDistanceDip = 48.0;
    public const int MaxTotalStaggerSpreadMs = 100;

    public static WidgetAnimationOptions From(AppSettings settings)
    {
        WidgetShellSettingsSlice shell = settings.WidgetShell;
        string effect = NormalizeEffect(shell.WidgetAnimationEffect);
        string slideDirection = WidgetAnimationKinds.NormalizeSlideDirectionForEffect(
            shell.WidgetAnimationEffect,
            shell.WidgetAnimationSlideDirection);

        return new WidgetAnimationOptions(
            effect,
            NormalizeSpeed(shell.WidgetAnimationSpeed),
            slideDirection,
            NormalizeEasingIntensity(shell.WidgetAnimationEasingIntensity));
    }

    public static WidgetAnimationOptions From(AppearanceAnimationSettings settings)
    {
        string effect = NormalizeEffect(settings.Effect);
        string slideDirection = WidgetAnimationKinds.NormalizeSlideDirectionForEffect(
            settings.Effect,
            settings.SlideDirection);

        return new WidgetAnimationOptions(
            effect,
            NormalizeSpeed(settings.Speed),
            slideDirection,
            NormalizeEasingIntensity(settings.EasingIntensity));
    }

    public static int GetDurationMs(string speed) =>
        GetDurationMs(speed, isShowing: true);

    public static int GetDurationMs(string speed, bool isShowing)
    {
        return NormalizeSpeed(speed) switch
        {
            SettingsService.WidgetAnimationSpeedFast => isShowing ? 150 : 110,
            SettingsService.WidgetAnimationSpeedRelaxed => isShowing ? 300 : 210,
            _ => isShowing ? DefaultShowDurationMs : DefaultHideDurationMs
        };
    }

    public static int GetSingleWidgetLifecycleDurationMs(string speed, bool isShowing)
    {
        return NormalizeSpeed(speed) switch
        {
            SettingsService.WidgetAnimationSpeedFast => isShowing ? 140 : 105,
            SettingsService.WidgetAnimationSpeedRelaxed => isShowing ? 230 : 170,
            _ => isShowing ? 180 : 135
        };
    }

    /// <summary>
    /// Per-widget containment replaces monitor-wide group offsets so a widget
    /// near a bezel never inherits a cross-screen throw from a distant sibling.
    /// </summary>
    public static bool UsesGroupOffset(string effect) => false;

    /// <summary>
    /// Resolves the direction a directional effect operates along.
    /// </summary>
    public static string GetEffectiveSlideDirection(string effect, string slideDirection)
    {
        if (WidgetAnimationKinds.TryExtractLegacyDirection(effect) is { } legacyDirection)
        {
            return legacyDirection;
        }

        return WidgetAnimationKinds.NormalizeSlideDirectionForEffect(effect, slideDirection);
    }

    public static (double X, double Y) GetDirectionalOffset(
        string direction,
        (double Left, double Right, double Up, double Down) offsets)
    {
        string normalized = NormalizeSlideDirection(direction);
        if (normalized is SettingsService.WidgetAnimationSlideDirectionNone or
            SettingsService.WidgetAnimationSlideDirectionAuto)
        {
            normalized = SettingsService.WidgetAnimationSlideDirectionDown;
        }

        double baseOffset = normalized switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => offsets.Left,
            SettingsService.WidgetAnimationSlideDirectionRight => offsets.Right,
            SettingsService.WidgetAnimationSlideDirectionUp => offsets.Up,
            SettingsService.WidgetAnimationSlideDirectionDown => offsets.Down,
            _ => 0
        };

        if (baseOffset < 1)
        {
            baseOffset = Math.Max(
                Math.Max(offsets.Left, offsets.Right),
                Math.Max(offsets.Up, offsets.Down));
        }

        if (baseOffset < 1)
        {
            baseOffset = ShortSlideDistanceDip;
        }

        return normalized switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => (-baseOffset, 0),
            SettingsService.WidgetAnimationSlideDirectionRight => (baseOffset, 0),
            SettingsService.WidgetAnimationSlideDirectionUp => (0, -baseOffset),
            _ => (0, baseOffset)
        };
    }

    /// <summary>
    /// Computes direction-aware per-widget stagger delays capped within
    /// <see cref="MaxTotalStaggerSpreadMs"/> so leading widgets never get
    /// overtaken by trailing widgets and stationary effects stagger cleanly.
    /// </summary>
    public static int[] ComputeStaggerDelaysMs(
        IReadOnlyList<(double BaseX, double BaseY, double MotionX, double MotionY)> items,
        bool staggerEnabled,
        string? effect)
    {
        var delays = new int[items.Count];
        if (!staggerEnabled ||
            items.Count < 2 ||
            NormalizeEffect(effect) == SettingsService.WidgetAnimationEffectNone)
        {
            return delays;
        }

        int stepMs = Math.Clamp(MaxTotalStaggerSpreadMs / (items.Count - 1), 6, 30);

        double sumMotionX = 0;
        double sumMotionY = 0;
        bool anyMotion = false;
        for (int i = 0; i < items.Count; i++)
        {
            double mx = items[i].MotionX;
            double my = items[i].MotionY;
            if (Math.Abs(mx) > 0.5 || Math.Abs(my) > 0.5)
            {
                anyMotion = true;
                sumMotionX += mx;
                sumMotionY += my;
            }
        }

        double avgMx = sumMotionX / items.Count;
        double avgMy = sumMotionY / items.Count;
        double avgLen = Math.Sqrt(avgMx * avgMx + avgMy * avgMy);

        List<int> orderedIndices;
        if (!anyMotion)
        {
            orderedIndices = Enumerable.Range(0, items.Count)
                .OrderBy(index => items[index].BaseY)
                .ThenBy(index => items[index].BaseX)
                .ToList();
        }
        else if (avgLen > 4.0)
        {
            double dirX = avgMx / avgLen;
            double dirY = avgMy / avgLen;
            orderedIndices = Enumerable.Range(0, items.Count)
                .OrderBy(index => -(items[index].BaseX * dirX + items[index].BaseY * dirY))
                .ThenBy(index => items[index].BaseY)
                .ThenBy(index => items[index].BaseX)
                .ToList();
        }
        else
        {
            // Divergent edge slides (e.g. Auto nearest bezel): widgets closest
            // to their target bezel lead the wave.
            orderedIndices = Enumerable.Range(0, items.Count)
                .OrderBy(index =>
                {
                    double mx = items[index].MotionX;
                    double my = items[index].MotionY;
                    return mx * mx + my * my;
                })
                .ThenBy(index => items[index].BaseY)
                .ThenBy(index => items[index].BaseX)
                .ToList();
        }

        for (int rank = 0; rank < orderedIndices.Count; rank++)
        {
            delays[orderedIndices[rank]] = rank * stepMs;
        }

        return delays;
    }

    public static double Ease(double progress, string easingIntensity, bool isShowing)
    {
        // Deterministic frame-driver unit tests pass EasingNone explicitly to
        // verify linear clock progression.
        if (string.Equals(
                easingIntensity,
                SettingsService.WidgetAnimationEasingNone,
                StringComparison.Ordinal))
        {
            return Math.Clamp(progress, 0.0, 1.0);
        }

        string intensity = NormalizeEasingIntensity(easingIntensity);
        if (isShowing)
        {
            return intensity == SettingsService.WidgetAnimationEasingSpring
                ? CubicBezierEase(progress, 0.22, 1.22, 0.36, 1.0)
                : CubicBezierEase(progress, 0.16, 1.0, 0.30, 1.0);
        }

        // Crisp, responsive hide curve without initial lag or backward bounce.
        return CubicBezierEase(progress, 0.32, 0.0, 0.67, 0.0);
    }

    public static string NormalizeEffect(string? effect) =>
        WidgetAnimationKinds.NormalizeEffect(effect);

    public static string NormalizeSpeed(string? speed) =>
        WidgetAnimationKinds.NormalizeSpeed(speed);

    public static string NormalizeSlideDirection(string? direction) =>
        WidgetAnimationKinds.NormalizeSlideDirection(direction);

    public static string NormalizeEasingIntensity(string? easingIntensity) =>
        WidgetAnimationKinds.NormalizeEasingIntensity(easingIntensity);

    private static double CubicBezierEase(double t, double x1, double y1, double x2, double y2)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;

        double cx = 3.0 * x1;
        double bx = 3.0 * (x2 - x1) - cx;
        double ax = 1.0 - cx - bx;
        double cy = 3.0 * y1;
        double by = 3.0 * (y2 - y1) - cy;
        double ay = 1.0 - cy - by;

        double tGuess = t;
        for (int i = 0; i < 8; i++)
        {
            double x = ((ax * tGuess + bx) * tGuess + cx) * tGuess - t;
            if (Math.Abs(x) < 1e-7) break;
            double dx = (3.0 * ax * tGuess + 2.0 * bx) * tGuess + cx;
            if (Math.Abs(dx) < 1e-10) break;
            tGuess -= x / dx;
        }

        return ((ay * tGuess + by) * tGuess + cy) * tGuess;
    }
}
