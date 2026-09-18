namespace DeskBox.Models;

/// <summary>
/// Search popup preferences: hotkey, providers, history, and custom bounds.
/// </summary>
public sealed class SearchSettingsSlice
{
    // ─── Search Settings ───────────────────────────────────────────────
    /// <summary>Whether the search global hotkey is enabled.</summary>
    public bool SearchHotkeyEnabled { get; set; }

    /// <summary>Search hotkey modifier bit flags.</summary>
    public int SearchHotkeyModifiers { get; set; } = (int)HotkeyModifierKeys.Alt;

    /// <summary>
    /// Search hotkey virtual key code. Default: D (0x44).
    /// Note: Alt+Space is reserved by Windows for the window system menu and
    /// cannot be registered via RegisterHotKey, so Alt+D is used instead.
    /// </summary>
    public int SearchHotkeyKey { get; set; } = 0x44;

    /// <summary>
    /// Search popup display mode. Valid values: "Spotlight", "Home", "Palette".
    /// </summary>
    public string SearchDisplayMode { get; set; } = "Spotlight";

    /// <summary>Whether to include DeskBox internal content (todos, notes, widget files) in search.</summary>
    public bool SearchIncludeDeskBoxContent { get; set; } = true;

    /// <summary>
    /// Whether the user has explicitly allowed DeskBox to query the locally running
    /// Everything instance through its read-only IPC API.
    /// </summary>
    public bool SearchEverythingEnabled { get; set; }

    /// <summary>
    /// An optional, user-selected Everything.exe path. An empty value keeps automatic
    /// detection enabled and is also the default for normal installed copies.
    /// </summary>
    public string SearchEverythingExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether search text is passed through as Everything syntax. Disabled by default
    /// so ordinary DeskBox searches are treated as literal filename phrases.
    /// </summary>
    public bool SearchEverythingAdvancedSyntaxEnabled { get; set; }

    /// <summary>Whether to show recommendations when the search popup opens.</summary>
    public bool SearchShowRecommendations { get; set; } = true;

    /// <summary>
    /// Whether the search popup records and shows search history (recent queries
    /// and pinned favorites). When false, searches are not saved and the empty
    /// state hint is shown instead of prior records.
    /// </summary>
    public bool SearchSaveHistory { get; set; } = true;

    /// <summary>Legacy result cap retained for settings compatibility; paged search ignores it.</summary>
    public int SearchMaxResults { get; set; } = 100;

    /// <summary>Default result tab for a new query: all, app, file, or deskbox.</summary>
    public string SearchDefaultTab { get; set; } = "all";

    /// <summary>
    /// Icon entrance animation style for the recommended-apps grid.
    /// 0 = Staggered fade + scale (Win11), 1 = Staggered fade + rise (Spotlight),
    /// 2 = Wave cascade, 3 = Soft bounce.
    /// </summary>
    public int SearchAppIconAnimation { get; set; } = 0;

    /// <summary>
    /// Custom search popup window bounds (physical pixels). When all four are set the
    /// popup opens at this position/size instead of the default centered, mode-based
    /// placement. Null means "use default". Double-clicking the popup title area clears
    /// these back to null.
    /// </summary>
    public int? SearchPopupCustomX { get; set; }

    public int? SearchPopupCustomY { get; set; }

    public int? SearchPopupCustomWidth { get; set; }

    public int? SearchPopupCustomHeight { get; set; }
}
