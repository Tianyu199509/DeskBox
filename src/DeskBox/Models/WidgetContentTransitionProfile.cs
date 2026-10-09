namespace DeskBox.Models;

public readonly record struct WidgetContentTransitionProfile(
    int DurationMilliseconds,
    int OutgoingDurationMilliseconds,
    int SwapGapMilliseconds,
    int IncomingDurationMilliseconds,
    double TranslationDistance,
    double MinimumScale,
    double IncomingStartOpacity,
    double OutgoingEndOpacity,
    bool UsesMotion,
    WidgetGroupSwitchAnimationEffect Effect)
{
    private const int VerticalTranslationDistance = 6;
    private const int HorizontalTranslationDistance = 12;

    public static WidgetContentTransitionProfile Create(
        bool animationsEnabled,
        bool directional,
        WidgetGroupSwitchAnimationEffect effect = WidgetGroupSwitchAnimationEffect.Vertical)
    {
        if (!animationsEnabled ||
            effect == WidgetGroupSwitchAnimationEffect.Suppress)
        {
            return new WidgetContentTransitionProfile(
                0,
                0,
                0,
                0,
                0,
                1,
                0,
                0,
                UsesMotion: false,
                Effect: WidgetGroupSwitchAnimationEffect.Vertical);
        }

        bool slides = effect is WidgetGroupSwitchAnimationEffect.Vertical
            or WidgetGroupSwitchAnimationEffect.Horizontal;
        double distance = slides && directional
            ? effect == WidgetGroupSwitchAnimationEffect.Horizontal
                ? HorizontalTranslationDistance
                : VerticalTranslationDistance
            : 0;
        return new WidgetContentTransitionProfile(
            210,
            78,
            12,
            120,
            distance,
            // A pure cross fade carries no depth cue, so the restrained
            // scale dip is reserved for the motion effects.
            effect == WidgetGroupSwitchAnimationEffect.CrossFade ? 1 : 0.975,
            0,
            0,
            UsesMotion: distance > 0,
            Effect: effect);
    }
}
