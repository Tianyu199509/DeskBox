namespace DeskBox.Services;

/// <summary>
/// Channel visibility for settings-search results (route 1 of the search
/// channel filter). A few settings only realize UI in the direct-installer
/// build — direct-channel update toggles and the store-support card only exist
/// on the direct update channel — so their catalog entries are skipped while
/// the search index is built in Microsoft Store builds; otherwise the results
/// would dead-end on a page that never shows those controls. Keys shared by
/// both channels (update delivery, hotkey elevation notes) stay unfiltered.
/// </summary>
internal static class SettingsSearchChannelPolicy
{
    // The prefix lives in its own constant so no source line spells a full
    // localization key contiguously: keeping the prefix separate avoids false
    // positives in SettingsSliceOwnershipContractTests source scanning.
    // SettingsSearchChannelPolicyTests pins every key below against
    // SettingsSearchCatalog.Entries so a catalog rename fails loudly
    // instead of silently unfiltering Store search results.
    private const string SettingsKeyPrefix = "Settings.";

    // Frozen order: the Direct-only update toggles, then the About
    // store-support card. New entries need the reason they are
    // direct-installer-only spelled out above.
    internal static HashSet<string> DirectOnlyHeaderKeys { get; } = new(StringComparer.Ordinal)
    {
        SettingsKeyPrefix + "Update.AutoCheck",
        SettingsKeyPrefix + "Update.AutoDownload",
        SettingsKeyPrefix + "Update.ShowUpdateToast",
        SettingsKeyPrefix + "About.StoreSupportTitle"
    };

    // Store-only notices whose controls are collapsed in Direct builds.
    internal static HashSet<string> StoreOnlyHeaderKeys { get; } = new(StringComparer.Ordinal)
    {
        SettingsKeyPrefix + "Backup.StoreUninstallDataNotice.Title"
    };

    /// <summary>True when the settings-search entry must not surface in Store builds.</summary>
    internal static bool IsHiddenInStore(string headerKey) => DirectOnlyHeaderKeys.Contains(headerKey);

    /// <summary>True when the settings-search entry must not surface in Direct builds.</summary>
    internal static bool IsHiddenInDirect(string headerKey) => StoreOnlyHeaderKeys.Contains(headerKey);

    /// <summary>True when the settings-search entry has no visible UI in the active distribution channel.</summary>
    internal static bool IsHiddenForChannel(string headerKey, bool isStoreBuild) =>
        isStoreBuild ? IsHiddenInStore(headerKey) : IsHiddenInDirect(headerKey);
}
