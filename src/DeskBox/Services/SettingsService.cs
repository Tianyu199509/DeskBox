using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using DeskBox.Helpers;
using DeskBox.Models;

[assembly: InternalsVisibleTo("DeskBox.Tests")]

namespace DeskBox.Services;

public readonly record struct LayoutDensityPresetValues(
    double IconSize,
    double TextSize,
    double DensityScale,
    double HorizontalSpacingScale,
    double VerticalSpacingScale,
    double FileNameWidthScale);

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
    public const string WidgetMaterialTypeMica = "Mica";
    public const string WidgetMaterialTypeMicaAlt = "MicaAlt";
    public const string WidgetMaterialTypeAcrylic = "Acrylic";
    public const string WidgetMaterialTypeAcrylicBase = "AcrylicBase";
    public const string WidgetMaterialTypeSolid = "Solid";
    public const string WidgetBorderColorModeNeutral = "Neutral";
    public const string WidgetBorderColorModeAccent = "Accent";
    public const string WidgetBorderColorModeNone = "None";
    public const string WidgetBorderStyleNone = "None";
    public const string WidgetBorderStyleThin = "Thin";
    public const string WidgetBorderStyleMedium = "Medium";
    public const string WidgetBorderStyleThick = "Thick";
    public const string WidgetCornerPreferenceSquare = "Square";
    public const string WidgetCornerPreferenceSmall = "Small";
    public const string WidgetCornerPreferenceRound = "Round";
    public const string WidgetAnimationEffectNone = "None";
    public const string WidgetAnimationEffectFade = "Fade";
    public const string WidgetAnimationEffectSlideRight = "SlideRight";
    public const string WidgetAnimationEffectSlideLeft = "SlideLeft";
    public const string WidgetAnimationEffectSlideUp = "SlideUp";
    public const string WidgetAnimationEffectSlideDown = "SlideDown";
    public const string WidgetAnimationEffectScaleFade = "ScaleFade";
    public const string WidgetAnimationEffectSlideFade = "SlideFade";
    public const string WidgetAnimationEffectZoom = "Zoom";
    public const string WidgetAnimationEffectSlideUpFade = "SlideUpFade";
    public const string WidgetAnimationEffectSlideDownFade = "SlideDownFade";
    public const string WidgetAnimationEffectSlideLeftFade = "SlideLeftFade";
    public const string WidgetAnimationEffectSlideRightFade = "SlideRightFade";
    public const string WidgetAnimationEffectScaleSlide = "ScaleSlide";
    public const string WidgetAnimationSpeedVeryFast = "VeryFast";
    public const string WidgetAnimationSpeedFast = "Fast";
    public const string WidgetAnimationSpeedStandard = "Standard";
    public const string WidgetAnimationSpeedRelaxed = "Relaxed";
    public const string WidgetAnimationSpeedSlow = "Slow";
    public const string WidgetAnimationSlideDirectionNone = "None";
    public const string WidgetAnimationSlideDirectionLeft = "Left";
    public const string WidgetAnimationSlideDirectionRight = "Right";
    public const string WidgetAnimationSlideDirectionUp = "Up";
    public const string WidgetAnimationSlideDirectionDown = "Down";
    public const string WidgetAnimationEasingNone = "None";
    public const string WidgetAnimationEasingLight = "Light";
    public const string WidgetAnimationEasingStandard = "Standard";
    public const string WidgetAnimationEasingStrong = "Strong";

    public static bool IsMicaMaterial(string? materialType) =>
        materialType is WidgetMaterialTypeMica or WidgetMaterialTypeMicaAlt;

    public static bool IsAcrylicMaterial(string? materialType) =>
        materialType is WidgetMaterialTypeAcrylic or WidgetMaterialTypeAcrylicBase;

    public static bool SupportsWidgetOpacity(string? materialType) =>
        IsAcrylicMaterial(materialType) || materialType == WidgetMaterialTypeSolid;

    public static bool SupportsMaterialIntensity(string? materialType) =>
        IsMicaMaterial(materialType) || IsAcrylicMaterial(materialType);
    public const string WidgetLayerModeDynamic = "Dynamic";
    public const string WidgetLayerModeDesktopPinned = "DesktopPinned";
    public const string WidgetLayerModeQuickReveal = "QuickReveal";
    public const string WidgetChromeModeStandard = WidgetChromeModeNames.Standard;
    public const string WidgetChromeModeCompact = WidgetChromeModeNames.Compact;
    public const string WidgetChromeModeOverlay = WidgetChromeModeNames.Overlay;
    public const string WidgetChromeModeHidden = WidgetChromeModeNames.Hidden;
    public const string WidgetCollapseBehaviorExpanded = WidgetCollapseBehaviorNames.Expanded;
    public const string WidgetCollapseBehaviorClick = WidgetCollapseBehaviorNames.Click;
    public const string WidgetCollapseBehaviorSmart = WidgetCollapseBehaviorNames.Smart;
    public const string WidgetCollapseBehaviorManual = WidgetCollapseBehaviorClick;
    public const string WidgetCollapseBehaviorAuto = WidgetCollapseBehaviorSmart;
    public const string WidgetCompactWidthModeAligned = "Aligned";
    public const string WidgetCompactWidthModeIndependent = "Independent";
    public const string WidgetCompactExpansionDirectionAuto = "Auto";
    public const string WidgetCompactExpansionDirectionDown = "Down";
    public const string WidgetCompactExpansionDirectionUp = "Up";
    public const string WidgetCapsuleArrangementFree = "Free";
    public const string WidgetCapsuleArrangementBar = "Bar";
    // Legacy top-level values retained for settings migration.
    public const string WidgetCapsuleArrangementHorizontal = "Horizontal";
    public const string WidgetCapsuleArrangementVertical = "Vertical";
    public const string WidgetCapsuleBarPlacementFloating = "Floating";
    public const string WidgetCapsuleBarPlacementTop = "Top";
    public const string WidgetCapsuleBarPlacementBottom = "Bottom";
    public const string WidgetCapsuleBarPlacementLeft = "Left";
    public const string WidgetCapsuleBarPlacementRight = "Right";
    public const string WidgetCapsuleBarDirectionAuto = "Auto";
    public const string WidgetCapsuleBarDirectionHorizontal = "Horizontal";
    public const string WidgetCapsuleBarDirectionVertical = "Vertical";
    public const double DefaultWidgetCapsuleBarSpacing = 8;
    public const double MinWidgetCapsuleBarSpacing = 0;
    public const double MaxWidgetCapsuleBarSpacing = 32;
    public const double DefaultWidgetSnapSpacing = 5;
    public const double MinWidgetSnapSpacing = 0;
    public const double MaxWidgetSnapSpacing = 32;
    public const string WidgetCollapsedStyleMinimal = "Minimal";
    public const string WidgetCollapsedStyleSummary = "Summary";
    public const string WidgetCollapsedStyleSmart = "Smart";
    public const string WidgetCollapsedStylePill = "Pill";
    public const string WidgetCompactContentModeMinimal = "Minimal";
    public const string WidgetCompactContentModeSummary = "Summary";
    public const string WidgetCompactContentModeSmart = "Smart";
    public const int CurrentWidgetCompactSettingsVersion = 2;
    public const string WidgetCompactAnimationSmooth = "Smooth";
    public const string WidgetCompactAnimationSlow = "Slow";
    public const string WidgetCompactAnimationSnappy = "Snappy";
    public const string WidgetCompactAnimationCustom = "Custom";
    public const string WidgetCompactAnimationNone = "None";
    public const string WidgetCompactMediaCornerFollowWidget = "FollowWidget";
    public const string WidgetCompactMediaCornerSquare = "Square";
    public const string WidgetCompactMediaCornerSmall = "Small";
    public const string WidgetCompactMediaCornerRound = "Round";
    public const int DefaultWidgetCompactAnimationDurationMs = 220;
    public const int SlowWidgetCompactAnimationDurationMs = 360;
    public const int SnappyWidgetCompactAnimationDurationMs = 160;
    public const int MinWidgetCompactAnimationDurationMs = 120;
    public const int MaxWidgetCompactAnimationDurationMs = 400;
    public const int DefaultWidgetCompactExpandDelayMs = 360;
    public const int MinWidgetCompactExpandDelayMs = 100;
    public const int MaxWidgetCompactExpandDelayMs = 1000;
    public const int DefaultWidgetCompactCollapseDelayMs = 620;
    public const int MinWidgetCompactCollapseDelayMs = 200;
    public const int MaxWidgetCompactCollapseDelayMs = 1500;
    public const string WidgetCompactHoverResponseSensitive = "Sensitive";
    public const string WidgetCompactHoverResponseBalanced = "Balanced";
    public const string WidgetCompactHoverResponsePreventAccidental = "PreventAccidental";
    public const string WidgetCompactHoverResponseCustom = "Custom";
    public const int SensitiveWidgetCompactExpandDelayMs = 100;
    public const int SensitiveWidgetCompactCollapseDelayMs = 200;
    public const int PreventAccidentalWidgetCompactExpandDelayMs = 620;
    public const int PreventAccidentalWidgetCompactCollapseDelayMs = 900;
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
    public const string ManagedDropActionMove = "Move";
    public const string ManagedDropActionCopy = "Copy";
    public const string ManagedDropActionFollowWindows = "FollowWindows";

    public const string AttachmentStorageModeLink = "Link";
    public const string AttachmentStorageModeCopy = "Copy";
    public const string FileStackGroupByKind = "Kind";
    public const string FileStackGroupByDateAdded = "DateAdded";
    // Legacy value used by the first Stack preview build.
    public const string FileStackGroupByDateCreated = "DateCreated";
    public const string FileStackGroupByDateModified = "DateModified";
    public const string FileStackGroupByCustom = "Custom";
    public const int DefaultFileStackThreshold = 3;
    public const string FileStackOrderByWidget = "Widget";
    public const string FileStackOrderByName = "Name";
    public const string FileStackOrderByDateAdded = "DateAdded";
    public const string FileStackOrderByDateModified = "DateModified";
    public const string FileStackOpenModeInline = "Inline";
    public const string FileStackOpenModePopover = "Popover";
    public const string FileStackPopoverLayoutAdaptive = "Adaptive";
    public const string FileStackPopoverLayoutGrid3 = "Grid3";
    public const string FileStackPopoverLayoutGrid5 = "Grid5";
    public const string FileStackPopoverStyleFollowMaterial = "FollowMaterial";
    public const string FileStackPopoverStyleNeutral = "Neutral";
    public const string FileStackUnmatchedKeepLoose = "KeepLoose";
    public const string FileStackUnmatchedOther = "Other";
    public const int MaxFileStackCustomRules = 32;
    public const int MaxFileStackExtensionsPerRule = 64;
    public const int DefaultQuickCaptureItemPreviewLineCount = 3;
    public const int DefaultTodoItemPreviewLineCount = 2;
    [Obsolete("Use the feature-specific preview line defaults.")]
    public const int DefaultItemPreviewLineCount = DefaultQuickCaptureItemPreviewLineCount;
    public const int MinItemPreviewLineCount = 1;
    public const int MaxItemPreviewLineCount = 10;
    public const string EditorEnterBehaviorCtrlEnterSaves = "CtrlEnterSaves";
    public const string EditorEnterBehaviorEnterSaves = "EnterSaves";
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
    public const double MinTextSize = 10;
    public const double MaxTextSize = 16;
    public const double DefaultLayoutDensityScale = 0.56;
    public const double MinLayoutDensityScale = 0.0;
    public const double MaxLayoutDensityScale = 1.0;
    public const double DefaultHorizontalSpacingScale = 0.40;
    public const double DefaultVerticalSpacingScale = 0.60;
    public const double DefaultFileNameWidthScale = 0.36;
    public const int HiddenFileNameLineCount = 0;
    public const int DefaultFileNameLineCount = 2;
    public const int MinFileNameLineCount = 1;
    public const int MaxFileNameLineCount = 2;
    public const double MinSpacingScale = 0.0;
    public const double MaxSpacingScale = 1.0;
    public const string LayoutDensityCompact = "Compact";
    public const string LayoutDensityStandard = "Standard";
    public const string LayoutDensityRelaxed = "Relaxed";
    public const string LayoutDensityCustom = "Custom";
    public const string MusicDisplayModeAuto = "Auto";
    public const string MusicDisplayModeCover = "Cover";
    public const string MusicDisplayModeControls = "Controls";
    public const string MusicDisplayModeRecordVertical = "RecordVertical";
    public const string MusicDisplayModeRecordHorizontal = "RecordHorizontal";
    public const int MaxRecentOrganizationHistoryCount = 24;
    public const string TodoNewTaskPositionTop = "Top";
    public const string TodoNewTaskPositionBottom = "Bottom";
    public const string TodoDefaultFilterAll = "All";
    public const string TodoDefaultFilterActive = "Active";
    public const string TodoDefaultFilterToday = "Today";
    public const string TodoDefaultFilterThisWeek = "ThisWeek";
    public const string TodoDefaultFilterThisMonth = "ThisMonth";
    public const string TodoDefaultFilterImportant = "Important";
    public const string TodoDefaultFilterCompleted = "Completed";
    public const string TodoLayoutModeAuto = "Auto";
    public const string TodoLayoutModeSinglePane = "SinglePane";
    public const string TodoLayoutModeDualPane = "DualPane";
    public const int DefaultTodoReminderOffsetMinutes = 5;
    public const int MinTodoReminderOffsetMinutes = 0;
    public const int MaxTodoReminderOffsetMinutes = 1440;
    public const string QuickCaptureDefaultViewRecords = "Records";
    public const string QuickCaptureDefaultViewPinned = "Pinned";
    public const string QuickCaptureDefaultViewRecent = "Recent";
    public const string QuickCaptureFormatMarkdown = "Markdown";
    public const string QuickCaptureFormatPlainText = "PlainText";
    public const string QuickCaptureWideLayoutAuto = "Auto";
    public const string QuickCaptureWideLayoutSinglePane = "SinglePane";
    public const string QuickCaptureWideLayoutDualPane = "DualPane";
    public const string QuickCaptureWideOpenReading = "Reading";
    public const string QuickCaptureWideOpenEditing = "Editing";
    public const string WidgetTabStylePivot = "Pivot";
    public const string WidgetTabStyleButton = "Button";
public const string WeatherTemperatureUnitCelsius = "Celsius";
public const string WeatherTemperatureUnitFahrenheit = "Fahrenheit";
public const string WeatherWindSpeedUnitKmh = "kmh";
public const string WeatherWindSpeedUnitMs = "ms";
public const string WeatherWindSpeedUnitMph = "mph";
public const string WeatherDefaultViewToday = "Today";
public const string WeatherDefaultViewWeek = "Week";
public const string WeatherSkinStandard = "Standard";
public const string WeatherSkinRich = "Rich";
public const string WeatherDataSourceMsn = "MSN";
public const string WeatherDataSourceOpenMeteo = "OpenMeteo";
public const int WeatherRefreshMinMinutes = 15;
public const int WeatherRefreshMaxMinutes = 180;
public const int DefaultSearchMaxResults = 100;

    internal static IReadOnlyDictionary<string, DefaultPreferencePreservationReason>
        DefaultPreferencePreservationPolicy { get; } =
            new Dictionary<string, DefaultPreferencePreservationReason>(StringComparer.Ordinal)
            {
                [nameof(AppSettings.Language)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.AutoStart)] = DefaultPreferencePreservationReason.SystemIntegration,
                [nameof(AppSettings.FeatureWidgetEnabledStates)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.QuickCaptureEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.TodoEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.Widgets)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.WidgetGroups)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.WidgetTopologyLayouts)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.ActiveWidgetTopologyKey)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.WidgetCapsuleBarOrder)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.WidgetCapsuleFreePlacements)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.DeletedWidgetIds)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.RecentOrganizationHistory)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.DesktopOrganizationRules)] = DefaultPreferencePreservationReason.UserData,
                [nameof(AppSettings.DesktopAutoOrganizationEnabled)] = DefaultPreferencePreservationReason.UserChoice,
                [nameof(AppSettings.DesktopAutoOrganizationBaselineUtc)] = DefaultPreferencePreservationReason.RuntimeState,
                [nameof(AppSettings.DefaultManagedStorageRootPath)] = DefaultPreferencePreservationReason.Storage,
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
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fileWriteLock = new(1, 1);
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
        settings.VisibleIdleCacheCleanupDelaySeconds =
            PerformanceSettingsPolicy.DefaultVisibleIdleCacheCleanupDelaySeconds;
        settings.TransientWindowReleaseDelaySeconds =
            PerformanceSettingsPolicy.DefaultTransientWindowReleaseDelaySeconds;
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
        settings.WidgetTextEdgeMode = WidgetForegroundSettings.EdgeOff;
        settings.WidgetBorderColorMode = WidgetBorderColorModeNeutral;
        settings.WidgetBorderStyle = WidgetBorderStyleThin;
        settings.WidgetAnimationEffect = WidgetAnimationEffectSlideFade;
        settings.WidgetAnimationSpeed = WidgetAnimationSpeedStandard;
        settings.WidgetAnimationSlideDirection = WidgetAnimationSlideDirectionRight;
        settings.WidgetAnimationEasingIntensity = WidgetAnimationEasingStandard;
        settings.WidgetLayerMode = WidgetLayerModeDynamic;
        settings.KeepWidgetsVisibleOnShowDesktop = true;
        settings.DisplayWidgetChromeMode = WidgetChromeModeOverlay;
        settings.InteractiveWidgetChromeMode = WidgetChromeModeStandard;
        settings.WidgetCollapseBehavior = WidgetCollapseBehaviorExpanded;
        settings.WidgetGroupDefaultNavigationStyle =
            WidgetGroupNavigationStyles.Stack;
        settings.WidgetGroupDefaultTitleDisplayMode =
            WidgetGroupTitleDisplayModes.IconAndText;
        settings.WidgetGroupWheelSwitchEnabled = true;
        settings.WidgetGroupHoverSwitchEnabled = false;
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
        settings.QuickCaptureClipboardEnabled = false;
        settings.QuickCaptureImageClipboardEnabled = false;
        settings.QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
        settings.QuickCaptureShowCreatedTime = true;
        settings.QuickCaptureItemPreviewLineCount = DefaultQuickCaptureItemPreviewLineCount;
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
settings.WeatherSkin = WeatherSkinRich;
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
        settings.GlobalHotkeyEnabled = DefaultGlobalHotkeyEnabled;
        settings.GlobalHotkeyActivationKind = DefaultGlobalHotkeyActivationKind;
        settings.GlobalHotkeyModifiers = DefaultGlobalHotkeyModifiers;
        settings.GlobalHotkeyKey = DefaultGlobalHotkeyKey;
        settings.DesktopDoubleClickEnabled = false;
        settings.DoubleClickToOpen = true;
        settings.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Explorer;
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
    }

    internal SettingsService(string dataDir)
    {
        _settingsPath = InitializeSettingsPath(dataDir);
    }

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

                // Run schema migrations if the loaded version is older than current
                var migrationPipeline = new SettingsMigrationPipeline();
                changed |= migrationPipeline.RunMigrations(_settings);

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
        await _fileWriteLock.WaitAsync();
        try
        {
            string json;
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
                json = JsonSerializer.Serialize(
                    _settings,
                    SettingsJsonContext.Default.AppSettings);
            }

            await ResilientJsonStore.SaveAsync(_settingsPath, json);
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
            _fileWriteLock.Release();
        }
    }

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

    private static bool NormalizeWidgetTopologyLayouts(AppSettings settings)
    {
        bool changed = false;
        if (settings.WidgetTopologyLayouts is null)
        {
            settings.WidgetTopologyLayouts = [];
            settings.ActiveWidgetTopologyKey = null;
            return true;
        }

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
            WidgetAnimationEffectZoom))
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
            WidgetAnimationEasingStrong))
        {
            settings.WidgetAnimationEasingIntensity = WidgetAnimationEasingStandard;
            changed = true;
        }

        if (settings.WidgetAnimationEffect != WidgetAnimationEffectSlideFade &&
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
        out LayoutDensityPresetValues values)
    {
        values = preset switch
        {
            LayoutDensityCompact => new LayoutDensityPresetValues(
                IconSize: 26,
                TextSize: 10.5,
                DensityScale: 0.20,
                HorizontalSpacingScale: 0.20,
                VerticalSpacingScale: 0.28,
                FileNameWidthScale: 0.30),
            LayoutDensityStandard => new LayoutDensityPresetValues(
                IconSize: DefaultIconSize,
                TextSize: DefaultTextSize,
                DensityScale: DefaultLayoutDensityScale,
                HorizontalSpacingScale: DefaultHorizontalSpacingScale,
                VerticalSpacingScale: DefaultVerticalSpacingScale,
                FileNameWidthScale: DefaultFileNameWidthScale),
            LayoutDensityRelaxed => new LayoutDensityPresetValues(
                IconSize: 36,
                TextSize: 13,
                DensityScale: 0.84,
                HorizontalSpacingScale: 0.68,
                VerticalSpacingScale: 0.82,
                FileNameWidthScale: 0.50),
            _ => default
        };

        return preset is LayoutDensityCompact or LayoutDensityStandard or LayoutDensityRelaxed;
    }

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
        foreach (string preset in new[] { LayoutDensityCompact, LayoutDensityStandard, LayoutDensityRelaxed })
        {
            TryGetLayoutDensityPresetValues(preset, out LayoutDensityPresetValues values);
            if (NearlyEqual(settings.IconSize, values.IconSize) &&
                NearlyEqual(settings.TextSize, values.TextSize) &&
                NearlyEqual(settings.LayoutDensityScale, values.DensityScale) &&
                NearlyEqual(settings.HorizontalSpacingScale, values.HorizontalSpacingScale) &&
                NearlyEqual(settings.VerticalSpacingScale, values.VerticalSpacingScale) &&
                NearlyEqual(settings.FileNameWidthScale, values.FileNameWidthScale))
            {
                return preset;
            }
        }

        return LayoutDensityCustom;
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.0001;

    public static int NormalizeFileNameLineCount(int value) =>
        value is HiddenFileNameLineCount or MinFileNameLineCount or MaxFileNameLineCount
            ? value
            : DefaultFileNameLineCount;

    public static string NormalizeWidgetChromeModeSetting(string? value, WidgetChromeMode fallback)
    {
        return WidgetChromeModeNames.NormalizeSettingValue(value, fallback);
    }

    public static string NormalizeWidgetCollapseBehavior(string? value)
    {
        return WidgetCollapseBehaviorNames.ToSettingValue(
            WidgetCollapseBehaviorNames.Normalize(value));
    }

    public static string NormalizeWidgetCompactWidthMode(string? value)
    {
        return string.Equals(
            value,
            WidgetCompactWidthModeIndependent,
            StringComparison.OrdinalIgnoreCase)
                ? WidgetCompactWidthModeIndependent
                : WidgetCompactWidthModeAligned;
    }

    public static string NormalizeWidgetCompactExpansionDirection(string? value)
    {
        if (string.Equals(
                value,
                WidgetCompactExpansionDirectionDown,
                StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactExpansionDirectionDown;
        }

        return string.Equals(
                value,
                WidgetCompactExpansionDirectionUp,
                StringComparison.OrdinalIgnoreCase)
            ? WidgetCompactExpansionDirectionUp
            : WidgetCompactExpansionDirectionAuto;
    }

    public static string NormalizeWidgetCapsuleArrangementMode(string? value)
    {
        return string.Equals(value, WidgetCapsuleArrangementBar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, WidgetCapsuleArrangementHorizontal, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, WidgetCapsuleArrangementVertical, StringComparison.OrdinalIgnoreCase)
            ? WidgetCapsuleArrangementBar
            : WidgetCapsuleArrangementFree;
    }

    public static string NormalizeWidgetCapsuleBarPlacement(string? value)
    {
        if (string.Equals(value, WidgetCapsuleBarPlacementTop, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCapsuleBarPlacementTop;
        }

        if (string.Equals(value, WidgetCapsuleBarPlacementBottom, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCapsuleBarPlacementBottom;
        }

        if (string.Equals(value, WidgetCapsuleBarPlacementLeft, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCapsuleBarPlacementLeft;
        }

        return string.Equals(value, WidgetCapsuleBarPlacementRight, StringComparison.OrdinalIgnoreCase)
            ? WidgetCapsuleBarPlacementRight
            : WidgetCapsuleBarPlacementFloating;
    }

    public static string NormalizeWidgetCapsuleBarDirection(string? value)
    {
        if (string.Equals(value, WidgetCapsuleBarDirectionHorizontal, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCapsuleBarDirectionHorizontal;
        }

        return string.Equals(value, WidgetCapsuleBarDirectionVertical, StringComparison.OrdinalIgnoreCase)
            ? WidgetCapsuleBarDirectionVertical
            : WidgetCapsuleBarDirectionAuto;
    }

    public static double NormalizeWidgetCapsuleBarSpacing(double value)
    {
        double finiteValue = double.IsFinite(value)
            ? value
            : DefaultWidgetCapsuleBarSpacing;
        return Math.Clamp(finiteValue, MinWidgetCapsuleBarSpacing, MaxWidgetCapsuleBarSpacing);
    }

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

    public static string NormalizeWidgetCompactContentMode(string? value)
    {
        if (string.Equals(value, WidgetCompactContentModeMinimal, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactContentModeMinimal;
        }

        return string.Equals(value, WidgetCompactContentModeSummary, StringComparison.OrdinalIgnoreCase)
            ? WidgetCompactContentModeSummary
            : WidgetCompactContentModeSmart;
    }

    public static string NormalizeWidgetCompactAnimationEffect(string? value)
    {
        if (string.Equals(value, WidgetCompactAnimationSlow, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactAnimationSlow;
        }

        if (string.Equals(value, WidgetCompactAnimationSnappy, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactAnimationSnappy;
        }

        if (string.Equals(value, WidgetCompactAnimationCustom, StringComparison.OrdinalIgnoreCase))
        {
            return WidgetCompactAnimationCustom;
        }

        return string.Equals(value, WidgetCompactAnimationNone, StringComparison.OrdinalIgnoreCase)
            ? WidgetCompactAnimationNone
            : WidgetCompactAnimationSmooth;
    }

    public static int NormalizeWidgetCompactAnimationDurationMs(int value) =>
        Math.Clamp(value, MinWidgetCompactAnimationDurationMs, MaxWidgetCompactAnimationDurationMs);

    public static int NormalizeWidgetCompactExpandDelayMs(int value) =>
        Math.Clamp(value, MinWidgetCompactExpandDelayMs, MaxWidgetCompactExpandDelayMs);

    public static int NormalizeWidgetCompactCollapseDelayMs(int value) =>
        Math.Clamp(value, MinWidgetCompactCollapseDelayMs, MaxWidgetCompactCollapseDelayMs);

    public static string NormalizeWidgetCompactHoverResponse(string? value) => value switch
    {
        WidgetCompactHoverResponseSensitive => WidgetCompactHoverResponseSensitive,
        WidgetCompactHoverResponsePreventAccidental => WidgetCompactHoverResponsePreventAccidental,
        WidgetCompactHoverResponseCustom => WidgetCompactHoverResponseCustom,
        _ => WidgetCompactHoverResponseBalanced
    };

    public static string ResolveWidgetCompactHoverResponse(int expandDelayMs, int collapseDelayMs)
    {
        int expand = NormalizeWidgetCompactExpandDelayMs(expandDelayMs);
        int collapse = NormalizeWidgetCompactCollapseDelayMs(collapseDelayMs);
        return (expand, collapse) switch
        {
            (SensitiveWidgetCompactExpandDelayMs, SensitiveWidgetCompactCollapseDelayMs) =>
                WidgetCompactHoverResponseSensitive,
            (DefaultWidgetCompactExpandDelayMs, DefaultWidgetCompactCollapseDelayMs) =>
                WidgetCompactHoverResponseBalanced,
            (PreventAccidentalWidgetCompactExpandDelayMs, PreventAccidentalWidgetCompactCollapseDelayMs) =>
                WidgetCompactHoverResponsePreventAccidental,
            _ => WidgetCompactHoverResponseCustom
        };
    }

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

        string normalizedRootPath = NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);
        if (!string.Equals(settings.DefaultManagedStorageRootPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultManagedStorageRootPath = normalizedRootPath;
            changed = true;
        }

        settings.RecentOrganizationHistory ??= [];
        int originalHistoryCount = settings.RecentOrganizationHistory.Count;
        settings.RecentOrganizationHistory = settings.RecentOrganizationHistory
            .Where(entry => entry is not null)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(MaxRecentOrganizationHistoryCount)
            .ToList();
        if (settings.RecentOrganizationHistory.Count != originalHistoryCount)
        {
            changed = true;
        }

        foreach (var entry in settings.RecentOrganizationHistory)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString();
                changed = true;
            }

            entry.WidgetId ??= string.Empty;
            entry.WidgetName ??= string.Empty;
            entry.ActionType = string.IsNullOrWhiteSpace(entry.ActionType)
                ? OrganizationActionType.ManagedDrop
                : entry.ActionType;
            entry.TransferMode = entry.TransferMode is "Move" or "Copy"
                ? entry.TransferMode
                : ManagedDropActionMove;
            entry.Items ??= [];
            entry.Targets ??= [];
            foreach (var item in entry.Items)
            {
                item.TargetWidgetId ??= string.Empty;
                item.TargetWidgetName ??= string.Empty;
            }
        }

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

    public static string NormalizeFileStackGroupBy(string? groupBy)
    {
        if (string.Equals(groupBy, FileStackGroupByDateAdded, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(groupBy, FileStackGroupByDateCreated, StringComparison.OrdinalIgnoreCase))
        {
            return FileStackGroupByDateAdded;
        }

        if (string.Equals(groupBy, FileStackGroupByDateModified, StringComparison.OrdinalIgnoreCase))
        {
            return FileStackGroupByDateModified;
        }

        return string.Equals(groupBy, FileStackGroupByCustom, StringComparison.OrdinalIgnoreCase)
            ? FileStackGroupByCustom
            : FileStackGroupByKind;
    }

    public static int NormalizeFileStackThreshold(int threshold) => threshold switch
    {
        2 or 3 or 5 => threshold,
        _ => DefaultFileStackThreshold
    };

    public static string NormalizeFileStackOrderBy(string? orderBy)
    {
        if (string.Equals(orderBy, FileStackOrderByName, StringComparison.OrdinalIgnoreCase))
        {
            return FileStackOrderByName;
        }

        if (string.Equals(orderBy, FileStackOrderByDateAdded, StringComparison.OrdinalIgnoreCase))
        {
            return FileStackOrderByDateAdded;
        }

        return string.Equals(orderBy, FileStackOrderByDateModified, StringComparison.OrdinalIgnoreCase)
            ? FileStackOrderByDateModified
            : FileStackOrderByWidget;
    }

    public static string NormalizeFileStackOpenMode(string? openMode) =>
        string.Equals(
            openMode,
            FileStackOpenModePopover,
            StringComparison.OrdinalIgnoreCase)
                ? FileStackOpenModePopover
                : FileStackOpenModeInline;

    public static string NormalizeFileStackPopoverLayout(string? layout) =>
        layout switch
        {
            FileStackPopoverLayoutGrid3 => FileStackPopoverLayoutGrid3,
            FileStackPopoverLayoutGrid5 => FileStackPopoverLayoutGrid5,
            _ => FileStackPopoverLayoutAdaptive
        };

    public static string NormalizeFileStackPopoverStyle(string? style) =>
        string.Equals(
            style,
            FileStackPopoverStyleFollowMaterial,
            StringComparison.OrdinalIgnoreCase)
                ? FileStackPopoverStyleFollowMaterial
                : FileStackPopoverStyleNeutral;

    public static string NormalizeFileStackUnmatchedBehavior(string? behavior) =>
        string.Equals(behavior, FileStackUnmatchedOther, StringComparison.OrdinalIgnoreCase)
            ? FileStackUnmatchedOther
            : FileStackUnmatchedKeepLoose;

    public static IReadOnlyList<string> NormalizeFileStackExtensions(
        IEnumerable<string>? extensions)
    {
        if (extensions is null)
        {
            return [];
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in extensions)
        {
            string extension = (value ?? string.Empty).Trim();
            if (extension.StartsWith("*.", StringComparison.Ordinal))
            {
                extension = extension[1..];
            }
            else if (extension.StartsWith('*'))
            {
                extension = extension[1..];
            }

            if (extension.Length == 0)
            {
                continue;
            }

            if (!extension.StartsWith('.'))
            {
                extension = $".{extension}";
            }

            extension = extension.ToLowerInvariant();
            if (extension.Length > 24 ||
                extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                extension.Contains(Path.DirectorySeparatorChar) ||
                extension.Contains(Path.AltDirectorySeparatorChar) ||
                !seen.Add(extension))
            {
                continue;
            }

            normalized.Add(extension);
        }

        return normalized;
    }

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

        // Alt+Space is reserved by Windows for the window system menu and cannot be
        // registered via RegisterHotKey. Reset a saved Alt+Space gesture to the
        // working default (Alt+D) so the search hotkey functions out of the box.
        var searchModifiers = (Models.HotkeyModifierKeys)settings.SearchHotkeyModifiers;
        if (settings.SearchHotkeyKey == 0x20 && searchModifiers == Models.HotkeyModifierKeys.Alt)
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

        if (settings.QuickCaptureDefaultView is not (
            QuickCaptureDefaultViewRecords or
            QuickCaptureDefaultViewPinned or
            QuickCaptureDefaultViewRecent))
        {
            settings.QuickCaptureDefaultView = QuickCaptureDefaultViewRecords;
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

        if (settings.TodoNewTaskPosition is not (TodoNewTaskPositionTop or TodoNewTaskPositionBottom))
        {
            settings.TodoNewTaskPosition = TodoNewTaskPositionTop;
            changed = true;
        }

        if (settings.TodoDefaultFilter is not (
            TodoDefaultFilterAll or
            TodoDefaultFilterActive or
            TodoDefaultFilterToday or
            TodoDefaultFilterThisWeek or
            TodoDefaultFilterThisMonth or
            TodoDefaultFilterImportant or
            TodoDefaultFilterCompleted))
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
        bool legacyUseWideDetailPane = true)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return legacyUseWideDetailPane
                ? TodoLayoutModeAuto
                : TodoLayoutModeSinglePane;
        }

        if (string.Equals(mode, TodoLayoutModeSinglePane, StringComparison.OrdinalIgnoreCase))
        {
            return TodoLayoutModeSinglePane;
        }

        if (string.Equals(mode, TodoLayoutModeDualPane, StringComparison.OrdinalIgnoreCase))
        {
            return TodoLayoutModeDualPane;
        }

        return TodoLayoutModeAuto;
    }

    public static int NormalizeItemPreviewLineCount(int lineCount) =>
        Math.Clamp(lineCount, MinItemPreviewLineCount, MaxItemPreviewLineCount);

    public static string NormalizeEditorEnterBehavior(string? behavior) =>
        string.Equals(
            behavior,
            EditorEnterBehaviorEnterSaves,
            StringComparison.OrdinalIgnoreCase)
            ? EditorEnterBehaviorEnterSaves
            : EditorEnterBehaviorCtrlEnterSaves;

    public static bool ShouldSubmitEditorOnEnter(string? behavior, bool controlPressed) =>
        NormalizeEditorEnterBehavior(behavior) == EditorEnterBehaviorEnterSaves
            ? !controlPressed
            : controlPressed;

    public static string NormalizeWidgetTabStyle(string? style)
    {
        return style == WidgetTabStylePivot
            ? WidgetTabStylePivot
            : WidgetTabStyleButton;
    }

    public static string NormalizeQuickCaptureFormat(string? format) =>
        string.Equals(format, QuickCaptureFormatPlainText, StringComparison.OrdinalIgnoreCase)
            ? QuickCaptureFormatPlainText
            : QuickCaptureFormatMarkdown;

    public static TextContentFormat ResolveQuickCaptureEditorContentFormat(string? format) =>
        NormalizeQuickCaptureFormat(format) == QuickCaptureFormatPlainText
            ? TextContentFormat.PlainText
            : TextContentFormat.Markdown;

    public static string NormalizeQuickCaptureWideLayout(string? layout)
    {
        if (string.Equals(layout, QuickCaptureWideLayoutSinglePane, StringComparison.OrdinalIgnoreCase))
        {
            return QuickCaptureWideLayoutSinglePane;
        }

        return string.Equals(layout, QuickCaptureWideLayoutDualPane, StringComparison.OrdinalIgnoreCase)
            ? QuickCaptureWideLayoutDualPane
            : QuickCaptureWideLayoutAuto;
    }

    public static string NormalizeQuickCaptureWideOpenMode(string? mode) =>
        string.Equals(mode, QuickCaptureWideOpenEditing, StringComparison.OrdinalIgnoreCase)
            ? QuickCaptureWideOpenEditing
            : QuickCaptureWideOpenReading;

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

    public static bool IsTodoTabVisible(AppSettings settings, string? filter) => filter switch
    {
        TodoDefaultFilterActive => settings.TodoShowActiveTab,
        TodoDefaultFilterToday => settings.TodoShowTodayTab,
        TodoDefaultFilterThisWeek => settings.TodoShowThisWeekTab,
        TodoDefaultFilterThisMonth => settings.TodoShowThisMonthTab,
        TodoDefaultFilterImportant => settings.TodoShowImportantTab,
        TodoDefaultFilterCompleted => settings.TodoShowCompletedTab,
        _ => settings.TodoShowAllTab
    };

    public static string GetFirstVisibleTodoTab(AppSettings settings)
    {
        if (settings.TodoShowAllTab) return TodoDefaultFilterAll;
        if (settings.TodoShowActiveTab) return TodoDefaultFilterActive;
        if (settings.TodoShowTodayTab) return TodoDefaultFilterToday;
        if (settings.TodoShowThisWeekTab) return TodoDefaultFilterThisWeek;
        if (settings.TodoShowThisMonthTab) return TodoDefaultFilterThisMonth;
        if (settings.TodoShowImportantTab) return TodoDefaultFilterImportant;
        if (settings.TodoShowCompletedTab) return TodoDefaultFilterCompleted;
        return TodoDefaultFilterAll;
    }

    public static int NormalizeTodoReminderOffsetMinutes(int minutes)
    {
        return minutes is 0 or 5 or 10 or 15 or 30 or 60 or 1440
            ? minutes
            : DefaultTodoReminderOffsetMinutes;
    }

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
