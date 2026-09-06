using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationPlanner
{
    public const int MaxNewWidgetCount = 4;
    public const int MinimumRecommendedItemCount = 5;
    public const int MinimumStandaloneCategoryItemCount = 2;

    private readonly DesktopOrganizationRuleResolver _ruleResolver;

    public DesktopOrganizationPlanner(DesktopOrganizationRuleResolver ruleResolver)
    {
        _ruleResolver = ruleResolver;
    }

    public DesktopOrganizationPlan CreatePlan(
        DesktopOrganizationScanResult scan,
        string storageRootPath,
        IReadOnlyCollection<WidgetConfig> widgets,
        IReadOnlyCollection<DesktopOrganizationRule> rules,
        Func<string, string>? categoryDisplayNameResolver = null,
        bool includePersonalDesktop = true,
        bool includePublicDesktop = false)
    {
        string normalizedRoot = Path.GetFullPath(storageRootPath);
        categoryDisplayNameResolver ??= categoryId => categoryId;
        var targets = new Dictionary<string, MutableTarget>(StringComparer.Ordinal);
        var pendingByCategory = new Dictionary<string, List<DesktopOrganizationFileSnapshot>>(StringComparer.Ordinal);
        var selectedItems = scan.Items.Select(item =>
            (item.SourceScope == DesktopOrganizationSourceScope.Public ? includePublicDesktop : includePersonalDesktop)
                ? item
                : item with { ExclusionReason = DesktopOrganizationExclusionReason.SourceNotSelected }).ToList();

        foreach (DesktopOrganizationFileSnapshot item in selectedItems.Where(item => item.IsEligible))
        {
            DesktopOrganizationRule? rule = _ruleResolver.Resolve(item, rules, widgets);
            WidgetConfig? widget = rule is null
                ? null
                : widgets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, rule.TargetWidgetId, StringComparison.Ordinal));

            if (widget is not null && !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                if (!targets.TryGetValue(widget.Id, out MutableTarget? existing))
                {
                    existing = new MutableTarget(
                        widget.Id,
                        item.CategoryId,
                        widget.Id,
                        widget.Name,
                        Path.GetFullPath(widget.MappedFolderPath),
                        createsWidget: false);
                    targets.Add(widget.Id, existing);
                }

                existing.Items.Add(item);
                continue;
            }

            if (!pendingByCategory.TryGetValue(item.CategoryId, out var pending))
            {
                pending = [];
                pendingByCategory.Add(item.CategoryId, pending);
            }

            pending.Add(item);
        }

        MergeSmallAndOverflowCategories(pendingByCategory);

        var reservedDirectories = widgets
            .Where(widget => !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .Select(widget => Path.GetFullPath(widget.MappedFolderPath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string categoryId in DesktopOrganizationCategoryIds.DefaultOrder)
        {
            if (!pendingByCategory.TryGetValue(categoryId, out var items) || items.Count == 0)
            {
                continue;
            }

            string displayName = categoryDisplayNameResolver(categoryId);
            string directory = FileService.GetAvailablePath(
                Path.Combine(normalizedRoot, SanitizeFolderName(displayName)),
                reservedDirectories);
            var target = new MutableTarget(
                $"category:{categoryId}",
                categoryId,
                Guid.NewGuid().ToString("N"),
                displayName,
                directory,
                createsWidget: true);
            target.Items.AddRange(items);
            targets.Add(target.WidgetId, target);
        }

        return new DesktopOrganizationPlan
        {
            DesktopPath = scan.DesktopPath,
            PublicDesktopPath = scan.PublicDesktopPath,
            PublicDesktopUnavailable = scan.PublicDesktopUnavailable,
            IncludePersonalDesktop = includePersonalDesktop,
            IncludePublicDesktop = includePublicDesktop,
            SourceItems = scan.Items.ToList(),
            StorageRootPath = normalizedRoot,
            Targets = targets.Values
                .Select(target => target.ToPlan())
                .ToList(),
            ExcludedItems = selectedItems.Where(item => !item.IsEligible).ToList()
        };
    }

    private static void MergeSmallAndOverflowCategories(
        IDictionary<string, List<DesktopOrganizationFileSnapshot>> categories)
    {
        var other = categories.TryGetValue(DesktopOrganizationCategoryIds.Other, out var existingOther)
            ? existingOther
            : [];
        categories[DesktopOrganizationCategoryIds.Other] = other;

        foreach (string categoryId in categories.Keys
                     .Where(categoryId => categoryId != DesktopOrganizationCategoryIds.Other)
                     .ToList())
        {
            if (categories[categoryId].Count >= MinimumStandaloneCategoryItemCount)
            {
                continue;
            }

            other.AddRange(categories[categoryId]);
            categories.Remove(categoryId);
        }

        while (categories.Count(pair => pair.Value.Count > 0) > MaxNewWidgetCount)
        {
            var smallest = categories
                .Where(pair =>
                    pair.Key != DesktopOrganizationCategoryIds.Other &&
                    pair.Value.Count > 0)
                .OrderBy(pair => pair.Value.Count)
                .ThenBy(pair => GetCategoryOrder(pair.Key))
                .First();
            other.AddRange(smallest.Value);
            categories.Remove(smallest.Key);
        }

        if (other.Count == 0)
        {
            categories.Remove(DesktopOrganizationCategoryIds.Other);
        }
    }

    public static DesktopOrganizationPlan CreateRetryPlan(
        DesktopOrganizationPlan previous, IReadOnlySet<string> remainingPaths, IReadOnlyCollection<WidgetConfig> widgets)
    {
        return new DesktopOrganizationPlan
        {
            Id = previous.Id,
            DesktopPath = previous.DesktopPath,
            PublicDesktopPath = previous.PublicDesktopPath,
            IncludePersonalDesktop = previous.IncludePersonalDesktop,
            IncludePublicDesktop = previous.IncludePublicDesktop,
            StorageRootPath = previous.StorageRootPath,
            SourceItems = previous.SourceItems,
            ExcludedItems = previous.ExcludedItems,
            Targets = previous.Targets.Select(target => target.CloneWith(target.TargetWidgetId,
                target.SuggestedDisplayName, target.TargetDirectoryPath,
                target.CreatesWidget && !widgets.Any(widget => widget.Id == target.TargetWidgetId),
                target.Items.Where(item => remainingPaths.Contains(item.SourcePath))))
                .Where(target => target.Items.Count > 0).ToList()
        };
    }

    private static int GetCategoryOrder(string categoryId)
    {
        for (int index = 0; index < DesktopOrganizationCategoryIds.DefaultOrder.Count; index++)
        {
            if (string.Equals(
                    DesktopOrganizationCategoryIds.DefaultOrder[index],
                    categoryId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static string SanitizeFolderName(string name)
    {
        string sanitized = string.Concat(name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? DesktopOrganizationCategoryIds.Other
            : sanitized;
    }

    private sealed class MutableTarget(
        string sourceBucketId,
        string categoryId,
        string widgetId,
        string displayName,
        string directoryPath,
        bool createsWidget)
    {
        public string SourceBucketId { get; } = sourceBucketId;
        public string CategoryId { get; } = categoryId;
        public string WidgetId { get; } = widgetId;
        public string DisplayName { get; } = displayName;
        public string DirectoryPath { get; } = directoryPath;
        public bool CreatesWidget { get; } = createsWidget;
        public List<DesktopOrganizationFileSnapshot> Items { get; } = [];

        public DesktopOrganizationTargetPlan ToPlan() => new()
        {
            SourceBucketId = SourceBucketId,
            CategoryId = CategoryId,
            TargetWidgetId = WidgetId,
            SuggestedDisplayName = DisplayName,
            TargetDirectoryPath = DirectoryPath,
            CreatesWidget = CreatesWidget,
            Items = Items.ToList()
        };
    }
}
