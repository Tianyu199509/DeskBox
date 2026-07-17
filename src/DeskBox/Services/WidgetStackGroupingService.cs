using DeskBox.Models;

namespace DeskBox.Services;

public enum WidgetStackCategory
{
    Folders,
    Applications,
    Documents,
    Images,
    Videos,
    Audio,
    Archives,
    Other,
    Today,
    Yesterday,
    PreviousSevenDays,
    PreviousThirtyDays,
    Earlier
}

public sealed record WidgetStackGroup(
    WidgetStackCategory Category,
    IReadOnlyList<WidgetItem> Items,
    string? StackKey = null,
    string? DisplayName = null,
    bool CanStack = true)
{
    public string EffectiveKey => StackKey ?? Category.ToString();
}

public static class WidgetStackGroupingService
{
    private static readonly HashSet<string> s_applicationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".appref-ms", ".bat", ".cmd", ".ps1"
    };

    private static readonly HashSet<string> s_documentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".rtf", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".csv", ".odt", ".ods", ".odp", ".json", ".xml", ".html", ".htm"
    };

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".svg"
    };

    private static readonly HashSet<string> s_videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv"
    };

    private static readonly HashSet<string> s_audioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma"
    };

    private static readonly HashSet<string> s_archiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".cab", ".iso"
    };

    public static IReadOnlyList<WidgetStackGroup> Group(
        IEnumerable<WidgetItem> items,
        string? groupBy,
        DateTime? now = null,
        string? orderBy = null,
        IReadOnlyList<FileStackCustomRule>? customRules = null,
        string? unmatchedBehavior = null)
    {
        string normalized = SettingsService.NormalizeFileStackGroupBy(groupBy);
        string normalizedOrder = SettingsService.NormalizeFileStackOrderBy(orderBy);
        DateTime today = (now ?? DateTime.Now).Date;
        var indexedItems = items
            .Select((item, index) => new IndexedItem(item, index))
            .ToList();
        if (normalized == SettingsService.FileStackGroupByCustom)
        {
            return GroupByCustomRules(
                indexedItems,
                customRules,
                unmatchedBehavior,
                normalizedOrder);
        }

        if (normalized == SettingsService.FileStackGroupByKind &&
            BuildCustomRuleMatchers(customRules).Count > 0)
        {
            return GroupByKindWithCustomRules(
                indexedItems,
                customRules,
                normalizedOrder,
                today);
        }

        return indexedItems
            .GroupBy(entry => ResolveCategory(entry.Item, normalized, today))
            .OrderBy(group => GetCategoryOrder(group.Key))
            .Select(group => new WidgetStackGroup(
                group.Key,
                OrderMembers(group, normalizedOrder)))
            .ToList();
    }

    private static IReadOnlyList<WidgetStackGroup> GroupByCustomRules(
        IReadOnlyList<IndexedItem> items,
        IReadOnlyList<FileStackCustomRule>? customRules,
        string? unmatchedBehavior,
        string orderBy)
    {
        var rules = BuildCustomRuleMatchers(customRules);
        var matches = rules.ToDictionary(
            rule => rule.Index,
            _ => new List<IndexedItem>());
        var unmatched = new List<IndexedItem>();

        foreach (IndexedItem entry in items)
        {
            string extension = Path.GetExtension(entry.Item.Path);
            CustomRuleMatcher? match = rules.FirstOrDefault(
                rule => rule.Extensions.Contains(extension));
            if (match is null)
            {
                unmatched.Add(entry);
            }
            else
            {
                matches[match.Index].Add(entry);
            }
        }

        var groups = new List<WidgetStackGroup>();
        foreach (CustomRuleMatcher rule in rules)
        {
            if (matches[rule.Index] is not { Count: > 0 } members)
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(rule.Rule.Name)
                ? string.Join(", ", rule.Extensions)
                : rule.Rule.Name.Trim();
            groups.Add(new WidgetStackGroup(
                WidgetStackCategory.Other,
                OrderMembers(members, orderBy),
                $"Custom:{rule.Rule.Id}",
                displayName));
        }

        if (SettingsService.NormalizeFileStackUnmatchedBehavior(unmatchedBehavior) ==
            SettingsService.FileStackUnmatchedOther)
        {
            if (unmatched.Count > 0)
            {
                groups.Add(new WidgetStackGroup(
                    WidgetStackCategory.Other,
                    OrderMembers(unmatched, orderBy),
                    "Custom:Other"));
            }
        }
        else
        {
            groups.AddRange(unmatched.Select(entry => new WidgetStackGroup(
                WidgetStackCategory.Other,
                [entry.Item],
                $"Loose:{entry.Index}:{entry.Item.Path}",
                CanStack: false)));
        }

        return groups;
    }

    private static IReadOnlyList<WidgetStackGroup> GroupByKindWithCustomRules(
        IReadOnlyList<IndexedItem> items,
        IReadOnlyList<FileStackCustomRule>? customRules,
        string orderBy,
        DateTime today)
    {
        var rules = BuildCustomRuleMatchers(customRules);
        var matches = rules.ToDictionary(
            rule => rule.Index,
            _ => new List<IndexedItem>());
        var unmatched = new List<IndexedItem>();

        foreach (IndexedItem entry in items)
        {
            string extension = Path.GetExtension(entry.Item.Path);
            CustomRuleMatcher? match = rules.FirstOrDefault(
                rule => rule.Extensions.Contains(extension));
            if (match is null)
            {
                unmatched.Add(entry);
            }
            else
            {
                matches[match.Index].Add(entry);
            }
        }

        var groups = new List<WidgetStackGroup>();
        foreach (CustomRuleMatcher rule in rules)
        {
            if (matches[rule.Index] is not { Count: > 0 } members)
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(rule.Rule.Name)
                ? string.Join(", ", rule.Extensions)
                : rule.Rule.Name.Trim();
            groups.Add(new WidgetStackGroup(
                WidgetStackCategory.Other,
                OrderMembers(members, orderBy),
                $"Custom:{rule.Rule.Id}",
                displayName));
        }

        groups.AddRange(unmatched
            .GroupBy(entry => ResolveCategory(
                entry.Item,
                SettingsService.FileStackGroupByKind,
                today))
            .OrderBy(group => GetCategoryOrder(group.Key))
            .Select(group => new WidgetStackGroup(
                group.Key,
                OrderMembers(group, orderBy))));
        return groups;
    }

    private static List<CustomRuleMatcher> BuildCustomRuleMatchers(
        IReadOnlyList<FileStackCustomRule>? customRules) =>
        (customRules ?? [])
            .Where(rule => rule is not null)
            .Select((rule, index) => new CustomRuleMatcher(
                rule,
                index,
                SettingsService.NormalizeFileStackExtensions(rule.Extensions)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .Where(rule => rule.Extensions.Count > 0)
            .ToList();

    public static WidgetStackCategory ResolveCategory(
        WidgetItem item,
        string? groupBy,
        DateTime? now = null)
    {
        string normalized = SettingsService.NormalizeFileStackGroupBy(groupBy);
        DateTime today = (now ?? DateTime.Now).Date;
        return ResolveCategory(item, normalized, today);
    }

    private static WidgetStackCategory ResolveCategory(
        WidgetItem item,
        string normalizedGroupBy,
        DateTime today)
    {
        if (normalizedGroupBy == SettingsService.FileStackGroupByDateAdded)
        {
            return ResolveDateCategory(item.AddedAt.LocalDateTime, today);
        }

        if (normalizedGroupBy == SettingsService.FileStackGroupByDateModified)
        {
            return ResolveDateCategory(item.LastModified, today);
        }

        if (item.IsFolder)
        {
            return WidgetStackCategory.Folders;
        }

        WidgetStackCategory? shellCategory = ResolveShellKindCategory(item.ShellKind);
        if (shellCategory is not null)
        {
            return shellCategory.Value;
        }

        if (item.IsShortcut)
        {
            return WidgetStackCategory.Applications;
        }

        string extension = Path.GetExtension(item.Path);
        if (s_applicationExtensions.Contains(extension)) return WidgetStackCategory.Applications;
        if (s_documentExtensions.Contains(extension)) return WidgetStackCategory.Documents;
        if (s_imageExtensions.Contains(extension)) return WidgetStackCategory.Images;
        if (s_videoExtensions.Contains(extension)) return WidgetStackCategory.Videos;
        if (s_audioExtensions.Contains(extension)) return WidgetStackCategory.Audio;
        if (s_archiveExtensions.Contains(extension)) return WidgetStackCategory.Archives;
        return WidgetStackCategory.Other;
    }

    private static WidgetStackCategory? ResolveShellKindCategory(string? shellKind) =>
        shellKind?.Trim().ToLowerInvariant() switch
        {
            "folder" => WidgetStackCategory.Folders,
            "program" => WidgetStackCategory.Applications,
            "document" => WidgetStackCategory.Documents,
            "picture" => WidgetStackCategory.Images,
            "video" => WidgetStackCategory.Videos,
            "music" => WidgetStackCategory.Audio,
            _ => null
        };

    private static WidgetStackCategory ResolveDateCategory(DateTime value, DateTime today)
    {
        if (value == default)
        {
            return WidgetStackCategory.Earlier;
        }

        DateTime date = value.Date;
        if (date >= today) return WidgetStackCategory.Today;
        if (date >= today.AddDays(-1)) return WidgetStackCategory.Yesterday;
        if (date >= today.AddDays(-7)) return WidgetStackCategory.PreviousSevenDays;
        if (date >= today.AddDays(-30)) return WidgetStackCategory.PreviousThirtyDays;
        return WidgetStackCategory.Earlier;
    }

    private static int GetCategoryOrder(WidgetStackCategory category) => category switch
    {
        WidgetStackCategory.Folders => 0,
        WidgetStackCategory.Applications => 1,
        WidgetStackCategory.Documents => 2,
        WidgetStackCategory.Images => 3,
        WidgetStackCategory.Videos => 4,
        WidgetStackCategory.Audio => 5,
        WidgetStackCategory.Archives => 6,
        WidgetStackCategory.Other => 7,
        WidgetStackCategory.Today => 10,
        WidgetStackCategory.Yesterday => 11,
        WidgetStackCategory.PreviousSevenDays => 12,
        WidgetStackCategory.PreviousThirtyDays => 13,
        _ => 14
    };

    private static IReadOnlyList<WidgetItem> OrderMembers(
        IEnumerable<IndexedItem> members,
        string orderBy)
    {
        IOrderedEnumerable<IndexedItem> ordered = orderBy switch
        {
            SettingsService.FileStackOrderByName => members
                .OrderBy(entry => entry.Item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Index),
            SettingsService.FileStackOrderByDateAdded => members
                .OrderByDescending(entry => entry.Item.AddedAt)
                .ThenBy(entry => entry.Index),
            SettingsService.FileStackOrderByDateModified => members
                .OrderByDescending(entry => entry.Item.LastModified)
                .ThenBy(entry => entry.Index),
            _ => members.OrderBy(entry => entry.Index)
        };
        return ordered.Select(entry => entry.Item).ToList();
    }

    private sealed record IndexedItem(WidgetItem Item, int Index);

    private sealed record CustomRuleMatcher(
        FileStackCustomRule Rule,
        int Index,
        HashSet<string> Extensions);
}
