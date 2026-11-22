using System.Collections.Concurrent;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Classifies a <see cref="CustomWidgetManifest"/> into DeskBox's unified host
/// <see cref="WidgetChromeCategory"/> (<c>Display</c> 展示格子 vs <c>Interactive</c> 内容格子)
/// so Custom Widgets follow the exact same user-configured title-bar, hover-grip,
/// grouping, and window-dragging rules as built-in widgets instead of declaring
/// widget-owned chrome or custom drag handlers.
/// </summary>
public static class CustomWidgetChromeCategoryClassifier
{
    internal const int MaxDiskCacheEntries = 256;

    private readonly record struct CachedManifestCategory(
        long LastWriteUtcTicks,
        WidgetChromeCategory Category,
        long CachedAtUtcTicks);

    private static readonly ConcurrentDictionary<string, CachedManifestCategory> DiskCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static WidgetChromeCategory Classify(CustomWidgetManifest? manifest)
    {
        if (manifest is null)
        {
            return WidgetChromeCategory.Interactive;
        }

        if (manifest.OnDrop is not null)
        {
            return WidgetChromeCategory.Interactive;
        }

        for (int i = 0; i < manifest.Sources.Count; i++)
        {
            string sourceType = manifest.Sources[i].Type;
            if (string.Equals(sourceType, "todo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sourceType, "clipboard", StringComparison.OrdinalIgnoreCase))
            {
                return WidgetChromeCategory.Interactive;
            }
        }

        if (ContainsInteractiveAuthoringNode(manifest.View))
        {
            return WidgetChromeCategory.Interactive;
        }

        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            if (ContainsInteractiveAuthoringNode(manifest.Popovers[i]))
            {
                return WidgetChromeCategory.Interactive;
            }
        }

        return WidgetChromeCategory.Display;
    }

    /// <summary>
    /// Determines whether the root view is a full-bleed structural stage (cover media, ZStack
    /// backdrop, canvas, flip view, or split view) that should default to <c>0</c> outer
    /// padding when <c>view.padding</c> is omitted.
    /// </summary>
    public static bool IsFullBleedRootLayout(CustomWidgetManifest? manifest)
    {
        if (manifest?.View is not { } root)
        {
            return false;
        }

        return root.Type.Trim() switch
        {
            var t when string.Equals(t, "ZStack", StringComparison.OrdinalIgnoreCase) => true,
            var t when string.Equals(t, "FlipView", StringComparison.OrdinalIgnoreCase) => true,
            var t when string.Equals(t, "Image", StringComparison.OrdinalIgnoreCase) => true,
            var t when string.Equals(t, "MediaPlayer", StringComparison.OrdinalIgnoreCase) => true,
            var t when string.Equals(t, "Canvas", StringComparison.OrdinalIgnoreCase) => true,
            var t when string.Equals(t, "SplitView", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    public static WidgetChromeCategory ResolveForWidgetId(string? widgetId, string? rootDataDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return WidgetChromeCategory.Interactive;
        }

        try
        {
            var store = new CustomWidgetDataStore(widgetId, rootDataDirectory, createDirectory: false);
            string manifestPath = store.ManifestFilePath;
            if (!File.Exists(manifestPath))
            {
                return WidgetChromeCategory.Interactive;
            }

            long lastWriteTicks = File.GetLastWriteTimeUtc(manifestPath).Ticks;
            if (DiskCache.TryGetValue(manifestPath, out CachedManifestCategory cached) &&
                cached.LastWriteUtcTicks == lastWriteTicks)
            {
                return cached.Category;
            }

            string json = File.ReadAllText(manifestPath);
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(json);
            WidgetChromeCategory resolved = Classify(parsed.Manifest);
            RecordBoundedDiskCache(manifestPath, lastWriteTicks, resolved);
            return resolved;
        }
        catch
        {
            return WidgetChromeCategory.Interactive;
        }
    }

    private static void RecordBoundedDiskCache(
        string manifestPath,
        long lastWriteTicks,
        WidgetChromeCategory category)
    {
        if (!DiskCache.ContainsKey(manifestPath) && DiskCache.Count >= MaxDiskCacheEntries)
        {
            string? oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach ((string key, CachedManifestCategory entry) in DiskCache)
            {
                if (entry.CachedAtUtcTicks < oldestTicks)
                {
                    oldestTicks = entry.CachedAtUtcTicks;
                    oldestKey = key;
                }
            }

            if (oldestKey is not null)
            {
                DiskCache.TryRemove(oldestKey, out _);
            }
        }

        DiskCache[manifestPath] = new CachedManifestCategory(
            lastWriteTicks,
            category,
            DateTimeOffset.UtcNow.UtcTicks);
    }

    private static bool ContainsInteractiveAuthoringNode(CustomWidgetNodeSpec? node)
    {
        if (node is null)
        {
            return false;
        }

        string type = node.Type.Trim();
        if (string.Equals(type, "Input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "TextArea", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "RichTextInput", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "SearchBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "DropZone", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((string.Equals(type, "List", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase)) &&
            (node.AllowReorder || !string.IsNullOrWhiteSpace(node.InlineEditField)))
        {
            return true;
        }

        if (ContainsAuthoringAction(node.OnClick) ||
            ContainsAuthoringAction(node.OnDropActions) ||
            ContainsAuthoringAction(node.OnRowClick) ||
            ContainsAuthoringAction(node.OnItemClick) ||
            ContainsAuthoringAction(node.OnChange) ||
            ContainsAuthoringAction(node.OnSubmit) ||
            ContainsAuthoringAction(node.OnReorder) ||
            ContainsAuthoringAction(node.OnInlineCommit))
        {
            return true;
        }

        for (int i = 0; i < node.HoverActions.Count; i++)
        {
            if (ContainsAuthoringAction(node.HoverActions[i].OnClick))
            {
                return true;
            }
        }

        for (int i = 0; i < node.MenuItems.Count; i++)
        {
            if (ContainsAuthoringAction(node.MenuItems[i].OnClick))
            {
                return true;
            }
        }

        for (int i = 0; i < node.ContextMenu.Count; i++)
        {
            if (ContainsAuthoringAction(node.ContextMenu[i].OnClick))
            {
                return true;
            }
        }

        if (node.ActionButton is not null && ContainsInteractiveAuthoringNode(node.ActionButton))
        {
            return true;
        }

        if (node.Pane is not null && ContainsInteractiveAuthoringNode(node.Pane))
        {
            return true;
        }

        if (node.ItemTemplate is not null && ContainsInteractiveAuthoringNode(node.ItemTemplate))
        {
            return true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (ContainsInteractiveAuthoringNode(node.Children[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAuthoringAction(IReadOnlyList<CustomWidgetActionSpec>? actions)
    {
        if (actions is null || actions.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < actions.Count; i++)
        {
            string actionType = actions[i].Type;
            if (actionType.StartsWith("collection.", StringComparison.OrdinalIgnoreCase) ||
                actionType.StartsWith("todo.", StringComparison.OrdinalIgnoreCase) ||
                actionType.StartsWith("file.", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actionType, "command.run", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
