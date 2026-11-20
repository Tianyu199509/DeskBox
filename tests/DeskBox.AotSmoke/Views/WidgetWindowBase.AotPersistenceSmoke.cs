#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    internal AotPersistenceSmokePhysicalBounds CaptureAotPersistenceSmokeBounds()
    {
        RectInt32 bounds = GetActualWindowBounds();
        return new AotPersistenceSmokePhysicalBounds(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height);
    }

    internal AotPersistenceSmokePhysicalBounds ApplyAotPersistenceSmokeBounds(
        AotPersistenceSmokePhysicalBounds requested)
    {
        if (IsCompactBoundsStateActive || Config.IsCollapsed)
        {
            throw new InvalidOperationException(
                "The persistence smoke only supports an expanded fixed widget surface.");
        }

        int requestedWidth = Math.Max(MinWidth, requested.Width);
        int requestedHeight = Math.Max(MinHeight, requested.Height);
        var requestedRect = new RectInt32(
            requested.X,
            requested.Y,
            requestedWidth,
            requestedHeight);
        RectInt32 workArea = DisplayArea.GetFromRect(
            requestedRect,
            DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min(requestedWidth, workArea.Width);
        int height = Math.Min(requestedHeight, workArea.Height);
        int x = Math.Clamp(
            requested.X,
            workArea.X,
            workArea.X + Math.Max(0, workArea.Width - width));
        int y = Math.Clamp(
            requested.Y,
            workArea.Y,
            workArea.Y + Math.Max(0, workArea.Height - height));
        var safeBounds = new RectInt32(x, y, width, height);

        MoveWindowWithoutPersisting(safeBounds);
        RectInt32 actual = GetActualWindowBounds();
        CapturePositionAnchor(
            actual.X,
            actual.Y,
            actual.Width,
            actual.Height,
            preserveCurrentEdge: false);
        UpdateConfigBoundsFromPhysical(
            actual.X,
            actual.Y,
            actual.Width,
            actual.Height,
            persist: true);

        return new AotPersistenceSmokePhysicalBounds(
            actual.X,
            actual.Y,
            actual.Width,
            actual.Height);
    }
}

internal sealed record AotPersistenceSmokePhysicalBounds(
    int X,
    int Y,
    int Width,
    int Height);
#endif
