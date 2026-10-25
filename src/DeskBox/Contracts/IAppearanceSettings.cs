namespace DeskBox.Contracts;

/// <summary>
/// Canonical widget material kinds plus the material capability matrix the
/// appearance editor derives its slider visibility gates from. Owned by the
/// contract so the editor can build option lists and gates without touching
/// the settings adapter; the adapter's constants alias these values.
/// </summary>
public static class WidgetMaterialKinds
{
    public const string Acrylic = "Acrylic";
    public const string AcrylicBase = "AcrylicBase";
    public const string Glass = "Glass";
    public const string Mica = "Mica";
    public const string MicaAlt = "MicaAlt";
    public const string Solid = "Solid";

    public static bool SupportsOpacity(string? materialType) =>
        materialType is Acrylic or AcrylicBase or Solid;

    public static bool SupportsMaterialIntensity(string? materialType) =>
        materialType is Acrylic or AcrylicBase or Glass or Mica or MicaAlt;
}

/// <summary>Canonical widget border color modes and border styles.</summary>
public static class WidgetBorderKinds
{
    public const string ColorNeutral = "Neutral";
    public const string ColorAccent = "Accent";
    public const string ColorNone = "None";

    public const string StyleNone = "None";
    public const string StyleThin = "Thin";
    public const string StyleMedium = "Medium";
    public const string StyleThick = "Thick";

    public static string NormalizeColorMode(string? mode) =>
        mode is ColorNeutral or ColorAccent or ColorNone ? mode : ColorNeutral;

    public static string NormalizeStyle(string? style) =>
        style is StyleThin or StyleMedium or StyleThick ? style : StyleThin;
}

/// <summary>Canonical widget corner preferences.</summary>
public static class WidgetCornerKinds
{
    public const string Square = "Square";
    public const string Small = "Small";
    public const string Round = "Round";

    public static string Normalize(string? preference) =>
        preference is Square or Small ? preference : Round;
}

/// <summary>Canonical widget background modes (global, appearance-level).</summary>
public static class WidgetBackgroundModeKinds
{
    /// <summary>Widgets follow the global window material (default).</summary>
    public const string Material = "Material";

    /// <summary>Every widget shows the same unified background image.</summary>
    public const string UnifiedImage = "UnifiedImage";

    /// <summary>
    /// One panorama image spans the desktop coordinate space; every widget
    /// samples the slice under its own screen position.
    /// </summary>
    public const string Panorama = "Panorama";

    public static string Normalize(string? mode) =>
        string.Equals(mode, UnifiedImage, StringComparison.OrdinalIgnoreCase)
            ? UnifiedImage
            : string.Equals(mode, Panorama, StringComparison.OrdinalIgnoreCase)
                ? Panorama
                : Material;
}

/// <summary>
/// Canonical background image fit and dim values shared by the appearance
/// editor and the per-widget customization services.
/// </summary>
public static class WidgetBackgroundKinds
{
    public const string FitFill = "Fill";
    public const string FitContain = "Contain";

    /// <summary>Default scrim strength in percent, applied over the image.</summary>
    public const double DefaultDimPercent = 35;

    public const double MinDimPercent = 0;
    public const double MaxDimPercent = 100;

    public static string NormalizeFit(string? value) =>
        string.Equals(value, FitContain, StringComparison.OrdinalIgnoreCase)
            ? FitContain
            : FitFill;
}

/// <summary>Canonical widget title icon modes.</summary>
public static class WidgetTitleIconKinds
{
    public const string FilledMono = "FilledMono";
    public const string LineMono = "LineMono";
    public const string Color = "Color";
    public const string Hidden = "Hidden";
    public const string TextLabel = "TextLabel";
}

/// <summary>Canonical widget chrome modes offered by the appearance combos.</summary>
public static class WidgetChromeKinds
{
    public const string Standard = "Standard";
    public const string Compact = "Compact";
    public const string Overlay = "Overlay";
    public const string Hidden = "Hidden";
}

/// <summary>
/// Canonical widget foreground modes and the default custom color, shared by
/// the appearance editor and the adapter-side foreground policy.
/// </summary>
public static class WidgetForegroundKinds
{
    public const string FollowTheme = "FollowTheme";
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string Custom = "Custom";

    public const string DefaultCustomColorHex = "#F5F5F5";

    public static string NormalizeMode(string? value) =>
        value is Light or Dark or Custom ? value : FollowTheme;
}

/// <summary>The seven projected fields a layout-density preset writes.</summary>
public readonly record struct LayoutDensityPresetValues(
    double IconSize,
    double TextSize,
    double DensityScale,
    double HorizontalSpacingScale,
    double VerticalSpacingScale,
    double FileNameWidthScale);

/// <summary>
/// Canonical layout-density labels, file-name line counts and the preset
/// value table. The preset resolution is pure so the appearance editor can
/// resolve the effective label from its own seven projected values without
/// touching the settings adapter.
/// </summary>
public static class LayoutDensityKinds
{
    public const string Compact = "Compact";
    public const string Standard = "Standard";
    public const string Relaxed = "Relaxed";
    public const string Custom = "Custom";

    public const int HiddenFileNameLineCount = 0;
    public const int MinFileNameLineCount = 1;
    public const int MaxFileNameLineCount = 2;
    public const int DefaultFileNameLineCount = 2;

    public static int NormalizeFileNameLineCount(int value) =>
        value is HiddenFileNameLineCount or MinFileNameLineCount or MaxFileNameLineCount
            ? value
            : DefaultFileNameLineCount;

    public static bool TryGetPresetValues(string? preset, out LayoutDensityPresetValues values)
    {
        values = preset switch
        {
            Compact => new LayoutDensityPresetValues(
                IconSize: 26,
                TextSize: 10.5,
                DensityScale: 0.20,
                HorizontalSpacingScale: 0.20,
                VerticalSpacingScale: 0.28,
                FileNameWidthScale: 0.30),
            Standard => new LayoutDensityPresetValues(
                IconSize: 30,
                TextSize: 11.5,
                DensityScale: 0.56,
                HorizontalSpacingScale: 0.40,
                VerticalSpacingScale: 0.60,
                FileNameWidthScale: 0.36),
            Relaxed => new LayoutDensityPresetValues(
                IconSize: 36,
                TextSize: 13,
                DensityScale: 0.84,
                HorizontalSpacingScale: 0.68,
                VerticalSpacingScale: 0.82,
                FileNameWidthScale: 0.50),
            _ => default
        };

        return preset is Compact or Standard or Relaxed;
    }

    public static string ResolvePreset(
        double iconSize,
        double textSize,
        double densityScale,
        double horizontalSpacingScale,
        double verticalSpacingScale,
        double fileNameWidthScale)
    {
        foreach (string preset in new[] { Compact, Standard, Relaxed })
        {
            TryGetPresetValues(preset, out LayoutDensityPresetValues values);
            if (NearlyEqual(iconSize, values.IconSize) &&
                NearlyEqual(textSize, values.TextSize) &&
                NearlyEqual(densityScale, values.DensityScale) &&
                NearlyEqual(horizontalSpacingScale, values.HorizontalSpacingScale) &&
                NearlyEqual(verticalSpacingScale, values.VerticalSpacingScale) &&
                NearlyEqual(fileNameWidthScale, values.FileNameWidthScale))
            {
                return preset;
            }
        }

        return Custom;
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.0001;
}

/// <summary>
/// Canonical widget animation kinds (effect, speed, slide direction, easing
/// intensity) with the normalization and preset resolution rules the
/// appearance editor's animation state machine needs.
/// </summary>
public static class WidgetAnimationKinds
{
    public const string EffectNone = "None";
    public const string EffectFade = "Fade";
    public const string EffectSlideRight = "SlideRight";
    public const string EffectSlideLeft = "SlideLeft";
    public const string EffectSlideUp = "SlideUp";
    public const string EffectSlideDown = "SlideDown";
    public const string EffectScaleFade = "ScaleFade";
    public const string EffectSlideFade = "SlideFade";
    public const string EffectZoom = "Zoom";
    public const string EffectSlideUpFade = "SlideUpFade";
    public const string EffectSlideDownFade = "SlideDownFade";
    public const string EffectSlideLeftFade = "SlideLeftFade";
    public const string EffectSlideRightFade = "SlideRightFade";
    public const string EffectScaleSlide = "ScaleSlide";
    public const string EffectEdgeScale = "EdgeScale";
    public const string EffectTilt = "Tilt";
    public const string EffectWipe = "Wipe";

    public const string SpeedVeryFast = "VeryFast";
    public const string SpeedFast = "Fast";
    public const string SpeedStandard = "Standard";
    public const string SpeedRelaxed = "Relaxed";
    public const string SpeedSlow = "Slow";

    public const string DirectionNone = "None";
    public const string DirectionLeft = "Left";
    public const string DirectionRight = "Right";
    public const string DirectionUp = "Up";
    public const string DirectionDown = "Down";

    public const string EasingNone = "None";
    public const string EasingLight = "Light";
    public const string EasingStandard = "Standard";
    public const string EasingStrong = "Strong";
    public const string EasingSpring = "Spring";

    public const string PresetGentle = "Gentle";
    public const string PresetStandard = "Standard";
    public const string PresetEmphasized = "Emphasized";
    public const string PresetCustom = "Custom";

    public static string NormalizeEffect(string? effect) =>
        effect is
            EffectFade or EffectSlideRight or EffectSlideLeft or EffectSlideUp or
            EffectSlideDown or EffectScaleFade or EffectSlideFade or EffectZoom or
            EffectSlideUpFade or EffectSlideDownFade or EffectSlideLeftFade or
            EffectSlideRightFade or EffectScaleSlide or
            EffectEdgeScale or EffectTilt or EffectWipe
            ? effect
            : EffectSlideFade;

    public static string NormalizeSpeed(string? speed) =>
        speed is SpeedVeryFast or SpeedFast or SpeedStandard or SpeedRelaxed or SpeedSlow
            ? speed
            : SpeedStandard;

    public static string NormalizeSlideDirection(string? direction) =>
        direction is DirectionNone or DirectionLeft or DirectionRight or DirectionUp or DirectionDown
            ? direction
            : DirectionRight;

    public static string NormalizeEasingIntensity(string? intensity) =>
        intensity is EasingNone or EasingLight or EasingStandard or EasingStrong or EasingSpring
            ? intensity
            : EasingStandard;

    /// <summary>
    /// Effects whose visual result depends on the configured slide
    /// direction: the travel direction for slides, the anchor edge for
    /// edge-scale, the tilt sign, and the wipe side. The settings
    /// persistence normalizer uses this to decide whether a stored
    /// direction is meaningful, and the appearance editor to enable the
    /// direction picker.
    /// </summary>
    public static bool UsesSlideDirection(string? effect)
    {
        return NormalizeEffect(effect) is
            EffectSlideFade or
            EffectScaleSlide or
            EffectEdgeScale or
            EffectTilt or
            EffectWipe;
    }

    public static string ResolvePreset(
        string effect,
        string speed,
        string slideDirection,
        string easingIntensity)
    {
        if (effect == EffectFade &&
            speed == SpeedRelaxed &&
            easingIntensity == EasingLight)
        {
            return PresetGentle;
        }

        if (effect == EffectSlideFade &&
            speed == SpeedStandard &&
            slideDirection == DirectionRight &&
            easingIntensity == EasingStandard)
        {
            return PresetStandard;
        }

        if (effect == EffectScaleFade &&
            speed == SpeedRelaxed &&
            easingIntensity == EasingStrong)
        {
            return PresetEmphasized;
        }

        return PresetCustom;
    }
}

/// <summary>
/// Outcome of a numeric appearance edit. The settings shell re-enters its own
/// binding with <see cref="Value"/> unless the update committed, which keeps
/// the slider normalization feedback loop inside the page.
/// </summary>
public readonly record struct AppearanceValueUpdate(double Value, bool Committed)
{
    public static AppearanceValueUpdate Rejected(double storedValue) =>
        new(storedValue, Committed: false);

    public static AppearanceValueUpdate NeedsNormalization(double normalizedValue) =>
        new(normalizedValue, Committed: false);

    public static AppearanceValueUpdate CommittedValue(double storedValue) =>
        new(storedValue, Committed: true);
}

public readonly record struct AppearanceMaterialSettings(
    string MaterialType,
    double Opacity,
    double MaterialIntensity,
    string CornerPreference,
    string BorderColorMode,
    string BorderStyle);

public readonly record struct AppearanceDensitySettings(
    string LayoutDensity,
    double IconSize,
    double TextSize,
    double LayoutDensityScale,
    double HorizontalSpacingScale,
    double VerticalSpacingScale,
    double FileNameWidthScale,
    int FileNameLineCount);

public readonly record struct AppearanceWindowChromeSettings(
    double DefaultWidgetWidth,
    double DefaultWidgetHeight,
    string DisplayWidgetChromeMode,
    string InteractiveWidgetChromeMode,
    string WidgetTitleIconMode);

public readonly record struct AppearanceAnimationSettings(
    string Effect,
    string Speed,
    string SlideDirection,
    string EasingIntensity,
    bool StaggerEnabled = false);

public readonly record struct AppearanceForegroundSettings(
    string ForegroundMode,
    string ForegroundColor);

public readonly record struct AppearanceWidgetBackgroundSettings(
    string Mode,
    string? UnifiedImageFileName,
    string? PanoramaImageFileName,
    double DimPercent,
    string UnifiedFit);

/// <summary>
/// Settings-page writes for the appearance section: material, density,
/// typography, default widget size, window chrome, animation, foreground and
/// tray icon style. The appearance editor owns the XAML binding surface and
/// the live-preview timing runs on the settings shell; this port owns the raw
/// persisted values only. Read snapshots apply the same normalization the
/// legacy shell constructor did, so the editor can project them directly.
/// </summary>
public interface IAppearanceSettings
{
    AppearanceMaterialSettings ReadMaterial();
    AppearanceDensitySettings ReadDensity();
    AppearanceWindowChromeSettings ReadWindowChrome();
    AppearanceAnimationSettings ReadAnimation();
    AppearanceForegroundSettings ReadForeground();

    /// <summary>Normalized tray icon style for the editor's read projection.</summary>
    string ReadTrayIconStyle();

    void SetTrayIconStyle(string? style);

    // Slider-driven values: normalize, write the raw field, and leave the
    // preview/debounce dance to the shell's SaveAppearanceChange path.
    AppearanceValueUpdate UpdateWidgetOpacity(double value);
    AppearanceValueUpdate UpdateWidgetMaterialIntensity(double value);
    AppearanceValueUpdate UpdateIconSize(double value);
    AppearanceValueUpdate UpdateTextSize(double value);
    AppearanceValueUpdate UpdateLayoutDensityScale(double value);
    AppearanceValueUpdate UpdateHorizontalSpacingScale(double value);
    AppearanceValueUpdate UpdateVerticalSpacingScale(double value);
    AppearanceValueUpdate UpdateFileNameWidthScale(double value);

    void SetWidgetMaterialType(string? materialType);
    void SetWidgetCornerPreference(string? preference);
    void SetWidgetBorderColorMode(string? mode);
    void SetWidgetBorderStyle(string? style);

    void SetFileNameLineCount(int lineCount);
    void MarkLayoutDensityCustom();
    void ApplyLayoutDensityPreset(string preset);

    AppearanceValueUpdate UpdateDefaultWidgetWidth(double value);
    AppearanceValueUpdate UpdateDefaultWidgetHeight(double value);
    void SetDisplayWidgetChromeMode(string? mode);
    void SetInteractiveWidgetChromeMode(string? mode);
    void SetWidgetTitleIconMode(string? mode);

    // Animation preset application writes all four fields before a single
    // save; individual edits pass the default and save immediately.
    void SetAnimationEffect(string? effect, bool scheduleSave = true);
    void SetAnimationSpeed(string? speed, bool scheduleSave = true);
    void SetAnimationSlideDirection(string? direction, bool scheduleSave = true);
    void SetAnimationEasingIntensity(string? intensity, bool scheduleSave = true);
    void SetAnimationStaggerEnabled(bool enabled, bool scheduleSave = true);

    void SetWidgetForegroundMode(string? mode);
    void SetWidgetForegroundColor(string colorHex);

    AppearanceWidgetBackgroundSettings ReadWidgetBackground();
    void SetWidgetBackgroundMode(string? mode);
    void SetWidgetBackgroundUnifiedImage(string? fileName);
    void SetWidgetBackgroundPanoramaImage(string? fileName);
    void SetWidgetBackgroundUnifiedFit(string? fit);
    AppearanceValueUpdate UpdateWidgetBackgroundDim(double percent);
    int ClearPerWidgetBackgrounds();

    bool ReadWidgetTextShadowEnabled();
    void SetWidgetTextShadowEnabled(bool enabled);
}
