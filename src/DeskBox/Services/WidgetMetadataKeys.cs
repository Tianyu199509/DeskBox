namespace DeskBox.Services;

/// <summary>
/// Central registry of every WidgetConfig.Metadata key used across the
/// application (pluginization roadmap section 7, stage 2 data hygiene).
/// Before this class the key literals lived in eight different files with
/// two naming styles (bare PascalCase vs dotted feature prefixes) and one
/// literal declared twice (QuickCaptureMasterPaneWidth).
///
/// The owning families keep their constants; this registry aliases them so
/// there is exactly one place to grep, review and register metadata keys.
/// New keys must be added here at the same time they are introduced - the
/// WidgetMetadataKeysContractTests freeze pins the complete inventory.
///
/// Note: dotted keys (Todo.*, Weather.*) are feature-owned state stored in
/// the shared WidgetConfig.Metadata dictionary; see the roadmap section on
/// per-kind settings stores for the long-term direction of moving feature
/// state out of the shared dictionary.
///
/// LIFECYCLE (roadmap 16.6): this is a migration-period inventory, not a
/// permanent global feature registry. It must SHRINK over time - host-owned
/// keys stay, feature-owned keys move to per-kind stores / instance payloads,
/// third-party plugins own their payload schemas outright and never register
/// here. If this file grows past the current 19 entries, treat that as
/// architecture regression, not progress.
/// </summary>
public static class WidgetMetadataKeys
{
    // Chrome & behavior (host-owned)
    public const string ChromeMode = WidgetChromeModeNames.MetadataKey;
    public const string CollapseBehavior = WidgetCollapseBehaviorNames.MetadataKey;
    public const string FolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.MetadataKey;

    // Foreground text overrides (host-owned)
    public const string WidgetForegroundMode = WidgetForegroundSettings.ModeOverrideMetadataKey;
    public const string WidgetForegroundColor = WidgetForegroundSettings.ColorOverrideMetadataKey;
    public const string WidgetTextEdgeMode = WidgetForegroundSettings.EdgeOverrideMetadataKey;

    // File stack overrides (File-widget owned)
    public const string FileStacksEnabled = WidgetFileStackSettings.EnabledOverrideMetadataKey;
    public const string FileStackGroupBy = WidgetFileStackSettings.GroupByOverrideMetadataKey;
    public const string FileStackThreshold = WidgetFileStackSettings.ThresholdOverrideMetadataKey;
    public const string FileStackOrderBy = WidgetFileStackSettings.OrderByOverrideMetadataKey;
    public const string FileStackOpenMode = WidgetFileStackSettings.OpenModeOverrideMetadataKey;
    public const string FileStackDisabledGroups = WidgetFileStackSettings.DisabledStacksMetadataKey;
    public const string FileStackNameOverrides = WidgetFileStackSettings.StackNameOverridesMetadataKey;
    public const string FileStackGroupOrder = WidgetFileStackSettings.StackOrderMetadataKey;
    public const string FileStackMemberOverrides = WidgetFileStackSettings.StackMemberOverridesMetadataKey;

    // Feature-owned instance state (dotted namespace style)
    public const string WeatherViewMode = WeatherWidgetViewModeSettings.MetadataKey;
    public const string TodoMasterPaneWidth = "Todo.MasterPaneWidth";
    public const string TodoTitleEditorHeight = "Todo.TitleEditorHeight";
    public const string QuickCaptureMasterPaneWidth = "QuickCaptureMasterPaneWidth";
}
