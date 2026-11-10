using DeskBox.Models;

namespace DeskBox.Services;

internal enum SearchInlineScope
{
    None,
    Apps,
    Files,
    Folders,
    Images,
    Documents,
    Todos,
    Notes,
    Clipboard,
    Settings,
    Commands
}

internal readonly record struct ParsedSearchQuery(
    string RawQuery,
    string EffectiveQuery,
    SearchInlineScope Scope,
    string? ScopeBadgeText,
    string? QuickTodoText,
    string? QuickNoteText,
    bool IsQuickTodoIntent = false,
    bool IsQuickNoteIntent = false)
{
    public bool HasScopeFilter => Scope != SearchInlineScope.None;
    public bool AllowsEmptyEffectiveQuery =>
        Scope is SearchInlineScope.Clipboard or
                 SearchInlineScope.Todos or
                 SearchInlineScope.Notes or
                 SearchInlineScope.Settings or
                 SearchInlineScope.Commands or
                 SearchInlineScope.Apps;
}

/// <summary>
/// Parses lightweight command-palette style prefixes (app:, file:, folder:, img:, doc:,
/// todo:, note:, clip:, set:, >) and quick-create commands (todo/待办, note/随记) without
/// colliding with Windows drive paths (e.g. C:\ or D:/).
/// </summary>
internal static class SearchQuerySyntaxParser
{
    private static readonly (string Prefix, SearchInlineScope Scope, string Badge)[] s_colonScopes =
    [
        ("apps", SearchInlineScope.Apps, "app:"),
        ("app", SearchInlineScope.Apps, "app:"),
        ("应用", SearchInlineScope.Apps, "应用:"),
        ("files", SearchInlineScope.Files, "file:"),
        ("file", SearchInlineScope.Files, "file:"),
        ("文件", SearchInlineScope.Files, "文件:"),
        ("folders", SearchInlineScope.Folders, "folder:"),
        ("folder", SearchInlineScope.Folders, "folder:"),
        ("dir", SearchInlineScope.Folders, "dir:"),
        ("文件夹", SearchInlineScope.Folders, "文件夹:"),
        ("目录", SearchInlineScope.Folders, "目录:"),
        ("images", SearchInlineScope.Images, "img:"),
        ("image", SearchInlineScope.Images, "img:"),
        ("img", SearchInlineScope.Images, "img:"),
        ("pic", SearchInlineScope.Images, "pic:"),
        ("图片", SearchInlineScope.Images, "图片:"),
        ("docs", SearchInlineScope.Documents, "doc:"),
        ("doc", SearchInlineScope.Documents, "doc:"),
        ("文档", SearchInlineScope.Documents, "文档:"),
        ("todos", SearchInlineScope.Todos, "todo:"),
        ("todo", SearchInlineScope.Todos, "todo:"),
        ("待办", SearchInlineScope.Todos, "待办:"),
        ("notes", SearchInlineScope.Notes, "note:"),
        ("note", SearchInlineScope.Notes, "note:"),
        ("随记", SearchInlineScope.Notes, "随记:"),
        ("clipboard", SearchInlineScope.Clipboard, "clip:"),
        ("clip", SearchInlineScope.Clipboard, "clip:"),
        ("cb", SearchInlineScope.Clipboard, "cb:"),
        ("剪贴板", SearchInlineScope.Clipboard, "剪贴板:"),
        ("settings", SearchInlineScope.Settings, "set:"),
        ("setting", SearchInlineScope.Settings, "set:"),
        ("set", SearchInlineScope.Settings, "set:"),
        ("设置", SearchInlineScope.Settings, "设置:"),
        ("cmd", SearchInlineScope.Commands, ">"),
        ("命令", SearchInlineScope.Commands, ">")
    ];

    private static readonly string[] s_quickTodoPrefixes =
        ["todo", "todos", "td", "task", "t", "待办"];

    private static readonly string[] s_quickNotePrefixes =
        ["note", "notes", "jot", "n", "随记", "速记", "记"];

    public static ParsedSearchQuery Parse(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return new ParsedSearchQuery(
                RawQuery: rawQuery ?? string.Empty,
                EffectiveQuery: string.Empty,
                Scope: SearchInlineScope.None,
                ScopeBadgeText: null,
                QuickTodoText: null,
                QuickNoteText: null);
        }

        string trimmed = rawQuery.Trim();
        string? quickTodo = TryParseQuickTodoInternal(trimmed, out bool isTodoIntent);
        bool isNoteIntent = false;
        string? quickNote = isTodoIntent ? null : TryParseQuickNoteInternal(trimmed, out isNoteIntent);
        bool quickNoteIntent = !isTodoIntent && isNoteIntent;

        // '>' prefix enters Command/Settings palette scope (VS Code / PowerToys CmdPal style).
        if (trimmed[0] is '>' or '＞')
        {
            string body = trimmed[1..].Trim();
            return new ParsedSearchQuery(
                RawQuery: rawQuery,
                EffectiveQuery: body,
                Scope: SearchInlineScope.Commands,
                ScopeBadgeText: ">",
                QuickTodoText: quickTodo,
                QuickNoteText: quickNote,
                IsQuickTodoIntent: isTodoIntent,
                IsQuickNoteIntent: quickNoteIntent);
        }

        // Guard Windows drive paths such as "C:\" or "D:/" or bare "C:".
        if (IsWindowsDrivePrefix(trimmed))
        {
            return new ParsedSearchQuery(
                RawQuery: rawQuery,
                EffectiveQuery: trimmed,
                Scope: SearchInlineScope.None,
                ScopeBadgeText: null,
                QuickTodoText: null,
                QuickNoteText: null);
        }

        foreach (var (prefix, scope, badge) in s_colonScopes)
        {
            if (trimmed.Length > prefix.Length &&
                trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                trimmed[prefix.Length] is ':' or '：')
            {
                string body = trimmed[(prefix.Length + 1)..].Trim();
                if (scope == SearchInlineScope.Todos && string.IsNullOrEmpty(quickTodo) && !string.IsNullOrWhiteSpace(body))
                {
                    quickTodo = body;
                }
                else if (scope == SearchInlineScope.Notes && string.IsNullOrEmpty(quickNote) && !string.IsNullOrWhiteSpace(body))
                {
                    quickNote = body;
                }

                return new ParsedSearchQuery(
                    RawQuery: rawQuery,
                    EffectiveQuery: body,
                    Scope: scope,
                    ScopeBadgeText: badge,
                    QuickTodoText: quickTodo,
                    QuickNoteText: quickNote,
                    IsQuickTodoIntent: isTodoIntent || scope == SearchInlineScope.Todos,
                    IsQuickNoteIntent: quickNoteIntent || scope == SearchInlineScope.Notes);
            }
        }

        string effective = !string.IsNullOrEmpty(quickTodo)
            ? quickTodo
            : !string.IsNullOrEmpty(quickNote)
                ? quickNote
                : (isTodoIntent || quickNoteIntent) ? string.Empty : trimmed;

        return new ParsedSearchQuery(
            RawQuery: rawQuery,
            EffectiveQuery: effective,
            Scope: SearchInlineScope.None,
            ScopeBadgeText: null,
            QuickTodoText: quickTodo,
            QuickNoteText: quickNote,
            IsQuickTodoIntent: isTodoIntent,
            IsQuickNoteIntent: quickNoteIntent);
    }

    public static bool TryParseQuickTodoCommand(string? query, out string todoText)
    {
        string? parsed = TryParseQuickTodoInternal(query, out _);
        todoText = parsed ?? string.Empty;
        return !string.IsNullOrEmpty(todoText);
    }

    public static bool TryParseQuickNoteCommand(string? query, out string noteText)
    {
        string? parsed = TryParseQuickNoteInternal(query, out _);
        noteText = parsed ?? string.Empty;
        return !string.IsNullOrEmpty(noteText);
    }

    public static bool LooksLikeFileSystemPath(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = query.AsSpan().Trim();
        return IsWindowsDrivePrefix(trimmed) ||
               trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
               trimmed.StartsWith(@"~\", StringComparison.Ordinal) ||
               trimmed.StartsWith(@"~/", StringComparison.Ordinal);
    }

    public static bool MatchesScope(SearchResultItem item, SearchInlineScope scope) => scope switch
    {
        SearchInlineScope.None => true,
        SearchInlineScope.Apps =>
            item.Kind == SearchResultKind.File &&
            FileCategoryHelper.Categorize(item.Title) == FileCategory.App,
        SearchInlineScope.Files =>
            item.Kind is SearchResultKind.File or SearchResultKind.Folder,
        SearchInlineScope.Folders =>
            item.Kind == SearchResultKind.Folder,
        SearchInlineScope.Images =>
            item.Kind == SearchResultKind.File &&
            FileCategoryHelper.Categorize(item.Title) == FileCategory.Image,
        SearchInlineScope.Documents =>
            item.Kind == SearchResultKind.File &&
            FileCategoryHelper.Categorize(item.Title) == FileCategory.Document,
        SearchInlineScope.Todos =>
            item.Kind == SearchResultKind.Todo ||
            (item.Kind == SearchResultKind.Action &&
             item.ActionId is not null &&
             (item.ActionId == "new-todo" ||
              item.ActionId == "open-todo" ||
              item.ActionId.StartsWith("quick-todo:", StringComparison.Ordinal))),
        SearchInlineScope.Notes =>
            item.Kind == SearchResultKind.QuickCapture ||
            (item.Kind == SearchResultKind.Action &&
             item.ActionId is not null &&
             (item.ActionId == "new-note" ||
              item.ActionId == "quick-jot" ||
              item.ActionId == "open-quickcapture" ||
              item.ActionId.StartsWith("quick-note:", StringComparison.Ordinal))),
        SearchInlineScope.Clipboard =>
            item.Kind == SearchResultKind.Clipboard ||
            (item.Kind == SearchResultKind.Action &&
             item.ActionId is not null &&
             (item.ActionId.StartsWith("clipboard:", StringComparison.Ordinal) ||
              item.ActionId.StartsWith("clipboard-copy:", StringComparison.Ordinal))),
        SearchInlineScope.Settings =>
            item.Kind == SearchResultKind.Action &&
            item.ActionId is not null &&
            (item.ActionId == "open-settings" ||
             item.ActionId.StartsWith("open-settings:", StringComparison.Ordinal)),
        SearchInlineScope.Commands =>
            item.Kind == SearchResultKind.Action,
        _ => true
    };

    public static bool NeedsFileProvider(SearchInlineScope scope) => scope is
        SearchInlineScope.None or
        SearchInlineScope.Apps or
        SearchInlineScope.Files or
        SearchInlineScope.Folders or
        SearchInlineScope.Images or
        SearchInlineScope.Documents;

    public static bool NeedsDeskBoxContentProvider(SearchInlineScope scope) => scope is
        SearchInlineScope.None or
        SearchInlineScope.Todos or
        SearchInlineScope.Notes or
        SearchInlineScope.Clipboard;

    private static bool IsWindowsDrivePrefix(ReadOnlySpan<char> trimmed) =>
        trimmed.Length >= 2 &&
        char.IsAsciiLetter(trimmed[0]) &&
        trimmed[1] == ':' &&
        (trimmed.Length == 2 || trimmed[2] is '\\' or '/');

    private static string? TryParseQuickTodoInternal(string? query, out bool isIntent)
    {
        isIntent = false;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        ReadOnlySpan<char> span = query.AsSpan().Trim();
        if (span[0] is '+' or '＋')
        {
            if (span.Length > 1 && SearchCalculatorEvaluator.TryEvaluate(query, out _))
            {
                return null;
            }

            isIntent = true;
            ReadOnlySpan<char> body = span[1..].Trim();
            return body.IsEmpty ? null : body.ToString();
        }

        return TryParseQuickCommand(query, s_quickTodoPrefixes, out isIntent);
    }

    private static string? TryParseQuickNoteInternal(string? query, out bool isIntent)
    {
        isIntent = false;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        ReadOnlySpan<char> span = query.AsSpan().Trim();
        if (span[0] is ';' or '；' or '~' or '～')
        {
            if (span[0] == '~' && span.Length > 1 && span[1] is '/' or '\\')
            {
                return null;
            }

            isIntent = true;
            ReadOnlySpan<char> body = span[1..].Trim();
            return body.IsEmpty ? null : body.ToString();
        }

        return TryParseQuickCommand(query, s_quickNotePrefixes, out isIntent);
    }

    private static string? TryParseQuickCommand(
        string? query,
        ReadOnlySpan<string> prefixes,
        out bool isIntent)
    {
        isIntent = false;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        ReadOnlySpan<char> span = query.AsSpan().Trim();
        foreach (string prefix in prefixes)
        {
            if (!span.StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                span.Length <= prefix.Length)
            {
                continue;
            }

            char delimiter = span[prefix.Length];
            if (!char.IsWhiteSpace(delimiter) && delimiter is not (':' or '：'))
            {
                continue;
            }

            isIntent = true;
            ReadOnlySpan<char> body = span[(prefix.Length + 1)..].Trim();
            return body.IsEmpty ? null : body.ToString();
        }

        return null;
    }
}
