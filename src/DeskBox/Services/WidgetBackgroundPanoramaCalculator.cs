namespace DeskBox.Services;

/// <summary>
/// Maps a widget window's physical screen rectangle onto the panorama image
/// for the global Panorama background mode. The image is fitted with
/// UniformToFill over the desktop canvas (the virtual-screen bounding box in
/// physical pixels), and every widget samples the slice under its own
/// position — the widget-level analogue of the Windows "Span" wallpaper fit.
/// Pure math so the exact matrix can be contract-tested.
/// </summary>
public static class WidgetBackgroundPanoramaCalculator
{
    /// <summary>
    /// Computes the ImageBrush Viewbox (absolute image pixels) for a window
    /// rectangle, or null when the window lies completely outside the fitted
    /// image (nothing to sample).
    /// </summary>
    public static Windows.Foundation.Rect? ComputeViewbox(
        double imageWidth,
        double imageHeight,
        double canvasX,
        double canvasY,
        double canvasWidth,
        double canvasHeight,
        double windowX,
        double windowY,
        double windowWidth,
        double windowHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 ||
            canvasWidth <= 0 || canvasHeight <= 0 ||
            windowWidth <= 0 || windowHeight <= 0)
        {
            return null;
        }

        // UniformToFill over the desktop canvas.
        double scale = Math.Max(
            canvasWidth / imageWidth,
            canvasHeight / imageHeight);
        double fittedWidth = imageWidth * scale;
        double fittedHeight = imageHeight * scale;
        double fittedX = canvasX - (fittedWidth - canvasWidth) / 2;
        double fittedY = canvasY - (fittedHeight - canvasHeight) / 2;

        // Intersect the window with the fitted image, then map back to
        // image pixels. Widgets may hang off the desktop bounds; sampling
        // clamps to the image instead of stretching the edge.
        double left = Math.Max(windowX, fittedX);
        double top = Math.Max(windowY, fittedY);
        double right = Math.Min(windowX + windowWidth, fittedX + fittedWidth);
        double bottom = Math.Min(windowY + windowHeight, fittedY + fittedHeight);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new Windows.Foundation.Rect(
            (left - fittedX) / scale,
            (top - fittedY) / scale,
            (right - left) / scale,
            (bottom - top) / scale);
    }

    /// <summary>
    /// UniformToFill fit of the image over ONE monitor, expressed as the
    /// fitted rectangle's top-left offset inside the monitor plus its size.
    /// This is the widget-level analogue of how Windows fills each monitor
    /// with the wallpaper independently. A single fit over the whole
    /// virtual-desktop union only works when every monitor shares one DPI:
    /// on mixed-DPI / mixed-resolution setups the union fit renders the
    /// sampled slice zoomed, while the per-monitor fit stays purely local
    /// (monitor rect + the window's own DPI scale) and recomputes cleanly
    /// after every topology or monitor change.
    /// </summary>
    public static (double OffsetX, double OffsetY, double Width, double Height) ComputeMonitorFit(
        double imageWidth,
        double imageHeight,
        double monitorWidth,
        double monitorHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 ||
            monitorWidth <= 0 || monitorHeight <= 0)
        {
            return (0, 0, monitorWidth, monitorHeight);
        }

        double scale = Math.Max(
            monitorWidth / imageWidth,
            monitorHeight / imageHeight);
        double fittedWidth = imageWidth * scale;
        double fittedHeight = imageHeight * scale;
        return (
            -(fittedWidth - monitorWidth) / 2,
            -(fittedHeight - monitorHeight) / 2,
            fittedWidth,
            fittedHeight);
    }
}
