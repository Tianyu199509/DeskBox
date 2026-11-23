namespace DeskBox.Models;

using System;
using System.Collections.Generic;
using System.Linq;

public readonly record struct SearchCategoryEntry(
    string Id,
    bool SearchEnabled,
    bool TabShown);

/// <summary>
/// Search popup preferences: hotkey, providers, history, and custom bounds.
/// </summary>
public sealed class SearchSettingsSlice
{
    public const string CategoryAll = "all";
    public const string CategoryFile = "file";
    public const string CategoryImage = "image";
    public const string CategoryApp = "app";
    public const string CategorySettings = "settings";
    public const string CategoryDocument = "document";
    public const string CategoryFolder = "folder";
    public const string CategoryTodo = "todo";
    public const string CategoryNote = "note";
    public const string CategoryClipboard = "clipboard";
    public const string CategoryWidget = "widget";
    public const string CategoryCommand = "command";
    public const string CategoryVideo = "video";
    public const string CategoryMusic = "music";
    public const string CategoryArchive = "archive";

    public static IReadOnlyList<string> DefaultCategoryOrder { get; } =
    [
        CategoryFile,
        CategoryImage,
        CategoryApp,
        CategorySettings,
        CategoryDocument,
        CategoryFolder,
        CategoryTodo,
        CategoryNote,
        CategoryClipboard,
        CategoryWidget,
        CategoryCommand,
        CategoryVideo,
        CategoryMusic,
        CategoryArchive
    ];

    public const string DefaultCategoryLayout =
        "file:tab,image:tab,app:search,settings:search,document:search,folder:search,todo:search,note:search,clipboard:search,widget:search,command:search,video:search,music:search,archive:search";

    public static string? TryResolveCanonicalCategoryId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return null;
        }

        return rawId.Trim().ToLowerInvariant() switch
        {
            "file" or "files" => CategoryFile,
            "image" or "images" or "img" => CategoryImage,
            "app" or "apps" => CategoryApp,
            "settings" or "setting" or "set" => CategorySettings,
            "document" or "documents" or "doc" => CategoryDocument,
            "folder" or "folders" or "dir" => CategoryFolder,
            "todo" or "todos" => CategoryTodo,
            "note" or "notes" or "quickcapture" => CategoryNote,
            "clipboard" or "clip" => CategoryClipboard,
            "widget" or "widgets" or "customwidget" => CategoryWidget,
            "command" or "commands" or "action" or "actions" => CategoryCommand,
            "video" or "videos" => CategoryVideo,
            "music" or "audio" => CategoryMusic,
            "archive" or "archives" or "zip" => CategoryArchive,
            _ => null
        };
    }

    public static bool IsDefaultTabShown(string categoryId) =>
        categoryId is CategoryFile or CategoryImage;

    public static IReadOnlyList<SearchCategoryEntry> ParseCategoryLayout(string? rawLayout)
    {
        var entries = new List<SearchCategoryEntry>(DefaultCategoryOrder.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(rawLayout))
        {
            string[] tokens = rawLayout.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string rawToken in tokens)
            {
                string idPart = rawToken;
                string? modePart = null;
                int colonIdx = rawToken.IndexOf(':');
                if (colonIdx >= 0)
                {
                    idPart = rawToken[..colonIdx].Trim();
                    modePart = rawToken[(colonIdx + 1)..].Trim().ToLowerInvariant();
                }
                else if (rawToken.StartsWith('-') || rawToken.StartsWith('!'))
                {
                    idPart = rawToken[1..].Trim();
                    modePart = "off";
                }
                else if (rawToken.StartsWith('+'))
                {
                    idPart = rawToken[1..].Trim();
                    modePart = "tab";
                }
                else if (rawToken.StartsWith('~'))
                {
                    idPart = rawToken[1..].Trim();
                    modePart = "search";
                }

                string? canonicalId = TryResolveCanonicalCategoryId(idPart);
                if (canonicalId is null || !seen.Add(canonicalId))
                {
                    continue;
                }

                (bool searchEnabled, bool tabShown) = modePart switch
                {
                    "tab" => (true, true),
                    "search" => (true, false),
                    "off" => (false, false),
                    _ => (true, IsDefaultTabShown(canonicalId))
                };
                entries.Add(new SearchCategoryEntry(canonicalId, searchEnabled, tabShown));
            }
        }

        foreach (string defaultId in DefaultCategoryOrder)
        {
            if (seen.Add(defaultId))
            {
                entries.Add(new SearchCategoryEntry(
                    defaultId,
                    SearchEnabled: true,
                    TabShown: IsDefaultTabShown(defaultId)));
            }
        }

        return entries;
    }

    public static string SerializeCategoryLayout(IReadOnlyList<SearchCategoryEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return DefaultCategoryLayout;
        }

        string candidate = string.Join(
            ",",
            entries.Select(static entry =>
            {
                string mode = !entry.SearchEnabled
                    ? "off"
                    : entry.TabShown
                        ? "tab"
                        : "search";
                return $"{entry.Id}:{mode}";
            }));
        IReadOnlyList<SearchCategoryEntry> normalized = ParseCategoryLayout(candidate);
        return string.Join(
            ",",
            normalized.Select(static entry =>
            {
                string mode = !entry.SearchEnabled
                    ? "off"
                    : entry.TabShown
                        ? "tab"
                        : "search";
                return $"{entry.Id}:{mode}";
            }));
    }

    public static string NormalizeCategoryLayout(string? rawLayout) =>
        SerializeCategoryLayout(ParseCategoryLayout(rawLayout));

    public static bool IsDefaultCategoryLayout(string? rawLayout) =>
        string.Equals(NormalizeCategoryLayout(rawLayout), DefaultCategoryLayout, StringComparison.Ordinal);

    public static string SetCategorySearchEnabled(string? rawLayout, string categoryId, bool searchEnabled)
    {
        string? canonicalId = TryResolveCanonicalCategoryId(categoryId);
        IReadOnlyList<SearchCategoryEntry> current = ParseCategoryLayout(rawLayout);
        if (canonicalId is null)
        {
            return SerializeCategoryLayout(current);
        }

        var updated = new List<SearchCategoryEntry>(current.Count);
        foreach (SearchCategoryEntry entry in current)
        {
            if (string.Equals(entry.Id, canonicalId, StringComparison.Ordinal))
            {
                updated.Add(new SearchCategoryEntry(
                    entry.Id,
                    SearchEnabled: searchEnabled,
                    TabShown: searchEnabled && entry.TabShown));
            }
            else
            {
                updated.Add(entry);
            }
        }

        return SerializeCategoryLayout(updated);
    }

    public static string SetCategoryTabShown(string? rawLayout, string categoryId, bool tabShown)
    {
        string? canonicalId = TryResolveCanonicalCategoryId(categoryId);
        IReadOnlyList<SearchCategoryEntry> current = ParseCategoryLayout(rawLayout);
        if (canonicalId is null)
        {
            return SerializeCategoryLayout(current);
        }

        var updated = new List<SearchCategoryEntry>(current.Count);
        foreach (SearchCategoryEntry entry in current)
        {
            if (string.Equals(entry.Id, canonicalId, StringComparison.Ordinal))
            {
                updated.Add(new SearchCategoryEntry(
                    entry.Id,
                    SearchEnabled: tabShown || entry.SearchEnabled,
                    TabShown: tabShown));
            }
            else
            {
                updated.Add(entry);
            }
        }

        return SerializeCategoryLayout(updated);
    }

    public static string MoveCategory(string? rawLayout, string categoryId, int delta)
    {
        string? canonicalId = TryResolveCanonicalCategoryId(categoryId);
        List<SearchCategoryEntry> list = [.. ParseCategoryLayout(rawLayout)];
        if (canonicalId is null || delta == 0)
        {
            return SerializeCategoryLayout(list);
        }

        int index = list.FindIndex(entry => string.Equals(entry.Id, canonicalId, StringComparison.Ordinal));
        int targetIndex = index + delta;
        if (index < 0 || targetIndex < 0 || targetIndex >= list.Count)
        {
            return SerializeCategoryLayout(list);
        }

        SearchCategoryEntry moving = list[index];
        list.RemoveAt(index);
        list.Insert(targetIndex, moving);
        return SerializeCategoryLayout(list);
    }

    public static string ReorderCategories(string? rawLayout, IReadOnlyList<string> orderedCategoryIds)
    {
        IReadOnlyList<SearchCategoryEntry> current = ParseCategoryLayout(rawLayout);
        var byId = current.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var reordered = new List<SearchCategoryEntry>(current.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (orderedCategoryIds is not null)
        {
            foreach (string rawId in orderedCategoryIds)
            {
                string? canonicalId = TryResolveCanonicalCategoryId(rawId);
                if (canonicalId is not null &&
                    seen.Add(canonicalId) &&
                    byId.TryGetValue(canonicalId, out SearchCategoryEntry entry))
                {
                    reordered.Add(entry);
                }
            }
        }

        foreach (SearchCategoryEntry entry in current)
        {
            if (seen.Add(entry.Id))
            {
                reordered.Add(entry);
            }
        }

        return SerializeCategoryLayout(reordered);
    }

    public static string NormalizeDefaultTabId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CategoryAll;
        }

        string trimmed = value.Trim().ToLowerInvariant();
        if (trimmed is CategoryAll or "deskbox")
        {
            return CategoryAll;
        }

        return TryResolveCanonicalCategoryId(trimmed) ?? CategoryAll;
    }

    public static string GetCategoryNameKey(string categoryId) => categoryId switch
    {
        CategoryAll => "Search.Tab.All",
        CategoryFile => "Search.Tab.File",
        CategoryImage => "Search.Tab.Image",
        CategoryApp => "Search.Tab.App",
        CategorySettings => "Search.Tab.Settings",
        CategoryDocument => "Search.Tab.Document",
        CategoryFolder => "Search.Tab.Folder",
        CategoryTodo => "Search.Tab.Todo",
        CategoryNote => "Search.Tab.Note",
        CategoryClipboard => "Search.Tab.Clipboard",
        CategoryWidget => "Search.Tab.Widget",
        CategoryCommand => "Search.Tab.Command",
        CategoryVideo => "Search.Tab.Video",
        CategoryMusic => "Search.Tab.Music",
        CategoryArchive => "Search.Tab.Archive",
        _ => "Search.Tab.Other"
    };

    public static string GetCategoryGlyph(string categoryId) => categoryId switch
    {
        CategoryAll => "\uE71D",
        CategoryFile => "\uE8E5",
        CategoryImage => "\uE8B9",
        CategoryApp => "\uE7AC",
        CategorySettings => "\uE713",
        CategoryDocument => "\uE8A5",
        CategoryFolder => "\uE8B7",
        CategoryTodo => "\uE9D5",
        CategoryNote => "\uE70F",
        CategoryClipboard => "\uE77F",
        CategoryWidget => "\uE71D",
        CategoryCommand => "\uE756",
        CategoryVideo => "\uE714",
        CategoryMusic => "\uE8D6",
        CategoryArchive => "\uF012",
        _ => "\uE8A5"
    };

    public static bool SupportsCategoryFileSort(string categoryId) => categoryId is
        CategoryAll or
        CategoryFile or
        CategoryFolder or
        CategoryImage or
        CategoryDocument or
        CategoryVideo or
        CategoryMusic or
        CategoryArchive;

    public static WidgetKind? GetLinkedFeatureWidgetKind(string categoryId) => categoryId switch
    {
        CategoryTodo => WidgetKind.Todo,
        CategoryNote => WidgetKind.QuickCapture,
        CategoryClipboard => WidgetKind.Clipboard,
        _ => null
    };

    public static bool IsCategoryFeatureEnabled(AppSettings settings, string categoryId)
    {
        WidgetKind? linked = GetLinkedFeatureWidgetKind(categoryId);
        if (linked is null)
        {
            return true;
        }

        if (Services.FeatureWidgetSettings.IsEnabled(settings, linked.Value))
        {
            return true;
        }

        string key = linked.Value.ToString();
        return settings.WidgetLayout.FeatureWidgetEnabledStates is null ||
               !settings.WidgetLayout.FeatureWidgetEnabledStates.ContainsKey(key);
    }

    public static bool IsSettingsActionItem(SearchResultItem item) =>
        item.Kind == SearchResultKind.Action &&
        item.ActionId is { } actionId &&
        (string.Equals(actionId, "open-settings", StringComparison.Ordinal) ||
         actionId.StartsWith("open-settings:", StringComparison.Ordinal));

    public static bool MatchesCategory(SearchResultItem item, string categoryId) => categoryId switch
    {
        CategoryAll => true,
        CategoryApp => item.Kind == SearchResultKind.File &&
                       FileCategoryHelper.Categorize(item.Title) == FileCategory.App,
        CategoryFile => item.Kind is SearchResultKind.File or SearchResultKind.Folder,
        CategoryFolder => item.Kind == SearchResultKind.Folder,
        CategoryImage => item.Kind == SearchResultKind.File &&
                         FileCategoryHelper.Categorize(item.Title) == FileCategory.Image,
        CategoryDocument => item.Kind == SearchResultKind.File &&
                            FileCategoryHelper.Categorize(item.Title) == FileCategory.Document,
        CategoryVideo => item.Kind == SearchResultKind.File &&
                         FileCategoryHelper.Categorize(item.Title) == FileCategory.Video,
        CategoryMusic => item.Kind == SearchResultKind.File &&
                         FileCategoryHelper.Categorize(item.Title) == FileCategory.Music,
        CategoryArchive => item.Kind == SearchResultKind.File &&
                           FileCategoryHelper.Categorize(item.Title) == FileCategory.Archive,
        CategorySettings => IsSettingsActionItem(item),
        CategoryTodo => item.Kind == SearchResultKind.Todo ||
                        (item.Kind == SearchResultKind.Action &&
                         item.ActionId?.StartsWith("quick-todo:", StringComparison.Ordinal) == true),
        CategoryNote => item.Kind == SearchResultKind.QuickCapture ||
                        (item.Kind == SearchResultKind.Action &&
                         item.ActionId?.StartsWith("quick-note:", StringComparison.Ordinal) == true),
        CategoryClipboard => item.Kind == SearchResultKind.Clipboard,
        CategoryWidget => item.Kind == SearchResultKind.CustomWidget,
        CategoryCommand => item.Kind == SearchResultKind.Action &&
                           !IsSettingsActionItem(item) &&
                           item.ActionId?.StartsWith("quick-todo:", StringComparison.Ordinal) != true &&
                           item.ActionId?.StartsWith("quick-note:", StringComparison.Ordinal) != true,
        _ => false
    };
    // ─── Search Settings ───────────────────────────────────────────────
    /// <summary>Whether the search global hotkey is enabled (on by default for new installs).</summary>
    public bool SearchHotkeyEnabled { get; set; } = true;

    /// <summary>Search hotkey activation mode (Chord, DoubleControl, WindowsTap, DoubleTap).</summary>
    public HotkeyActivationKind SearchHotkeyActivationKind { get; set; } = HotkeyActivationKind.Chord;

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

    /// <summary>Default result tab for a new query ("all" or any enabled category ID in <see cref="SearchCategoryLayout"/>).</summary>
    public string SearchDefaultTab { get; set; } = "all";

    /// <summary>
    /// Icon entrance animation style for the recommended-apps grid.
    /// 0 = Staggered fade + scale (Win11), 1 = Staggered fade + rise (Spotlight),
    /// 2 = Wave cascade, 3 = Soft bounce.
    /// </summary>
    public int SearchAppIconAnimation { get; set; } = 0;

    /// <summary>
    /// Custom search popup window placement and horizontal width.
    /// <see cref="SearchPopupCustomX"/> and <see cref="SearchPopupCustomY"/> are stored in
    /// physical screen pixels; <see cref="SearchPopupCustomWidth"/> is stored in logical pixels
    /// (DIPs) so the user's preferred width stays consistent across DPI scales, while height
    /// remains fully content-adaptive (<see cref="SearchPopupCustomHeight"/> stays null).
    /// Double-clicking the popup top drag strip clears these back to null.
    /// </summary>
    public int? SearchPopupCustomX { get; set; }

    public int? SearchPopupCustomY { get; set; }

    public int? SearchPopupCustomWidth { get; set; }

    public int? SearchPopupCustomHeight { get; set; }

    /// <summary>Category pill bar visibility mode: "auto" (on-demand across multiple categories), "always", or "hidden".</summary>
    public string SearchCategoryBarMode { get; set; } = "auto";

    /// <summary>Whether to show the sortable column header bar above search results. False by default.</summary>
    public bool SearchShowSortHeader { get; set; }

    /// <summary>Default sort column for results: "relevance", "date", "name", or "size".</summary>
    public string SearchDefaultSortColumn { get; set; } = "relevance";

    /// <summary>Whether global search queries DeskBox clipboard history.</summary>
    public bool SearchIncludeClipboard { get; set; } = true;

    /// <summary>Whether global search evaluates inline arithmetic expressions.</summary>
    public bool SearchIncludeCalculator { get; set; } = true;

    /// <summary>Whether the right-side details sidebar is expanded by default when searching.</summary>
    public bool SearchShowDetailsPane { get; set; } = true;

    /// <summary>Whether global search includes direct URL opening and fallback web search.</summary>
    public bool SearchIncludeWebFallback { get; set; } = true;

    /// <summary>Default web search engine identifier: "bing", "google", "baidu", "duckduckgo", or "custom".</summary>
    public string SearchDefaultWebEngine { get; set; } = "bing";

    /// <summary>Custom web search URL template containing {q} or %s placeholder when <see cref="SearchDefaultWebEngine"/> is "custom".</summary>
    public string SearchCustomWebUrl { get; set; } = string.Empty;

    /// <summary>Whether quick-create Todo actions are enabled in global search.</summary>
    public bool SearchQuickCreateTodoEnabled { get; set; } = true;

    /// <summary>Whether quick-create QuickCapture note actions are enabled in global search.</summary>
    public bool SearchQuickCreateNoteEnabled { get; set; } = true;

    /// <summary>
    /// When inline quick-create rows appear for ordinary queries:
    /// "always" (auto-top when zero local hits, below local hits otherwise),
    /// "fallback" (only when zero local hits), or "prefix" (only with explicit prefix/scope).
    /// </summary>
    public string SearchQuickCreateDisplayMode { get; set; } = "always";

    /// <summary>Default primary quick-create target when both Todo and QuickCapture are enabled: "todo" or "note".</summary>
    public string SearchQuickCreateDefaultTarget { get; set; } = "todo";

    /// <summary>Whether saving a Todo or QuickCapture note from search shows the desktop bottom capsule HUD with a View button.</summary>
    public bool SearchQuickCreateShowHud { get; set; } = true;

    /// <summary>
    /// Ordered search content &amp; category tab layout ("id:tab", "id:search", "id:off").
    /// Default enables searching across all categories and shows only All + File + Image tabs.
    /// </summary>
    public string SearchCategoryLayout { get; set; } = DefaultCategoryLayout;
}
