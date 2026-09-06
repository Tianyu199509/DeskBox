using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>Refreshes a preview without silently selecting files discovered after the user's review.</summary>
internal sealed record DesktopOrganizationPreviewReconciliation(
    HashSet<string> ExcludedPaths, HashSet<string> OptionalPaths, HashSet<string> NewPaths,
    int AddedCount, int RemovedCount)
{
    public static DesktopOrganizationPreviewReconciliation Calculate(
        IReadOnlyCollection<DesktopOrganizationFileSnapshot> previous,
        IReadOnlyCollection<DesktopOrganizationFileSnapshot> current,
        IEnumerable<string> excludedPaths, IEnumerable<string> optionalPaths, IEnumerable<string> newPaths)
    {
        var old = previous.Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = current.Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = present.Where(path => !old.Contains(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = excludedPaths.Where(present.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(added);
        var optionalCandidates = current.Where(item => item.CanOptIn).Select(item => item.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var optional = optionalPaths.Where(optionalCandidates.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var markedNew = newPaths.Where(present.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        markedNew.UnionWith(added);
        return new(excluded, optional, markedNew, added.Count, old.Count(path => !present.Contains(path)));
    }
}
