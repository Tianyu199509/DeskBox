using System.Collections.Frozen;

namespace DeskBox.Models;

/// <summary>
/// Curated semantic icon registry mapping human-readable icon identifiers (e.g. <c>"add"</c>,
/// <c>"edit"</c>, <c>"delete"</c>, <c>"export"</c>, <c>"folder"</c>) to Windows 11 Segoe Fluent Icons
/// codepoints, eliminating LLM private-use-area (<c>\uE7xx</c>) hallucination while staying 100%
/// backward-compatible with explicit Unicode glyphs and Emoji.
/// </summary>
public static class CustomWidgetSemanticIcons
{
    private static readonly FrozenDictionary<string, string> SemanticToGlyph =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Core CRUD & editing
            ["add"] = "\uE710",
            ["plus"] = "\uE710",
            ["new"] = "\uE710",
            ["create"] = "\uE710",
            ["minus"] = "\uE738",
            ["subtract"] = "\uE738",
            ["edit"] = "\uE70F",
            ["pencil"] = "\uE70F",
            ["rename"] = "\uE8AC",
            ["swap"] = "\uE8AB",
            ["switch"] = "\uE8AB",
            ["delete"] = "\uE74D",
            ["trash"] = "\uE74D",
            ["remove"] = "\uE74D",
            ["recycle"] = "\uE74D",
            ["recycle-bin"] = "\uE74D",
            ["save"] = "\uE74E",
            ["copy"] = "\uE8C8",
            ["paste"] = "\uE77F",
            ["cut"] = "\uE8C6",
            ["clipboard"] = "\uE77F",
            ["undo"] = "\uE7A7",
            ["redo"] = "\uE7A6",
            ["clear"] = "\uE894",
            ["eraser"] = "\uE75C",
            ["font-size"] = "\uE8D2",
            ["typography"] = "\uE8D2",
            ["align-left"] = "\uE8E4",
            ["text-align"] = "\uE8E4",
            ["dictionary"] = "\uE8F2",
            ["spell-check"] = "\uE8F2",

            // Files, folders & export/import
            ["export"] = "\uEDE1",
            ["import"] = "\uE8B5",
            ["download"] = "\uE896",
            ["upload"] = "\uE898",
            ["share"] = "\uE72D",
            ["folder"] = "\uE8B7",
            ["folder-open"] = "\uE838",
            ["open-folder"] = "\uE838",
            ["folder-add"] = "\uE8F4",
            ["file"] = "\uE8A5",
            ["document"] = "\uE8A5",
            ["article"] = "\uE8A1",
            ["page"] = "\uE8A1",
            ["open-file"] = "\uE8E5",
            ["file-edit"] = "\uE8E5",
            ["note"] = "\uE70B",
            ["notes"] = "\uE70B",
            ["quick-note"] = "\uE8D1",
            ["action-center"] = "\uE8D1",
            ["book"] = "\uE82D",
            ["library"] = "\uE8F1",
            ["books"] = "\uE8F1",
            ["archive"] = "\uE7B8",
            ["zip"] = "\uE7B8",
            ["attach"] = "\uE723",
            ["paperclip"] = "\uE723",
            ["code"] = "\uE943",
            ["terminal"] = "\uE756",
            ["console"] = "\uE756",
            ["print"] = "\uE749",

            // Navigation, arrows & window chrome
            ["close"] = "\uE711",
            ["cancel"] = "\uE711",
            ["x"] = "\uE711",
            ["back"] = "\uE72B",
            ["arrow-left"] = "\uE72B",
            ["forward"] = "\uE72A",
            ["arrow-right"] = "\uE72A",
            ["arrow-up"] = "\uE74A",
            ["arrow-down"] = "\uE74B",
            ["chevron-left"] = "\uE76B",
            ["chevron-right"] = "\uE76C",
            ["chevron-up"] = "\uE70E",
            ["chevron-down"] = "\uE70D",
            ["expand"] = "\uE740",
            ["collapse"] = "\uE73F",
            ["maximize"] = "\uE922",
            ["minimize"] = "\uE921",
            ["open"] = "\uE8A7",
            ["open-external"] = "\uE8A7",
            ["external-link"] = "\uE8A7",
            ["link"] = "\uE71B",
            ["home"] = "\uE80F",
            ["menu"] = "\uE700",
            ["more"] = "\uE712",
            ["more-horizontal"] = "\uE712",
            ["more-vertical"] = "\uE712",
            ["drag"] = "\uE784",
            ["grip"] = "\uE784",

            // Search, filter, sort & sync
            ["search"] = "\uE721",
            ["find"] = "\uE721",
            ["filter"] = "\uE71C",
            ["sort"] = "\uE8CB",
            ["refresh"] = "\uE72C",
            ["reload"] = "\uE72C",
            ["sync"] = "\uE895",
            ["reset"] = "\uE777",
            ["zoom-in"] = "\uE8A3",
            ["zoom-out"] = "\uE71F",

            // Check, status & alerts
            ["check"] = "\uE73E",
            ["done"] = "\uE73E",
            ["check-circle"] = "\uE73E",
            ["checkbox"] = "\uE73A",
            ["checkbox-empty"] = "\uE739",
            ["circle"] = "\uEA3A",
            ["warning"] = "\uE7BA",
            ["alert"] = "\uE7BA",
            ["error"] = "\uEA39",
            ["info"] = "\uE946",
            ["help"] = "\uE897",
            ["question"] = "\uE897",
            ["shield"] = "\uEA18",
            ["shield-check"] = "\uEA18",
            ["verified"] = "\uE73E",
            ["bug"] = "\uEBE8",

            // Calendar, time & productivity
            ["calendar"] = "\uE787",
            ["calendar-day"] = "\uE8BF",
            ["calendar-week"] = "\uE8C0",
            ["schedule"] = "\uE787",
            ["clock"] = "\uE823",
            ["time"] = "\uE823",
            ["history"] = "\uE81C",
            ["clock-history"] = "\uE81C",
            ["timer"] = "\uE916",
            ["stopwatch"] = "\uE916",
            ["alarm"] = "\uE823",
            ["bell"] = "\uEA8F",
            ["notification"] = "\uEA8F",
            ["notifications"] = "\uEA8F",
            ["bell-off"] = "\uE7ED",
            ["task"] = "\uE762",
            ["checklist"] = "\uE762",
            ["todo"] = "\uE762",
            ["target"] = "\uE734",
            ["focus"] = "\uE706",
            ["flag"] = "\uE7C1",
            ["bookmark"] = "\uE8A4",
            ["tag"] = "\uE8EC",
            ["pin"] = "\uE718",
            ["unpin"] = "\uE77A",
            ["star"] = "\uE734",
            ["star-filled"] = "\uE735",
            ["heart"] = "\uEB51",
            ["heart-filled"] = "\uEB52",
            ["fire"] = "\uE945",
            ["bolt"] = "\uE945",
            ["lightning"] = "\uE945",
            ["trophy"] = "\uE734",

            // Media & audio
            ["play"] = "\uE768",
            ["pause"] = "\uE769",
            ["stop"] = "\uE71A",
            ["next"] = "\uE893",
            ["skip-next"] = "\uE893",
            ["previous"] = "\uE892",
            ["skip-previous"] = "\uE892",
            ["repeat"] = "\uE8EE",
            ["shuffle"] = "\uE8B1",
            ["volume"] = "\uE767",
            ["volume0"] = "\uE992",
            ["volume1"] = "\uE993",
            ["volume2"] = "\uE994",
            ["volume3"] = "\uE995",
            ["volume-up"] = "\uE995",
            ["volume-down"] = "\uE993",
            ["mute"] = "\uE74F",
            ["volume-off"] = "\uE74F",
            ["music"] = "\uEC4F",
            ["headphones"] = "\uE7F6",
            ["mic"] = "\uE720",
            ["microphone"] = "\uE720",
            ["video"] = "\uE714",
            ["camera"] = "\uE722",
            ["image"] = "\uEB9F",
            ["photo"] = "\uEB9F",
            ["gallery"] = "\uE8B9",

            // Charts, layouts & data
            ["list"] = "\uEA37",
            ["grid"] = "\uF0E2",
            ["table"] = "\uF0E2",
            ["chart"] = "\uE9D2",
            ["bar-chart"] = "\uE9D2",
            ["line-chart"] = "\uE9D9",
            ["trending"] = "\uE9D9",
            ["pie-chart"] = "\uEB05",
            ["heatmap"] = "\uE80A",
            ["dashboard"] = "\uE9D9",
            ["activity"] = "\uE9D9",
            ["soundscape"] = "\uE9D9",
            ["equalizer"] = "\uE9D9",
            ["wave"] = "\uE9D9",
            ["layers"] = "\uE81E",
            ["sidebar"] = "\uE8A0",

            // System, hardware & settings
            ["settings"] = "\uE713",
            ["gear"] = "\uE713",
            ["sliders"] = "\uE9E9",
            ["tune"] = "\uE9E9",
            ["cpu"] = "\uE950",
            ["chip"] = "\uE950",
            ["memory"] = "\uE964",
            ["ram"] = "\uE964",
            ["hard-drive"] = "\uEDA2",
            ["disk"] = "\uEDA2",
            ["storage"] = "\uEDA2",
            ["network"] = "\uE968",
            ["wifi"] = "\uE701",
            ["bluetooth"] = "\uE702",
            ["battery"] = "\uE83F",
            ["power"] = "\uE7E8",
            ["lock"] = "\uE72E",
            ["unlock"] = "\uE785",
            ["key"] = "\uE8D7",
            ["eye"] = "\uE7B3",
            ["eye-off"] = "\uED1A",
            ["sun"] = "\uE706",
            ["brightness"] = "\uE706",
            ["moon"] = "\uE708",
            ["power-sleep"] = "\uE708",
            ["cloud"] = "\uE753",
            ["weather"] = "\uE753",
            ["rain"] = "\uE753",
            ["thermometer"] = "\uE9CA",
            ["globe"] = "\uE774",
            ["world"] = "\uE774",
            ["map"] = "\uE707",
            ["map-pin"] = "\uE707",
            ["location"] = "\uE707",
            ["compass"] = "\uE707",
            ["gps"] = "\uE81D",
            ["navigate"] = "\uE81D",

            // Communication & AI
            ["send"] = "\uE724",
            ["mail"] = "\uE715",
            ["inbox"] = "\uE715",
            ["chat"] = "\uE8BD",
            ["message"] = "\uE8BD",
            ["comment"] = "\uE90A",
            ["user"] = "\uE77B",
            ["users"] = "\uE716",
            ["sparkles"] = "\uE945",
            ["ai"] = "\uE945",
            ["magic"] = "\uE945",
            ["robot"] = "\uE99A",
            ["bot"] = "\uE99A",
            ["translate"] = "\uE8C1",
            ["language"] = "\uE775",
            ["palette"] = "\uE790",
            ["brush"] = "\uE790",
            ["calculator"] = "\uE8EF",
            ["game"] = "\uE7FC",
            ["dice"] = "\uE7FC",
            ["coffee"] = "\uE706",
            ["water"] = "\uEB42",
            ["drop"] = "\uEB42",
            ["wallet"] = "\uE8C7",
            ["money"] = "\uE8C7",
            ["currency"] = "\uE8C7",
            ["news"] = "\uE8A1",
            ["rss"] = "\uE968",
            ["widget"] = "\uE71D"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes invalid PUA codepoints and Windows 11-only Fluent glyphs
    /// (such as <c>\uED1A</c> Hide and <c>\uE7C2</c> Move) to guaranteed
    /// Windows 10 <c>Segoe MDL2 Assets</c> fallbacks when running on pre-Win11.
    /// </summary>
    public static string NormalizeFluentGlyphForOs(string? normalizedGlyph, bool isWindows11OrLater)
    {
        if (string.IsNullOrEmpty(normalizedGlyph))
        {
            return string.Empty;
        }

        string mapped = normalizedGlyph switch
        {
            "\uE9C4" or "\uE9C0" or "\uE9CB" => "\uE753", // Non-existent PUA codepoints -> Cloud
            "\uE91D" => "\uE73F",                         // Non-existent PUA codepoint -> Pill / switch
            "\uEAFD" => "\uE82D",                         // Non-existent PUA codepoint -> Markets / reference
            _ => normalizedGlyph
        };

        if (isWindows11OrLater)
        {
            return mapped;
        }

        return mapped switch
        {
            "\uED1A" => "\uE890", // Win11 Fluent Hide -> Win10 MDL2 View fallback
            "\uE7C2" => "\uE8AB", // Win11 Fluent Move -> Win10 MDL2 Switch fallback
            "\uF8AC" => "\uE892", // Win11 BackSolidBold -> Win10 MDL2 Previous
            "\uF8AD" => "\uE893", // Win11 ForwardSolidBold -> Win10 MDL2 Next
            "\uF8AE" => "\uE769", // Win11 PauseBold -> Win10 MDL2 Pause
            _ => mapped
        };
    }

    /// <summary>Canonical reverse map from Segoe Fluent PUA glyph back to preferred semantic name.</summary>
    private static readonly FrozenDictionary<string, string> GlyphToCanonicalSemantic =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["\uE710"] = "add",
            ["\uE738"] = "minus",
            ["\uE70F"] = "edit",
            ["\uE8AB"] = "swap",
            ["\uE74D"] = "delete",
            ["\uE74E"] = "save",
            ["\uE8C8"] = "copy",
            ["\uE77F"] = "paste",
            ["\uE8C6"] = "cut",
            ["\uE7A7"] = "undo",
            ["\uE7A6"] = "redo",
            ["\uE894"] = "clear",
            ["\uE8D2"] = "font-size",
            ["\uE8E4"] = "align-left",
            ["\uE8F2"] = "dictionary",
            ["\uEDE1"] = "export",
            ["\uE8B5"] = "import",
            ["\uE896"] = "download",
            ["\uE898"] = "upload",
            ["\uE72D"] = "share",
            ["\uE8B7"] = "folder",
            ["\uE838"] = "folder-open",
            ["\uE8A5"] = "file",
            ["\uE8A1"] = "article",
            ["\uE8E5"] = "open-file",
            ["\uE70B"] = "note",
            ["\uE8D1"] = "quick-note",
            ["\uE82D"] = "book",
            ["\uE8F1"] = "library",
            ["\uE7B8"] = "archive",
            ["\uE723"] = "attach",
            ["\uE943"] = "code",
            ["\uE756"] = "terminal",
            ["\uE711"] = "close",
            ["\uE72B"] = "back",
            ["\uE72A"] = "forward",
            ["\uE74A"] = "arrow-up",
            ["\uE74B"] = "arrow-down",
            ["\uE76B"] = "chevron-left",
            ["\uE76C"] = "chevron-right",
            ["\uE70E"] = "chevron-up",
            ["\uE70D"] = "chevron-down",
            ["\uE740"] = "expand",
            ["\uE73F"] = "collapse",
            ["\uE8A7"] = "external-link",
            ["\uE71B"] = "link",
            ["\uE80F"] = "home",
            ["\uE700"] = "menu",
            ["\uE712"] = "more",
            ["\uE721"] = "search",
            ["\uE71C"] = "filter",
            ["\uE8CB"] = "sort",
            ["\uE72C"] = "refresh",
            ["\uE895"] = "sync",
            ["\uE777"] = "reset",
            ["\uE73E"] = "check",
            ["\uE7BA"] = "warning",
            ["\uEA39"] = "error",
            ["\uE946"] = "info",
            ["\uE897"] = "help",
            ["\uE787"] = "calendar",
            ["\uE823"] = "clock",
            ["\uE81C"] = "history",
            ["\uE916"] = "timer",
            ["\uEA8F"] = "bell",
            ["\uE762"] = "checklist",
            ["\uE7C1"] = "flag",
            ["\uE8A4"] = "bookmark",
            ["\uE8EC"] = "tag",
            ["\uE718"] = "pin",
            ["\uE77A"] = "unpin",
            ["\uE734"] = "star",
            ["\uE735"] = "star-filled",
            ["\uEB51"] = "heart",
            ["\uE768"] = "play",
            ["\uE769"] = "pause",
            ["\uE71A"] = "stop",
            ["\uE893"] = "skip-next",
            ["\uE892"] = "skip-previous",
            ["\uE767"] = "volume",
            ["\uE74F"] = "mute",
            ["\uEC4F"] = "music",
            ["\uE714"] = "video",
            ["\uE722"] = "camera",
            ["\uEB9F"] = "image",
            ["\uEA37"] = "list",
            ["\uF0E2"] = "grid",
            ["\uE9D2"] = "bar-chart",
            ["\uE9D9"] = "line-chart",
            ["\uEB05"] = "pie-chart",
            ["\uE713"] = "settings",
            ["\uE9E9"] = "sliders",
            ["\uE950"] = "cpu",
            ["\uE964"] = "memory",
            ["\uEDA2"] = "hard-drive",
            ["\uE968"] = "network",
            ["\uE701"] = "wifi",
            ["\uE702"] = "bluetooth",
            ["\uE83F"] = "battery",
            ["\uE7E8"] = "power",
            ["\uE72E"] = "lock",
            ["\uE785"] = "unlock",
            ["\uE8D7"] = "key",
            ["\uE7B3"] = "eye",
            ["\uED1A"] = "eye-off",
            ["\uE706"] = "sun",
            ["\uE708"] = "moon",
            ["\uE753"] = "cloud",
            ["\uE774"] = "globe",
            ["\uE707"] = "location",
            ["\uE81D"] = "gps",
            ["\uE724"] = "send",
            ["\uE715"] = "mail",
            ["\uE8BD"] = "chat",
            ["\uE77B"] = "user",
            ["\uE716"] = "users",
            ["\uE945"] = "sparkles",
            ["\uE99A"] = "bot",
            ["\uE8C1"] = "translate",
            ["\uE790"] = "palette",
            ["\uE8EF"] = "calculator",
            ["\uE7FC"] = "game",
            ["\uEB42"] = "water",
            ["\uE8C7"] = "wallet",
            ["\uE71D"] = "widget"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> AllSemanticNames => SemanticToGlyph.Keys;

    public static bool TryResolve(string? rawName, out string glyph)
    {
        glyph = string.Empty;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return false;
        }

        string trimmed = rawName.Trim();
        if (SemanticToGlyph.TryGetValue(trimmed, out string? exact))
        {
            glyph = exact;
            return true;
        }

        // Normalize snake_case or space-separated names to kebab-case (e.g. "folder_open" -> "folder-open")
        if (trimmed.Contains('_', StringComparison.Ordinal) || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            string kebab = trimmed.Replace('_', '-').Replace(' ', '-');
            if (SemanticToGlyph.TryGetValue(kebab, out string? normalized))
            {
                glyph = normalized;
                return true;
            }
        }

        return false;
    }

    public static bool TryReverseLookup(string? glyph, out string semanticName)
    {
        semanticName = string.Empty;
        if (string.IsNullOrEmpty(glyph))
        {
            return false;
        }

        if (GlyphToCanonicalSemantic.TryGetValue(glyph, out string? found))
        {
            semanticName = found;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="raw"/> looks like an unresolved ASCII semantic icon identifier
    /// (e.g. <c>"pencil-square"</c> or <c>"file_export"</c>) rather than an expression, emoji, single character,
    /// or hex codepoint.
    /// </summary>
    public static bool LooksLikeUnresolvedSemanticIconName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.Length <= 2 ||
            trimmed.Contains("${", StringComparison.Ordinal) ||
            trimmed.StartsWith("\\u", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryResolve(trimmed, out _))
        {
            return false;
        }

        // Hex 4-digit PUA strings like "E70F" are already parsed as glyphs by NormalizeGlyph
        if (trimmed.Length == 4 && trimmed.All(static c => char.IsAsciiHexDigit(c)))
        {
            return false;
        }

        bool hasAsciiLetter = false;
        foreach (char c in trimmed)
        {
            if (char.IsAsciiLetter(c))
            {
                hasAsciiLetter = true;
            }
            else if (!(char.IsAsciiDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return hasAsciiLetter;
    }

    public static IReadOnlyList<string> SuggestClosest(string? unknownName, int maxSuggestions = 3)
    {
        if (string.IsNullOrWhiteSpace(unknownName) || maxSuggestions <= 0)
        {
            return [];
        }

        string needle = unknownName.Trim().ToLowerInvariant().Replace('_', '-');
        return SemanticToGlyph.Keys
            .Select(k => (Name: k, Score: ComputeSimilarityScore(needle, k)))
            .OrderBy(static x => x.Score)
            .ThenBy(static x => x.Name.Length)
            .Take(maxSuggestions)
            .Select(static x => x.Name)
            .ToList();
    }

    private static int ComputeSimilarityScore(string a, string b)
    {
        if (b.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase))
        {
            return Math.Abs(a.Length - b.Length);
        }

        int m = a.Length;
        int n = b.Length;
        Span<int> prev = n + 1 <= 64 ? stackalloc int[n + 1] : new int[n + 1];
        Span<int> curr = n + 1 <= 64 ? stackalloc int[n + 1] : new int[n + 1];

        for (int j = 0; j <= n; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            curr.CopyTo(prev);
        }

        return prev[n] + 2;
    }
}
