namespace DeskBox.Models;

/// <summary>
/// Projection record for one widget carrying capsule overrides. The shell's
/// override-list state machine builds these; the capsule editor exposes them
/// as a pushed binding surface. Kept WinRT-bindable for the XAML item
/// template.
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record CapsuleOverrideSettingsItem(
    string WidgetId,
    string DisplayName,
    string Summary,
    string Glyph,
    string ColorIconAssetName = "default");
