using DeskBox.Models;

namespace DeskBox.Services;

internal readonly record struct DesktopOrganizationPreviewSummary(
    int TotalCount, int SelectedCount, int DestinationCount, int NewDestinationCount)
{
    public int RetainedCount => Math.Max(0, TotalCount - SelectedCount);

    public static bool IncludesSource(DesktopOrganizationPlan plan, DesktopOrganizationFileSnapshot item) =>
        item.SourceScope == DesktopOrganizationSourceScope.Public ? plan.IncludePublicDesktop : plan.IncludePersonalDesktop;

    public static DesktopOrganizationPreviewSummary Calculate(
        DesktopOrganizationPlan plan,
        IReadOnlyDictionary<string, DesktopOrganizationTargetSelection> selections,
        ISet<string> excludedPaths)
    {
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var newDestinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in plan.Targets)
        {
            selections.TryGetValue(target.SourceBucketId, out var selection);
            if (selection?.IsSelected == false) continue;
            var selected = target.Items.Where(item => IncludesSource(plan, item) && !excludedPaths.Contains(item.SourcePath)).ToList();
            if (selected.Count == 0) continue;
            foreach (var item in selected) selectedPaths.Add(item.SourcePath);
            bool creates = target.CreatesWidget && selection?.DestinationMode != DesktopOrganizationDestinationMode.ExistingWidget;
            string key = creates ? "new:" + target.SourceBucketId
                : "existing:" + (selection?.ExistingWidgetId ?? target.TargetWidgetId);
            destinations.Add(key);
            if (creates) newDestinations.Add(key);
        }
        var sources = plan.SourceItems.Count > 0 ? plan.SourceItems
            : plan.Targets.SelectMany(target => target.Items).Concat(plan.ExcludedItems);
        int total = sources.Where(item => IncludesSource(plan, item))
            .Select(item => item.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new(total, selectedPaths.Count, destinations.Count, newDestinations.Count);
    }
}
