using DeskBox.Models;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal static WidgetItem[] ResolvePendingPointerDragItemsOnPress(
        WidgetItem pressedItem,
        IReadOnlyList<WidgetItem> currentSelectedItems,
        bool shiftPressed,
        bool controlPressed = false)
    {
        if (currentSelectedItems.Contains(pressedItem))
        {
            return currentSelectedItems
                .Distinct()
                .ToArray();
        }

        // When nothing is visibly selected and the user presses Shift+Drag on an
        // item, WinUI ListViewBase's hidden focus anchor at index 0 would otherwise
        // range-select 0..N before DragStarting fires. Snapshotting [pressedItem]
        // ensures anchorless Shift+Drag drags only the pressed item.
        if (shiftPressed &&
            currentSelectedItems.Count == 0)
        {
            return [pressedItem];
        }

        // Plain-pressing an unselected item while another selection (e.g. 3 items)
        // is active must snapshot [pressedItem] so Items_DragStarting immediately
        // converges SelectedItems to [pressedItem] instead of leaving the stale
        // multi-selection highlighted during a single-file drag.
        if (!shiftPressed &&
            !controlPressed &&
            currentSelectedItems.Count > 0)
        {
            return [pressedItem];
        }

        return [];
    }

    private async Task TraceDragSourcePresenceAsync(
        string[] sourcePaths,
        string? sessionId,
        DataPackageOperation reportedOperation)
    {
        // Observe off the dispatcher; this is evidence only. Never recover,
        // delete, or move a source based on a receiver's completion receipt.
        string widgetId = WidgetId;
        CancellationToken cancellationToken = _lifetimeCancellation.Token;
        try
        {
            for (int observation = 0; observation < 2; observation++)
            {
                if (observation > 0)
                {
                    await Task.Delay(1000, cancellationToken);
                }
                string[] missing = await Task.Run(() => sourcePaths
                    .Where(path => !File.Exists(path) && !Directory.Exists(path))
                    .ToArray(), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                App.Log($"[DragProtocol] stage=SourcePresence widget={widgetId} " +
                    $"session={FormatDragSessionId(sessionId)} policy=SourceGuard " +
                    $"reported={reportedOperation} observation={observation} " +
                    $"tracked={sourcePaths.Length} missingOrUnavailable={missing.Length} " +
                    $"pathSample='{string.Join(" | ", missing.Take(5))}'");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[DragProtocol] Source presence observation failed " +
                $"widget={widgetId} session={FormatDragSessionId(sessionId)}: {ex.Message}");
        }
    }
}
