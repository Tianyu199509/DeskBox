namespace DeskBox.Contracts;

/// <summary>
/// Canonical capsule/compact option values, normalizers, numeric clamps and
/// the preset duration/delay mappings for the capsule settings family
/// (collapse behavior, compact width/expansion/content, capsule bar
/// arrangement, compact animation, hover response). Batch 44 lifted them out
/// of <c>SettingsService</c> so the WinUI-free capsule editor binds through
/// Contracts; <c>SettingsService</c> keeps public aliases over these values
/// for its own persistence/migration paths. The collapse-behavior setting
/// values stay canonical in <see cref="DeskBox.Models.WidgetCollapseBehaviorNames"/>
/// (Models) and are only referenced here.
/// </summary>
public static class CapsuleOptionKinds
{
    // --- Collapse behavior ---
    // Canonical string form of the collapse-behavior setting values (the
    // enum plus names class stay in Services for the widget host); the
    // editor normalizes at the string level with the same legacy semantics
    // ("Manual"→Click, "Auto"→Smart, System disallowed for the global value).
    public const string CollapseSystem = "System";
    public const string CollapseExpanded = "Expanded";
    public const string CollapseClick = "Click";
    public const string CollapseSmart = "Smart";

    /// <summary>
    /// Normalizes a global collapse-behavior value to its canonical string
    /// (never System; unknown values collapse to Click like the legacy
    /// settings-page normalizer).
    /// </summary>
    public static string NormalizeCollapseBehavior(string? value)
    {
        if (string.Equals(value, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            return CollapseClick;
        }

        if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return CollapseSmart;
        }

        if (string.Equals(value, CollapseExpanded, StringComparison.OrdinalIgnoreCase))
        {
            return CollapseExpanded;
        }

        return string.Equals(value, CollapseSmart, StringComparison.OrdinalIgnoreCase)
            ? CollapseSmart
            : CollapseClick;
    }

    /// <summary>True when the value denotes the Smart collapse behavior.</summary>
    public static bool IsCollapseSmart(string? value) =>
        NormalizeCollapseBehavior(value) == CollapseSmart;

    /// <summary>True when the value denotes the always-Expanded collapse behavior.</summary>
    public static bool IsCollapseExpanded(string? value) =>
        NormalizeCollapseBehavior(value) == CollapseExpanded;

    // --- Compact width mode ---
    public const string WidthModeAligned = "Aligned";
    public const string WidthModeIndependent = "Independent";

    // --- Compact expansion direction ---
    public const string ExpansionDirectionAuto = "Auto";
    public const string ExpansionDirectionDown = "Down";
    public const string ExpansionDirectionUp = "Up";

    // --- Compact content mode ---
    public const string ContentModeMinimal = "Minimal";
    public const string ContentModeSummary = "Summary";
    public const string ContentModeSmart = "Smart";

    // --- Capsule arrangement ---
    public const string ArrangementFree = "Free";
    public const string ArrangementBar = "Bar";
    // Legacy top-level values retained for settings migration.
    public const string ArrangementHorizontal = "Horizontal";
    public const string ArrangementVertical = "Vertical";

    // --- Capsule bar placement ---
    public const string BarPlacementFloating = "Floating";
    public const string BarPlacementTop = "Top";
    public const string BarPlacementBottom = "Bottom";
    public const string BarPlacementLeft = "Left";
    public const string BarPlacementRight = "Right";

    // --- Capsule bar direction ---
    public const string BarDirectionAuto = "Auto";
    public const string BarDirectionHorizontal = "Horizontal";
    public const string BarDirectionVertical = "Vertical";

    // --- Capsule bar spacing ---
    public const double DefaultBarSpacing = 8;
    public const double MinBarSpacing = 0;
    public const double MaxBarSpacing = 32;

    // --- Compact animation ---
    public const string AnimationSmooth = "Smooth";
    public const string AnimationSlow = "Slow";
    public const string AnimationSnappy = "Snappy";
    public const string AnimationCustom = "Custom";
    public const string AnimationNone = "None";
    public const int DefaultAnimationDurationMs = 220;
    public const int SlowAnimationDurationMs = 360;
    public const int SnappyAnimationDurationMs = 160;
    public const int MinAnimationDurationMs = 120;
    public const int MaxAnimationDurationMs = 400;

    // --- Hover expand/collapse delays and derived response presets ---
    public const string HoverResponseSensitive = "Sensitive";
    public const string HoverResponseBalanced = "Balanced";
    public const string HoverResponsePreventAccidental = "PreventAccidental";
    public const string HoverResponseCustom = "Custom";
    public const int DefaultExpandDelayMs = 360;
    public const int MinExpandDelayMs = 100;
    public const int MaxExpandDelayMs = 1000;
    public const int DefaultCollapseDelayMs = 620;
    public const int MinCollapseDelayMs = 200;
    public const int MaxCollapseDelayMs = 1500;
    public const int SensitiveExpandDelayMs = 100;
    public const int SensitiveCollapseDelayMs = 200;
    public const int PreventAccidentalExpandDelayMs = 620;
    public const int PreventAccidentalCollapseDelayMs = 900;

    public static string NormalizeWidthMode(string? value)
    {
        return string.Equals(value, WidthModeIndependent, StringComparison.OrdinalIgnoreCase)
            ? WidthModeIndependent
            : WidthModeAligned;
    }

    public static string NormalizeExpansionDirection(string? value)
    {
        if (string.Equals(value, ExpansionDirectionDown, StringComparison.OrdinalIgnoreCase))
        {
            return ExpansionDirectionDown;
        }

        return string.Equals(value, ExpansionDirectionUp, StringComparison.OrdinalIgnoreCase)
            ? ExpansionDirectionUp
            : ExpansionDirectionAuto;
    }

    public static string NormalizeContentMode(string? value)
    {
        if (string.Equals(value, ContentModeMinimal, StringComparison.OrdinalIgnoreCase))
        {
            return ContentModeMinimal;
        }

        return string.Equals(value, ContentModeSummary, StringComparison.OrdinalIgnoreCase)
            ? ContentModeSummary
            : ContentModeSmart;
    }

    public static string NormalizeArrangementMode(string? value)
    {
        return string.Equals(value, ArrangementBar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, ArrangementHorizontal, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, ArrangementVertical, StringComparison.OrdinalIgnoreCase)
            ? ArrangementBar
            : ArrangementFree;
    }

    public static string NormalizeBarPlacement(string? value)
    {
        if (string.Equals(value, BarPlacementTop, StringComparison.OrdinalIgnoreCase))
        {
            return BarPlacementTop;
        }

        if (string.Equals(value, BarPlacementBottom, StringComparison.OrdinalIgnoreCase))
        {
            return BarPlacementBottom;
        }

        if (string.Equals(value, BarPlacementLeft, StringComparison.OrdinalIgnoreCase))
        {
            return BarPlacementLeft;
        }

        return string.Equals(value, BarPlacementRight, StringComparison.OrdinalIgnoreCase)
            ? BarPlacementRight
            : BarPlacementFloating;
    }

    public static string NormalizeBarDirection(string? value)
    {
        if (string.Equals(value, BarDirectionHorizontal, StringComparison.OrdinalIgnoreCase))
        {
            return BarDirectionHorizontal;
        }

        return string.Equals(value, BarDirectionVertical, StringComparison.OrdinalIgnoreCase)
            ? BarDirectionVertical
            : BarDirectionAuto;
    }

    public static double NormalizeBarSpacing(double value)
    {
        double finiteValue = double.IsFinite(value) ? value : DefaultBarSpacing;
        return Math.Clamp(finiteValue, MinBarSpacing, MaxBarSpacing);
    }

    public static string NormalizeAnimationEffect(string? value)
    {
        if (string.Equals(value, AnimationSlow, StringComparison.OrdinalIgnoreCase))
        {
            return AnimationSlow;
        }

        if (string.Equals(value, AnimationSnappy, StringComparison.OrdinalIgnoreCase))
        {
            return AnimationSnappy;
        }

        if (string.Equals(value, AnimationCustom, StringComparison.OrdinalIgnoreCase))
        {
            return AnimationCustom;
        }

        return string.Equals(value, AnimationNone, StringComparison.OrdinalIgnoreCase)
            ? AnimationNone
            : AnimationSmooth;
    }

    public static int NormalizeAnimationDurationMs(int value) =>
        Math.Clamp(value, MinAnimationDurationMs, MaxAnimationDurationMs);

    public static int NormalizeExpandDelayMs(int value) =>
        Math.Clamp(value, MinExpandDelayMs, MaxExpandDelayMs);

    public static int NormalizeCollapseDelayMs(int value) =>
        Math.Clamp(value, MinCollapseDelayMs, MaxCollapseDelayMs);

    public static string NormalizeHoverResponse(string? value) => value switch
    {
        HoverResponseSensitive => HoverResponseSensitive,
        HoverResponsePreventAccidental => HoverResponsePreventAccidental,
        HoverResponseCustom => HoverResponseCustom,
        _ => HoverResponseBalanced
    };

    public static string ResolveHoverResponse(int expandDelayMs, int collapseDelayMs)
    {
        int expand = NormalizeExpandDelayMs(expandDelayMs);
        int collapse = NormalizeCollapseDelayMs(collapseDelayMs);
        return (expand, collapse) switch
        {
            (SensitiveExpandDelayMs, SensitiveCollapseDelayMs) => HoverResponseSensitive,
            (DefaultExpandDelayMs, DefaultCollapseDelayMs) => HoverResponseBalanced,
            (PreventAccidentalExpandDelayMs, PreventAccidentalCollapseDelayMs) =>
                HoverResponsePreventAccidental,
            _ => HoverResponseCustom
        };
    }

    /// <summary>
    /// The persisted duration a preset animation effect implies, or
    /// <see langword="null"/> for Custom/None (no implicit duration).
    /// </summary>
    public static int? AnimationPresetDurationMs(string? effect)
    {
        return NormalizeAnimationEffect(effect) switch
        {
            AnimationSmooth => DefaultAnimationDurationMs,
            AnimationSlow => SlowAnimationDurationMs,
            AnimationSnappy => SnappyAnimationDurationMs,
            _ => null
        };
    }

    /// <summary>
    /// The persisted delay pair a hover-response preset implies, or
    /// <see langword="null"/> for Custom (the selection itself is derived
    /// view state and is never persisted).
    /// </summary>
    public static (int Expand, int Collapse)? HoverResponsePresetDelays(string? response)
    {
        return NormalizeHoverResponse(response) switch
        {
            HoverResponseSensitive => (SensitiveExpandDelayMs, SensitiveCollapseDelayMs),
            HoverResponsePreventAccidental =>
                (PreventAccidentalExpandDelayMs, PreventAccidentalCollapseDelayMs),
            HoverResponseBalanced => (DefaultExpandDelayMs, DefaultCollapseDelayMs),
            _ => null
        };
    }
}
