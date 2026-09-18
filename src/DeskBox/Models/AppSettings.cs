using System.Text.Json.Serialization;

namespace DeskBox.Models;

/// <summary>
/// Root settings object that is serialized to/from the JSON config file.
/// State lives in per-domain <c>*SettingsSlice</c> objects; the flat
/// properties below are the serialization facade and the legacy access
/// surface. Declaration order here defines the on-disk member order.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Settings schema version for migration purposes.
    /// New or legacy settings without this field begin at version 1 and are
    /// advanced by <see cref="DeskBox.Services.SettingsMigrationPipeline"/>.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    // ─── Ownership slices (never serialized directly) ───────────────

    [JsonIgnore]
    public CoreSettingsSlice Core { get; } = new();

    [JsonIgnore]
    public PerformanceSettingsSlice Performance { get; } = new();

    [JsonIgnore]
    public QuickCaptureSettingsSlice QuickCapture { get; } = new();

    [JsonIgnore]
    public TodoSettingsSlice Todo { get; } = new();

    [JsonIgnore]
    public MusicSettingsSlice Music { get; } = new();

    [JsonIgnore]
    public WidgetShellSettingsSlice WidgetShell { get; } = new();

    [JsonIgnore]
    public FileWidgetSettingsSlice FileWidget { get; } = new();

    [JsonIgnore]
    public BackupSettingsSlice Backup { get; } = new();

    [JsonIgnore]
    public CloudBackupSettingsSlice CloudBackup { get; } = new();

    [JsonIgnore]
    public DesktopOrganizationSettingsSlice DesktopOrganization { get; } = new();

    [JsonIgnore]
    public WidgetLayoutSettingsSlice WidgetLayout { get; } = new();

    [JsonIgnore]
    public WeatherSettingsSlice Weather { get; } = new();

    [JsonIgnore]
    public SearchSettingsSlice Search { get; } = new();

    // ─── Serialization facade (wire contract, order-significant) ──

    /// <inheritdoc cref="CoreSettingsSlice.Theme"/>
    public string Theme { get => Core.Theme; set => Core.Theme = value; }

    /// <inheritdoc cref="CoreSettingsSlice.TrayIconStyle"/>
    public string TrayIconStyle { get => Core.TrayIconStyle; set => Core.TrayIconStyle = value; }

    /// <inheritdoc cref="CoreSettingsSlice.Language"/>
    public string Language { get => Core.Language; set => Core.Language = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AccentColorMode"/>
    public string AccentColorMode { get => Core.AccentColorMode; set => Core.AccentColorMode = value; }

    /// <inheritdoc cref="CoreSettingsSlice.CustomAccentColor"/>
    public string CustomAccentColor { get => Core.CustomAccentColor; set => Core.CustomAccentColor = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AutoStart"/>
    public bool AutoStart { get => Core.AutoStart; set => Core.AutoStart = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AutoStartDefaultApplied"/>
    public bool AutoStartDefaultApplied { get => Core.AutoStartDefaultApplied; set => Core.AutoStartDefaultApplied = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AutoStartMode"/>
    public StartupMode? AutoStartMode { get => Core.AutoStartMode; set => Core.AutoStartMode = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.PerformanceMode"/>
    public string PerformanceMode { get => Performance.PerformanceMode; set => Performance.PerformanceMode = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.HiddenCacheCleanupDelaySeconds"/>
    public int HiddenCacheCleanupDelaySeconds { get => Performance.HiddenCacheCleanupDelaySeconds; set => Performance.HiddenCacheCleanupDelaySeconds = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.HiddenCacheCleanupScope"/>
    public string HiddenCacheCleanupScope { get => Performance.HiddenCacheCleanupScope; set => Performance.HiddenCacheCleanupScope = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.VisibleIdleCacheCleanupDelaySeconds"/>
    public int VisibleIdleCacheCleanupDelaySeconds { get => Performance.VisibleIdleCacheCleanupDelaySeconds; set => Performance.VisibleIdleCacheCleanupDelaySeconds = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.IdleWorkingSetTrimEnabled"/>
    public bool IdleWorkingSetTrimEnabled { get => Performance.IdleWorkingSetTrimEnabled; set => Performance.IdleWorkingSetTrimEnabled = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.ImmediateHiddenWorkingSetTrimEnabled"/>
    public bool ImmediateHiddenWorkingSetTrimEnabled { get => Performance.ImmediateHiddenWorkingSetTrimEnabled; set => Performance.ImmediateHiddenWorkingSetTrimEnabled = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.TransientWindowReleaseDelaySeconds"/>
    public int TransientWindowReleaseDelaySeconds { get => Performance.TransientWindowReleaseDelaySeconds; set => Performance.TransientWindowReleaseDelaySeconds = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.PerformanceCacheBudget"/>
    public string PerformanceCacheBudget { get => Performance.PerformanceCacheBudget; set => Performance.PerformanceCacheBudget = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableContinuousDecorativeAnimations"/>
    public bool EnableContinuousDecorativeAnimations { get => Performance.EnableContinuousDecorativeAnimations; set => Performance.EnableContinuousDecorativeAnimations = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableTextMarqueeAnimations"/>
    public bool EnableTextMarqueeAnimations { get => Performance.EnableTextMarqueeAnimations; set => Performance.EnableTextMarqueeAnimations = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableVinylRotationAnimations"/>
    public bool EnableVinylRotationAnimations { get => Performance.EnableVinylRotationAnimations; set => Performance.EnableVinylRotationAnimations = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableGlanceImageAutoRotation"/>
    public bool EnableGlanceImageAutoRotation { get => Performance.EnableGlanceImageAutoRotation; set => Performance.EnableGlanceImageAutoRotation = value; }

    /// <inheritdoc cref="PerformanceSettingsSlice.EnableCompactAmbientAnimations"/>
    public bool EnableCompactAmbientAnimations { get => Performance.EnableCompactAmbientAnimations; set => Performance.EnableCompactAmbientAnimations = value; }

    /// <inheritdoc cref="CoreSettingsSlice.AutoCheckForUpdates"/>
    public bool AutoCheckForUpdates { get => Core.AutoCheckForUpdates; set => Core.AutoCheckForUpdates = value; }

    /// <inheritdoc cref="CoreSettingsSlice.LastUpdateCheckAt"/>
    public DateTimeOffset? LastUpdateCheckAt { get => Core.LastUpdateCheckAt; set => Core.LastUpdateCheckAt = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureEnabled"/>
    public bool QuickCaptureEnabled { get => QuickCapture.QuickCaptureEnabled; set => QuickCapture.QuickCaptureEnabled = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoEnabled"/>
    public bool TodoEnabled { get => Todo.TodoEnabled; set => Todo.TodoEnabled = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.FeatureWidgetEnabledStates"/>
    public Dictionary<string, bool> FeatureWidgetEnabledStates { get => WidgetLayout.FeatureWidgetEnabledStates; set => WidgetLayout.FeatureWidgetEnabledStates = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureClipboardEnabled"/>
    public bool QuickCaptureClipboardEnabled { get => QuickCapture.QuickCaptureClipboardEnabled; set => QuickCapture.QuickCaptureClipboardEnabled = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureImageClipboardEnabled"/>
    public bool QuickCaptureImageClipboardEnabled { get => QuickCapture.QuickCaptureImageClipboardEnabled; set => QuickCapture.QuickCaptureImageClipboardEnabled = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureRecentLimit"/>
    public int QuickCaptureRecentLimit { get => QuickCapture.QuickCaptureRecentLimit; set => QuickCapture.QuickCaptureRecentLimit = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureShowCreatedTime"/>
    public bool QuickCaptureShowCreatedTime { get => QuickCapture.QuickCaptureShowCreatedTime; set => QuickCapture.QuickCaptureShowCreatedTime = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureItemPreviewLineCount"/>
    public int QuickCaptureItemPreviewLineCount { get => QuickCapture.QuickCaptureItemPreviewLineCount; set => QuickCapture.QuickCaptureItemPreviewLineCount = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureListTextSize"/>
    public double QuickCaptureListTextSize { get => QuickCapture.QuickCaptureListTextSize; set => QuickCapture.QuickCaptureListTextSize = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureContentTextSize"/>
    public double QuickCaptureContentTextSize { get => QuickCapture.QuickCaptureContentTextSize; set => QuickCapture.QuickCaptureContentTextSize = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureEditorEnterBehavior"/>
    public string QuickCaptureEditorEnterBehavior { get => QuickCapture.QuickCaptureEditorEnterBehavior; set => QuickCapture.QuickCaptureEditorEnterBehavior = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureDefaultFormat"/>
    public string QuickCaptureDefaultFormat { get => QuickCapture.QuickCaptureDefaultFormat; set => QuickCapture.QuickCaptureDefaultFormat = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureWideLayout"/>
    public string QuickCaptureWideLayout { get => QuickCapture.QuickCaptureWideLayout; set => QuickCapture.QuickCaptureWideLayout = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureWideOpenMode"/>
    public string QuickCaptureWideOpenMode { get => QuickCapture.QuickCaptureWideOpenMode; set => QuickCapture.QuickCaptureWideOpenMode = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureAllowRemoteImages"/>
    public bool QuickCaptureAllowRemoteImages { get => QuickCapture.QuickCaptureAllowRemoteImages; set => QuickCapture.QuickCaptureAllowRemoteImages = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.AttachmentStorageMode"/>
    public string AttachmentStorageMode { get => QuickCapture.AttachmentStorageMode; set => QuickCapture.AttachmentStorageMode = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureDefaultView"/>
    public string QuickCaptureDefaultView { get => QuickCapture.QuickCaptureDefaultView; set => QuickCapture.QuickCaptureDefaultView = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureTabStyle"/>
    public string QuickCaptureTabStyle { get => QuickCapture.QuickCaptureTabStyle; set => QuickCapture.QuickCaptureTabStyle = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureShowTabBar"/>
    public bool QuickCaptureShowTabBar { get => QuickCapture.QuickCaptureShowTabBar; set => QuickCapture.QuickCaptureShowTabBar = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureShowRecordsTab"/>
    public bool QuickCaptureShowRecordsTab { get => QuickCapture.QuickCaptureShowRecordsTab; set => QuickCapture.QuickCaptureShowRecordsTab = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureShowPinnedTab"/>
    public bool QuickCaptureShowPinnedTab { get => QuickCapture.QuickCaptureShowPinnedTab; set => QuickCapture.QuickCaptureShowPinnedTab = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.QuickCaptureShowRecentTab"/>
    public bool QuickCaptureShowRecentTab { get => QuickCapture.QuickCaptureShowRecentTab; set => QuickCapture.QuickCaptureShowRecentTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoNewTaskPosition"/>
    public string TodoNewTaskPosition { get => Todo.TodoNewTaskPosition; set => Todo.TodoNewTaskPosition = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoTabStyle"/>
    public string TodoTabStyle { get => Todo.TodoTabStyle; set => Todo.TodoTabStyle = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowTabBar"/>
    public bool TodoShowTabBar { get => Todo.TodoShowTabBar; set => Todo.TodoShowTabBar = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowAllTab"/>
    public bool TodoShowAllTab { get => Todo.TodoShowAllTab; set => Todo.TodoShowAllTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowActiveTab"/>
    public bool TodoShowActiveTab { get => Todo.TodoShowActiveTab; set => Todo.TodoShowActiveTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowTodayTab"/>
    public bool TodoShowTodayTab { get => Todo.TodoShowTodayTab; set => Todo.TodoShowTodayTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowThisWeekTab"/>
    public bool TodoShowThisWeekTab { get => Todo.TodoShowThisWeekTab; set => Todo.TodoShowThisWeekTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowThisMonthTab"/>
    public bool TodoShowThisMonthTab { get => Todo.TodoShowThisMonthTab; set => Todo.TodoShowThisMonthTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowImportantTab"/>
    public bool TodoShowImportantTab { get => Todo.TodoShowImportantTab; set => Todo.TodoShowImportantTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowCompletedTab"/>
    public bool TodoShowCompletedTab { get => Todo.TodoShowCompletedTab; set => Todo.TodoShowCompletedTab = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoDefaultFilter"/>
    public string TodoDefaultFilter { get => Todo.TodoDefaultFilter; set => Todo.TodoDefaultFilter = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowCompletedTasks"/>
    public bool TodoShowCompletedTasks { get => Todo.TodoShowCompletedTasks; set => Todo.TodoShowCompletedTasks = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoItemPreviewLineCount"/>
    public int TodoItemPreviewLineCount { get => Todo.TodoItemPreviewLineCount; set => Todo.TodoItemPreviewLineCount = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoListTextSize"/>
    public double TodoListTextSize { get => Todo.TodoListTextSize; set => Todo.TodoListTextSize = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoContentTextSize"/>
    public double TodoContentTextSize { get => Todo.TodoContentTextSize; set => Todo.TodoContentTextSize = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoEditorEnterBehavior"/>
    public string TodoEditorEnterBehavior { get => Todo.TodoEditorEnterBehavior; set => Todo.TodoEditorEnterBehavior = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowFooterStats"/>
    public bool TodoShowFooterStats { get => Todo.TodoShowFooterStats; set => Todo.TodoShowFooterStats = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoShowClearCompletedButton"/>
    public bool TodoShowClearCompletedButton { get => Todo.TodoShowClearCompletedButton; set => Todo.TodoShowClearCompletedButton = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoReminderEnabled"/>
    public bool TodoReminderEnabled { get => Todo.TodoReminderEnabled; set => Todo.TodoReminderEnabled = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoDefaultReminderOffsetMinutes"/>
    public int TodoDefaultReminderOffsetMinutes { get => Todo.TodoDefaultReminderOffsetMinutes; set => Todo.TodoDefaultReminderOffsetMinutes = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoUseWideDetailPane"/>
    public bool TodoUseWideDetailPane { get => Todo.TodoUseWideDetailPane; set => Todo.TodoUseWideDetailPane = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoLayoutMode"/>
    public string TodoLayoutMode { get => Todo.TodoLayoutMode; set => Todo.TodoLayoutMode = value; }

    /// <inheritdoc cref="TodoSettingsSlice.TodoAutoSelectFirstInWideLayout"/>
    public bool TodoAutoSelectFirstInWideLayout { get => Todo.TodoAutoSelectFirstInWideLayout; set => Todo.TodoAutoSelectFirstInWideLayout = value; }

    /// <inheritdoc cref="MusicSettingsSlice.MusicUseArtworkBackdrop"/>
    public bool MusicUseArtworkBackdrop { get => Music.MusicUseArtworkBackdrop; set => Music.MusicUseArtworkBackdrop = value; }

    /// <inheritdoc cref="MusicSettingsSlice.MusicEnableCoverHoverMotion"/>
    public bool MusicEnableCoverHoverMotion { get => Music.MusicEnableCoverHoverMotion; set => Music.MusicEnableCoverHoverMotion = value; }

    /// <inheritdoc cref="MusicSettingsSlice.MusicDisplayMode"/>
    public string MusicDisplayMode { get => Music.MusicDisplayMode; set => Music.MusicDisplayMode = value; }

    /// <inheritdoc cref="QuickCaptureSettingsSlice.LastQuickCaptureFileWidgetId"/>
    public string LastQuickCaptureFileWidgetId { get => QuickCapture.LastQuickCaptureFileWidgetId; set => QuickCapture.LastQuickCaptureFileWidgetId = value; }

    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyEnabled"/>
    public bool GlobalHotkeyEnabled { get => Core.GlobalHotkeyEnabled; set => Core.GlobalHotkeyEnabled = value; }

    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyActivationKind"/>
    public HotkeyActivationKind GlobalHotkeyActivationKind { get => Core.GlobalHotkeyActivationKind; set => Core.GlobalHotkeyActivationKind = value; }

    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyModifiers"/>
    public int GlobalHotkeyModifiers { get => Core.GlobalHotkeyModifiers; set => Core.GlobalHotkeyModifiers = value; }

    /// <inheritdoc cref="CoreSettingsSlice.GlobalHotkeyKey"/>
    public int GlobalHotkeyKey { get => Core.GlobalHotkeyKey; set => Core.GlobalHotkeyKey = value; }

    /// <inheritdoc cref="CoreSettingsSlice.DesktopDoubleClickEnabled"/>
    public bool DesktopDoubleClickEnabled { get => Core.DesktopDoubleClickEnabled; set => Core.DesktopDoubleClickEnabled = value; }

    /// <inheritdoc cref="CoreSettingsSlice.HasCompletedOnboarding"/>
    public bool HasCompletedOnboarding { get => Core.HasCompletedOnboarding; set => Core.HasCompletedOnboarding = value; }

    /// <inheritdoc cref="CoreSettingsSlice.OnboardingStepIndex"/>
    public int OnboardingStepIndex { get => Core.OnboardingStepIndex; set => Core.OnboardingStepIndex = value; }

    /// <inheritdoc cref="CoreSettingsSlice.CompletedOnboardingVersion"/>
    public int CompletedOnboardingVersion { get => Core.CompletedOnboardingVersion; set => Core.CompletedOnboardingVersion = value; }

    /// <inheritdoc cref="CoreSettingsSlice.HasResolvedInitialFileWidgetSetup"/>
    public bool HasResolvedInitialFileWidgetSetup { get => Core.HasResolvedInitialFileWidgetSetup; set => Core.HasResolvedInitialFileWidgetSetup = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.DefaultWidgetWidth"/>
    public double DefaultWidgetWidth { get => WidgetShell.DefaultWidgetWidth; set => WidgetShell.DefaultWidgetWidth = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.DefaultWidgetHeight"/>
    public double DefaultWidgetHeight { get => WidgetShell.DefaultWidgetHeight; set => WidgetShell.DefaultWidgetHeight = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetOpacity"/>
    public double WidgetOpacity { get => WidgetShell.WidgetOpacity; set => WidgetShell.WidgetOpacity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetMaterialType"/>
    public string WidgetMaterialType { get => WidgetShell.WidgetMaterialType; set => WidgetShell.WidgetMaterialType = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetMaterialIntensity"/>
    public double WidgetMaterialIntensity { get => WidgetShell.WidgetMaterialIntensity; set => WidgetShell.WidgetMaterialIntensity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetForegroundMode"/>
    public string WidgetForegroundMode { get => WidgetShell.WidgetForegroundMode; set => WidgetShell.WidgetForegroundMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetForegroundColor"/>
    public string WidgetForegroundColor { get => WidgetShell.WidgetForegroundColor; set => WidgetShell.WidgetForegroundColor = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBorderColorMode"/>
    public string WidgetBorderColorMode { get => WidgetShell.WidgetBorderColorMode; set => WidgetShell.WidgetBorderColorMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetBorderStyle"/>
    public string WidgetBorderStyle { get => WidgetShell.WidgetBorderStyle; set => WidgetShell.WidgetBorderStyle = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCornerPreference"/>
    public string WidgetCornerPreference { get => WidgetShell.WidgetCornerPreference; set => WidgetShell.WidgetCornerPreference = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationEffect"/>
    public string WidgetAnimationEffect { get => WidgetShell.WidgetAnimationEffect; set => WidgetShell.WidgetAnimationEffect = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationSpeed"/>
    public string WidgetAnimationSpeed { get => WidgetShell.WidgetAnimationSpeed; set => WidgetShell.WidgetAnimationSpeed = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationSlideDirection"/>
    public string WidgetAnimationSlideDirection { get => WidgetShell.WidgetAnimationSlideDirection; set => WidgetShell.WidgetAnimationSlideDirection = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetAnimationEasingIntensity"/>
    public string WidgetAnimationEasingIntensity { get => WidgetShell.WidgetAnimationEasingIntensity; set => WidgetShell.WidgetAnimationEasingIntensity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetLayerMode"/>
    public string WidgetLayerMode { get => WidgetShell.WidgetLayerMode; set => WidgetShell.WidgetLayerMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.KeepWidgetsVisibleOnShowDesktop"/>
    public bool KeepWidgetsVisibleOnShowDesktop { get => WidgetShell.KeepWidgetsVisibleOnShowDesktop; set => WidgetShell.KeepWidgetsVisibleOnShowDesktop = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.DisplayWidgetChromeMode"/>
    public string DisplayWidgetChromeMode { get => WidgetShell.DisplayWidgetChromeMode; set => WidgetShell.DisplayWidgetChromeMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.InteractiveWidgetChromeMode"/>
    public string InteractiveWidgetChromeMode { get => WidgetShell.InteractiveWidgetChromeMode; set => WidgetShell.InteractiveWidgetChromeMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCollapseBehavior"/>
    public string WidgetCollapseBehavior { get => WidgetShell.WidgetCollapseBehavior; set => WidgetShell.WidgetCollapseBehavior = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.LegacyWidgetCapsuleModeEnabled"/>
    [JsonPropertyName("widgetCapsuleModeEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyWidgetCapsuleModeEnabled { get => WidgetShell.LegacyWidgetCapsuleModeEnabled; set => WidgetShell.LegacyWidgetCapsuleModeEnabled = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactWidthMode"/>
    public string WidgetCompactWidthMode { get => WidgetShell.WidgetCompactWidthMode; set => WidgetShell.WidgetCompactWidthMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactExpansionDirection"/>
    public string WidgetCompactExpansionDirection { get => WidgetShell.WidgetCompactExpansionDirection; set => WidgetShell.WidgetCompactExpansionDirection = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleArrangementMode"/>
    public string WidgetCapsuleArrangementMode { get => WidgetShell.WidgetCapsuleArrangementMode; set => WidgetShell.WidgetCapsuleArrangementMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarSpacing"/>
    public double WidgetCapsuleBarSpacing { get => WidgetShell.WidgetCapsuleBarSpacing; set => WidgetShell.WidgetCapsuleBarSpacing = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarPlacement"/>
    public string WidgetCapsuleBarPlacement { get => WidgetShell.WidgetCapsuleBarPlacement; set => WidgetShell.WidgetCapsuleBarPlacement = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarDirection"/>
    public string WidgetCapsuleBarDirection { get => WidgetShell.WidgetCapsuleBarDirection; set => WidgetShell.WidgetCapsuleBarDirection = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleBarOrder"/>
    public List<string> WidgetCapsuleBarOrder { get => WidgetShell.WidgetCapsuleBarOrder; set => WidgetShell.WidgetCapsuleBarOrder = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCapsuleFreePlacements"/>
    public Dictionary<string, WidgetCompactPlacement> WidgetCapsuleFreePlacements { get => WidgetShell.WidgetCapsuleFreePlacements; set => WidgetShell.WidgetCapsuleFreePlacements = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCollapsedStyle"/>
    public string WidgetCollapsedStyle { get => WidgetShell.WidgetCollapsedStyle; set => WidgetShell.WidgetCollapsedStyle = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactContentMode"/>
    public string WidgetCompactContentMode { get => WidgetShell.WidgetCompactContentMode; set => WidgetShell.WidgetCompactContentMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactHideSensitiveContent"/>
    public bool WidgetCompactHideSensitiveContent { get => WidgetShell.WidgetCompactHideSensitiveContent; set => WidgetShell.WidgetCompactHideSensitiveContent = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactSettingsVersion"/>
    public int WidgetCompactSettingsVersion { get => WidgetShell.WidgetCompactSettingsVersion; set => WidgetShell.WidgetCompactSettingsVersion = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactAnimationEffect"/>
    public string WidgetCompactAnimationEffect { get => WidgetShell.WidgetCompactAnimationEffect; set => WidgetShell.WidgetCompactAnimationEffect = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactAnimationDurationMs"/>
    public int WidgetCompactAnimationDurationMs { get => WidgetShell.WidgetCompactAnimationDurationMs; set => WidgetShell.WidgetCompactAnimationDurationMs = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactExpandDelayMs"/>
    public int WidgetCompactExpandDelayMs { get => WidgetShell.WidgetCompactExpandDelayMs; set => WidgetShell.WidgetCompactExpandDelayMs = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactCollapseDelayMs"/>
    public int WidgetCompactCollapseDelayMs { get => WidgetShell.WidgetCompactCollapseDelayMs; set => WidgetShell.WidgetCompactCollapseDelayMs = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetCompactMediaCornerMode"/>
    public string WidgetCompactMediaCornerMode { get => WidgetShell.WidgetCompactMediaCornerMode; set => WidgetShell.WidgetCompactMediaCornerMode = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetTitleIconMode"/>
    public string WidgetTitleIconMode { get => WidgetShell.WidgetTitleIconMode; set => WidgetShell.WidgetTitleIconMode = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.DoubleClickToOpen"/>
    public bool DoubleClickToOpen { get => FileWidget.DoubleClickToOpen; set => FileWidget.DoubleClickToOpen = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileWidgetFolderOpenBehavior"/>
    public string FileWidgetFolderOpenBehavior { get => FileWidget.FileWidgetFolderOpenBehavior; set => FileWidget.FileWidgetFolderOpenBehavior = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileItemSystemContextMenuEnabled"/>
    public bool FileItemSystemContextMenuEnabled { get => FileWidget.FileItemSystemContextMenuEnabled; set => FileWidget.FileItemSystemContextMenuEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.HideShortcutArrowOverlay"/>
    public bool HideShortcutArrowOverlay { get => FileWidget.HideShortcutArrowOverlay; set => FileWidget.HideShortcutArrowOverlay = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowImageFilesAsIcons"/>
    public bool ShowImageFilesAsIcons { get => FileWidget.ShowImageFilesAsIcons; set => FileWidget.ShowImageFilesAsIcons = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.ShowHoverButtons"/>
    public bool ShowHoverButtons { get => WidgetShell.ShowHoverButtons; set => WidgetShell.ShowHoverButtons = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetHoverButtonActions"/>
    public string WidgetHoverButtonActions { get => WidgetShell.WidgetHoverButtonActions; set => WidgetShell.WidgetHoverButtonActions = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.ResizeSnapEnabled"/>
    public bool ResizeSnapEnabled { get => WidgetShell.ResizeSnapEnabled; set => WidgetShell.ResizeSnapEnabled = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.WidgetSnapSpacing"/>
    public double WidgetSnapSpacing { get => WidgetShell.WidgetSnapSpacing; set => WidgetShell.WidgetSnapSpacing = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowListItemDetails"/>
    public bool ShowListItemDetails { get => FileWidget.ShowListItemDetails; set => FileWidget.ShowListItemDetails = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowFileItemPathTooltips"/>
    public bool ShowFileItemPathTooltips { get => FileWidget.ShowFileItemPathTooltips; set => FileWidget.ShowFileItemPathTooltips = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStacksEnabled"/>
    public bool FileStacksEnabled { get => FileWidget.FileStacksEnabled; set => FileWidget.FileStacksEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackAutoStacking"/>
    public bool FileStackAutoStacking { get => FileWidget.FileStackAutoStacking; set => FileWidget.FileStackAutoStacking = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackGroupBy"/>
    public string FileStackGroupBy { get => FileWidget.FileStackGroupBy; set => FileWidget.FileStackGroupBy = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackThreshold"/>
    public int FileStackThreshold { get => FileWidget.FileStackThreshold; set => FileWidget.FileStackThreshold = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackOrderBy"/>
    public string FileStackOrderBy { get => FileWidget.FileStackOrderBy; set => FileWidget.FileStackOrderBy = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackOpenMode"/>
    public string FileStackOpenMode { get => FileWidget.FileStackOpenMode; set => FileWidget.FileStackOpenMode = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackPopoverLayout"/>
    public string FileStackPopoverLayout { get => FileWidget.FileStackPopoverLayout; set => FileWidget.FileStackPopoverLayout = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackPopoverStyle"/>
    public string FileStackPopoverStyle { get => FileWidget.FileStackPopoverStyle; set => FileWidget.FileStackPopoverStyle = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackCustomRules"/>
    public List<FileStackCustomRule> FileStackCustomRules { get => FileWidget.FileStackCustomRules; set => FileWidget.FileStackCustomRules = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileStackUnmatchedBehavior"/>
    public string FileStackUnmatchedBehavior { get => FileWidget.FileStackUnmatchedBehavior; set => FileWidget.FileStackUnmatchedBehavior = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ManagedDropAction"/>
    public string ManagedDropAction { get => FileWidget.ManagedDropAction; set => FileWidget.ManagedDropAction = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.DefaultManagedStorageRootPath"/>
    public string DefaultManagedStorageRootPath { get => FileWidget.DefaultManagedStorageRootPath; set => FileWidget.DefaultManagedStorageRootPath = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ManagedStorageDesktopShortcutEnabled"/>
    public bool ManagedStorageDesktopShortcutEnabled { get => FileWidget.ManagedStorageDesktopShortcutEnabled; set => FileWidget.ManagedStorageDesktopShortcutEnabled = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ManagedStorageDesktopShortcutPath"/>
    public string ManagedStorageDesktopShortcutPath { get => FileWidget.ManagedStorageDesktopShortcutPath; set => FileWidget.ManagedStorageDesktopShortcutPath = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupEnabled"/>
    public bool AutomaticBackupEnabled { get => Backup.AutomaticBackupEnabled; set => Backup.AutomaticBackupEnabled = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupIntervalMinutes"/>
    public int AutomaticBackupIntervalMinutes { get => Backup.AutomaticBackupIntervalMinutes; set => Backup.AutomaticBackupIntervalMinutes = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupRetentionCount"/>
    public int AutomaticBackupRetentionCount { get => Backup.AutomaticBackupRetentionCount; set => Backup.AutomaticBackupRetentionCount = value; }

    /// <inheritdoc cref="BackupSettingsSlice.AutomaticBackupDirectory"/>
    public string AutomaticBackupDirectory { get => Backup.AutomaticBackupDirectory; set => Backup.AutomaticBackupDirectory = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.RecentOrganizationHistory"/>
    public List<OrganizationHistoryEntry> RecentOrganizationHistory { get => DesktopOrganization.RecentOrganizationHistory; set => DesktopOrganization.RecentOrganizationHistory = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.DesktopOrganizationRules"/>
    public List<DesktopOrganizationRule> DesktopOrganizationRules { get => DesktopOrganization.DesktopOrganizationRules; set => DesktopOrganization.DesktopOrganizationRules = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.DesktopAutoOrganizationEnabled"/>
    public bool DesktopAutoOrganizationEnabled { get => DesktopOrganization.DesktopAutoOrganizationEnabled; set => DesktopOrganization.DesktopAutoOrganizationEnabled = value; }

    /// <inheritdoc cref="DesktopOrganizationSettingsSlice.DesktopAutoOrganizationBaselineUtc"/>
    public DateTimeOffset? DesktopAutoOrganizationBaselineUtc { get => DesktopOrganization.DesktopAutoOrganizationBaselineUtc; set => DesktopOrganization.DesktopAutoOrganizationBaselineUtc = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.IconSize"/>
    public double IconSize { get => WidgetShell.IconSize; set => WidgetShell.IconSize = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.TextSize"/>
    public double TextSize { get => WidgetShell.TextSize; set => WidgetShell.TextSize = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.LayoutDensity"/>
    public string LayoutDensity { get => WidgetShell.LayoutDensity; set => WidgetShell.LayoutDensity = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.LayoutDensityScale"/>
    public double LayoutDensityScale { get => WidgetShell.LayoutDensityScale; set => WidgetShell.LayoutDensityScale = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.HorizontalSpacingScale"/>
    public double HorizontalSpacingScale { get => WidgetShell.HorizontalSpacingScale; set => WidgetShell.HorizontalSpacingScale = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.VerticalSpacingScale"/>
    public double VerticalSpacingScale { get => WidgetShell.VerticalSpacingScale; set => WidgetShell.VerticalSpacingScale = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileNameWidthScale"/>
    public double FileNameWidthScale { get => FileWidget.FileNameWidthScale; set => FileWidget.FileNameWidthScale = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.FileNameLineCount"/>
    public int FileNameLineCount { get => FileWidget.FileNameLineCount; set => FileWidget.FileNameLineCount = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.ShowFileExtensions"/>
    public bool ShowFileExtensions { get => FileWidget.ShowFileExtensions; set => FileWidget.ShowFileExtensions = value; }

    /// <inheritdoc cref="FileWidgetSettingsSlice.HideShortcutExtensionWhenShowingFileExtensions"/>
    public bool HideShortcutExtensionWhenShowingFileExtensions { get => FileWidget.HideShortcutExtensionWhenShowingFileExtensions; set => FileWidget.HideShortcutExtensionWhenShowingFileExtensions = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.Widgets"/>
    public List<WidgetConfig> Widgets { get => WidgetLayout.Widgets; set => WidgetLayout.Widgets = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroups"/>
    public List<WidgetGroupConfig> WidgetGroups { get => WidgetLayout.WidgetGroups; set => WidgetLayout.WidgetGroups = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetTopologyLayouts"/>
    public Dictionary<string, WidgetTopologyLayoutProfile> WidgetTopologyLayouts { get => WidgetLayout.WidgetTopologyLayouts; set => WidgetLayout.WidgetTopologyLayouts = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.ActiveWidgetTopologyKey"/>
    public string? ActiveWidgetTopologyKey { get => WidgetLayout.ActiveWidgetTopologyKey; set => WidgetLayout.ActiveWidgetTopologyKey = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupsEnabled"/>
    public bool WidgetGroupsEnabled { get => WidgetLayout.WidgetGroupsEnabled; set => WidgetLayout.WidgetGroupsEnabled = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupDefaultNavigationStyle"/>
    public string WidgetGroupDefaultNavigationStyle { get => WidgetLayout.WidgetGroupDefaultNavigationStyle; set => WidgetLayout.WidgetGroupDefaultNavigationStyle = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupDefaultTitleDisplayMode"/>
    public string WidgetGroupDefaultTitleDisplayMode { get => WidgetLayout.WidgetGroupDefaultTitleDisplayMode; set => WidgetLayout.WidgetGroupDefaultTitleDisplayMode = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupWheelSwitchEnabled"/>
    public bool WidgetGroupWheelSwitchEnabled { get => WidgetLayout.WidgetGroupWheelSwitchEnabled; set => WidgetLayout.WidgetGroupWheelSwitchEnabled = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.WidgetGroupHoverSwitchEnabled"/>
    public bool WidgetGroupHoverSwitchEnabled { get => WidgetLayout.WidgetGroupHoverSwitchEnabled; set => WidgetLayout.WidgetGroupHoverSwitchEnabled = value; }

    /// <inheritdoc cref="WidgetShellSettingsSlice.FocusClickedWidgetOnRaise"/>
    public bool FocusClickedWidgetOnRaise { get => WidgetShell.FocusClickedWidgetOnRaise; set => WidgetShell.FocusClickedWidgetOnRaise = value; }

    /// <inheritdoc cref="WidgetLayoutSettingsSlice.DeletedWidgetIds"/>
    public List<string> DeletedWidgetIds { get => WidgetLayout.DeletedWidgetIds; set => WidgetLayout.DeletedWidgetIds = value; }

    // ─── Weather Widget Settings ───────────────────────────────────
    /// <inheritdoc cref="WeatherSettingsSlice.WeatherAutoLocation"/>
    public bool WeatherAutoLocation { get => Weather.WeatherAutoLocation; set => Weather.WeatherAutoLocation = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherCityName"/>
    public string WeatherCityName { get => Weather.WeatherCityName; set => Weather.WeatherCityName = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherLatitude"/>
    public double WeatherLatitude { get => Weather.WeatherLatitude; set => Weather.WeatherLatitude = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherLongitude"/>
    public double WeatherLongitude { get => Weather.WeatherLongitude; set => Weather.WeatherLongitude = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherTemperatureUnit"/>
    public string WeatherTemperatureUnit { get => Weather.WeatherTemperatureUnit; set => Weather.WeatherTemperatureUnit = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherWindSpeedUnit"/>
    public string WeatherWindSpeedUnit { get => Weather.WeatherWindSpeedUnit; set => Weather.WeatherWindSpeedUnit = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherDataSource"/>
    public string WeatherDataSource { get => Weather.WeatherDataSource; set => Weather.WeatherDataSource = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherDefaultView"/>
    public string WeatherDefaultView { get => Weather.WeatherDefaultView; set => Weather.WeatherDefaultView = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherSkin"/>
    public string WeatherSkin { get => Weather.WeatherSkin; set => Weather.WeatherSkin = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherShowForecast"/>
    public bool WeatherShowForecast { get => Weather.WeatherShowForecast; set => Weather.WeatherShowForecast = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherShowSunrise"/>
    public bool WeatherShowSunrise { get => Weather.WeatherShowSunrise; set => Weather.WeatherShowSunrise = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherShowUvIndex"/>
    public bool WeatherShowUvIndex { get => Weather.WeatherShowUvIndex; set => Weather.WeatherShowUvIndex = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherShowPrecipitation"/>
    public bool WeatherShowPrecipitation { get => Weather.WeatherShowPrecipitation; set => Weather.WeatherShowPrecipitation = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherShowHumidity"/>
    public bool WeatherShowHumidity { get => Weather.WeatherShowHumidity; set => Weather.WeatherShowHumidity = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherShowWind"/>
    public bool WeatherShowWind { get => Weather.WeatherShowWind; set => Weather.WeatherShowWind = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherShowPressure"/>
    public bool WeatherShowPressure { get => Weather.WeatherShowPressure; set => Weather.WeatherShowPressure = value; }

    /// <inheritdoc cref="WeatherSettingsSlice.WeatherRefreshIntervalMinutes"/>
    public int WeatherRefreshIntervalMinutes { get => Weather.WeatherRefreshIntervalMinutes; set => Weather.WeatherRefreshIntervalMinutes = value; }

    // ─── Search Settings ───────────────────────────────────────────────
    /// <inheritdoc cref="SearchSettingsSlice.SearchHotkeyEnabled"/>
    public bool SearchHotkeyEnabled { get => Search.SearchHotkeyEnabled; set => Search.SearchHotkeyEnabled = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchHotkeyModifiers"/>
    public int SearchHotkeyModifiers { get => Search.SearchHotkeyModifiers; set => Search.SearchHotkeyModifiers = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchHotkeyKey"/>
    public int SearchHotkeyKey { get => Search.SearchHotkeyKey; set => Search.SearchHotkeyKey = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchDisplayMode"/>
    public string SearchDisplayMode { get => Search.SearchDisplayMode; set => Search.SearchDisplayMode = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchIncludeDeskBoxContent"/>
    public bool SearchIncludeDeskBoxContent { get => Search.SearchIncludeDeskBoxContent; set => Search.SearchIncludeDeskBoxContent = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchEverythingEnabled"/>
    public bool SearchEverythingEnabled { get => Search.SearchEverythingEnabled; set => Search.SearchEverythingEnabled = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchEverythingExecutablePath"/>
    public string SearchEverythingExecutablePath { get => Search.SearchEverythingExecutablePath; set => Search.SearchEverythingExecutablePath = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchEverythingAdvancedSyntaxEnabled"/>
    public bool SearchEverythingAdvancedSyntaxEnabled { get => Search.SearchEverythingAdvancedSyntaxEnabled; set => Search.SearchEverythingAdvancedSyntaxEnabled = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchShowRecommendations"/>
    public bool SearchShowRecommendations { get => Search.SearchShowRecommendations; set => Search.SearchShowRecommendations = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchSaveHistory"/>
    public bool SearchSaveHistory { get => Search.SearchSaveHistory; set => Search.SearchSaveHistory = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchMaxResults"/>
    public int SearchMaxResults { get => Search.SearchMaxResults; set => Search.SearchMaxResults = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchDefaultTab"/>
    public string SearchDefaultTab { get => Search.SearchDefaultTab; set => Search.SearchDefaultTab = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchAppIconAnimation"/>
    public int SearchAppIconAnimation { get => Search.SearchAppIconAnimation; set => Search.SearchAppIconAnimation = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchPopupCustomX"/>
    public int? SearchPopupCustomX { get => Search.SearchPopupCustomX; set => Search.SearchPopupCustomX = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchPopupCustomY"/>
    public int? SearchPopupCustomY { get => Search.SearchPopupCustomY; set => Search.SearchPopupCustomY = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchPopupCustomWidth"/>
    public int? SearchPopupCustomWidth { get => Search.SearchPopupCustomWidth; set => Search.SearchPopupCustomWidth = value; }

    /// <inheritdoc cref="SearchSettingsSlice.SearchPopupCustomHeight"/>
    public int? SearchPopupCustomHeight { get => Search.SearchPopupCustomHeight; set => Search.SearchPopupCustomHeight = value; }

    // ─── Cloud Backup (roadmap §10 — non-secret provider config only) ───

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupProvider"/>
    public string CloudBackupProvider { get => CloudBackup.CloudBackupProvider; set => CloudBackup.CloudBackupProvider = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupServerUrl"/>
    public string CloudBackupServerUrl { get => CloudBackup.CloudBackupServerUrl; set => CloudBackup.CloudBackupServerUrl = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupRemotePath"/>
    public string CloudBackupRemotePath { get => CloudBackup.CloudBackupRemotePath; set => CloudBackup.CloudBackupRemotePath = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupUsername"/>
    public string CloudBackupUsername { get => CloudBackup.CloudBackupUsername; set => CloudBackup.CloudBackupUsername = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupTodoDataEnabled"/>
    public bool CloudBackupTodoDataEnabled { get => CloudBackup.CloudBackupTodoDataEnabled; set => CloudBackup.CloudBackupTodoDataEnabled = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupQuickCaptureDataEnabled"/>
    public bool CloudBackupQuickCaptureDataEnabled { get => CloudBackup.CloudBackupQuickCaptureDataEnabled; set => CloudBackup.CloudBackupQuickCaptureDataEnabled = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupWidgetStyleEnabled"/>
    public bool CloudBackupWidgetStyleEnabled { get => CloudBackup.CloudBackupWidgetStyleEnabled; set => CloudBackup.CloudBackupWidgetStyleEnabled = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupRetentionCount"/>
    public int CloudBackupRetentionCount { get => CloudBackup.CloudBackupRetentionCount; set => CloudBackup.CloudBackupRetentionCount = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupIntervalMinutes"/>
    public int CloudBackupIntervalMinutes { get => CloudBackup.CloudBackupIntervalMinutes; set => CloudBackup.CloudBackupIntervalMinutes = value; }

    /// <inheritdoc cref="CloudBackupSettingsSlice.CloudBackupLastSuccessUtcTicks"/>
    public long CloudBackupLastSuccessUtcTicks { get => CloudBackup.CloudBackupLastSuccessUtcTicks; set => CloudBackup.CloudBackupLastSuccessUtcTicks = value; }
}
