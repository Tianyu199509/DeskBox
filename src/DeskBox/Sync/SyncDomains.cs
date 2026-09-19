namespace DeskBox.Sync;

/// <summary>
/// Wire names for the three syncable domains and their collection/entity
/// conventions (sync-protocol-contract §2). The strings intentionally match
/// the cloud-backup manifest names — the same semantic boundaries carry both
/// transports; a contract test pins the equality so they cannot drift.
/// </summary>
public static class SyncDomains
{
    public const string TodoData = "todo-data";
    public const string QuickCaptureData = "quick-capture-data";
    public const string WidgetStyle = "widget-style";

    /// <summary>Singleton collection ids (§2.1): non-widget collections that
    /// merge across devices by construction.</summary>
    public const string QuickCaptureCollection = "quick-capture";
    public const string WidgetStyleCollection = "widget-style";

    /// <summary>The shell entity inside the widget-style singleton collection.</summary>
    public const string ShellEntityId = "shell";

    public static bool IsKnownDomain(string? domain) =>
        domain is TodoData or QuickCaptureData or WidgetStyle;

    /// <summary>todo-data is scoped per widget: the collection id IS the
    /// widget id (§2.1 — independently created todo widgets never merge).</summary>
    public static string TodoCollectionForWidget(string widgetId) => widgetId;
}
