using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using DeskBox.Core.Persistence;
using DeskBox.Contracts;
using DeskBox.FileSafety;
using LayoutDensityPresetValues = DeskBox.Contracts.LayoutDensityPresetValues;
using DeskBox.Helpers;
using DeskBox.Models;

[assembly: InternalsVisibleTo("DeskBox.Tests")]

namespace DeskBox.Services;

internal enum DefaultPreferencePreservationReason
{
    UserChoice,
    SystemIntegration,
    UserData,
    Storage,
    RuntimeState
}

public enum SettingsLoadRecoveryState
{
    Primary,
    RecoveredFromBackup,
    DefaultsForMissingFile,
    DefaultsAfterFailure
}

public sealed record SettingsPersistenceFailure(
    string Operation,
    string Message,
    DateTimeOffset OccurredAt);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings), TypeInfoPropertyName = "AppSettings")]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Dimension tag for SettingsChanged notifications. Appearance-only saves
/// are fully applied to windows through the AppearancePreviewChanged channel,
/// so general subscribers can skip their redundant re-apply work.
/// </summary>
public enum SettingsChangeKind
{
    General = 0,
    Appearance = 1,
}

/// <summary>
/// Manages application settings persistence using JSON files stored in the application directory.
/// </summary>
public sealed class SettingsService
{
    public const double DefaultWidgetOpacity = 0.80;
    public const double MinWidgetOpacity = 0.0;
    public const double MaxWidgetOpacity = 1.0;
    public const double DefaultWidgetMaterialIntensity = 0.65;
    public const double MinWidgetMaterialIntensity = 0.0;
    public const double MaxWidgetMaterialIntensity = 1.0;
    public const string WidgetMaterialTypeMica = Contracts.WidgetMaterialKinds.Mica;
    public const string WidgetMaterialTypeMicaAlt = Contracts.WidgetMaterialKinds.MicaAlt;
    public const string WidgetMaterialTypeAcrylic = Contracts.WidgetMaterialKinds.Acrylic;
    public const string WidgetMaterialTypeAcrylicBase = Contracts.WidgetMaterialKinds.AcrylicBase;
    public const string WidgetMaterialTypeSolid = Contracts.WidgetMaterialKinds.Solid;
    public const string WidgetBorderColorModeNeutral = Contracts.WidgetBorderKinds.ColorNeutral;
    public const string WidgetBorderColorModeAccent = Contracts.WidgetBorderKinds.ColorAccent;
    public const string WidgetBorderColorModeNone = Contracts.WidgetBorderKinds.ColorNone;
    public const string WidgetBorderStyleNone = Contracts.WidgetBorderKinds.StyleNone;
    public const string WidgetBorderStyleThin = Contracts.WidgetBorderKinds.StyleThin;
    public const string WidgetBorderStyleMedium = Contracts.WidgetBorderKinds.StyleMedium;
    public const string WidgetBorderStyleThick = Contracts.WidgetBorderKinds.StyleThick;
    public const string WidgetCornerPreferenceSquare = Contracts.WidgetCornerKinds.Square;
    public const string WidgetCornerPreferenceSmall = Contracts.WidgetCornerKinds.Small;
    public const string WidgetCornerPreferenceRound = Contracts.WidgetCornerKinds.Round;
    public const string WidgetAnimationEffectNone = Contracts.WidgetAnimationKinds.EffectNone;
    public const string WidgetAnimationEffectFade = Contracts.WidgetAnimationKinds.EffectFade;
    public const string WidgetAnimationEffectSlideRight = Contracts.WidgetAnimationKinds.EffectSlideRight;
    public const string WidgetAnimationEffectSlideLeft = Contracts.WidgetAnimationKinds.EffectSlideLeft;
    public const string WidgetAnimationEffectSlideUp = Contracts.WidgetAnimationKinds.EffectSlideUp;
    public const string WidgetAnimationEffectSlideDown = Contracts.WidgetAnimationKinds.EffectSlideDown;
    public const string WidgetAnimationEffectScaleFade = Contracts.WidgetAnimationKinds.EffectScaleFade;
    public const string WidgetAnimationEffectSlideFade = Contracts.WidgetAnimationKinds.EffectSlideFade;
    public const string WidgetAnimationEffectZoom = Contracts.WidgetAnimationKinds.EffectZoom;
    public const string WidgetAnimationEffectSlideUpFade = Contracts.WidgetAnimationKinds.EffectSlideUpFade;
    public const string WidgetAnimationEffectSlideDownFade = Contracts.WidgetAnimationKinds.EffectSlideDownFade;
    public const string WidgetAnimationEffectSlideLeftFade = Contracts.WidgetAnimationKinds.EffectSlideLeftFade;
    public const string WidgetAnimationEffectSlideRightFade = Contracts.WidgetAnimationKinds.EffectSlideRightFade;
    public const string WidgetAnimationEffectScaleSlide = Contracts.WidgetAnimationKinds.EffectScaleSlide;
    public const string WidgetAnimationEffectEdgeScale = Contracts.WidgetAnimationKinds.EffectEdgeScale;
    public const string WidgetAnimationEffectTilt = Contracts.WidgetAnimationKinds.EffectTilt;
    public const string WidgetAnimationEffectWipe = Contracts.WidgetAnimationKinds.EffectWipe;
    public const string WidgetAnimationSpeedVeryFast = Contracts.WidgetAnimationKinds.SpeedVeryFast;
    public const string WidgetAnimationSpeedFast = Contracts.WidgetAnimationKinds.SpeedFast;
    public const string WidgetAnimationSpeedStandard = Contracts.WidgetAnimationKinds.SpeedStandard;
    public const string WidgetAnimationSpeedRelaxed = Contracts.WidgetAnimationKinds.SpeedRelaxed;
    public const string WidgetAnimationSpeedSlow = Contracts.WidgetAnimationKinds.SpeedSlow;
    public const string WidgetAnimationSlideDirectionNone = Contracts.WidgetAnimationKinds.DirectionNone;
    public const string WidgetAnimationSlideDirectionLeft = Contracts.WidgetAnimationKinds.DirectionLeft;
    public const string WidgetAnimationSlideDirectionRight = Contracts.WidgetAnimationKinds.DirectionRight;
    public const string WidgetAnimationSlideDirectionUp = Contracts.WidgetAnimationKinds.DirectionUp;
    public const string WidgetAnimationSlideDirectionDown = Contracts.WidgetAnimationKinds.DirectionDown;
    public const string WidgetAnimationEasingNone = Contracts.WidgetAnimationKinds.EasingNone;
    public const string WidgetAnimationEasingLight = Contracts.WidgetAnimationKinds.EasingLight;
    public const string WidgetAnimationEasingStandard = Contracts.WidgetAnimationKinds.EasingStandard;
    public const string WidgetAnimationEasingStrong = Contracts.WidgetAnimationKinds.EasingStrong;
    public const string WidgetAnimationEasingSpring = Contracts.WidgetAnimationKinds.EasingSpring;

    public static bool IsMicaMaterial(string? materialType) =>
        materialType is WidgetMaterialTypeMica or WidgetMaterialTypeMicaAlt;

    public static bool IsAcrylicMaterial(string? materialType) =>
        materialType is WidgetMaterialTypeAcrylic or WidgetMaterialTypeAcrylicBase;

    public static bool SupportsWidgetOpacity(string? materialType) =>
        Contracts.WidgetMaterialKinds.SupportsOpacity(materialType);

    public static bool SupportsMaterialIntensity(string? materialType) =>
        Contracts.WidgetMaterialKinds.SupportsMaterialIntensity(materialType);
    // Aliases of the contract-owned canonical values so the interaction
    // editor can build its option list without referencing the adapter.
    public const string WidgetLayerModeDynamic = Contracts.WidgetLayerModes.Dynamic;
    public const string WidgetLayerModeDesktopPinned = Contracts.WidgetLayerModes.DesktopPinned;
    public const string WidgetLayerModeQuickReveal = Contracts.WidgetLayerModes.QuickReveal;
    public const string WidgetChromeModeStandard = WidgetChromeModeNames.Standard;
    public const string WidgetChromeModeCompact = WidgetChromeModeNames.Compact;
    public const string WidgetChromeModeOverlay = WidgetChromeModeNames.Overlay;
    public const string WidgetChromeModeHidden = WidgetChromeModeNames.Hidden;
    public const string WidgetCollapseBehaviorExpanded = WidgetCollapseBehaviorNames.Expanded;
    public const string WidgetCollapseBehaviorClick = WidgetCollapseBehaviorNames.Click;
    public const string WidgetCollapseBehaviorSmart = WidgetCollapseBehaviorNames.Smart;
    public const string WidgetCollapseBehaviorManual = WidgetCollapseBehaviorClick;
    public const string WidgetCollapseBehaviorAuto = WidgetCollapseBehaviorSmart;
    public const string WidgetCompactWidthModeAligned = CapsuleOptionKinds.WidthModeAligned;
    public const string WidgetCompactWidthModeIndependent = CapsuleOptionKinds.WidthModeIndependent;
    public const string WidgetCompactExpansionDirectionAuto = CapsuleOptionKinds.ExpansionDirectionAuto;
    public const string WidgetCompactExpansionDirectionDown = CapsuleOptionKinds.ExpansionDirectionDown;
    public const string WidgetCompactExpansionDirectionUp = CapsuleOptionKinds.ExpansionDirectionUp;
    public const string WidgetCapsuleArrangementFree = CapsuleOptionKinds.ArrangementFree;
    public const string WidgetCapsuleArrangementBar = CapsuleOptionKinds.ArrangementBar;
    // Legacy top-level values retained for settings migration.
    public const string WidgetCapsuleArrangementHorizontal = CapsuleOptionKinds.ArrangementHorizontal;
    public const string WidgetCapsuleArrangementVertical = CapsuleOptionKinds.ArrangementVertical;
    public const string WidgetCapsuleBarPlacementFloating = CapsuleOptionKinds.BarPlacementFloating;
    public const string WidgetCapsuleBarPlacementTop = CapsuleOptionKinds.BarPlacementTop;
    public const string WidgetCapsuleBarPlacementBottom = CapsuleOptionKinds.BarPlacementBottom;
    public const string WidgetCapsuleBarPlacementLeft = CapsuleOptionKinds.BarPlacementLeft;
    public const string WidgetCapsuleBarPlacementRight = CapsuleOptionKinds.BarPlacementRight;
    public const string WidgetCapsuleBarDirectionAuto = CapsuleOptionKinds.BarDirectionAuto;
    public const string WidgetCapsuleBarDirectionHorizontal = CapsuleOptionKinds.BarDirectionHorizontal;
    public const string WidgetCapsuleBarDirectionVertical = CapsuleOptionKinds.BarDirectionVertical;
    public const double DefaultWidgetCapsuleBarSpacing = CapsuleOptionKinds.DefaultBarSpacing;
    public const double MinWidgetCapsuleBarSpacing = CapsuleOptionKinds.MinBarSpacing;
    public const double MaxWidgetCapsuleBarSpacing = CapsuleOptionKinds.MaxBarSpacing;
    public const double DefaultWidgetSnapSpacing = 5;
    public const double MinWidgetSnapSpacing = 0;
    public const double MaxWidgetSnapSpacing = 32;
    public const string WidgetCollapsedStyleMinimal = "Minimal";
    public const string WidgetCollapsedStyleSummary = "Summary";
    public const string WidgetCollapsedStyleSmart = "Smart";
    public const string WidgetCollapsedStylePill = "Pill";
    public const string WidgetCompactContentModeMinimal = CapsuleOptionKinds.ContentModeMinimal;
    public const string WidgetCompactContentModeSummary = CapsuleOptionKinds.ContentModeSummary;
    public const string WidgetCompactContentModeSmart = CapsuleOptionKinds.ContentModeSmart;
    public const int CurrentWidgetCompactSettingsVersion = 2;
    public const string WidgetCompactAnimationSmooth = CapsuleOptionKinds.AnimationSmooth;
    public const string WidgetCompactAnimationSlow = CapsuleOptionKinds.AnimationSlow;
    public const string WidgetCompactAnimationSnappy = CapsuleOptionKinds.AnimationSnappy;
    public const string WidgetCompactAnimationCustom = CapsuleOptionKinds.AnimationCustom;
    public const string WidgetCompactAnimationNone = CapsuleOptionKinds.AnimationNone;
    public const string WidgetCompactMediaCornerFollowWidget = "FollowWidget";
    public const string WidgetCompactMediaCornerSquare = "Square";
    public const string WidgetCompactMediaCornerSmall = "Small";
    public const string WidgetCompactMediaCornerRound = "Round";
    public const int DefaultWidgetCompactAnimationDurationMs = CapsuleOptionKinds.DefaultAnimationDurationMs;
    public const int SlowWidgetCompactAnimationDurationMs = CapsuleOptionKinds.SlowAnimationDurationMs;
    public const int SnappyWidgetCompactAnimationDurationMs = CapsuleOptionKinds.SnappyAnimationDurationMs;
    public const int MinWidgetCompactAnimationDurationMs = CapsuleOptionKinds.MinAnimationDurationMs;
    public const int MaxWidgetCompactAnimationDurationMs = CapsuleOptionKinds.MaxAnimationDurationMs;
    public const int DefaultWidgetCompactExpandDelayMs = CapsuleOptionKinds.DefaultExpandDelayMs;
    public const int MinWidgetCompactExpandDelayMs = CapsuleOptionKinds.MinExpandDelayMs;
    public const int MaxWidgetCompactExpandDelayMs = CapsuleOptionKinds.MaxExpandDelayMs;
    public const int DefaultWidgetCompactCollapseDelayMs = CapsuleOptionKinds.DefaultCollapseDelayMs;
    public const int MinWidgetCompactCollapseDelayMs = CapsuleOptionKinds.MinCollapseDelayMs;
    public const int MaxWidgetCompactCollapseDelayMs = CapsuleOptionKinds.MaxCollapseDelayMs;
    public const string WidgetCompactHoverResponseSensitive = CapsuleOptionKinds.HoverResponseSensitive;
    public const string WidgetCompactHoverResponseBalanced = CapsuleOptionKinds.HoverResponseBalanced;
    public const string WidgetCompactHoverResponsePreventAccidental = CapsuleOptionKinds.HoverResponsePreventAccidental;
    public const string WidgetCompactHoverResponseCustom = CapsuleOptionKinds.HoverResponseCustom;
    public const int SensitiveWidgetCompactExpandDelayMs = CapsuleOptionKinds.SensitiveExpandDelayMs;
    public const int SensitiveWidgetCompactCollapseDelayMs = CapsuleOptionKinds.SensitiveCollapseDelayMs;
    public const int PreventAccidentalWidgetCompactExpandDelayMs = CapsuleOptionKinds.PreventAccidentalExpandDelayMs;
    public const int PreventAccidentalWidgetCompactCollapseDelayMs = CapsuleOptionKinds.PreventAccidentalCollapseDelayMs;
    public const string WidgetTitleIconModeFilledMono = WidgetTitleIconModeNames.FilledMono;
    public const string WidgetTitleIconModeLineMono = WidgetTitleIconModeNames.LineMono;
    public const string WidgetTitleIconModeColor = WidgetTitleIconModeNames.Color;
    public const string WidgetTitleIconModeHidden = WidgetTitleIconModeNames.Hidden;
    public const string WidgetTitleIconModeTextLabel = WidgetTitleIconModeNames.TextLabel;
    public const string WidgetHoverActionLockPosition = "LockPosition";
    public const string WidgetHoverActionLockSize = "LockSize";
    public const string WidgetHoverActionAdd = "Add";
    public const string WidgetHoverActionMore = "More";
    public const string WidgetHoverActionDelete = "Delete";
    public const string DefaultWidgetHoverButtonActions =
        WidgetHoverActionAdd + "," + WidgetHoverActionMore;
    public static IReadOnlyList<string> SupportedWidgetHoverButtonActions { get; } =
        Array.AsReadOnly(new string[]
        {
            WidgetHoverActionLockPosition,
            WidgetHoverActionLockSize,
            WidgetHoverActionAdd,
            WidgetHoverActionMore,
            WidgetHoverActionDelete
        });
    public const string ManagedDropActionMove = Contracts.ManagedDropActions.Move;
    public const string ManagedDropActionCopy = Contracts.ManagedDropActions.Copy;
    public const string ManagedDropActionFollowWindows = Contracts.ManagedDropActions.FollowWindows;
    public const string ManagedDragOutActionMove = Contracts.ManagedDropActions.Move;
    public const string ManagedDragOutActionCopy = Contracts.ManagedDropActions.Copy;
    public const string ManagedDragOutActionFollowWindows = Contracts.ManagedDropActions.FollowWindows;

    // Canonical attachment storage modes live in Contracts
    // (AttachmentStorageModes, batch 50); these historical constants are
    // aliases so existing consumers keep compiling unchanged.
    public const string AttachmentStorageModeLink = Contracts.AttachmentStorageModes.Link;
    public const string AttachmentStorageModeCopy = Contracts.AttachmentStorageModes.Copy;
    // Canonical file-stack option values and caps live in Contracts
    // (FileStackOptionKinds, batch 45); these historical constants are
    // aliases so existing consumers keep compiling unchanged.
    public const string FileStackGroupByKind = FileStackOptionKinds.GroupByKind;
    public const string FileStackGroupByDateAdded = FileStackOptionKinds.GroupByDateAdded;
    // Legacy value used by the first Stack preview build.
    public const string FileStackGroupByDateCreated = FileStackOptionKinds.GroupByDateCreated;
    public const string FileStackGroupByDateModified = FileStackOptionKinds.GroupByDateModified;
    public const string FileStackGroupByCustom = FileStackOptionKinds.GroupByCustom;
    public const int DefaultFileStackThreshold = FileStackOptionKinds.DefaultThreshold;
    public const string FileStackOrderByWidget = FileStackOptionKinds.OrderByWidget;
    public const string FileStackOrderByName = FileStackOptionKinds.OrderByName;
    public const string FileStackOrderByDateAdded = FileStackOptionKinds.OrderByDateAdded;
    public const string FileStackOrderByDateModified = FileStackOptionKinds.OrderByDateModified;
    public const string FileStackOpenModeInline = FileStackOptionKinds.OpenModeInline;
    public const string FileStackOpenModePopover = FileStackOptionKinds.OpenModePopover;
    public const string FileStackPopoverLayoutAdaptive = FileStackOptionKinds.PopoverLayoutAdaptive;
    public const string FileStackPopoverLayoutGrid3 = FileStackOptionKinds.PopoverLayoutGrid3;
    public const string FileStackPopoverLayoutGrid5 = FileStackOptionKinds.PopoverLayoutGrid5;
    public const string FileStackPopoverStyleFollowMaterial = FileStackOptionKinds.PopoverStyleFollowMaterial;
    public const string FileStackPopoverStyleNeutral = FileStackOptionKinds.PopoverStyleNeutral;
    public const string FileStackUnmatchedKeepLoose = FileStackOptionKinds.UnmatchedKeepLoose;
    public const string FileStackUnmatchedOther = FileStackOptionKinds.UnmatchedOther;
    public const int MaxFileStackCustomRules = FileStackOptionKinds.MaxCustomRules;
    public const int MaxFileStackExtensionsPerRule = FileStackOptionKinds.MaxExtensionsPerRule;
    public const int DefaultQuickCaptureItemPreviewLineCount = 3;
    public const int DefaultTodoItemPreviewLineCount = TodoOptionKinds.DefaultItemPreviewLineCount;
    public const int MinItemPreviewLineCount = QuickCaptureOptionKinds.MinItemPreviewLineCount;
    public const int MaxItemPreviewLineCount = QuickCaptureOptionKinds.MaxItemPreviewLineCount;
    public const string EditorEnterBehaviorCtrlEnterSaves = QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves;
    public const string EditorEnterBehaviorEnterSaves = QuickCaptureOptionKinds.EnterBehaviorEnterSaves;
    public const string LanguageSystem = "System";
    public const string LanguageChinese = "zh-CN";
    public const string LanguageChineseTraditional = "zh-TW";
    public const string LanguageEnglish = "en-US";
    public const string LanguageJapanese = "ja-JP";
    public const string LanguageGerman = "de-DE";
    public const string LanguagePortuguese = "pt-BR";
    public const string LanguageHindi = "hi-IN";
    public const string LanguageSpanish = "es-ES";
    public const string LanguageFrench = "fr-FR";
    public const string LanguageArabic = "ar-SA";
    public const string LanguageBengali = "bn-BD";
    public const string LanguageRussian = "ru-RU";
    public const double DefaultWidgetWidth = 280;
    public const double DefaultWidgetHeight = 400;
    public const bool DefaultGlobalHotkeyEnabled = true;
    public const Models.HotkeyActivationKind DefaultGlobalHotkeyActivationKind =
        Models.HotkeyActivationKind.Chord;
    public const int DefaultGlobalHotkeyModifiers = (int)Models.HotkeyModifierKeys.None;
    public const int DefaultGlobalHotkeyKey = (int)Windows.System.VirtualKey.F7;
    public const double MinWidgetWidth = 50;
    public const double MinWidgetHeight = 50;
    public const double DefaultIconSize = 30;
    public const double MinIconSize = 24;
    public const double MaxIconSize = 56;
    public const double DefaultTextSize = 11.5;
    public const double MinTextSize = QuickCaptureOptionKinds.MinTextSize;
    public const double MaxTextSize = QuickCaptureOptionKinds.MaxTextSize;
    public const double DefaultLayoutDensityScale = 0.56;
    public const double MinLayoutDensityScale = 0.0;
    public const double MaxLayoutDensityScale = 1.0;
    public const double DefaultHorizontalSpacingScale = 0.40;
    public const double DefaultVerticalSpacingScale = 0.60;
    public const double DefaultFileNameWidthScale = 0.36;
    public const int HiddenFileNameLineCount = Contracts.LayoutDensityKinds.HiddenFileNameLineCount;
    public const int DefaultFileNameLineCount = Contracts.LayoutDensityKinds.DefaultFileNameLineCount;
    public const int MinFileNameLineCount = Contracts.LayoutDensityKinds.MinFileNameLineCount;
    public const int MaxFileNameLineCount = Contracts.LayoutDensityKinds.MaxFileNameLineCount;
    public const double MinSpacingScale = 0.0;
    public const double MaxSpacingScale = 1.0;
    public const string LayoutDensityCompact = Contracts.LayoutDensityKinds.Compact;
    public const string LayoutDensityStandard = Contracts.LayoutDensityKinds.Standard;
    public const string LayoutDensityRelaxed = Contracts.LayoutDensityKinds.Relaxed;
    public const string LayoutDensityCustom = Contracts.LayoutDensityKinds.Custom;
    // Aliases of the contract-owned canonical values so the feature editor
    // can build its option list without referencing the settings adapter.
    public const string MusicDisplayModeAuto = Contracts.MusicDisplayModes.Auto;
    public const string MusicDisplayModeCover = Contracts.MusicDisplayModes.Cover;
    public const string MusicDisplayModeControls = Contracts.MusicDisplayModes.Controls;
    public const string MusicDisplayModeRecordVertical = Contracts.MusicDisplayModes.RecordVertical;
    public const string MusicDisplayModeRecordHorizontal = Contracts.MusicDisplayModes.RecordHorizontal;
    public const int MaxRecentOrganizationHistoryCount = 24;
    public const string TodoNewTaskPositionTop = TodoOptionKinds.NewTaskPositionTop;
    public const string TodoNewTaskPositionBottom = TodoOptionKinds.NewTaskPositionBottom;
    public const string TodoDefaultFilterAll = TodoOptionKinds.DefaultFilterAll;
    public const string TodoDefaultFilterActive = TodoOptionKinds.DefaultFilterActive;
    public const string TodoDefaultFilterToday = TodoOptionKinds.DefaultFilterToday;
    public const string TodoDefaultFilterThisWeek = TodoOptionKinds.DefaultFilterThisWeek;
    public const string TodoDefaultFilterThisMonth = TodoOptionKinds.DefaultFilterThisMonth;
    public const string TodoDefaultFilterImportant = TodoOptionKinds.DefaultFilterImportant;
    public const string TodoDefaultFilterCompleted = TodoOptionKinds.DefaultFilterCompleted;
    public const string TodoLayoutModeAuto = TodoOptionKinds.LayoutModeAuto;
    public const string TodoLayoutModeSinglePane = TodoOptionKinds.LayoutModeSinglePane;
    public const string TodoLayoutModeDualPane = TodoOptionKinds.LayoutModeDualPane;
    public const int DefaultTodoReminderOffsetMinutes = TodoOptionKinds.DefaultReminderOffsetMinutes;
    public const int MinTodoReminderOffsetMinutes = TodoOptionKinds.MinReminderOffsetMinutes;
    public const int MaxTodoReminderOffsetMinutes = TodoOptionKinds.MaxReminderOffsetMinutes;
    public const string QuickCaptureDefaultViewRecords = QuickCaptureOptionKinds.DefaultViewRecords;
    public const string QuickCaptureDefaultViewPinned = QuickCaptureOptionKinds.DefaultViewPinned;
    public const string QuickCaptureDefaultViewRecent = QuickCaptureOptionKinds.DefaultViewRecent;
    public const string QuickCaptureFormatMarkdown = QuickCaptureOptionKinds.FormatMarkdown;
    public const string QuickCaptureFormatPlainText = QuickCaptureOptionKinds.FormatPlainText;
    public const string QuickCaptureWideLayoutAuto = QuickCaptureOptionKinds.WideLayoutAuto;
    public const string QuickCaptureWideLayoutSinglePane = QuickCaptureOptionKinds.WideLayoutSinglePane;
    public const string QuickCaptureWideLayoutDualPane = QuickCaptureOptionKinds.WideLayoutDualPane;
    public const string QuickCaptureWideOpenReading = QuickCaptureOptionKinds.WideOpenReading;
    public const string QuickCaptureWideOpenEditing = QuickCaptureOptionKinds.WideOpenEditing;
    public const string WidgetTabStylePivot = QuickCaptureOptionKinds.TabStylePivot;
    public const string WidgetTabStyleButton = QuickCaptureOptionKinds.TabStyleButton;
public const string WeatherTemperatureUnitCelsius = WeatherOptionKinds.TemperatureUnitCelsius;
public const string WeatherTemperatureUnitFahrenheit = WeatherOptionKinds.TemperatureUnitFahrenheit;
public const string WeatherWindSpeedUnitKmh = WeatherOptionKinds.WindSpeedUnitKmh;
public const string WeatherWindSpeedUnitMs = WeatherOptionKinds.WindSpeedUnitMs;
public const string WeatherWindSpeedUnitMph = WeatherOptionKinds.WindSpeedUnitMph;
public const string WeatherDefaultViewToday = WeatherOptionKinds.DefaultViewToday;
public const string WeatherDefaultViewWeek = WeatherOptionKinds.DefaultViewWeek;
public const string WeatherSkinStandard = WeatherOptionKinds.SkinStandard;
public const string WeatherSkinRich = WeatherOptionKinds.SkinRich;
public const string WeatherIconStyleDeskBox = WeatherOptionKinds.IconStyleDeskBox;
public const string WeatherIconStyleFlat = WeatherOptionKinds.IconStyleFlat;
public const string WeatherIconStyleLine = WeatherOptionKinds.IconStyleLine;
public const string WeatherIconStyleFluent = WeatherOptionKinds.IconStyleFluent;
public const string WeatherDataSourceMsn = WeatherOptionKinds.DataSourceMsn;
public const string WeatherDataSourceOpenMeteo = WeatherOptionKinds.DataSourceOpenMeteo;
public const int WeatherRefreshMinMinutes = WeatherOptionKinds.RefreshMinMinutes;
public const int WeatherRefreshMaxMinutes = WeatherOptionKinds.RefreshMaxMinutes;
public const int DefaultSearchMaxResults = 100;

    internal static IReadOnlyDictionary<string, DefaultPreferencePreservationReason>
        DefaultPreferencePreservationPolicy { get; } =
            new Dictionary<string, DefaultPreferencePreservationReason>(StringComparer.Ordinal)
            {
                [nameof(AppSettings.Language)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.AutoStart)] = DefaultPreferencePreservationReason.SystemIntegration,
                [nameof(AppSettings.AutoStartDefaultApplied)] = DefaultPreferencePreservationReason.SystemIntegration,
                [nameof(AppSettings.AutoStartMode)] = DefaultPreferencePreservationReason.SystemIntegration,
                [nameof(AppSettings.FeatureWidgetEnabledStates)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.QuickCaptureEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.TodoEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.Widgets)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.WidgetGroups)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.WidgetTopologyLayouts)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.ActiveWidgetTopologyKey)] = DefaultPreferencePreservationReason.RuntimeState,
                // Device-local monitor identity; the screen it names may not
                // exist on the machine "reset defaults" runs on.
                [nameof(AppSettings.WidgetDefaultBoundScreenId)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.WidgetNewPlacementTarget)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.WidgetDisplayDisconnectBehavior)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.WidgetCapsuleBarOrder)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.WidgetCapsuleFreePlacements)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.DeletedWidgetIds)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.RecentOrganizationHistory)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.DesktopOrganizationRules)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.DesktopAutoOrganizationEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.DesktopAutoOrganizationDelaySeconds)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.DesktopAutoOrganizationBaselineUtc)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.DefaultManagedStorageRootPath)] = DefaultPreferencePreservationReason.Storage,
                [nameof(AppSettings.AutomaticBackupDirectory)] = DefaultPreferencePreservationReason.Storage,
                [nameof(AppSettings.CloudBackupProvider)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.CloudBackupServerUrl)] = DefaultPreferencePreservationReason.Storage,
                [nameof(AppSettings.CloudBackupRemotePath)] = DefaultPreferencePreservationReason.Storage,
                [nameof(AppSettings.CloudBackupUsername)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.CloudBackupTodoDataEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.CloudBackupQuickCaptureDataEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.CloudBackupWidgetStyleEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.CloudBackupLastSuccessUtcTicks)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.CloudBackupLastFailureUtcTicks)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.CloudBackupLastUnverifiedUtcTicks)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.ManagedStorageDesktopShortcutEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.ManagedStorageDesktopShortcutPath)] = DefaultPreferencePreservationReason.SystemIntegration,
                [nameof(AppSettings.HasCompletedOnboarding)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.OnboardingStepIndex)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.CompletedOnboardingVersion)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.HasResolvedInitialFileWidgetSetup)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.LastQuickCaptureFileWidgetId)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.LastUpdateCheckAt)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.SchemaVersion)] = DefaultPreferencePreservationReason.RuntimeState
            };

    private readonly string _settingsPath;
    private AppSettings _settings = new();
    // Set at load when the on-disk settings.json carries a schema version
    // from a newer build; every save is refused for the session.
    private bool _settingsSchemaNewerThanBuild;
    private readonly object _lock = new();

    // The settings.json / widget-layout.json commit pair is protected by a
    // per-data-directory gate shared with coherent-snapshot readers:
    // DeskBoxDataBackupService takes the same gate while copying the pair
    // into a backup, and two service instances pointed at one directory
    // serialize instead of racing the same files.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_fileWriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal static SemaphoreSlim FileWriteLockFor(string dataDirectory) =>
        s_fileWriteLocks.GetOrAdd(
            Path.GetFullPath(dataDirectory).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            static _ => new SemaphoreSlim(1, 1));

    private SemaphoreSlim FileWriteLock =>
        FileWriteLockFor(Path.GetDirectoryName(_settingsPath)!);
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _appearancePreviewCts;
    private long _debounceGeneration;
    private int _hasPendingSave;

    public event Action? SettingsChanged;
    public event Action? AppearancePreviewChanged;
    public event Action<SettingsPersistenceFailure>? PersistenceFailed;

    /// <summary>
    /// Dimension of the most recent SettingsChanged notification. Subscribers
    /// whose work is fully covered by the appearance preview channel can skip
    /// the redundant re-apply when this is <see cref="SettingsChangeKind.Appearance"/>.
    /// Read it at handler entry — before any dispatcher enqueue — because a
    /// deferred read may observe a newer notification.
    /// </summary>
    public SettingsChangeKind LastNotifiedChangeKind { get; private set; } =
        SettingsChangeKind.General;

    public SettingsLoadRecoveryState LastLoadRecoveryState { get; private set; } =
        SettingsLoadRecoveryState.DefaultsForMissingFile;

    public SettingsPersistenceFailure? LastPersistenceFailure { get; private set; }

    public bool HasPendingSave => Volatile.Read(ref _hasPendingSave) != 0;

    public AppSettings Settings
    {
        get { lock (_lock) return _settings; }
    }

    /// <summary>
    /// Restores user preference defaults without touching user data, widget instances, or storage paths.
    /// </summary>
    public static void ApplyDefaultPreferences(AppSettings settings)
    {
        settings.Theme = "System";
        settings.TrayIconStyle = "Colorful";
        settings.AccentColorMode = "System";
        settings.PerformanceMode = PerformanceSettingsPolicy.DefaultMode;
        settings.HiddenCacheCleanupDelaySeconds =
            PerformanceSettingsPolicy.DefaultHiddenCacheCleanupDelaySeconds;
        settings.HiddenCacheCleanupScope =
            PerformanceSettingsPolicy.DefaultHiddenCacheCleanupScope;
        settings.VisibleIdleCacheCleanupDelaySeconds =
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds;
        settings.TransientWindowReleaseDelaySeconds =
            PerformanceSettingsPolicy.DefaultTransientWindowReleaseDelaySeconds;
        settings.IdleWorkingSetTrimEnabled =
            PerformanceSettingsPolicy.DefaultIdleWorkingSetTrimEnabled;
        settings.ImmediateHiddenWorkingSetTrimEnabled =
            PerformanceSettingsPolicy.DefaultImmediateHiddenWorkingSetTrimEnabled;
        settings.Performance.QuiescenceWorkingSetTrimEnabled =
            PerformanceSettingsPolicy.DefaultQuiescenceWorkingSetTrimEnabled;
        settings.PerformanceCacheBudget =
            PerformanceSettingsPolicy.DefaultCacheBudget;
        settings.EnableContinuousDecorativeAnimations =
            PerformanceSettingsPolicy.DefaultContinuousDecorativeAnimationsEnabled;
        settings.EnableTextMarqueeAnimations =
            PerformanceSettingsPolicy.DefaultTextMarqueeAnimationsEnabled;
        settings.EnableVinylRotationAnimations =
            PerformanceSettingsPolicy.DefaultVinylRotationAnimationsEnabled;
        settings.EnableGlanceImageAutoRotation =
            PerformanceSettingsPolicy.DefaultGlanceImageAutoRotationEnabled;
        settings.EnableCompactAmbientAnimations =
            PerformanceSettingsPolicy.DefaultCompactAmbientAnimationsEnabled;
        settings.DefaultWidgetWidth = DefaultWidgetWidth;
        settings.DefaultWidgetHeight = DefaultWidgetHeight;
        settings.WidgetCornerPreference = WidgetCornerPreferenceRound;
        settings.WidgetMaterialType = WidgetMaterialTypeMica;
        settings.WidgetMaterialIntensity = DefaultWidgetMaterialIntensity;
        settings.WidgetForegroundMode = WidgetForegroundSettings.ModeFollowTheme;
        settings.WidgetForegroundColor = WidgetForegroundSettings.DefaultCustomColorHex;
        settings.WidgetBorderColorMode = WidgetBorderColorModeNeutral;
        settings.WidgetBorderStyle = WidgetBorderStyleThin;
        // Schema-v10 background fields reset through the slice so the
        // facade-access ratchet stays untouched.
        WidgetShellSettingsSlice backgroundShell = settings.WidgetShell;
        backgroundShell.WidgetBackgroundMode = null;
        backgroundShell.WidgetBackgroundUnifiedImage = null;
        backgroundShell.WidgetBackgroundPanoramaImage = null;
        backgroundShell.WidgetBackgroundDim = null;
        backgroundShell.WidgetBackgroundUnifiedFit = null;
        backgroundShell.WidgetTextShadowEnabled = false;
        settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
        settings.WidgetAnimationSpeed = WidgetAnimationSpeedStandard;
        settings.WidgetAnimationSlideDirection = WidgetAnimationSlideDirectionRight;
        settings.WidgetAnimationEasingIntensity = WidgetAnimationEasingStandard;
        settings.WidgetAnimationStaggerEnabled = false;
        settings.WidgetLayerMode = WidgetLayerModeDynamic;
        settings.KeepWidgetsVisibleOnShowDesktop = true;
        settings.DisplayWidgetChromeMode = WidgetChromeModeOverlay;
        settings.InteractiveWidgetChromeMode = WidgetChromeModeStandard;
        settings.WidgetCollapseBehavior = WidgetCollapseBehaviorExpanded;
        settings.WidgetGroupDefaultNavigationStyle =
            WidgetGroupNavigationStyles.Tabs;
        settings.WidgetGroupDefaultTitleDisplayMode =
            WidgetGroupTitleDisplayModes.IconAndText;
        settings.WidgetGroupWheelSwitchEnabled = true;
        settings.WidgetGroupHoverSwitchEnabled = false;
        // Reset through the slice so the facade-access ratchet stays untouched.
        settings.WidgetLayout.WidgetGroupSwitchAnimationStyle =
            WidgetGroupSwitchAnimationStyles.Auto;
        settings.WidgetGroupsEnabled = true;
        settings.LegacyWidgetCapsuleModeEnabled = null;
        settings.WidgetCompactWidthMode = WidgetCompactWidthModeAligned;
        settings.WidgetCompactExpansionDirection = WidgetCompactExpansionDirectionDown;
        settings.WidgetCapsuleArrangementMode = WidgetCapsuleArrangementFree;
        settings.WidgetCapsuleBarSpacing = DefaultWidgetCapsuleBarSpacing;
        settings.WidgetCapsuleBarPlacement = WidgetCapsuleBarPlacementFloating;
        settings.WidgetCapsuleBarDirection = WidgetCapsuleBarDirectionAuto;
        settings.WidgetCollapsedStyle = WidgetCollapsedStyleSmart;
        settings.WidgetCompactContentMode = WidgetCompactContentModeSmart;
        settings.WidgetCompactHideSensitiveContent = false;
        settings.WidgetCompactSettingsVersion = CurrentWidgetCompactSettingsVersion;
        settings.WidgetCompactAnimationEffect = WidgetCompactAnimationSlow;
        settings.WidgetCompactAnimationDurationMs = SlowWidgetCompactAnimationDurationMs;
        settings.WidgetCompactExpandDelayMs = SensitiveWidgetCompactExpandDelayMs;
        settings.WidgetCompactCollapseDelayMs = SensitiveWidgetCompactCollapseDelayMs;
        settings.WidgetCompactMediaCornerMode = WidgetCompactMediaCornerFollowWidget;
        settings.WidgetTitleIconMode = WidgetTitleIconModeColor;
        settings.WidgetOpacity = DefaultWidgetOpacity;
        settings.IconSize = DefaultIconSize;
        settings.TextSize = DefaultTextSize;
        settings.LayoutDensityScale = DefaultLayoutDensityScale;
        settings.LayoutDensity = LayoutDensityStandard;
        settings.HorizontalSpacingScale = DefaultHorizontalSpacingScale;
        settings.VerticalSpacingScale = DefaultVerticalSpacingScale;
        settings.FileNameWidthScale = DefaultFileNameWidthScale;
        settings.FileNameLineCount = DefaultFileNameLineCount;
        settings.ShowFileExtensions = false;
        settings.ShowImageFilesAsIcons = false;
        settings.FileStacksEnabled = true;
        settings.FileStackAutoStacking = false;
        settings.FileStackGroupBy = FileStackGroupByKind;
        settings.FileStackThreshold = DefaultFileStackThreshold;
        settings.FileStackOrderBy = FileStackOrderByWidget;
        settings.FileStackOpenMode = FileStackOpenModeInline;
        settings.FileStackPopoverLayout = FileStackPopoverLayoutGrid3;
        settings.FileStackPopoverStyle = FileStackPopoverStyleNeutral;
        settings.FileStackCustomRules = [];
        settings.FileStackUnmatchedBehavior = FileStackUnmatchedKeepLoose;
        settings.HideShortcutExtensionWhenShowingFileExtensions = true;
        settings.ShowHoverButtons = true;
        settings.WidgetHoverButtonActions = DefaultWidgetHoverButtonActions;
        settings.AutoCheckForUpdates = true;
        settings.SilentStartup = false;
        settings.QuickCaptureClipboardEnabled = false;
        settings.QuickCaptureImageClipboardEnabled = false;
        settings.QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
        settings.QuickCaptureShowCreatedTime = true;
        settings.QuickCaptureItemPreviewLineCount = DefaultQuickCaptureItemPreviewLineCount;
        settings.QuickCaptureListTextSize = 0;
        settings.QuickCaptureContentTextSize = 0;
        settings.QuickCaptureEditorEnterBehavior = EditorEnterBehaviorCtrlEnterSaves;
        settings.QuickCaptureDefaultFormat = QuickCaptureFormatMarkdown;
        settings.QuickCaptureWideLayout = QuickCaptureWideLayoutAuto;
        settings.QuickCaptureWideOpenMode = QuickCaptureWideOpenReading;
        settings.QuickCaptureAllowRemoteImages = false;
        settings.AttachmentStorageMode = AttachmentStorageModeLink;
        settings.QuickCaptureDefaultView = QuickCaptureDefaultViewRecords;
        settings.QuickCaptureTabStyle = WidgetTabStyleButton;
        settings.QuickCaptureShowTabBar = true;
        settings.QuickCaptureShowRecordsTab = true;
        settings.QuickCaptureShowPinnedTab = true;
        settings.QuickCaptureShowRecentTab = true;
        settings.TodoShowCompletedTasks = false;
        settings.TodoItemPreviewLineCount = DefaultTodoItemPreviewLineCount;
        settings.TodoListTextSize = 0;
        settings.TodoContentTextSize = 0;
        settings.TodoEditorEnterBehavior = EditorEnterBehaviorCtrlEnterSaves;
        settings.TodoShowFooterStats = false;
        settings.TodoShowClearCompletedButton = true;
        settings.TodoReminderEnabled = true;
        settings.TodoDefaultReminderOffsetMinutes = DefaultTodoReminderOffsetMinutes;
        settings.TodoUseWideDetailPane = true;
        settings.TodoLayoutMode = TodoLayoutModeAuto;
        settings.TodoAutoSelectFirstInWideLayout = true;
        settings.MusicUseArtworkBackdrop = true;
        settings.MusicEnableCoverHoverMotion = true;
        settings.MusicDisplayMode = MusicDisplayModeAuto;
settings.WeatherAutoLocation = true;
settings.WeatherCityName = string.Empty;
settings.WeatherLatitude = 0;
settings.WeatherLongitude = 0;
settings.WeatherTemperatureUnit = WeatherTemperatureUnitCelsius;
settings.WeatherWindSpeedUnit = WeatherWindSpeedUnitKmh;
settings.WeatherDefaultView = WeatherDefaultViewToday;
settings.WeatherSkin = WeatherSkinStandard;
settings.WeatherIconStyle = WeatherIconStyleFluent;
settings.WeatherDataSource = WeatherDataSourceMsn;
settings.WeatherShowForecast = true;
settings.WeatherShowSunrise = true;
settings.WeatherShowUvIndex = true;
settings.WeatherShowPrecipitation = true;
settings.WeatherShowHumidity = true;
settings.WeatherShowWind = true;
settings.WeatherShowPressure = false;
settings.WeatherRefreshIntervalMinutes = 60;
        settings.SearchHotkeyEnabled = false;
        settings.SearchHotkeyModifiers = (int)HotkeyModifierKeys.Alt;
        settings.SearchHotkeyKey = 0x44;
        settings.SearchDisplayMode = "Spotlight";
        settings.SearchIncludeDeskBoxContent = true;
        settings.SearchEverythingEnabled = false;
        settings.SearchEverythingExecutablePath = string.Empty;
        settings.SearchEverythingAdvancedSyntaxEnabled = false;
        settings.SearchShowRecommendations = true;
        settings.SearchMaxResults = DefaultSearchMaxResults;
        settings.SearchDefaultTab = "all";
        settings.SearchSaveHistory = true;
        settings.SearchAppIconAnimation = 0;
        settings.SearchPopupCustomX = null;
        settings.SearchPopupCustomY = null;
        settings.SearchPopupCustomWidth = null;
        settings.SearchPopupCustomHeight = null;
        settings.TodoNewTaskPosition = TodoNewTaskPositionTop;
        settings.TodoDefaultFilter = TodoDefaultFilterAll;
        settings.TodoTabStyle = WidgetTabStyleButton;
        settings.TodoShowTabBar = true;
        settings.TodoShowAllTab = true;
        settings.TodoShowActiveTab = false;
        settings.TodoShowTodayTab = true;
        settings.TodoShowThisWeekTab = false;
        settings.TodoShowThisMonthTab = false;
        settings.TodoShowImportantTab = true;
        settings.TodoShowCompletedTab = true;
        settings.ManagedDropAction = ManagedDropActionMove;
        settings.ManagedDragOutAction = ManagedDragOutActionFollowWindows;
        settings.DragOutModifierTipEnabled = true;
        settings.DragOutResultHintEnabled = true;
        settings.AutomaticBackupEnabled = DataBackupSettingsPolicy.DefaultEnabled;
        settings.AutomaticBackupIntervalMinutes = DataBackupSettingsPolicy.DefaultIntervalMinutes;
        settings.AutomaticBackupRetentionCount = DataBackupSettingsPolicy.DefaultRetentionCount;
        // Cloud cadence prefs reset like the local ones; the channel itself
        // (provider/url/path/user/toggles) stays preserved — see the policy.
        settings.CloudBackup.CloudBackupRetentionCount =
            CloudBackupSettingsPolicy.DefaultRetentionCount;
        settings.CloudBackup.CloudBackupIntervalMinutes =
            CloudBackupSettingsPolicy.DefaultIntervalMinutes;
        settings.GlobalHotkeyEnabled = DefaultGlobalHotkeyEnabled;
        settings.GlobalHotkeyActivationKind = DefaultGlobalHotkeyActivationKind;
        settings.GlobalHotkeyModifiers = DefaultGlobalHotkeyModifiers;
        settings.GlobalHotkeyKey = DefaultGlobalHotkeyKey;
        settings.DesktopDoubleClickEnabled = false;
        settings.DoubleClickToOpen = true;
        settings.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Explorer;
        settings.FileItemSystemContextMenuEnabled = false;
        settings.HideShortcutArrowOverlay = true;
        settings.ResizeSnapEnabled = true;
        settings.WidgetSnapSpacing = DefaultWidgetSnapSpacing;
settings.ShowListItemDetails = false;
settings.ShowFileItemPathTooltips = true;
settings.CustomAccentColor = "#0078D4";
settings.FocusClickedWidgetOnRaise = false;
    }

    public SettingsService()
    {
        _settingsPath = InitializeSettingsPath(DeskBoxDataPathService.Current.DataDirectory);
        OrganizationHistory = new DesktopOrganizationHistoryStore(
            Path.Combine(Path.GetDirectoryName(_settingsPath)!, "desktop-organization-history.json"));
        Layout = new WidgetLayoutStore(
            Path.Combine(Path.GetDirectoryName(_settingsPath)!, "widget-layout.json"));
    }

    internal SettingsService(string dataDir)
    {
        _settingsPath = InitializeSettingsPath(dataDir);
        OrganizationHistory = new DesktopOrganizationHistoryStore(
            Path.Combine(dataDir, "desktop-organization-history.json"));
        Layout = new WidgetLayoutStore(
            Path.Combine(dataDir, "widget-layout.json"));
    }

    /// <summary>
    /// FileSafety-domain store owning the desktop-organization undo receipts.
    /// Local-layer data (machine-local transaction state) — deliberately not
    /// part of settings.json so it can never join the sync layer. Loaded and
    /// migrated inside <see cref="LoadAsync"/>.
    /// </summary>
    public DesktopOrganizationHistoryStore OrganizationHistory { get; }

    /// <summary>
    /// Device-domain store owning widget-layout.json — widget configs, groups,
    /// per-topology layouts, deletion tombstones and group-navigation defaults.
    /// Loaded and adopted inside <see cref="LoadAsync"/>. While it reports
    /// non-authoritative the settings writer keeps emitting the layout keys
    /// so an interrupted migration loses nothing; once authoritative the
    /// writer strips them (single owner, no dual-write).
    /// </summary>
    public WidgetLayoutStore Layout { get; }

    internal string ManagedStorageMigrationDirectory =>
        Path.Combine(Path.GetDirectoryName(_settingsPath)!, "managed-storage-migrations");

    private static string InitializeSettingsPath(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "settings.json");
    }

    /// <summary>
    /// Load settings from disk. Creates default settings if file doesn't exist.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            await MigrateLegacySettingsIfNeededAsync();

            ResilientJsonLoadResult<AppSettings> loadResult =
                await ResilientJsonStore.LoadWithResultAsync(
                    _settingsPath,
                    json => JsonSerializer.Deserialize(
                                json,
                                SettingsJsonContext.Default.AppSettings) ??
                            throw new InvalidDataException("DeskBox settings JSON is empty."),
                    () => new AppSettings(),
                    "SettingsService");
            bool loadedFromDisk = loadResult.Source is
                ResilientJsonLoadSource.Primary or
                ResilientJsonLoadSource.Backup;
            LastLoadRecoveryState = loadResult.Source switch
            {
                ResilientJsonLoadSource.Primary => SettingsLoadRecoveryState.Primary,
                ResilientJsonLoadSource.Backup => SettingsLoadRecoveryState.RecoveredFromBackup,
                ResilientJsonLoadSource.DefaultAfterFailure => SettingsLoadRecoveryState.DefaultsAfterFailure,
                _ => SettingsLoadRecoveryState.DefaultsForMissingFile
            };
            lock (_lock)
            {
                _settings = loadResult.Value;
                // A settings.json stamped by a NEWER build must never be
                // overwritten by this one: the typed model silently drops its
                // unknown fields, so a save would strip them irreversibly
                // (and the stripped file would never re-migrate). Same stance
                // the widget-layout store takes via Layout.CanWrite.
                _settingsSchemaNewerThanBuild = loadedFromDisk &&
                    _settings.SchemaVersion > SettingsMigrationPipeline.CurrentSchemaVersion;
                if (_settingsSchemaNewerThanBuild)
                {
                    App.Log(
                        $"[SettingsService] settings.json schema {_settings.SchemaVersion} is newer " +
                        $"than this build ({SettingsMigrationPipeline.CurrentSchemaVersion}); " +
                        "this session's changes cannot persist");
                }
            }

            bool changed;
            lock (_lock)
            {
                changed = false;
                if (!loadedFromDisk)
                {
                    ApplyDefaultPreferences(_settings);
                    if (_settings.SchemaVersion != SettingsMigrationPipeline.CurrentSchemaVersion)
                    {
                        // A newly created or recovery-default profile already
                        // contains current defaults. Historical migrations are
                        // only for settings that were actually loaded from disk.
                        _settings.SchemaVersion = SettingsMigrationPipeline.CurrentSchemaVersion;
                        changed = true;
                    }
                    if (string.IsNullOrWhiteSpace(_settings.DefaultManagedStorageRootPath))
                    {
                        _settings.DefaultManagedStorageRootPath =
                            ManagedStoragePathService.GetRecommendedPath();
                        changed = true;
                    }
                }

                // Run schema migrations if the loaded version is older than current.
                // Copy-on-write: the pipeline returns the migrated graph (or the
                // input untouched on failure) and this service swaps the reference.
                var migrationPipeline = new SettingsMigrationPipeline();
                (_settings, bool migrationsApplied) = migrationPipeline.RunMigrationsOnCopy(_settings);
                changed |= migrationsApplied;

                // Schema migration treats every existing profile as having resolved
                // the legacy default file-widget setup. Only a genuinely missing
                // settings file represents a new profile that may still be offered
                // the default widget on its first interactive launch. Recovery after
                // a load failure must not manufacture new widgets.
                bool shouldResolveInitialFileWidgetSetup =
                    LastLoadRecoveryState == SettingsLoadRecoveryState.DefaultsAfterFailure;
                if (LastLoadRecoveryState == SettingsLoadRecoveryState.DefaultsForMissingFile)
                {
                    if (_settings.HasResolvedInitialFileWidgetSetup)
                    {
                        _settings.HasResolvedInitialFileWidgetSetup = false;
                        changed = true;
                    }
                }
                else if (shouldResolveInitialFileWidgetSetup &&
                         !_settings.HasResolvedInitialFileWidgetSetup)
                {
                    _settings.HasResolvedInitialFileWidgetSetup = true;
                    changed = true;
                }

                changed |= PerformanceSettingsPolicy.Normalize(_settings);
                changed |= NormalizePresentationSettings(_settings);
                changed |= NormalizeAppearanceSettings(_settings);
                changed |= NormalizeFeatureWidgetSettings(_settings);
                changed |= NormalizeWidgetContentSettings(_settings);
                changed |= NormalizeWidgetTopologyLayouts(_settings);
                changed |= NormalizeOrganizerSettings(_settings);
                changed |= NormalizeHotkeySettings(_settings);
                changed |= NormalizeSearchSettings(_settings);
                changed |= NormalizeQuickCaptureSettings(_settings);
                changed |= NormalizeTodoSettings(_settings);
                changed |= NormalizeWeatherSettings(_settings);
                changed |= NormalizeDeletionSettings(_settings);
                changed |= DataBackupSettingsPolicy.Normalize(_settings);
            }

            // Device-layer migration: widget-layout.json either loads its own
            // data (which then wins over any legacy keys still inside
            // settings.json) or adopts the migrated, normalized legacy slice.
            // While adoption is pending the settings writer keeps the layout
            // keys so nothing is lost; once authoritative the writer strips
            // them and the layout file is the single owner.
            WidgetLayoutLoadResult layoutResult =
                await Layout.LoadAsync(_settings.WidgetLayout);
            lock (_lock)
            {
                _settings.WidgetLayout.CopyFrom(layoutResult.Data);
                if (layoutResult.Authoritative)
                {
                    // File-won data has not seen this session's normalize
                    // passes — re-run the ones that own layout members
                    // (idempotent on an adopted seed, safe unconditionally).
                    changed |= NormalizeWidgetContentSettings(_settings);
                    changed |= NormalizeFeatureWidgetSettings(_settings);
                    changed |= NormalizeWidgetTopologyLayouts(_settings);
                    changed |= NormalizeDeletionSettings(_settings);
                }

                changed |= layoutResult.NeedsPersist;
            }

            // Local-layer migration: the FileSafety-domain history store
            // adopts the legacy settings list only once its own file is
            // durable. If the migration write failed, the legacy list stays
            // so the next launch retries instead of losing receipts.
            bool historyStoreReady = await OrganizationHistory.LoadAsync(
                _settings.RecentOrganizationHistory);
            if (historyStoreReady && _settings.RecentOrganizationHistory.Count > 0)
            {
                _settings.RecentOrganizationHistory = [];
                changed = true;
            }

            if (changed)
            {
                await SaveToFileOnlyAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsService] Failed to load settings: {ex}");
            LastLoadRecoveryState = SettingsLoadRecoveryState.DefaultsAfterFailure;
            lock (_lock) _settings = new AppSettings();
            ApplyDefaultPreferences(_settings);
            _settings.HasResolvedInitialFileWidgetSetup = true;
            try
            {
                // The layout file survives a settings.json failure
                // independently — re-adopt it so one corrupt file does not
                // take the whole desktop down with it.
                WidgetLayoutLoadResult layoutResult =
                    await Layout.LoadAsync(_settings.WidgetLayout);
                lock (_lock) _settings.WidgetLayout.CopyFrom(layoutResult.Data);
            }
            catch (Exception layoutEx) when (layoutEx is not OperationCanceledException)
            {
                App.Log(
                    $"[SettingsService] Layout recovery after settings failure failed: " +
                    layoutEx.Message);
            }
        }
    }

    private async Task MigrateLegacySettingsIfNeededAsync()
    {
        if (File.Exists(_settingsPath))
        {
            return;
        }

        var legacyPath = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        if (!File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(legacyPath);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService] Failed to migrate legacy settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Save settings to disk immediately.
    /// </summary>
    public async Task SaveAsync(bool notifySubscribers = true)
    {
        CancelPendingDebouncedSave();
        bool saved = await SaveToFileOnlyAsync();
        if (saved)
        {
            Volatile.Write(ref _hasPendingSave, 0);
        }
        if (notifySubscribers)
        {
            NotifySettingsChangedSafely();
        }
    }

    /// <summary>
    /// Persists settings and reports whether the atomic file replacement
    /// succeeded. Transactional callers use this before retiring a live
    /// surface so a disk failure can still roll back safely.
    /// </summary>
    public async Task<bool> SaveCheckedAsync(bool notifySubscribers = true)
    {
        CancelPendingDebouncedSave();
        bool saved = await SaveToFileOnlyAsync();
        if (saved)
        {
            Volatile.Write(ref _hasPendingSave, 0);
        }
        if (saved && notifySubscribers)
        {
            NotifySettingsChangedSafely();
        }

        return saved;
    }

    /// <summary>
    /// Cancels the debounce delay and persists the latest in-memory settings.
    /// Used by shutdown and Windows end-session handling.
    /// </summary>
    public Task<bool> FlushPendingSaveAsync(bool notifySubscribers = false)
    {
        return SaveCheckedAsync(notifySubscribers);
    }

    private async Task<bool> SaveToFileOnlyAsync()
    {
        if (_settingsSchemaNewerThanBuild)
        {
            App.Log(
                "[SettingsService] Save refused: settings.json schema " +
                $"{_settings.SchemaVersion} is newer than this build understands; " +
                "settings changes cannot persist");
            return false;
        }

        await FileWriteLock.WaitAsync();
        try
        {
            // Layout first when the device store is authoritative: it owns the
            // durable layout state, and a crash between the two commits still
            // leaves a consistent pair (newer layout file plus an older
            // settings file that no longer carries those keys anyway). A file
            // stamped by a NEWER schema than this build understands is never
            // overwritten — the typed slice cannot represent its unknown
            // fields. That read-only stance must be honest: layout mutations
            // made this session cannot persist anywhere, so the save reports
            // failure (and keeps the settings keys) instead of a false
            // success that silently discards the changes.
            bool layoutSaved = true;
            bool layoutCommitted = false;
            string layoutFailureReason = "widget-layout.json commit failed";
            if (Layout.IsAuthoritative)
            {
                if (Layout.CanWrite)
                {
                    layoutSaved = await Layout.SaveCheckedAsync(WriteLayoutTempFileAsync);
                    layoutCommitted = layoutSaved;
                }
                else
                {
                    layoutSaved = false;
                    layoutFailureReason =
                        $"widget-layout.json schema {Layout.LoadedSchemaVersion} is newer " +
                        "than this build understands; layout changes cannot persist";
                    App.Log($"[SettingsService] Save refused: {layoutFailureReason}");
                }
            }

            // Fail-closed: when the layout commit failed, the layout slice
            // exists only in memory — the settings file must keep carrying
            // the keys so the data survives to the next successful save.
            bool stripLayoutKeys = Layout.IsAuthoritative && layoutSaved;
            try
            {
                await ResilientJsonStore.SaveAsync(
                    _settingsPath,
                    tempPath => WriteSettingsTempFileAsync(tempPath, stripLayoutKeys));
            }
            catch
            {
                // The two stores cannot commit atomically, so callers treat
                // a false result as "nothing persisted". That contract is
                // only honest if the layout commit is undone too — restore
                // the layout primary to its pre-commit bytes (held in .bak
                // by the commit that just ran) so the durable pair stays
                // consistent instead of pointing half at the new state.
                if (layoutCommitted)
                {
                    try
                    {
                        Layout.RevertLastCommit();
                    }
                    catch (Exception revertException)
                    {
                        App.Log(
                            $"[SettingsService] Settings commit failed and the " +
                            $"layout rollback failed too: {revertException}");
                    }
                }

                throw;
            }
            if (!layoutSaved)
            {
                var failure = new SettingsPersistenceFailure(
                    "save",
                    layoutFailureReason,
                    DateTimeOffset.UtcNow);
                LastPersistenceFailure = failure;
                try
                {
                    PersistenceFailed?.Invoke(failure);
                }
                catch (Exception notificationException)
                {
                    App.Log(
                        $"[SettingsService] Persistence failure observer threw: " +
                        notificationException);
                }
                return false;
            }

            LastPersistenceFailure = null;
            return true;
        }
        catch (Exception ex)
        {
            var failure = new SettingsPersistenceFailure(
                "save",
                ex.Message,
                DateTimeOffset.UtcNow);
            LastPersistenceFailure = failure;
            App.Log($"[SettingsService] Failed to save settings: {ex}");
            try
            {
                PersistenceFailed?.Invoke(failure);
            }
            catch (Exception notificationException)
            {
                App.Log(
                    $"[SettingsService] Persistence failure observer threw: " +
                    notificationException);
            }
            return false;
        }
        finally
        {
            FileWriteLock.Release();
        }
    }

    /// <summary>
    /// Streams the locked settings snapshot straight into the store's temp
    /// file. Serializing directly to the stream replaces the previous
    /// SerializeToUtf8Bytes materialization of the whole document - with the
    /// file-count-backed settings graphs of a large import that buffer was
    /// the multi-megabyte large-object garbage measured in the 2026-09-17
    /// memory investigation. The snapshot consistency contract is
    /// deliberately unchanged from the buffered path: every normalization
    /// pass and the serialization itself run under <c>_lock</c>, so one
    /// save still corresponds to exactly one coherent snapshot; only the
    /// destination of the encoder changed. The synchronous encode runs on a
    /// pool thread (the caller already holds FileWriteLock) so UI-thread
    /// savers pay the same lock time as before, not the encode.
    /// </summary>
    private Task WriteSettingsTempFileAsync(string tempPath, bool stripLayoutKeys) => Task.Run(() =>
    {
        using var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024);
        lock (_lock)
        {
            PerformanceSettingsPolicy.Normalize(_settings);
            NormalizePresentationSettings(_settings);
            NormalizeAppearanceSettings(_settings);
            NormalizeFeatureWidgetSettings(_settings);
            NormalizeWidgetContentSettings(_settings);
            NormalizeWidgetTopologyLayouts(_settings);
            NormalizeOrganizerSettings(_settings);
            NormalizeHotkeySettings(_settings);
            NormalizeSearchSettings(_settings);
            NormalizeQuickCaptureSettings(_settings);
            NormalizeTodoSettings(_settings);
            NormalizeWeatherSettings(_settings);
            if (stripLayoutKeys)
            {
                // Write-time projection: while the layout store owns the
                // device domain, its 11 wire keys are stripped from
                // settings.json. The typed facade keeps serializing for
                // migrations, style backups and round-trip baselines — only
                // the file view changes — and the strip set is derived from
                // the slice's own wire names. The DOM detour (vs. streaming)
                // is the price of key removal without a second serializer
                // profile; it runs on a pool thread under the same lock.
                JsonObject settingsDom = JsonSerializer.SerializeToNode(
                        _settings,
                        SettingsJsonContext.Default.AppSettings)!
                    .AsObject();
                foreach (string layoutKey in WidgetLayoutStore.SettingsWireKeys)
                {
                    settingsDom.Remove(layoutKey);
                }

                using var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions { Indented = true });
                settingsDom.WriteTo(writer);
                writer.Flush();
            }
            else
            {
                JsonSerializer.Serialize(
                    stream,
                    _settings,
                    SettingsJsonContext.Default.AppSettings);
            }
        }

        stream.Flush();
    });

    /// <summary>
    /// Streams the locked layout slice into the device store's temp file,
    /// under the same <c>_lock</c> that gives settings.json its
    /// coherent-snapshot contract — one save commits one consistent pair of
    /// files.
    /// </summary>
    private Task WriteLayoutTempFileAsync(string tempPath) => Task.Run(() =>
    {
        using var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024);
        lock (_lock)
        {
            JsonSerializer.Serialize(
                stream,
                new WidgetLayoutDocument { Layout = _settings.WidgetLayout },
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
        }

        stream.Flush();
    });

    /// <summary>
    /// Save settings with debouncing (waits 1 second after last call before actually saving).
    /// Use this for frequent changes like window drag/resize.
    /// </summary>
    public void SaveDebounced(
        bool notifySubscribers = true,
        SettingsChangeKind changeKind = SettingsChangeKind.General)
    {
        if (notifySubscribers)
        {
            NotifySettingsChangedSafely(changeKind);
        }

        CancellationTokenSource debounceCts;
        long generation;
        lock (_debounceLock)
        {
            try
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            debounceCts = new CancellationTokenSource();
            _debounceCts = debounceCts;
            generation = ++_debounceGeneration;
            Volatile.Write(ref _hasPendingSave, 1);
        }

        CancellationToken token = debounceCts.Token;

        Task.Run(async () =>
        {
            bool saved = false;
            try
            {
                await Task.Delay(1000, token);
                if (!token.IsCancellationRequested)
                {
                    saved = await SaveToFileOnlyAsync();
                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                lock (_debounceLock)
                {
                    if (ReferenceEquals(_debounceCts, debounceCts))
                    {
                        _debounceCts = null;
                        debounceCts.Dispose();
                        if (saved && generation == _debounceGeneration)
                        {
                            Volatile.Write(ref _hasPendingSave, 0);
                        }
                    }
                }
            }
        });
    }

    private void NotifySettingsChangedSafely(
        SettingsChangeKind kind = SettingsChangeKind.General)
    {
        lock (_lock)
        {
            LastNotifiedChangeKind = kind;
        }

        Delegate[] handlers = SettingsChanged?.GetInvocationList() ?? [];
        foreach (Action handler in handlers.Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                App.Log($"[SettingsService] SettingsChanged observer failed: {ex}");
            }
        }
    }

    private void CancelPendingDebouncedSave()
    {
        lock (_debounceLock)
        {
            _debounceGeneration++;
            try
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            _debounceCts = null;
        }
    }

    public void RequestAppearancePreview()
    {
        // Dispose the previous CTS to avoid leaking native handles.
        try
        {
            _appearancePreviewCts?.Cancel();
            _appearancePreviewCts?.Dispose();
        }
        catch (ObjectDisposedException) { }
        _appearancePreviewCts = new CancellationTokenSource();
        var token = _appearancePreviewCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(66, token);
                if (!token.IsCancellationRequested)
                {
                    AppearancePreviewChanged?.Invoke();
                }
            }
            catch (TaskCanceledException) { }
            // Do NOT dispose the CTS here — same rationale as SaveDebounced.
        });
    }

    public void NotifyAppearancePreviewNow()
    {
        _appearancePreviewCts?.Cancel();
        AppearancePreviewChanged?.Invoke();
    }

    /// <summary>
    /// Update a widget's configuration. If the widget doesn't exist, it will be added.
    /// </summary>
    public void UpdateWidget(WidgetConfig config, bool notifySubscribers = true)
    {
        lock (_lock)
        {
            if (_settings.DeletedWidgetIds.Contains(config.Id))
            {
                return;
            }

            var existing = _settings.Widgets.FindIndex(w => w.Id == config.Id);
            if (existing >= 0)
                _settings.Widgets[existing] = config;
            else
                _settings.Widgets.Add(config);
        }
        SaveDebounced(notifySubscribers);
    }

    public void UpdateWidgetsBatch(
        IEnumerable<WidgetConfig> configs,
        bool notifySubscribers = true)
    {
        ArgumentNullException.ThrowIfNull(configs);
        WidgetConfig[] distinctConfigs = configs
            .Where(config => config is not null)
            .GroupBy(config => config.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        if (distinctConfigs.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (WidgetConfig config in distinctConfigs)
            {
                if (_settings.DeletedWidgetIds.Contains(config.Id))
                {
                    continue;
                }

                int existing = _settings.Widgets.FindIndex(widget => widget.Id == config.Id);
                if (existing >= 0)
                {
                    _settings.Widgets[existing] = config;
                }
                else
                {
                    _settings.Widgets.Add(config);
                }
            }
        }

        SaveDebounced(notifySubscribers);
    }

    /// <summary>
    /// Remove a widget configuration.
    /// </summary>
    public void RemoveWidget(string widgetId)
    {
        lock (_lock)
        {
            if (!_settings.DeletedWidgetIds.Contains(widgetId))
            {
                _settings.DeletedWidgetIds.Add(widgetId);
            }

            _settings.Widgets.RemoveAll(w => w.Id == widgetId);
        }
        SaveDebounced();
    }

    public void RemoveWidgetImmediate(string widgetId)
    {
        lock (_lock)
        {
            if (!_settings.DeletedWidgetIds.Contains(widgetId))
            {
                _settings.DeletedWidgetIds.Add(widgetId);
            }

            _settings.Widgets.RemoveAll(w => w.Id == widgetId);
        }
    }

    /// <summary>
    /// Downgrades pins that lost their bound monitor id (hand-edited settings,
    /// partial migration) back to the legacy unbound chain so resolution can
    /// never dead-end on an empty reference. Internal for direct policy tests.
    /// </summary>
    internal static void NormalizeScreenBindings(AppSettings settings)
    {
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
                string.IsNullOrWhiteSpace(widget.BoundScreenId))
            {
                widget.ScreenBindingMode = WidgetScreenBindingMode.Unbound;
                widget.BoundScreenId = null;
            }
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (group.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
                string.IsNullOrWhiteSpace(group.BoundScreenId))
            {
                group.ScreenBindingMode = WidgetScreenBindingMode.Unbound;
                group.BoundScreenId = null;
            }
        }

        settings.WidgetNewPlacementTarget = NormalizeWidgetNewPlacementTarget(
            settings.WidgetNewPlacementTarget);
        settings.WidgetDisplayDisconnectBehavior = NormalizeWidgetDisplayDisconnectBehavior(
            settings.WidgetDisplayDisconnectBehavior);
    }

    public const string WidgetNewPlacementCursorDisplay = "CursorDisplay";
    public const string WidgetNewPlacementMainDisplay = "MainDisplay";
    public const string WidgetNewPlacementSpecificDisplay = "SpecificDisplay";

    public static string NormalizeWidgetNewPlacementTarget(string? value)
    {
        return value switch
        {
            WidgetNewPlacementMainDisplay => WidgetNewPlacementMainDisplay,
            WidgetNewPlacementSpecificDisplay => WidgetNewPlacementSpecificDisplay,
            _ => WidgetNewPlacementCursorDisplay
        };
    }

    public const string WidgetDisplayDisconnectMoveToRemaining = "MoveToRemaining";
    public const string WidgetDisplayDisconnectCollapseToCapsule = "CollapseToCapsule";

    public static string NormalizeWidgetDisplayDisconnectBehavior(string? value)
    {
        return string.Equals(value, WidgetDisplayDisconnectCollapseToCapsule)
            ? WidgetDisplayDisconnectCollapseToCapsule
            : WidgetDisplayDisconnectMoveToRemaining;
    }

    private static bool NormalizeWidgetTopologyLayouts(AppSettings settings)
    {
        bool changed = false;
        if (settings.WidgetTopologyLayouts is null)
        {
            settings.WidgetTopologyLayouts = [];
            settings.ActiveWidgetTopologyKey = null;
            NormalizeScreenBindings(settings);
            return true;
        }

        NormalizeScreenBindings(settings);
        foreach (string invalidKey in settings.WidgetTopologyLayouts
                     .Where(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            changed |= settings.WidgetTopologyLayouts.Remove(invalidKey);
        }

        foreach (WidgetTopologyLayoutProfile profile in settings.WidgetTopologyLayouts.Values)
        {
            if (profile.Version != WidgetTopologyLayoutProfile.CurrentVersion)
            {
                profile.Version = WidgetTopologyLayoutProfile.CurrentVersion;
                changed = true;
            }

            if (profile.Monitors is null)
            {
                profile.Monitors = [];
                changed = true;
            }

            if (profile.Surfaces is null)
            {
                profile.Surfaces = [];
                changed = true;
            }

            foreach (string invalidSurfaceId in profile.Surfaces
                         .Where(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                changed |= profile.Surfaces.Remove(invalidSurfaceId);
            }
        }

        while (settings.WidgetTopologyLayouts.Count > WidgetTopologyLayoutService.MaximumRetainedProfiles)
        {
            string? oldest = settings.WidgetTopologyLayouts
                .Where(pair => !string.Equals(
                    pair.Key,
                    settings.ActiveWidgetTopologyKey,
                    StringComparison.Ordinal))
                .OrderBy(pair => pair.Value.LastUsedAtUtc)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (oldest is null)
            {
                break;
            }

            changed |= settings.WidgetTopologyLayouts.Remove(oldest);
        }

        if (!string.IsNullOrWhiteSpace(settings.ActiveWidgetTopologyKey) &&
            !settings.WidgetTopologyLayouts.ContainsKey(settings.ActiveWidgetTopologyKey))
        {
            settings.ActiveWidgetTopologyKey = null;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizePresentationSettings(AppSettings settings)
    {
        bool changed = false;

        double normalizedWidgetOpacity = double.IsFinite(settings.WidgetOpacity)
            ? Math.Clamp(settings.WidgetOpacity, MinWidgetOpacity, MaxWidgetOpacity)
            : DefaultWidgetOpacity;
        if (Math.Abs(settings.WidgetOpacity - normalizedWidgetOpacity) > 0.0001)
        {
            settings.WidgetOpacity = normalizedWidgetOpacity;
            changed = true;
        }

        if (settings.WidgetCornerPreference is not (
            WidgetCornerPreferenceSquare or
            WidgetCornerPreferenceSmall or
            WidgetCornerPreferenceRound))
        {
            settings.WidgetCornerPreference = WidgetCornerPreferenceRound;
            changed = true;
        }

        if (settings.WidgetMaterialType is not (
            WidgetMaterialTypeMica or
            WidgetMaterialTypeMicaAlt or
            WidgetMaterialTypeAcrylic or
            WidgetMaterialTypeAcrylicBase or
            WidgetMaterialTypeSolid))
        {
            // Migrate legacy "Auto" to "Acrylic"
            if (settings.WidgetMaterialType == "Auto")
            {
                settings.WidgetMaterialType = WidgetMaterialTypeAcrylic;
            }
            else
            {
                settings.WidgetMaterialType = WidgetMaterialTypeAcrylic;
            }
            changed = true;
        }

        double normalizedMaterialIntensity = double.IsFinite(settings.WidgetMaterialIntensity)
            ? Math.Clamp(
                settings.WidgetMaterialIntensity,
                MinWidgetMaterialIntensity,
                MaxWidgetMaterialIntensity)
            : DefaultWidgetMaterialIntensity;
        if (Math.Abs(settings.WidgetMaterialIntensity - normalizedMaterialIntensity) > 0.0001)
        {
            settings.WidgetMaterialIntensity = normalizedMaterialIntensity;
            changed = true;
        }

        changed |= WidgetForegroundSettings.NormalizeGlobal(settings);
        changed |= WidgetBackgroundCustomization.NormalizeGlobal(settings);

        if (settings.WidgetBorderColorMode is not (
            WidgetBorderColorModeNeutral or
            WidgetBorderColorModeAccent or
            WidgetBorderColorModeNone))
        {
            settings.WidgetBorderColorMode = WidgetBorderColorModeNeutral;
            changed = true;
        }

        if (settings.WidgetBorderStyle is not (
            WidgetBorderStyleThin or
            WidgetBorderStyleMedium or
            WidgetBorderStyleThick))
        {
            if (settings.WidgetBorderStyle == WidgetBorderStyleNone)
            {
                settings.WidgetBorderColorMode = WidgetBorderColorModeNone;
            }

            settings.WidgetBorderStyle = WidgetBorderStyleThin;
            changed = true;
        }

        string? migratedAnimationDirection = settings.WidgetAnimationEffect switch
        {
            WidgetAnimationEffectSlideLeft or WidgetAnimationEffectSlideLeftFade =>
                WidgetAnimationSlideDirectionLeft,
            WidgetAnimationEffectSlideRight or WidgetAnimationEffectSlideRightFade =>
                WidgetAnimationSlideDirectionRight,
            WidgetAnimationEffectSlideUp or WidgetAnimationEffectSlideUpFade =>
                WidgetAnimationSlideDirectionUp,
            WidgetAnimationEffectSlideDown or WidgetAnimationEffectSlideDownFade =>
                WidgetAnimationSlideDirectionDown,
            _ => null
        };
        if (migratedAnimationDirection is not null)
        {
            settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
            settings.WidgetAnimationSlideDirection = migratedAnimationDirection;
            changed = true;
        }
        else if (settings.WidgetAnimationEffect == WidgetAnimationEffectScaleSlide)
        {
            settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
            changed = true;
        }

        if (settings.WidgetAnimationEffect == WidgetAnimationEffectNone)
        {
            settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
            settings.WidgetAnimationSpeed = WidgetAnimationSpeedStandard;
            settings.WidgetAnimationSlideDirection = WidgetAnimationSlideDirectionRight;
            settings.WidgetAnimationEasingIntensity = WidgetAnimationEasingStandard;
            changed = true;
        }
        else if (settings.WidgetAnimationEffect is not (
            WidgetAnimationEffectFade or
            WidgetAnimationEffectScaleFade or
            WidgetAnimationEffectSlideFade or
            WidgetAnimationEffectZoom or
            WidgetAnimationEffectEdgeScale or
            WidgetAnimationEffectTilt or
            WidgetAnimationEffectWipe))
        {
            settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
            changed = true;
        }

        if (settings.WidgetAnimationSpeed is not (
            WidgetAnimationSpeedVeryFast or
            WidgetAnimationSpeedFast or
            WidgetAnimationSpeedStandard or
            WidgetAnimationSpeedRelaxed or
            WidgetAnimationSpeedSlow))
        {
            settings.WidgetAnimationSpeed = WidgetAnimationSpeedStandard;
            changed = true;
        }

        if (settings.WidgetAnimationSlideDirection is not (
            WidgetAnimationSlideDirectionNone or
            WidgetAnimationSlideDirectionLeft or
            WidgetAnimationSlideDirectionRight or
            WidgetAnimationSlideDirectionUp or
            WidgetAnimationSlideDirectionDown))
        {
            settings.WidgetAnimationSlideDirection = WidgetAnimationSlideDirectionRight;
            changed = true;
        }

        if (settings.WidgetAnimationEffect == WidgetAnimationEffectSlideFade &&
            settings.WidgetAnimationSlideDirection == WidgetAnimationSlideDirectionNone)
        {
            settings.WidgetAnimationSlideDirection = WidgetAnimationSlideDirectionRight;
            changed = true;
        }

        if (settings.WidgetAnimationEasingIntensity is not (
            WidgetAnimationEasingNone or
            WidgetAnimationEasingLight or
            WidgetAnimationEasingStandard or
            WidgetAnimationEasingStrong or
            WidgetAnimationEasingSpring))
        {
            settings.WidgetAnimationEasingIntensity = WidgetAnimationEasingStandard;
            changed = true;
        }

        if (!Contracts.WidgetAnimationKinds.UsesSlideDirection(settings.WidgetAnimationEffect) &&
            settings.WidgetAnimationSlideDirection != WidgetAnimationSlideDirectionNone)
        {
            settings.WidgetAnimationSlideDirection = WidgetAnimationSlideDirectionNone;
            changed = true;
        }

        string normalizedLayerMode = NormalizeWidgetLayerModeSetting(settings.WidgetLayerMode);
        if (!string.Equals(settings.WidgetLayerMode, normalizedLayerMode, StringComparison.Ordinal))
        {
            settings.WidgetLayerMode = normalizedLayerMode;
            changed = true;
        }

        string normalizedDisplayChrome = NormalizeWidgetChromeModeSetting(
            settings.DisplayWidgetChromeMode,
            WidgetChromeMode.Overlay);
        if (!string.Equals(settings.DisplayWidgetChromeMode, normalizedDisplayChrome, StringComparison.Ordinal))
        {
            settings.DisplayWidgetChromeMode = normalizedDisplayChrome;
            changed = true;
        }

        string normalizedInteractiveChrome = NormalizeWidgetChromeModeSetting(
            settings.InteractiveWidgetChromeMode,
            WidgetChromeMode.Standard);
        if (!string.Equals(settings.InteractiveWidgetChromeMode, normalizedInteractiveChrome, StringComparison.Ordinal))
        {
            settings.InteractiveWidgetChromeMode = normalizedInteractiveChrome;
            changed = true;
        }

        string normalizedCollapseBehavior = NormalizeWidgetCollapseBehavior(settings.WidgetCollapseBehavior);
        if (settings.WidgetCompactSettingsVersion < 2)
        {
            // Before version 2, the enable switch was the real gate and the
            // stored behavior was ignored while it was off. Fold that legacy
            // combination into the new single three-state default.
            normalizedCollapseBehavior = settings.LegacyWidgetCapsuleModeEnabled.GetValueOrDefault()
                ? normalizedCollapseBehavior == WidgetCollapseBehaviorExpanded
                    ? WidgetCollapseBehaviorClick
                    : normalizedCollapseBehavior
                : WidgetCollapseBehaviorExpanded;
        }
        if (!string.Equals(settings.WidgetCollapseBehavior, normalizedCollapseBehavior, StringComparison.Ordinal))
        {
            settings.WidgetCollapseBehavior = normalizedCollapseBehavior;
            changed = true;
        }

        if (settings.LegacyWidgetCapsuleModeEnabled is not null)
        {
            settings.LegacyWidgetCapsuleModeEnabled = null;
            changed = true;
        }

        string normalizedCompactWidthMode = NormalizeWidgetCompactWidthMode(
            settings.WidgetCompactWidthMode);
        if (!string.Equals(
                settings.WidgetCompactWidthMode,
                normalizedCompactWidthMode,
                StringComparison.Ordinal))
        {
            settings.WidgetCompactWidthMode = normalizedCompactWidthMode;
            changed = true;
        }

        string normalizedCompactExpansionDirection = NormalizeWidgetCompactExpansionDirection(
            settings.WidgetCompactExpansionDirection);
        if (!string.Equals(
                settings.WidgetCompactExpansionDirection,
                normalizedCompactExpansionDirection,
                StringComparison.Ordinal))
        {
            settings.WidgetCompactExpansionDirection = normalizedCompactExpansionDirection;
            changed = true;
        }

        string? legacyCapsuleArrangement = settings.WidgetCapsuleArrangementMode;
        string normalizedCapsuleArrangement = NormalizeWidgetCapsuleArrangementMode(
            legacyCapsuleArrangement);
        if (!string.Equals(
                settings.WidgetCapsuleArrangementMode,
                normalizedCapsuleArrangement,
                StringComparison.Ordinal))
        {
            settings.WidgetCapsuleArrangementMode = normalizedCapsuleArrangement;
            changed = true;
        }

        if (string.Equals(
                legacyCapsuleArrangement,
                WidgetCapsuleArrangementHorizontal,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                legacyCapsuleArrangement,
                WidgetCapsuleArrangementVertical,
                StringComparison.OrdinalIgnoreCase))
        {
            settings.WidgetCapsuleBarDirection = string.Equals(
                legacyCapsuleArrangement,
                WidgetCapsuleArrangementVertical,
                StringComparison.OrdinalIgnoreCase)
                    ? WidgetCapsuleBarDirectionVertical
                    : WidgetCapsuleBarDirectionHorizontal;
            settings.WidgetCapsuleBarOrder = [];
            changed = true;
        }

        string normalizedCapsulePlacement = NormalizeWidgetCapsuleBarPlacement(
            settings.WidgetCapsuleBarPlacement);
        if (!string.Equals(
                settings.WidgetCapsuleBarPlacement,
                normalizedCapsulePlacement,
                StringComparison.Ordinal))
        {
            settings.WidgetCapsuleBarPlacement = normalizedCapsulePlacement;
            changed = true;
        }

        string normalizedCapsuleDirection = NormalizeWidgetCapsuleBarDirection(
            settings.WidgetCapsuleBarDirection);
        if (!string.Equals(
                settings.WidgetCapsuleBarDirection,
                normalizedCapsuleDirection,
                StringComparison.Ordinal))
        {
            settings.WidgetCapsuleBarDirection = normalizedCapsuleDirection;
            changed = true;
        }

        double normalizedCapsuleSpacing = NormalizeWidgetCapsuleBarSpacing(
            settings.WidgetCapsuleBarSpacing);
        if (!NearlyEqual(settings.WidgetCapsuleBarSpacing, normalizedCapsuleSpacing))
        {
            settings.WidgetCapsuleBarSpacing = normalizedCapsuleSpacing;
            changed = true;
        }

        double normalizedWidgetSnapSpacing = NormalizeWidgetSnapSpacing(
            settings.WidgetSnapSpacing);
        if (!NearlyEqual(settings.WidgetSnapSpacing, normalizedWidgetSnapSpacing))
        {
            settings.WidgetSnapSpacing = normalizedWidgetSnapSpacing;
            changed = true;
        }

        if (settings.WidgetCapsuleBarOrder is null)
        {
            settings.WidgetCapsuleBarOrder = [];
            changed = true;
        }
        else
        {
            List<string> normalizedOrder = settings.WidgetCapsuleBarOrder
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (!settings.WidgetCapsuleBarOrder.SequenceEqual(normalizedOrder, StringComparer.Ordinal))
            {
                settings.WidgetCapsuleBarOrder = normalizedOrder;
                changed = true;
            }
        }

        if (settings.WidgetCapsuleFreePlacements is null)
        {
            settings.WidgetCapsuleFreePlacements = [];
            changed = true;
        }
        else
        {
            foreach (string invalidId in settings.WidgetCapsuleFreePlacements
                         .Where(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                settings.WidgetCapsuleFreePlacements.Remove(invalidId);
                changed = true;
            }
        }

        string normalizedCollapsedStyle = NormalizeWidgetCollapsedStyle(settings.WidgetCollapsedStyle);
        if (!string.Equals(settings.WidgetCollapsedStyle, normalizedCollapsedStyle, StringComparison.Ordinal))
        {
            settings.WidgetCollapsedStyle = normalizedCollapsedStyle;
            changed = true;
        }

        if (settings.WidgetCompactSettingsVersion < 1)
        {
            settings.WidgetCompactContentMode = normalizedCollapsedStyle switch
            {
                WidgetCollapsedStyleMinimal => WidgetCompactContentModeMinimal,
                WidgetCollapsedStyleSmart => WidgetCompactContentModeSmart,
                _ => WidgetCompactContentModeSummary
            };
            changed = true;
        }

        if (settings.WidgetCompactSettingsVersion < CurrentWidgetCompactSettingsVersion)
        {
            settings.WidgetCompactSettingsVersion = CurrentWidgetCompactSettingsVersion;
            changed = true;
        }

        string normalizedCompactContentMode = NormalizeWidgetCompactContentMode(
            settings.WidgetCompactContentMode);
        if (!string.Equals(settings.WidgetCompactContentMode, normalizedCompactContentMode, StringComparison.Ordinal))
        {
            settings.WidgetCompactContentMode = normalizedCompactContentMode;
            changed = true;
        }

        string normalizedCompactAnimation = NormalizeWidgetCompactAnimationEffect(settings.WidgetCompactAnimationEffect);
        if (!string.Equals(settings.WidgetCompactAnimationEffect, normalizedCompactAnimation, StringComparison.Ordinal))
        {
            settings.WidgetCompactAnimationEffect = normalizedCompactAnimation;
            changed = true;
        }

        int normalizedCompactDuration = NormalizeWidgetCompactAnimationDurationMs(settings.WidgetCompactAnimationDurationMs);
        if (settings.WidgetCompactAnimationDurationMs != normalizedCompactDuration)
        {
            settings.WidgetCompactAnimationDurationMs = normalizedCompactDuration;
            changed = true;
        }

        int normalizedCompactExpandDelay = NormalizeWidgetCompactExpandDelayMs(settings.WidgetCompactExpandDelayMs);
        if (settings.WidgetCompactExpandDelayMs != normalizedCompactExpandDelay)
        {
            settings.WidgetCompactExpandDelayMs = normalizedCompactExpandDelay;
            changed = true;
        }

        int normalizedCompactCollapseDelay = NormalizeWidgetCompactCollapseDelayMs(settings.WidgetCompactCollapseDelayMs);
        if (settings.WidgetCompactCollapseDelayMs != normalizedCompactCollapseDelay)
        {
            settings.WidgetCompactCollapseDelayMs = normalizedCompactCollapseDelay;
            changed = true;
        }

        string normalizedCompactMediaCorner = NormalizeWidgetCompactMediaCornerMode(settings.WidgetCompactMediaCornerMode);
        if (!string.Equals(settings.WidgetCompactMediaCornerMode, normalizedCompactMediaCorner, StringComparison.Ordinal))
        {
            settings.WidgetCompactMediaCornerMode = normalizedCompactMediaCorner;
            changed = true;
        }

        string normalizedTitleIconMode = NormalizeWidgetTitleIconModeSetting(settings.WidgetTitleIconMode);
        if (!string.Equals(settings.WidgetTitleIconMode, normalizedTitleIconMode, StringComparison.Ordinal))
        {
            settings.WidgetTitleIconMode = normalizedTitleIconMode;
            changed = true;
        }

        string normalizedHoverActions = NormalizeWidgetHoverButtonActions(settings.WidgetHoverButtonActions);
        if (!string.Equals(settings.WidgetHoverButtonActions, normalizedHoverActions, StringComparison.Ordinal))
        {
            settings.WidgetHoverButtonActions = normalizedHoverActions;
            changed = true;
        }

        double normalizedIconSize = NormalizeIconSize(settings.IconSize);
        if (Math.Abs(settings.IconSize - normalizedIconSize) > 0.0001)
        {
            settings.IconSize = normalizedIconSize;
            changed = true;
        }

        double normalizedTextSize = NormalizeTextSize(settings.TextSize);
        if (Math.Abs(settings.TextSize - normalizedTextSize) > 0.0001)
        {
            settings.TextSize = normalizedTextSize;
            changed = true;
        }

        changed |= NormalizeOptionalTextSize(
            settings.QuickCaptureListTextSize,
            value => settings.QuickCaptureListTextSize = value);
        changed |= NormalizeOptionalTextSize(
            settings.QuickCaptureContentTextSize,
            value => settings.QuickCaptureContentTextSize = value);
        changed |= NormalizeOptionalTextSize(
            settings.TodoListTextSize,
            value => settings.TodoListTextSize = value);
        changed |= NormalizeOptionalTextSize(
            settings.TodoContentTextSize,
            value => settings.TodoContentTextSize = value);

        double legacyLayoutDensityScale = settings.LayoutDensityScale;
        if (!double.IsFinite(legacyLayoutDensityScale))
        {
            legacyLayoutDensityScale = DefaultLayoutDensityScale;
        }

        double normalizedLayoutDensityScale = Math.Clamp(legacyLayoutDensityScale, MinLayoutDensityScale, MaxLayoutDensityScale);
        if (Math.Abs(settings.LayoutDensityScale - normalizedLayoutDensityScale) > 0.0001)
        {
            settings.LayoutDensityScale = normalizedLayoutDensityScale;
            changed = true;
        }

        double normalizedHorizontalSpacingScale = NormalizeScale(
            settings.HorizontalSpacingScale,
            DefaultHorizontalSpacingScale,
            MinSpacingScale,
            MaxSpacingScale);
        double normalizedVerticalSpacingScale = NormalizeScale(
            settings.VerticalSpacingScale,
            DefaultVerticalSpacingScale,
            MinSpacingScale,
            MaxSpacingScale);
        double normalizedFileNameWidthScale = NormalizeScale(
            settings.FileNameWidthScale,
            DefaultFileNameWidthScale,
            MinSpacingScale,
            MaxSpacingScale);

        if (Math.Abs(settings.HorizontalSpacingScale - normalizedHorizontalSpacingScale) > 0.0001)
        {
            settings.HorizontalSpacingScale = normalizedHorizontalSpacingScale;
            changed = true;
        }

        if (Math.Abs(settings.VerticalSpacingScale - normalizedVerticalSpacingScale) > 0.0001)
        {
            settings.VerticalSpacingScale = normalizedVerticalSpacingScale;
            changed = true;
        }

        if (Math.Abs(settings.FileNameWidthScale - normalizedFileNameWidthScale) > 0.0001)
        {
            settings.FileNameWidthScale = normalizedFileNameWidthScale;
            changed = true;
        }

        int normalizedFileNameLineCount = NormalizeFileNameLineCount(settings.FileNameLineCount);
        if (settings.FileNameLineCount != normalizedFileNameLineCount)
        {
            settings.FileNameLineCount = normalizedFileNameLineCount;
            changed = true;
        }

        string resolvedLayoutDensity = settings.LayoutDensity == LayoutDensityCustom
            ? LayoutDensityCustom
            : ResolveLayoutDensityPreset(settings);
        if (!string.Equals(settings.LayoutDensity, resolvedLayoutDensity, StringComparison.Ordinal))
        {
            settings.LayoutDensity = resolvedLayoutDensity;
            changed = true;
        }

        string normalizedMusicDisplayMode = NormalizeMusicDisplayMode(settings.MusicDisplayMode);
        if (!string.Equals(settings.MusicDisplayMode, normalizedMusicDisplayMode, StringComparison.Ordinal))
        {
            settings.MusicDisplayMode = normalizedMusicDisplayMode;
            changed = true;
        }

        double normalizedWidgetWidth = double.IsFinite(settings.DefaultWidgetWidth)
            ? Math.Clamp(settings.DefaultWidgetWidth, MinWidgetWidth, 1200)
            : DefaultWidgetWidth;
        if (Math.Abs(settings.DefaultWidgetWidth - normalizedWidgetWidth) > 0.0001)
        {
            settings.DefaultWidgetWidth = normalizedWidgetWidth;
            changed = true;
        }

        double normalizedWidgetHeight = double.IsFinite(settings.DefaultWidgetHeight)
            ? Math.Clamp(settings.DefaultWidgetHeight, MinWidgetHeight, 1200)
            : DefaultWidgetHeight;
        if (Math.Abs(settings.DefaultWidgetHeight - normalizedWidgetHeight) > 0.0001)
        {
            settings.DefaultWidgetHeight = normalizedWidgetHeight;
            changed = true;
        }

        return changed;
    }

    private static double NormalizeScale(double value, double defaultValue, double min, double max)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    public static double NormalizeIconSize(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinIconSize, MaxIconSize)
            : DefaultIconSize;
    }

    public static double NormalizeTextSize(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinTextSize, MaxTextSize)
            : DefaultTextSize;
    }

    private static bool NormalizeOptionalTextSize(double value, Action<double> assign)
    {
        if (value <= 0 || !double.IsFinite(value))
        {
            return false;
        }

        double normalized = NormalizeTextSize(value);
        if (Math.Abs(value - normalized) <= 0.0001)
        {
            return false;
        }

        assign(normalized);
        return true;
    }

    public static string NormalizeMusicDisplayMode(string? mode)
    {
        return mode switch
        {
            MusicDisplayModeCover => MusicDisplayModeCover,
            MusicDisplayModeControls => MusicDisplayModeControls,
            MusicDisplayModeRecordVertical => MusicDisplayModeRecordVertical,
            MusicDisplayModeRecordHorizontal => MusicDisplayModeRecordHorizontal,
            _ => MusicDisplayModeAuto
        };
    }

    public static bool TryGetLayoutDensityPresetValues(
        string? preset,
        out LayoutDensityPresetValues values) =>
        Contracts.LayoutDensityKinds.TryGetPresetValues(preset, out values);

    public static void ApplyLayoutDensityPreset(AppSettings settings, string preset)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryGetLayoutDensityPresetValues(preset, out LayoutDensityPresetValues values))
        {
            return;
        }

        settings.IconSize = values.IconSize;
        settings.TextSize = values.TextSize;
        settings.LayoutDensityScale = values.DensityScale;
        settings.HorizontalSpacingScale = values.HorizontalSpacingScale;
        settings.VerticalSpacingScale = values.VerticalSpacingScale;
        settings.FileNameWidthScale = values.FileNameWidthScale;
        settings.LayoutDensity = preset;
    }

    public static string ResolveLayoutDensityPreset(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Contracts.LayoutDensityKinds.ResolvePreset(
            settings.IconSize,
            settings.TextSize,
            settings.LayoutDensityScale,
            settings.HorizontalSpacingScale,
            settings.VerticalSpacingScale,
            settings.FileNameWidthScale);
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.0001;

    public static int NormalizeFileNameLineCount(int value) =>
        Contracts.LayoutDensityKinds.NormalizeFileNameLineCount(value);

    public static string NormalizeWidgetChromeModeSetting(string? value, WidgetChromeMode fallback)
    {
        return WidgetChromeModeNames.NormalizeSettingValue(value, fallback);
    }

    public static string NormalizeWidgetCollapseBehavior(string? value)
    {
        return WidgetCollapseBehaviorNames.ToSettingValue(
            WidgetCollapseBehaviorNames.Normalize(value));
    }

    public static string NormalizeWidgetAnimationEffect(string? effect) =>
        Contracts.WidgetAnimationKinds.NormalizeEffect(effect);

    public static string NormalizeWidgetAnimationSpeed(string? speed) =>
        Contracts.WidgetAnimationKinds.NormalizeSpeed(speed);

    public static string NormalizeWidgetAnimationSlideDirection(string? direction) =>
        Contracts.WidgetAnimationKinds.NormalizeSlideDirection(direction);

    public static string NormalizeWidgetAnimationEasingIntensity(string? intensity) =>
        Contracts.WidgetAnimationKinds.NormalizeEasingIntensity(intensity);

    public static string NormalizeWidgetCompactWidthMode(string? value) =>
        CapsuleOptionKinds.NormalizeWidthMode(value);

    public static string NormalizeWidgetCompactExpansionDirection(string? value) =>
        CapsuleOptionKinds.NormalizeExpansionDirection(value);

    public static string NormalizeWidgetCapsuleArrangementMode(string? value) =>
        CapsuleOptionKinds.NormalizeArrangementMode(value);

    public static string NormalizeWidgetCapsuleBarPlacement(string? value) =>
        CapsuleOptionKinds.NormalizeBarPlacement(value);

    public static string NormalizeWidgetCapsuleBarDirection(string? value) =>
        CapsuleOptionKinds.NormalizeBarDirection(value);

    public static double NormalizeWidgetCapsuleBarSpacing(double value) =>
        CapsuleOptionKinds.NormalizeBarSpacing(value);

    public static double NormalizeWidgetSnapSpacing(double value)
    {
        double finiteValue = double.IsFinite(value)
            ? value
            : DefaultWidgetSnapSpacing;
        return Math.Clamp(finiteValue, MinWidgetSnapSpacing, MaxWidgetSnapSpacing);
    }

    public static string NormalizeWidgetCollapsedStyle(string? value)
    {
        if (string.Equals(value, WidgetCollapsedStylePill, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCollapsedStylePill;
        }

        if (string.Equals(value, WidgetCollapsedStyleSmart, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCollapsedStyleSmart;
        }

        return string.Equals(value, WidgetCollapsedStyleMinimal, StringComparison.OrdinalIgnoreCase)
            ? WidgetCollapsedStyleMinimal
            : WidgetCollapsedStyleSummary;
    }

    public static string NormalizeWidgetCompactContentMode(string? value) =>
        CapsuleOptionKinds.NormalizeContentMode(value);

    public static string NormalizeWidgetCompactAnimationEffect(string? value) =>
        CapsuleOptionKinds.NormalizeAnimationEffect(value);

    public static int NormalizeWidgetCompactAnimationDurationMs(int value) =>
        CapsuleOptionKinds.NormalizeAnimationDurationMs(value);

    public static int NormalizeWidgetCompactExpandDelayMs(int value) =>
        CapsuleOptionKinds.NormalizeExpandDelayMs(value);

    public static int NormalizeWidgetCompactCollapseDelayMs(int value) =>
        CapsuleOptionKinds.NormalizeCollapseDelayMs(value);

    public static string NormalizeWidgetCompactHoverResponse(string? value) =>
        CapsuleOptionKinds.NormalizeHoverResponse(value);

    public static string ResolveWidgetCompactHoverResponse(int expandDelayMs, int collapseDelayMs) =>
        CapsuleOptionKinds.ResolveHoverResponse(expandDelayMs, collapseDelayMs);

    public static string NormalizeWidgetCompactMediaCornerMode(string? value)
    {
        if (string.Equals(value, WidgetCompactMediaCornerSquare, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactMediaCornerSquare;
        }

        if (string.Equals(value, WidgetCompactMediaCornerSmall, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactMediaCornerSmall;
        }

        return string.Equals(value, WidgetCompactMediaCornerRound, StringComparison.OrdinalIgnoreCase)
            ? WidgetCompactMediaCornerRound
            : WidgetCompactMediaCornerFollowWidget;
    }

    // Uplifted from the settings shell's capsule section (batch 33) so the
    // CapsuleSettingsCoordinator and the shell's binding state share one
    // preset mapping, like the animation-normalizer precedent of batch 29.
    public static int? WidgetCompactAnimationPresetDurationMs(string? effect) =>
        CapsuleOptionKinds.AnimationPresetDurationMs(effect);

    public static (int Expand, int Collapse)? WidgetCompactHoverResponsePresetDelays(string? response) =>
        CapsuleOptionKinds.HoverResponsePresetDelays(response);

    public static string NormalizeWidgetTitleIconModeSetting(string? value)
    {
        return WidgetTitleIconModeNames.NormalizeSettingValue(value);
    }

    public static string NormalizeWidgetHoverButtonActions(string? value)
    {
        var normalized = ParseWidgetHoverButtonActions(value);
        return normalized.Count == 0
            ? DefaultWidgetHoverButtonActions
            : string.Join(",", normalized);
    }

    public static bool CanToggleWidgetHoverButtonAction(string? value, string action)
    {
        var selected = ParseWidgetHoverButtonActions(value);
        return selected.Contains(action, StringComparer.Ordinal)
            ? selected.Count > 1
            : selected.Count < 3 && SupportedWidgetHoverButtonActions.Contains(action, StringComparer.Ordinal);
    }

    public static bool TryUpdateWidgetHoverButtonAction(
        string? value,
        string action,
        bool isSelected,
        out string updatedValue)
    {
        var selected = ParseWidgetHoverButtonActions(value).ToHashSet(StringComparer.Ordinal);
        if (!SupportedWidgetHoverButtonActions.Contains(action, StringComparer.Ordinal) ||
            (isSelected && !selected.Contains(action) && selected.Count >= 3) ||
            (!isSelected && selected.Contains(action) && selected.Count <= 1))
        {
            updatedValue = string.Join(",", SupportedWidgetHoverButtonActions.Where(selected.Contains));
            return false;
        }

        if (isSelected)
        {
            selected.Add(action);
        }
        else
        {
            selected.Remove(action);
        }

        updatedValue = string.Join(",", SupportedWidgetHoverButtonActions.Where(selected.Contains));
        return true;
    }

    public static IReadOnlyList<string> ParseWidgetHoverButtonActions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [WidgetHoverActionAdd, WidgetHoverActionMore];
        }

        var selected = new List<string>();
        foreach (string rawPart in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string? normalized = SupportedWidgetHoverButtonActions.FirstOrDefault(action =>
                string.Equals(action, rawPart, StringComparison.OrdinalIgnoreCase));
            if (normalized is null || selected.Contains(normalized))
            {
                continue;
            }

            selected.Add(normalized);
            if (selected.Count == 3)
            {
                break;
            }
        }

        return selected.Count == 0
            ? [WidgetHoverActionAdd, WidgetHoverActionMore]
            : selected;
    }

    public static string NormalizeWidgetLayerModeSetting(string? value)
    {
        return value switch
        {
            WidgetLayerModeDesktopPinned => WidgetLayerModeDesktopPinned,
            WidgetLayerModeQuickReveal => WidgetLayerModeQuickReveal,
            _ => WidgetLayerModeDynamic
        };
    }

    private static bool NormalizeAppearanceSettings(AppSettings settings)
    {
        bool changed = false;

        if (settings.Theme is not ("System" or "Light" or "Dark"))
        {
            settings.Theme = "System";
            changed = true;
        }

        if (settings.Language is not (LanguageSystem or LanguageChinese or LanguageChineseTraditional or LanguageEnglish or LanguageJapanese or LanguageGerman or LanguagePortuguese
            or LanguageHindi or LanguageSpanish or LanguageFrench or LanguageArabic or LanguageBengali or LanguageRussian))
        {
            settings.Language = LanguageSystem;
            changed = true;
        }

        if (settings.AccentColorMode is not ("System" or "Custom"))
        {
            settings.AccentColorMode = "System";
            changed = true;
        }

        if (!AccentColorHelper.TryParseHex(settings.CustomAccentColor, out _))
        {
            settings.CustomAccentColor = AccentColorHelper.DefaultAccentColorHex;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeWidgetContentSettings(AppSettings settings)
    {
        bool changed = false;

        string normalizedFolderOpenBehavior =
            FileWidgetFolderOpenBehaviorNames.NormalizeGlobal(
                settings.FileWidgetFolderOpenBehavior);
        if (!string.Equals(
                settings.FileWidgetFolderOpenBehavior,
                normalizedFolderOpenBehavior,
                StringComparison.Ordinal))
        {
            settings.FileWidgetFolderOpenBehavior = normalizedFolderOpenBehavior;
            changed = true;
        }

        changed |= WidgetGroupSettings.Normalize(settings);
        if (!settings.WidgetGroupsEnabled)
        {
            // Grouping is a normal widget operation rather than an optional
            // runtime capability. Keep the old flag readable, but migrate all
            // settings files to the always-available behavior.
            settings.WidgetGroupsEnabled = true;
            changed = true;
        }

        int removedProductivityWidgets = settings.Widgets.RemoveAll(widget => widget.WidgetKind == WidgetKind.Productivity);
        if (removedProductivityWidgets > 0)
        {
            changed = true;
        }

        foreach (var widget in settings.Widgets)
        {
            if (widget.WidgetKind is WidgetKind.Productivity)
            {
                widget.WidgetKind = WidgetKind.File;
                changed = true;
            }

            if (!WidgetRegistry.Default.IsKnown(widget.WidgetKind))
            {
                widget.WidgetKind = WidgetKind.File;
                changed = true;
            }

            widget.Metadata ??= [];

            if (widget.IconSizeOverride is { } iconSizeOverride)
            {
                double normalizedIconSize = NormalizeIconSize(iconSizeOverride);
                if (Math.Abs(iconSizeOverride - normalizedIconSize) > 0.0001)
                {
                    widget.IconSizeOverride = normalizedIconSize;
                    changed = true;
                }
            }

            if (widget.CompactWidth is { } compactWidth)
            {
                double normalizedCompactWidth = WidgetCompactBoundsCalculator.ClampLogicalWidth(compactWidth);
                if (Math.Abs(compactWidth - normalizedCompactWidth) > 0.0001)
                {
                    widget.CompactWidth = normalizedCompactWidth;
                    changed = true;
                }
            }

            if (widget.Metadata.TryGetValue(WidgetChromeModeNames.MetadataKey, out string? chromeModeValue))
            {
                var normalizedChromeMode = WidgetChromeModeNames.NormalizeMode(
                    chromeModeValue,
                    WidgetChromeMode.System,
                    allowSystem: true);
                if (normalizedChromeMode == WidgetChromeMode.System)
                {
                    widget.Metadata.Remove(WidgetChromeModeNames.MetadataKey);
                    changed = true;
                }
                else
                {
                    string normalizedChromeModeValue = WidgetChromeModeNames.ToSettingValue(normalizedChromeMode);
                    if (!string.Equals(chromeModeValue, normalizedChromeModeValue, StringComparison.Ordinal))
                    {
                        widget.Metadata[WidgetChromeModeNames.MetadataKey] = normalizedChromeModeValue;
                        changed = true;
                    }
                }
            }

            if (widget.Metadata.TryGetValue(WidgetCollapseBehaviorNames.MetadataKey, out string? collapseBehaviorValue))
            {
                WidgetCollapseBehavior normalizedBehavior = WidgetCollapseBehaviorNames.Normalize(
                    collapseBehaviorValue,
                    WidgetCollapseBehavior.System,
                    allowSystem: true);
                if (normalizedBehavior == WidgetCollapseBehavior.System)
                {
                    widget.Metadata.Remove(WidgetCollapseBehaviorNames.MetadataKey);
                    changed = true;
                }
                else
                {
                    string normalizedValue = WidgetCollapseBehaviorNames.ToSettingValue(normalizedBehavior);
                    if (!string.Equals(collapseBehaviorValue, normalizedValue, StringComparison.Ordinal))
                    {
                        widget.Metadata[WidgetCollapseBehaviorNames.MetadataKey] = normalizedValue;
                        changed = true;
                    }
                }
            }

            if (WidgetFileStackSettings.NormalizeOverrides(widget))
            {
                changed = true;
            }

            if (FileWidgetFolderOpenBehaviorNames.NormalizeOverride(widget))
            {
                changed = true;
            }

            if (WidgetForegroundSettings.NormalizeOverrides(widget))
            {
                changed = true;
            }

            if (WidgetTitleIconCustomization.NormalizeOverrides(widget))
            {
                changed = true;
            }

            if (WidgetBackgroundCustomization.NormalizeOverrides(widget))
            {
                changed = true;
            }

            if (WidgetBorderCustomization.NormalizeOverrides(widget))
            {
                changed = true;
            }

            if (widget.IsDisabled && widget.WidgetKind != WidgetKind.Glance)
            {
                widget.IsDisabled = false;
                changed = true;
            }
        }

        return changed;
    }

    internal static bool NormalizeFeatureWidgetSettings(AppSettings settings)
    {
        return FeatureWidgetSettings.Normalize(settings);
    }

    public static string GetDefaultManagedStorageRootPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "DeskBox");
    }

    public static string GetRecommendedManagedStorageRootPath()
    {
        return ManagedStoragePathService.GetRecommendedPath();
    }

    public static string NormalizeManagedStorageRootPath(string? path)
    {
        string candidate = string.IsNullOrWhiteSpace(path)
            ? GetDefaultManagedStorageRootPath()
            : Environment.ExpandEnvironmentVariables(path.Trim());

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return GetDefaultManagedStorageRootPath();
        }
    }

    private static bool NormalizeOrganizerSettings(AppSettings settings)
    {
        bool changed = false;

        string normalizedAttachmentStorageMode = NormalizeAttachmentStorageMode(settings.AttachmentStorageMode);
        if (!string.Equals(settings.AttachmentStorageMode, normalizedAttachmentStorageMode, StringComparison.Ordinal))
        {
            settings.AttachmentStorageMode = normalizedAttachmentStorageMode;
            changed = true;
        }

        string normalizedFileStackGroupBy = NormalizeFileStackGroupBy(settings.FileStackGroupBy);
        if (normalizedFileStackGroupBy == FileStackGroupByDateAdded)
        {
            normalizedFileStackGroupBy = FileStackGroupByKind;
        }
        if (!string.Equals(settings.FileStackGroupBy, normalizedFileStackGroupBy, StringComparison.Ordinal))
        {
            settings.FileStackGroupBy = normalizedFileStackGroupBy;
            changed = true;
        }

        int normalizedFileStackThreshold = NormalizeFileStackThreshold(settings.FileStackThreshold);
        if (settings.FileStackThreshold != normalizedFileStackThreshold)
        {
            settings.FileStackThreshold = normalizedFileStackThreshold;
            changed = true;
        }

        string normalizedFileStackOrderBy = NormalizeFileStackOrderBy(settings.FileStackOrderBy);
        if (!string.Equals(settings.FileStackOrderBy, normalizedFileStackOrderBy, StringComparison.Ordinal))
        {
            settings.FileStackOrderBy = normalizedFileStackOrderBy;
            changed = true;
        }

        string normalizedFileStackOpenMode = NormalizeFileStackOpenMode(
            settings.FileStackOpenMode);
        if (!string.Equals(
                settings.FileStackOpenMode,
                normalizedFileStackOpenMode,
                StringComparison.Ordinal))
        {
            settings.FileStackOpenMode = normalizedFileStackOpenMode;
            changed = true;
        }

        string normalizedPopoverLayout = NormalizeFileStackPopoverLayout(
            settings.FileStackPopoverLayout);
        if (!string.Equals(
                settings.FileStackPopoverLayout,
                normalizedPopoverLayout,
                StringComparison.Ordinal))
        {
            settings.FileStackPopoverLayout = normalizedPopoverLayout;
            changed = true;
        }

        string normalizedPopoverStyle = NormalizeFileStackPopoverStyle(
            settings.FileStackPopoverStyle);
        if (!string.Equals(
                settings.FileStackPopoverStyle,
                normalizedPopoverStyle,
                StringComparison.Ordinal))
        {
            settings.FileStackPopoverStyle = normalizedPopoverStyle;
            changed = true;
        }

        string normalizedUnmatchedBehavior = NormalizeFileStackUnmatchedBehavior(
            settings.FileStackUnmatchedBehavior);
        if (!string.Equals(
                settings.FileStackUnmatchedBehavior,
                normalizedUnmatchedBehavior,
                StringComparison.Ordinal))
        {
            settings.FileStackUnmatchedBehavior = normalizedUnmatchedBehavior;
            changed = true;
        }

        settings.FileStackCustomRules ??= [];
        var normalizedRules = new List<FileStackCustomRule>();
        var usedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.FileStackCustomRules
                     .Where(rule => rule is not null)
                     .Take(MaxFileStackCustomRules))
        {
            string id = rule.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || !usedRuleIds.Add(id))
            {
                do
                {
                    id = Guid.NewGuid().ToString("N");
                }
                while (!usedRuleIds.Add(id));
            }

            string name = (rule.Name ?? string.Empty).Trim();
            if (name.Length > 80)
            {
                name = name[..80];
            }

            var extensions = NormalizeFileStackExtensions(rule.Extensions)
                .Take(MaxFileStackExtensionsPerRule)
                .ToList();
            normalizedRules.Add(new FileStackCustomRule
            {
                Id = id,
                Name = name,
                Extensions = extensions
            });
        }

        if (!FileStackCustomRulesEqual(settings.FileStackCustomRules, normalizedRules))
        {
            settings.FileStackCustomRules = normalizedRules;
            changed = true;
        }

        if (!string.Equals(settings.ManagedDropAction, ManagedDropActionMove, StringComparison.Ordinal) &&
            !string.Equals(settings.ManagedDropAction, ManagedDropActionCopy, StringComparison.Ordinal) &&
            !string.Equals(settings.ManagedDropAction, ManagedDropActionFollowWindows, StringComparison.Ordinal))
        {
            settings.ManagedDropAction = ManagedDropActionMove;
            changed = true;
        }

        if (!string.Equals(settings.ManagedDragOutAction, ManagedDragOutActionFollowWindows, StringComparison.Ordinal) &&
            !string.Equals(settings.ManagedDragOutAction, ManagedDragOutActionMove, StringComparison.Ordinal) &&
            !string.Equals(settings.ManagedDragOutAction, ManagedDragOutActionCopy, StringComparison.Ordinal))
        {
            settings.ManagedDragOutAction = ManagedDragOutActionFollowWindows;
            changed = true;
        }

        string normalizedRootPath = NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);
        if (!string.Equals(settings.DefaultManagedStorageRootPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultManagedStorageRootPath = normalizedRootPath;
            changed = true;
        }

        // Null-guard only for the migration seed read in LoadAsync: the live
        // list, sorting, and field defaults moved to
        // DesktopOrganizationHistoryStore (local-layer domain). The entry cap
        // stays with OrganizationHistoryPolicy as before.
        settings.RecentOrganizationHistory ??= [];

        settings.DesktopOrganizationRules ??= [];
        var validFileWidgetIds = settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !settings.DeletedWidgetIds.Contains(widget.Id) &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .Select(widget => widget.Id)
            .ToHashSet(StringComparer.Ordinal);
        var normalizedDesktopRules = settings.DesktopOrganizationRules
            .Where(rule => rule is not null)
            .Select(rule =>
            {
                rule.Id = string.IsNullOrWhiteSpace(rule.Id)
                    ? Guid.NewGuid().ToString("N")
                    : rule.Id.Trim();
                rule.TargetWidgetId = rule.TargetWidgetId?.Trim() ?? string.Empty;
                rule.CategoryIds = NormalizeDesktopOrganizationValues(
                    rule.CategoryIds,
                    StringComparer.Ordinal);
                rule.SubtypeIds = NormalizeDesktopOrganizationValues(
                    rule.SubtypeIds,
                    StringComparer.Ordinal);
                rule.Extensions = NormalizeDesktopOrganizationExtensions(rule.Extensions);
                rule.ExcludedExtensions = NormalizeDesktopOrganizationExtensions(rule.ExcludedExtensions);
                if (!validFileWidgetIds.Contains(rule.TargetWidgetId))
                {
                    rule.IsEnabled = false;
                }
                return rule;
            })
            .Where(rule => !string.IsNullOrWhiteSpace(rule.TargetWidgetId))
            .GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (normalizedDesktopRules.Count != settings.DesktopOrganizationRules.Count)
        {
            changed = true;
        }
        settings.DesktopOrganizationRules = normalizedDesktopRules;
        bool hasEffectiveDesktopOrganizationRule = normalizedDesktopRules.Any(rule =>
            rule.IsEnabled &&
            validFileWidgetIds.Contains(rule.TargetWidgetId) &&
            (rule.CategoryIds.Count > 0 ||
             rule.SubtypeIds.Count > 0 ||
             rule.Extensions.Count > 0));
        if (settings.DesktopAutoOrganizationEnabled &&
            !hasEffectiveDesktopOrganizationRule)
        {
            settings.DesktopAutoOrganizationEnabled = false;
            settings.DesktopAutoOrganizationBaselineUtc = null;
            changed = true;
        }
        int normalizedAutoOrganizationDelay = DesktopAutoOrganizationPolicy.NormalizeDelaySeconds(
            settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds);
        if (settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds != normalizedAutoOrganizationDelay)
        {
            settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds = normalizedAutoOrganizationDelay;
            changed = true;
        }

        foreach (var widget in settings.Widgets)
        {
            if (!widget.FollowsDefaultStoragePath)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(widget.ManagedFolderName) && !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                widget.ManagedFolderName = Path.GetFileName(widget.MappedFolderPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(widget.ManagedFolderName))
            {
                string normalizedWidgetPath = string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                    ? Path.Combine(normalizedRootPath, widget.ManagedFolderName)
                    : NormalizeManagedStorageRootPath(widget.MappedFolderPath);
                if (!string.Equals(widget.MappedFolderPath, normalizedWidgetPath, StringComparison.OrdinalIgnoreCase))
                {
                    widget.MappedFolderPath = normalizedWidgetPath;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static List<string> NormalizeDesktopOrganizationValues(
        IEnumerable<string>? values,
        StringComparer comparer)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(comparer)
            .ToList();
    }

    private static List<string> NormalizeDesktopOrganizationExtensions(
        IEnumerable<string>? extensions)
    {
        return (extensions ?? [])
            .Select(DesktopOrganizationClassifier.NormalizeExtension)
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeAttachmentStorageMode(string? storageMode)
    {
        return string.Equals(storageMode, AttachmentStorageModeCopy, StringComparison.OrdinalIgnoreCase)
            ? AttachmentStorageModeCopy
            : AttachmentStorageModeLink;
    }

    // Canonical file-stack normalizers live in Contracts
    // (FileStackOptionKinds, batch 45); these historical overloads delegate.
    public static string NormalizeFileStackGroupBy(string? groupBy) =>
        FileStackOptionKinds.NormalizeGroupBy(groupBy);

    public static int NormalizeFileStackThreshold(int threshold) =>
        FileStackOptionKinds.NormalizeThreshold(threshold);

    public static string NormalizeFileStackOrderBy(string? orderBy) =>
        FileStackOptionKinds.NormalizeOrderBy(orderBy);

    public static string NormalizeFileStackOpenMode(string? openMode) =>
        FileStackOptionKinds.NormalizeOpenMode(openMode);

    public static string NormalizeFileStackPopoverLayout(string? layout) =>
        FileStackOptionKinds.NormalizePopoverLayout(layout);

    public static string NormalizeFileStackPopoverStyle(string? style) =>
        FileStackOptionKinds.NormalizePopoverStyle(style);

    public static string NormalizeFileStackUnmatchedBehavior(string? behavior) =>
        FileStackOptionKinds.NormalizeUnmatchedBehavior(behavior);

    public static IReadOnlyList<string> NormalizeFileStackExtensions(
        IEnumerable<string>? extensions) =>
        FileStackOptionKinds.NormalizeExtensions(extensions);

    private static bool FileStackCustomRulesEqual(
        IReadOnlyList<FileStackCustomRule> left,
        IReadOnlyList<FileStackCustomRule> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            FileStackCustomRule leftRule = left[index];
            FileStackCustomRule rightRule = right[index];
            if (!string.Equals(leftRule.Id, rightRule.Id, StringComparison.Ordinal) ||
                !string.Equals(leftRule.Name, rightRule.Name, StringComparison.Ordinal) ||
                !leftRule.Extensions.SequenceEqual(
                    rightRule.Extensions,
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NormalizeHotkeySettings(AppSettings settings)
    {
        bool changed = false;
        if (!Enum.IsDefined(settings.GlobalHotkeyActivationKind))
        {
            settings.GlobalHotkeyActivationKind = DefaultGlobalHotkeyActivationKind;
            changed = true;
        }

        int normalizedModifiers = (int)((Models.HotkeyModifierKeys)settings.GlobalHotkeyModifiers &
            (Models.HotkeyModifierKeys.Alt |
             Models.HotkeyModifierKeys.Control |
             Models.HotkeyModifierKeys.Shift |
             Models.HotkeyModifierKeys.Windows));

        if (settings.GlobalHotkeyModifiers != normalizedModifiers)
        {
            settings.GlobalHotkeyModifiers = normalizedModifiers;
            changed = true;
        }

        var gesture = GlobalHotkeyService.NormalizeGesture(settings.GlobalHotkeyModifiers, settings.GlobalHotkeyKey);
        if (!GlobalHotkeyService.IsValidGesture(gesture))
        {
            settings.GlobalHotkeyActivationKind = DefaultGlobalHotkeyActivationKind;
            settings.GlobalHotkeyModifiers = DefaultGlobalHotkeyModifiers;
            settings.GlobalHotkeyKey = DefaultGlobalHotkeyKey;
            changed = true;
        }

        // Normalize the search hotkey modifiers.
        int searchNormalizedModifiers = (int)((Models.HotkeyModifierKeys)settings.SearchHotkeyModifiers &
            (Models.HotkeyModifierKeys.Alt | Models.HotkeyModifierKeys.Control | Models.HotkeyModifierKeys.Shift));
        if (settings.SearchHotkeyModifiers != searchNormalizedModifiers)
        {
            settings.SearchHotkeyModifiers = searchNormalizedModifiers;
            changed = true;
        }

        // Alt+Space rides the opt-in reserved hook and can only belong to one
        // hotkey at a time. When the main hotkey owns it, reset a saved Alt+Space
        // search gesture to the working default (Alt+D) so search stays usable.
        var searchModifiers = (Models.HotkeyModifierKeys)settings.SearchHotkeyModifiers;
        if (settings.SearchHotkeyKey == 0x20 && searchModifiers == Models.HotkeyModifierKeys.Alt &&
            settings.GlobalHotkeyActivationKind == Models.HotkeyActivationKind.Chord &&
            settings.GlobalHotkeyModifiers == (int)Models.HotkeyModifierKeys.Alt &&
            settings.GlobalHotkeyKey == 0x20)
        {
            settings.SearchHotkeyModifiers = (int)Models.HotkeyModifierKeys.Alt;
            settings.SearchHotkeyKey = 0x44; // D
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeSearchSettings(AppSettings settings)
    {
        bool changed = false;

        string everythingPath = settings.SearchEverythingExecutablePath?.Trim() ?? string.Empty;
        if (!string.Equals(
                settings.SearchEverythingExecutablePath,
                everythingPath,
                StringComparison.Ordinal))
        {
            settings.SearchEverythingExecutablePath = everythingPath;
            changed = true;
        }

        string normalized = settings.SearchDefaultTab?.Trim().ToLowerInvariant() ?? "all";
        if (normalized is not ("all" or "app" or "file" or "deskbox"))
        {
            normalized = "all";
        }

        if (!string.Equals(settings.SearchDefaultTab, normalized, StringComparison.Ordinal))
        {
            settings.SearchDefaultTab = normalized;
            changed = true;
        }

        if (settings.SearchMaxResults is not (50 or 100 or 200))
        {
            settings.SearchMaxResults = DefaultSearchMaxResults;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeQuickCaptureSettings(AppSettings settings)
    {
        bool changed = false;

        int normalizedPreviewLineCount = NormalizeItemPreviewLineCount(
            settings.QuickCaptureItemPreviewLineCount);
        if (settings.QuickCaptureItemPreviewLineCount != normalizedPreviewLineCount)
        {
            settings.QuickCaptureItemPreviewLineCount = normalizedPreviewLineCount;
            changed = true;
        }

        string normalizedEnterBehavior = NormalizeEditorEnterBehavior(
            settings.QuickCaptureEditorEnterBehavior);
        if (!string.Equals(
                settings.QuickCaptureEditorEnterBehavior,
                normalizedEnterBehavior,
                StringComparison.Ordinal))
        {
            settings.QuickCaptureEditorEnterBehavior = normalizedEnterBehavior;
            changed = true;
        }

        string normalizedFormat = NormalizeQuickCaptureFormat(settings.QuickCaptureDefaultFormat);
        if (!string.Equals(settings.QuickCaptureDefaultFormat, normalizedFormat, StringComparison.Ordinal))
        {
            settings.QuickCaptureDefaultFormat = normalizedFormat;
            changed = true;
        }

        string normalizedWideLayout = NormalizeQuickCaptureWideLayout(settings.QuickCaptureWideLayout);
        if (!string.Equals(settings.QuickCaptureWideLayout, normalizedWideLayout, StringComparison.Ordinal))
        {
            settings.QuickCaptureWideLayout = normalizedWideLayout;
            changed = true;
        }

        string normalizedWideOpenMode = NormalizeQuickCaptureWideOpenMode(settings.QuickCaptureWideOpenMode);
        if (!string.Equals(settings.QuickCaptureWideOpenMode, normalizedWideOpenMode, StringComparison.Ordinal))
        {
            settings.QuickCaptureWideOpenMode = normalizedWideOpenMode;
            changed = true;
        }

        int normalizedLimit = QuickCaptureService.NormalizeRecentLimit(settings.QuickCaptureRecentLimit);
        if (settings.QuickCaptureRecentLimit != normalizedLimit)
        {
            settings.QuickCaptureRecentLimit = normalizedLimit;
            changed = true;
        }

        string normalizedLastFileWidgetId = string.IsNullOrWhiteSpace(settings.LastQuickCaptureFileWidgetId)
            ? string.Empty
            : settings.LastQuickCaptureFileWidgetId.Trim();
        if (!string.Equals(settings.LastQuickCaptureFileWidgetId, normalizedLastFileWidgetId, StringComparison.Ordinal))
        {
            settings.LastQuickCaptureFileWidgetId = normalizedLastFileWidgetId;
            changed = true;
        }

        string normalizedDefaultView = NormalizeQuickCaptureDefaultView(
            settings.QuickCaptureDefaultView);
        if (settings.QuickCaptureDefaultView != normalizedDefaultView)
        {
            settings.QuickCaptureDefaultView = normalizedDefaultView;
            changed = true;
        }

        if (!settings.QuickCaptureShowRecordsTab &&
            !settings.QuickCaptureShowPinnedTab &&
            !settings.QuickCaptureShowRecentTab)
        {
            settings.QuickCaptureShowRecordsTab = true;
            changed = true;
        }

        if (!IsQuickCaptureTabVisible(settings, settings.QuickCaptureDefaultView))
        {
            settings.QuickCaptureDefaultView = GetFirstVisibleQuickCaptureTab(settings);
            changed = true;
        }

        string normalizedTabStyle = NormalizeWidgetTabStyle(settings.QuickCaptureTabStyle);
        if (!string.Equals(settings.QuickCaptureTabStyle, normalizedTabStyle, StringComparison.Ordinal))
        {
            settings.QuickCaptureTabStyle = normalizedTabStyle;
            changed = true;
        }

        if (!FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture))
        {
            if (settings.QuickCaptureClipboardEnabled)
            {
                settings.QuickCaptureClipboardEnabled = false;
                changed = true;
            }

            if (settings.QuickCaptureImageClipboardEnabled)
            {
                settings.QuickCaptureImageClipboardEnabled = false;
                changed = true;
            }
        }
        else if (!settings.QuickCaptureClipboardEnabled && settings.QuickCaptureImageClipboardEnabled)
        {
            settings.QuickCaptureImageClipboardEnabled = false;
            changed = true;
        }

        return changed;
    }

    internal static bool NormalizeTodoSettings(AppSettings settings)
    {
        bool changed = false;

        string normalizedLayoutMode = NormalizeTodoLayoutMode(
            settings.TodoLayoutMode,
            settings.TodoUseWideDetailPane);
        if (!string.Equals(settings.TodoLayoutMode, normalizedLayoutMode, StringComparison.Ordinal))
        {
            settings.TodoLayoutMode = normalizedLayoutMode;
            changed = true;
        }

        bool legacyWideDetailValue = normalizedLayoutMode != TodoLayoutModeSinglePane;
        if (settings.TodoUseWideDetailPane != legacyWideDetailValue)
        {
            settings.TodoUseWideDetailPane = legacyWideDetailValue;
            changed = true;
        }

        int normalizedPreviewLineCount = NormalizeItemPreviewLineCount(
            settings.TodoItemPreviewLineCount);
        if (settings.TodoItemPreviewLineCount != normalizedPreviewLineCount)
        {
            settings.TodoItemPreviewLineCount = normalizedPreviewLineCount;
            changed = true;
        }

        string normalizedEnterBehavior = NormalizeEditorEnterBehavior(
            settings.TodoEditorEnterBehavior);
        if (!string.Equals(
                settings.TodoEditorEnterBehavior,
                normalizedEnterBehavior,
                StringComparison.Ordinal))
        {
            settings.TodoEditorEnterBehavior = normalizedEnterBehavior;
            changed = true;
        }

        if (settings.TodoNewTaskPosition != NormalizeTodoNewTaskPosition(
                settings.TodoNewTaskPosition))
        {
            settings.TodoNewTaskPosition = TodoNewTaskPositionTop;
            changed = true;
        }

        if (settings.TodoDefaultFilter != NormalizeTodoDefaultFilter(
                settings.TodoDefaultFilter))
        {
            settings.TodoDefaultFilter = TodoDefaultFilterAll;
            changed = true;
        }

        if (!settings.TodoShowAllTab &&
            !settings.TodoShowActiveTab &&
            !settings.TodoShowTodayTab &&
            !settings.TodoShowThisWeekTab &&
            !settings.TodoShowThisMonthTab &&
            !settings.TodoShowImportantTab &&
            !settings.TodoShowCompletedTab)
        {
            settings.TodoShowAllTab = true;
            changed = true;
        }

        if (!IsTodoTabVisible(settings, settings.TodoDefaultFilter))
        {
            settings.TodoDefaultFilter = GetFirstVisibleTodoTab(settings);
            changed = true;
        }

        int normalizedReminderOffset = NormalizeTodoReminderOffsetMinutes(settings.TodoDefaultReminderOffsetMinutes);
        if (settings.TodoDefaultReminderOffsetMinutes != normalizedReminderOffset)
        {
            settings.TodoDefaultReminderOffsetMinutes = normalizedReminderOffset;
            changed = true;
        }

        string normalizedTabStyle = NormalizeWidgetTabStyle(settings.TodoTabStyle);
        if (!string.Equals(settings.TodoTabStyle, normalizedTabStyle, StringComparison.Ordinal))
        {
            settings.TodoTabStyle = normalizedTabStyle;
            changed = true;
        }

        return changed;
    }

    public static string NormalizeTodoLayoutMode(
        string? mode,
        bool legacyUseWideDetailPane = true) =>
        TodoOptionKinds.NormalizeLayoutMode(mode, legacyUseWideDetailPane);

    public static int NormalizeItemPreviewLineCount(int lineCount) =>
        QuickCaptureOptionKinds.NormalizeItemPreviewLineCount(lineCount);

    public static string NormalizeEditorEnterBehavior(string? behavior) =>
        QuickCaptureOptionKinds.NormalizeEnterBehavior(behavior);

    public static bool ShouldSubmitEditorOnEnter(string? behavior, bool controlPressed) =>
        NormalizeEditorEnterBehavior(behavior) == EditorEnterBehaviorEnterSaves
            ? !controlPressed
            : controlPressed;

    public static string NormalizeTodoNewTaskPosition(string? position) =>
        TodoOptionKinds.NormalizeNewTaskPosition(position);

    public static string NormalizeWidgetTabStyle(string? style) =>
        QuickCaptureOptionKinds.NormalizeTabStyle(style);

    public static string NormalizeQuickCaptureFormat(string? format) =>
        QuickCaptureOptionKinds.NormalizeFormat(format);

    public static TextContentFormat ResolveQuickCaptureEditorContentFormat(string? format) =>
        NormalizeQuickCaptureFormat(format) == QuickCaptureFormatPlainText
            ? TextContentFormat.PlainText
            : TextContentFormat.Markdown;

    public static string NormalizeQuickCaptureWideLayout(string? layout) =>
        QuickCaptureOptionKinds.NormalizeWideLayout(layout);

    public static string NormalizeQuickCaptureWideOpenMode(string? mode) =>
        QuickCaptureOptionKinds.NormalizeWideOpenMode(mode);

    public static string NormalizeQuickCaptureDefaultView(string? view) =>
        QuickCaptureOptionKinds.NormalizeDefaultView(view);

    public static bool IsQuickCaptureTabVisible(AppSettings settings, string? view) => view switch
    {
        QuickCaptureDefaultViewPinned => settings.QuickCaptureShowPinnedTab,
        QuickCaptureDefaultViewRecent => settings.QuickCaptureShowRecentTab,
        _ => settings.QuickCaptureShowRecordsTab
    };

    public static string GetFirstVisibleQuickCaptureTab(AppSettings settings)
    {
        if (settings.QuickCaptureShowRecordsTab) return QuickCaptureDefaultViewRecords;
        if (settings.QuickCaptureShowPinnedTab) return QuickCaptureDefaultViewPinned;
        if (settings.QuickCaptureShowRecentTab) return QuickCaptureDefaultViewRecent;
        return QuickCaptureDefaultViewRecords;
    }

    public static string NormalizeTodoDefaultFilter(string? filter) =>
        TodoOptionKinds.NormalizeDefaultFilter(filter);

    public static bool IsTodoTabVisible(AppSettings settings, string? filter) =>
        IsTodoTabVisible(settings.Todo, filter);

    public static bool IsTodoTabVisible(TodoSettingsSlice todo, string? filter) => filter switch
    {
        TodoDefaultFilterActive => todo.TodoShowActiveTab,
        TodoDefaultFilterToday => todo.TodoShowTodayTab,
        TodoDefaultFilterThisWeek => todo.TodoShowThisWeekTab,
        TodoDefaultFilterThisMonth => todo.TodoShowThisMonthTab,
        TodoDefaultFilterImportant => todo.TodoShowImportantTab,
        TodoDefaultFilterCompleted => todo.TodoShowCompletedTab,
        _ => todo.TodoShowAllTab
    };

    public static string GetFirstVisibleTodoTab(AppSettings settings) =>
        GetFirstVisibleTodoTab(settings.Todo);

    public static string GetFirstVisibleTodoTab(TodoSettingsSlice todo)
    {
        if (todo.TodoShowAllTab) return TodoDefaultFilterAll;
        if (todo.TodoShowActiveTab) return TodoDefaultFilterActive;
        if (todo.TodoShowTodayTab) return TodoDefaultFilterToday;
        if (todo.TodoShowThisWeekTab) return TodoDefaultFilterThisWeek;
        if (todo.TodoShowThisMonthTab) return TodoDefaultFilterThisMonth;
        if (todo.TodoShowImportantTab) return TodoDefaultFilterImportant;
        if (todo.TodoShowCompletedTab) return TodoDefaultFilterCompleted;
        return TodoDefaultFilterAll;
    }

    public static int NormalizeTodoReminderOffsetMinutes(int minutes) =>
        TodoOptionKinds.NormalizeReminderOffsetMinutes(minutes);

    internal static bool NormalizeWeatherSettings(AppSettings settings)
    {
        bool changed = false;

        string normalizedTempUnit = settings.WeatherTemperatureUnit is WeatherTemperatureUnitFahrenheit
            ? WeatherTemperatureUnitFahrenheit
            : WeatherTemperatureUnitCelsius;
        if (!string.Equals(settings.WeatherTemperatureUnit, normalizedTempUnit, StringComparison.Ordinal))
        {
            settings.WeatherTemperatureUnit = normalizedTempUnit;
            changed = true;
        }

        string normalizedWindUnit = settings.WeatherWindSpeedUnit is WeatherWindSpeedUnitMs or WeatherWindSpeedUnitMph
            ? settings.WeatherWindSpeedUnit
            : WeatherWindSpeedUnitKmh;
        if (!string.Equals(settings.WeatherWindSpeedUnit, normalizedWindUnit, StringComparison.Ordinal))
        {
            settings.WeatherWindSpeedUnit = normalizedWindUnit;
            changed = true;
        }

        string normalizedView = settings.WeatherDefaultView is WeatherDefaultViewWeek
            ? WeatherDefaultViewWeek
            : WeatherDefaultViewToday;
        if (!string.Equals(settings.WeatherDefaultView, normalizedView, StringComparison.Ordinal))
        {
            settings.WeatherDefaultView = normalizedView;
            changed = true;
        }

        string normalizedSkin = settings.WeatherSkin is WeatherSkinRich
            ? WeatherSkinRich
            : WeatherSkinStandard;
        if (!string.Equals(settings.WeatherSkin, normalizedSkin, StringComparison.Ordinal))
        {
            settings.WeatherSkin = normalizedSkin;
            changed = true;
        }

        string normalizedIconStyle = WeatherOptionKinds.NormalizeIconStyle(settings.WeatherIconStyle);
        if (!string.Equals(settings.WeatherIconStyle, normalizedIconStyle, StringComparison.Ordinal))
        {
            settings.WeatherIconStyle = normalizedIconStyle;
            changed = true;
        }

        string normalizedDataSource = settings.WeatherDataSource is WeatherDataSourceOpenMeteo
            ? WeatherDataSourceOpenMeteo
            : WeatherDataSourceMsn;
        if (!string.Equals(settings.WeatherDataSource, normalizedDataSource, StringComparison.Ordinal))
        {
            settings.WeatherDataSource = normalizedDataSource;
            changed = true;
        }

        int clampedRefresh = Math.Clamp(
            settings.WeatherRefreshIntervalMinutes,
            WeatherRefreshMinMinutes,
            WeatherRefreshMaxMinutes);
        if (settings.WeatherRefreshIntervalMinutes != clampedRefresh)
        {
            settings.WeatherRefreshIntervalMinutes = clampedRefresh;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeDeletionSettings(AppSettings settings)
    {
        int beforeIds = settings.DeletedWidgetIds.Count;
        settings.DeletedWidgetIds = settings.DeletedWidgetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool changed = settings.DeletedWidgetIds.Count != beforeIds;

        int removed = settings.Widgets.RemoveAll(widget => settings.DeletedWidgetIds.Contains(widget.Id));
        if (removed > 0)
        {
            changed = true;
        }

        int staleRemoved = settings.Widgets.RemoveAll(widget => IsStaleHiddenWidget(settings, widget));
        if (staleRemoved > 0)
        {
            changed = true;
        }

        return changed;
    }

    private static bool IsStaleHiddenWidget(AppSettings settings, WidgetConfig widget)
    {
        if (widget.WidgetKind != WidgetKind.File ||
            widget.IsVisible ||
            widget.IsDisabled ||
            !string.IsNullOrEmpty(widget.MappedFolderPath))
        {
            return false;
        }

        bool hasGenericName =
            string.Equals(widget.Name, "New Widget", StringComparison.Ordinal) ||
            string.Equals(widget.Name, "Deskbox", StringComparison.Ordinal) ||
            string.Equals(widget.Name, "\u65B0\u5EFA\u7EC4\u4EF6", StringComparison.Ordinal) ||
            string.Equals(widget.Name, "\u65B0\u5EFA\u5C0F\u7EC4\u4EF6", StringComparison.Ordinal);

        if (!hasGenericName)
        {
            return false;
        }

        return Math.Abs(widget.X - 100) < 0.01 &&
               Math.Abs(widget.Y - 100) < 0.01 &&
               Math.Abs(widget.Width - settings.DefaultWidgetWidth) < 0.01 &&
               Math.Abs(widget.Height - settings.DefaultWidgetHeight) < 0.01;
    }
}
