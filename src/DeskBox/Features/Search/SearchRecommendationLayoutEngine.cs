using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeskBox.Features.Search;

internal readonly record struct RecommendedDropDecision(
    bool IsMerge,
    RecommendedGridEntry? MergeTarget,
    FrameworkElement? MergeTargetElement,
    int ReorderTargetIndex,
    Rect HighlightRect,
    Point InsertBarPosition,
    Rect AnchorRect);

/// <summary>
/// Shared drop-hysteresis geometry, visual-tree traversal, and Composition animation
/// primitives shared by <see cref="Views.SearchPopupWindow"/> and its extracted
/// recommendation grid / overlay controls.
/// </summary>
internal static class SearchRecommendationLayoutEngine
{
    internal const double RecommendationDragThreshold = 6.0;
    internal const double DropAnchorDeadband = 4.0;
    internal const double PopupExternalDragEdgeInset = 1.5;

    internal static readonly SolidColorBrush TransparentCardBrush = new(Colors.Transparent);

    internal static Rect InflateDropAnchorRect(Rect anchor, double margin) =>
        new(
            anchor.X - margin,
            anchor.Y - margin,
            Math.Max(0, anchor.Width + (2 * margin)),
            Math.Max(0, anchor.Height + (2 * margin)));

    internal static Rect ComputeTileMergeAnchorRect(Rect tileRect, bool targetIsFolder)
    {
        double minX = targetIsFolder ? 0.16 : 0.34;
        double maxX = targetIsFolder ? 0.84 : 0.66;
        double minY = targetIsFolder ? 0.12 : 0.20;
        double maxY = targetIsFolder ? 0.88 : 0.80;
        return new Rect(
            tileRect.X + (tileRect.Width * minX),
            tileRect.Y + (tileRect.Height * minY),
            tileRect.Width * (maxX - minX),
            tileRect.Height * (maxY - minY));
    }

    internal static Rect ComputeTileReorderAnchorRect(
        Rect tileRect,
        double relX,
        double relY,
        bool beforeTile,
        bool canMergeOntoTarget,
        bool targetIsFolder)
    {
        double halfWidth = tileRect.Width * 0.5;
        double halfX = beforeTile ? tileRect.X : tileRect.X + halfWidth;

        if (!canMergeOntoTarget)
        {
            return new Rect(halfX, tileRect.Y, halfWidth, tileRect.Height);
        }

        double mergeMinX = targetIsFolder ? 0.16 : 0.34;
        double mergeMaxX = targetIsFolder ? 0.84 : 0.66;
        double mergeMinY = targetIsFolder ? 0.12 : 0.20;
        double mergeMaxY = targetIsFolder ? 0.88 : 0.80;

        if (beforeTile && relX < mergeMinX)
        {
            return new Rect(tileRect.X, tileRect.Y, tileRect.Width * mergeMinX, tileRect.Height);
        }

        if (!beforeTile && relX > mergeMaxX)
        {
            double stripX = tileRect.X + (tileRect.Width * mergeMaxX);
            return new Rect(stripX, tileRect.Y, Math.Max(1.0, tileRect.Right - stripX), tileRect.Height);
        }

        if (relY < mergeMinY)
        {
            return new Rect(halfX, tileRect.Y, halfWidth, tileRect.Height * mergeMinY);
        }

        if (relY > mergeMaxY)
        {
            double stripY = tileRect.Y + (tileRect.Height * mergeMaxY);
            return new Rect(halfX, stripY, halfWidth, Math.Max(1.0, tileRect.Bottom - stripY));
        }

        return new Rect(halfX, tileRect.Y, halfWidth, tileRect.Height);
    }

    internal static Rect ComputeTrailingAppendAnchorRect(
        Point pointInSurface,
        Rect lastTileRect,
        double surfaceWidth,
        double surfaceHeight)
    {
        if (pointInSurface.Y > lastTileRect.Bottom + DropAnchorDeadband)
        {
            double startY = lastTileRect.Bottom + DropAnchorDeadband + 1.0;
            return new Rect(
                0,
                startY,
                Math.Max(16.0, Math.Max(surfaceWidth, lastTileRect.Right)),
                Math.Max(16.0, surfaceHeight - startY));
        }

        double startX = lastTileRect.Right + DropAnchorDeadband + 1.0;
        return new Rect(
            startX,
            lastTileRect.Y,
            Math.Max(16.0, surfaceWidth - startX),
            Math.Max(16.0, surfaceHeight - lastTileRect.Y));
    }

    internal static bool IsPointLeavingPopupDragBounds(
        Point pointInRoot,
        double rootWidth,
        double rootHeight) =>
        rootWidth > (2 * PopupExternalDragEdgeInset) &&
        rootHeight > (2 * PopupExternalDragEdgeInset) &&
        (pointInRoot.X <= PopupExternalDragEdgeInset ||
         pointInRoot.Y <= PopupExternalDragEdgeInset ||
         pointInRoot.X >= rootWidth - PopupExternalDragEdgeInset ||
         pointInRoot.Y >= rootHeight - PopupExternalDragEdgeInset);

    internal static bool WouldBatchReorderChangeOrder(
        IReadOnlyList<RecommendedGridEntry> entries,
        IReadOnlySet<string> draggedEntryKeys,
        int targetVisibleIndex)
    {
        int count = entries.Count;
        if (count <= 1 || draggedEntryKeys.Count == 0)
        {
            return false;
        }

        int clampedSlot = Math.Clamp(targetVisibleIndex, 0, count);
        int draggedBeforeSlot = 0;
        for (int i = 0; i < clampedSlot; i++)
        {
            if (draggedEntryKeys.Contains(entries[i].EntryKey))
            {
                draggedBeforeSlot++;
            }
        }

        List<string> projected = ProjectReorderedEntryKeys(
            entries,
            draggedEntryKeys,
            clampedSlot - draggedBeforeSlot);
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(projected[i], entries[i].EntryKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Projects the entry-key order a reorder commit produces: the dragged
    /// block (kept in current order) removed and re-inserted at
    /// <paramref name="insertIndexInRemaining"/> of the remaining entries.
    /// Returns an empty list for degenerate inputs (nothing to move).
    /// </summary>
    internal static List<string> ProjectReorderedEntryKeys(
        IReadOnlyList<RecommendedGridEntry> entries,
        IReadOnlySet<string> draggedEntryKeys,
        int insertIndexInRemaining)
    {
        int count = entries.Count;
        if (count <= 1 || draggedEntryKeys.Count == 0)
        {
            return [];
        }

        var movingKeys = new List<string>(draggedEntryKeys.Count);
        var remainingKeys = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string entryKey = entries[i].EntryKey;
            if (draggedEntryKeys.Contains(entryKey))
            {
                movingKeys.Add(entryKey);
            }
            else
            {
                remainingKeys.Add(entryKey);
            }
        }

        if (movingKeys.Count == 0)
        {
            return [];
        }

        remainingKeys.InsertRange(
            Math.Clamp(insertIndexInRemaining, 0, remainingKeys.Count),
            movingKeys);
        return remainingKeys;
    }

    /// <summary>
    /// Merge-zone geometry for drop targets. Folder tiles accept across most
    /// of the card while narrow edge strips keep insert-here reachable.
    /// App tiles keep the small centre band so folders are only created
    /// deliberately, and batch selections never merge onto single apps.
    /// </summary>
    internal static bool ShouldMergeOntoTile(
        double relX,
        double relY,
        bool targetIsFolder,
        bool isBatchDrag) =>
        targetIsFolder
            ? relX >= 0.16 && relX <= 0.84 && relY >= 0.12 && relY <= 0.88
            : !isBatchDrag && relX >= 0.34 && relX <= 0.66 && relY >= 0.20 && relY <= 0.80;

    internal static void AutoScrollScrollViewerOnDrag(ScrollViewer viewer, Point pointInViewer)
    {
        if (viewer.ScrollableHeight <= 0 || viewer.ActualHeight <= 0)
        {
            return;
        }

        const double edgeZone = 34.0;
        const double step = 10.0;
        if (pointInViewer.Y < edgeZone && viewer.VerticalOffset > 0)
        {
            viewer.ChangeView(null, Math.Max(0, viewer.VerticalOffset - step), null, disableAnimation: true);
        }
        else if (pointInViewer.Y > viewer.ActualHeight - edgeZone &&
                 viewer.VerticalOffset < viewer.ScrollableHeight)
        {
            viewer.ChangeView(
                null,
                Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + step),
                null,
                disableAnimation: true);
        }
    }

    internal static bool Intersects(Rect a, Rect b) =>
        a.X < b.X + b.Width &&
        a.X + a.Width > b.X &&
        a.Y < b.Y + b.Height &&
        a.Y + a.Height > b.Y;

    internal static bool IsVisualDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    internal static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    internal static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
            {
                return result;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    internal static T? FindVisualChild<T>(DependencyObject root, string childName)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T fe && string.Equals(fe.Name, childName, StringComparison.Ordinal))
            {
                return fe;
            }

            var nested = FindVisualChild<T>(child, childName);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    internal static Brush? ResolveThemeBrush(FrameworkElement scope, string key) =>
        NeutralInteractionBrush.ResolveThemedResource(key, scope);

    internal static void ResetCardContentHostVisual(FrameworkElement contentHost)
    {
        var visual = ElementCompositionPreview.GetElementVisual(contentHost);
        if (visual is null)
        {
            return;
        }

        visual.StopAnimation("Scale.X");
        visual.StopAnimation("Scale.Y");
        visual.Scale = System.Numerics.Vector3.One;
    }

    internal static void ArmImplicitOffsetAnimationAfterLayout(
        FrameworkElement card,
        DispatcherQueue dispatcherQueue,
        Func<bool> canArmPredicate,
        Func<bool> areAnimationsEnabled)
    {
        if (!areAnimationsEnabled())
        {
            return;
        }

        dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!canArmPredicate() || card.DataContext is null || !areAnimationsEnabled())
            {
                return;
            }

            var visual = ElementCompositionPreview.GetElementVisual(card);
            if (visual is null || visual.ImplicitAnimations is not null)
            {
                return;
            }

            var compositor = visual.Compositor;
            var decel = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.0f, 0.0f),
                new System.Numerics.Vector2(0.1f, 1.0f));
            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.Target = "Offset";
            offsetAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", decel);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(190);

            var implicitCollection = compositor.CreateImplicitAnimationCollection();
            implicitCollection["Offset"] = offsetAnim;
            visual.ImplicitAnimations = implicitCollection;
        });
    }

    internal static void DetachImplicitOffsetAnimation(FrameworkElement card)
    {
        var visual = ElementCompositionPreview.GetElementVisual(card);
        if (visual is not null)
        {
            visual.ImplicitAnimations = null;
        }
    }

    internal static void AnimateMergeTargetScale(FrameworkElement host, float targetScale, int durationMs, bool animationsEnabled)
    {
        var visual = ElementCompositionPreview.GetElementVisual(host);
        if (visual is null)
        {
            return;
        }

        if (!animationsEnabled)
        {
            visual.Scale = new System.Numerics.Vector3(targetScale, targetScale, 1f);
            return;
        }

        float cx = visual.Size.X > 0
            ? visual.Size.X / 2f
            : (float)(host.ActualWidth > 0 ? host.ActualWidth : 86) / 2f;
        float cy = visual.Size.Y > 0
            ? visual.Size.Y / 2f
            : (float)(host.ActualHeight > 0 ? host.ActualHeight : 78) / 2f;
        visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

        var compositor = visual.Compositor;
        var decel = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.0f, 0.0f),
            new System.Numerics.Vector2(0.1f, 1.0f));
        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.InsertKeyFrame(1.0f, targetScale, decel);
        scaleAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale.X", scaleAnim);
        visual.StartAnimation("Scale.Y", scaleAnim);
    }

    internal static void AnimateDragGhostPickup(FrameworkElement ghostElement, bool animationsEnabled)
    {
        var visual = ElementCompositionPreview.GetElementVisual(ghostElement);
        if (visual is null)
        {
            return;
        }

        if (!animationsEnabled)
        {
            visual.Scale = System.Numerics.Vector3.One;
            return;
        }

        float cx = (float)(ghostElement.Width > 0 && !double.IsNaN(ghostElement.Width)
            ? ghostElement.Width
            : 100) / 2f;
        float cy = (float)(ghostElement.Height > 0 && !double.IsNaN(ghostElement.Height)
            ? ghostElement.Height
            : 88) / 2f;
        visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

        var compositor = visual.Compositor;
        var decel = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.0f, 0.0f),
            new System.Numerics.Vector2(0.1f, 1.0f));
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0.0f, 0.91f);
        anim.InsertKeyFrame(1.0f, 1.04f, decel);
        anim.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Scale.X", anim);
        visual.StartAnimation("Scale.Y", anim);
    }

    internal static void StartScalarAnim(
        Compositor compositor,
        Visual visual,
        string property,
        TimeSpan delay,
        int durationMs,
        CompositionEasingFunction easing,
        float from,
        float to)
    {
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, from);
        anim.InsertKeyFrame(1f, to, easing);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        anim.DelayTime = delay;
        visual.StartAnimation(property, anim);
    }

    internal static T? FindDataContext<T>(DependencyObject? element) where T : class
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: T data })
            {
                return data;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }
}
