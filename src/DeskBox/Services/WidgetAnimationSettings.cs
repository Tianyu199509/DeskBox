using DeskBox.Models;

namespace DeskBox.Services;

public sealed record WidgetAnimationOptions(
    string Effect,
    string Speed,
    string SlideDirection,
    string EasingIntensity)
{
    public int DurationMs => WidgetAnimationSettings.GetDurationMs(Speed);

    public bool UsesGroupOffset => WidgetAnimationSettings.UsesGroupOffset(Effect);
}

public static class WidgetAnimationSettings
{
    public const int DefaultDurationMs = 240;

    public static WidgetAnimationOptions From(AppSettings settings)
    {
        string effect = NormalizeEffect(settings.WidgetAnimationEffect);
        string slideDirection = NormalizeSlideDirection(settings.WidgetAnimationSlideDirection);
        if (effect == SettingsService.WidgetAnimationEffectSlideFade &&
            slideDirection == SettingsService.WidgetAnimationSlideDirectionNone)
        {
            slideDirection = SettingsService.WidgetAnimationSlideDirectionRight;
        }

        return new WidgetAnimationOptions(
            effect,
            NormalizeSpeed(settings.WidgetAnimationSpeed),
            slideDirection,
            NormalizeEasingIntensity(settings.WidgetAnimationEasingIntensity));
    }

    public static int GetDurationMs(string speed)
    {
        return NormalizeSpeed(speed) switch
        {
            SettingsService.WidgetAnimationSpeedVeryFast => 120,
            SettingsService.WidgetAnimationSpeedFast => 220,
            SettingsService.WidgetAnimationSpeedRelaxed => 520,
            SettingsService.WidgetAnimationSpeedSlow => 680,
            _ => DefaultDurationMs
        };
    }

    public static bool UsesGroupOffset(string effect)
    {
        return NormalizeEffect(effect) is not (
            SettingsService.WidgetAnimationEffectNone or
            SettingsService.WidgetAnimationEffectFade or
            SettingsService.WidgetAnimationEffectScaleFade or
            SettingsService.WidgetAnimationEffectZoom);
    }

    public static (double X, double Y) GetDirectionalOffset(
        string direction,
        (double Left, double Right, double Up, double Down) offsets)
    {
        double baseOffset = Math.Max(Math.Max(offsets.Left, offsets.Right), Math.Max(offsets.Up, offsets.Down));
        if (baseOffset < 1)
        {
            baseOffset = 200;
        }

        return NormalizeSlideDirection(direction) switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => (-baseOffset, 0),
            SettingsService.WidgetAnimationSlideDirectionRight => (baseOffset, 0),
            SettingsService.WidgetAnimationSlideDirectionUp => (0, -baseOffset),
            SettingsService.WidgetAnimationSlideDirectionDown => (0, baseOffset),
            _ => (baseOffset, 0)
        };
    }

    public static double Ease(double progress, string easingIntensity, bool isShowing)
    {
        string intensity = NormalizeEasingIntensity(easingIntensity);
        if (intensity == SettingsService.WidgetAnimationEasingNone)
        {
            return progress;
        }

        if (isShowing)
        {
            return intensity switch
            {
                SettingsService.WidgetAnimationEasingLight => CubicBezierEase(progress, 0.25, 0.9, 0.25, 1.0),
                SettingsService.WidgetAnimationEasingStrong => CubicBezierEase(progress, 0.05, 1.1, 0.15, 1.0),
                _ => CubicBezierEase(progress, 0.16, 1.0, 0.3, 1.0)
            };
        }

        return intensity switch
        {
            SettingsService.WidgetAnimationEasingLight => CubicBezierEase(progress, 0.6, 0.1, 0.9, 0.3),
            SettingsService.WidgetAnimationEasingStrong => CubicBezierEase(progress, 0.7, 0.0, 0.95, -0.1),
            _ => CubicBezierEase(progress, 0.7, 0.0, 0.84, 0.0)
        };
    }

    public static string NormalizeEffect(string? effect)
    {
        return effect is
            SettingsService.WidgetAnimationEffectNone or
            SettingsService.WidgetAnimationEffectFade or
            SettingsService.WidgetAnimationEffectSlideRight or
            SettingsService.WidgetAnimationEffectSlideLeft or
            SettingsService.WidgetAnimationEffectSlideUp or
            SettingsService.WidgetAnimationEffectSlideDown or
            SettingsService.WidgetAnimationEffectScaleFade or
            SettingsService.WidgetAnimationEffectSlideFade or
            SettingsService.WidgetAnimationEffectZoom or
            SettingsService.WidgetAnimationEffectSlideUpFade or
            SettingsService.WidgetAnimationEffectSlideDownFade or
            SettingsService.WidgetAnimationEffectSlideLeftFade or
            SettingsService.WidgetAnimationEffectSlideRightFade or
            SettingsService.WidgetAnimationEffectScaleSlide
            ? effect
            : SettingsService.WidgetAnimationEffectSlideFade;
    }

    public static string NormalizeSpeed(string? speed)
    {
        return speed is
            SettingsService.WidgetAnimationSpeedVeryFast or
            SettingsService.WidgetAnimationSpeedFast or
            SettingsService.WidgetAnimationSpeedStandard or
            SettingsService.WidgetAnimationSpeedRelaxed or
            SettingsService.WidgetAnimationSpeedSlow
            ? speed
            : SettingsService.WidgetAnimationSpeedStandard;
    }

    public static string NormalizeSlideDirection(string? direction)
    {
        return direction is
            SettingsService.WidgetAnimationSlideDirectionNone or
            SettingsService.WidgetAnimationSlideDirectionLeft or
            SettingsService.WidgetAnimationSlideDirectionRight or
            SettingsService.WidgetAnimationSlideDirectionUp or
            SettingsService.WidgetAnimationSlideDirectionDown
            ? direction
            : SettingsService.WidgetAnimationSlideDirectionRight;
    }

    public static string NormalizeEasingIntensity(string? easingIntensity)
    {
        return easingIntensity is
            SettingsService.WidgetAnimationEasingNone or
            SettingsService.WidgetAnimationEasingLight or
            SettingsService.WidgetAnimationEasingStandard or
            SettingsService.WidgetAnimationEasingStrong
            ? easingIntensity
            : SettingsService.WidgetAnimationEasingStandard;
    }

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
