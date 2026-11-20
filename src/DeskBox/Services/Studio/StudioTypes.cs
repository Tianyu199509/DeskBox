using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Services.Studio;

internal enum DiscardDecision
{
    KeepEditing,
    SaveDraftAndClose,
    DiscardCleanly
}

internal enum StudioPage
{
    Start,
    Waiting,
    Preview
}

internal enum WaitingKind
{
    None,
    Direct,
    AgentMcp
}

internal enum StudioAiChannelKind
{
    DirectApi,
    AgentMcp
}

internal enum StageBackdropMode
{
    Wallpaper,
    Light,
    Dark
}

internal enum OnboardingOverlayKind
{
    None,
    ChannelModal,
    StepTour
}

internal enum StartGallerySourceMode
{
    Templates,
    MyWidgets
}

internal enum TemplateGalleryCategory
{
    All,
    Habits,
    Tools,
    Live,
    Ai
}

internal sealed record ExistingWidgetCardItem(
    string Id,
    string Name,
    string Emoji,
    string Summary,
    string ManifestJson,
    bool IsVisibleOnDesktop,
    int RevisionCount,
    long LastUpdatedUtcTicks,
    string? LogicJs = null,
    CustomWidgetLocaleCatalog? Locales = null,
    string ManifestVersion = "1.0.0");

internal sealed record WorkshopCardSpec(
    string Id,
    string Emoji,
    string LabelKey,
    string DescriptionKey,
    string PromptKey,
    string ManifestJson,
    TemplateGalleryCategory Category,
    string? DirectLabel = null,
    string? DirectDescription = null,
    bool RequiresClientUpgrade = false,
    string UpgradeRequirementLabel = "",
    CustomWidgetLocaleCatalog? Locales = null,
    string? LogicJs = null,
    string Version = "1.0.0",
    bool IsRemoteOnly = false,
    bool IsRemoteUpgraded = false);

internal sealed record StudioShellVisualSettings(
    double DefaultWidgetWidth,
    double DefaultWidgetHeight,
    string WidgetMaterialType,
    double WidgetOpacity,
    double WidgetMaterialIntensity,
    string WidgetCornerPreference,
    bool WidgetTextShadowEnabled,
    string WidgetBorderStyle,
    string WidgetBorderColorMode,
    string WidgetTitleIconMode,
    double TextSize,
    double IconSize);
