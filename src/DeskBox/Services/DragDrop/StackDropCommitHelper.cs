using DeskBox.Models;

namespace DeskBox.Services.DragDrop;

/// <summary>
/// Pure selection and key-resolution rules shared by native OLE and XAML
/// stack-drop commit paths so overwritten same-name files still join the
/// target stack and auto-to-manual stack key promotions stay synchronized.
/// </summary>
internal static class StackDropCommitHelper
{
    internal static WidgetItem[] SelectImportedItemsForStack(
        IEnumerable<WidgetItem> currentItems,
        IReadOnlySet<string> preExistingNormalizedPaths,
        IEnumerable<string>? importedDestinationPaths)
    {
        HashSet<string>? destinationSet = null;
        if (importedDestinationPaths is not null)
        {
            foreach (string rawPath in importedDestinationPaths)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                destinationSet ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                destinationSet.Add(NormalizePath(rawPath));
            }
        }

        var selected = new List<WidgetItem>();
        foreach (WidgetItem item in currentItems)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            string normalized = NormalizePath(item.Path);
            if (!preExistingNormalizedPaths.Contains(normalized) ||
                (destinationSet is not null && destinationSet.Contains(normalized)))
            {
                selected.Add(item);
            }
        }

        return selected.ToArray();
    }

    internal static string ResolveEffectiveStackKey(
        string requestedStackKey,
        string? lastResolvedStackKey) =>
        string.IsNullOrWhiteSpace(lastResolvedStackKey)
            ? requestedStackKey
            : lastResolvedStackKey;

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }
}
