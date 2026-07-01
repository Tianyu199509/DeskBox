using DeskBox.Models;

namespace DeskBox.Services;

public sealed record FeatureWidgetEntry(
    WidgetKind Kind,
    string Title,
    string Description,
    string Glyph,
    bool IsEnabled,
    bool CanToggle,
    bool HasSettingsPage,
    string? SettingsSectionTag,
    string StatusLabel,
    string DisplayDescription,
    bool ShowToggle,
    bool IsAvailable);
