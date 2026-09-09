using Microsoft.UI.Xaml;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// ABI-facing router: forwards host lifecycle events (ABI v4) to the widget
/// controller that owns the live instance state. Probe-era visual side
/// effects are gone (audit rounds 18-19) - the events now drive real timer
/// and refresh behavior inside the controller.
/// </summary>
internal sealed class GlanceWidgetHandle(GlanceWidgetController controller)
{
    private readonly GlanceWidgetController _controller = controller;

    internal GlanceWidgetController Controller => _controller;
    internal int EventsReceived => _controller.EventsReceived;

    internal void OnLifecycleEvent(uint eventKind, double width, double height, uint flags)
    {
        switch (eventKind)
        {
            case 1: // RefreshRequested
                _controller.RefreshRequested();
                break;
            case 5: // VisibilityChanged (flags bit 0: 1=visible)
                _controller.OnVisibilityChanged((flags & 1) != 0);
                break;
            case 7: // LongHidden
                _controller.OnLongHidden();
                break;
            case 8: // CompactStateChanged (flags bit 0: 1=collapsed)
                _controller.OnCompactStateChanged((flags & 1) != 0);
                break;
            case 9: // ViewportChanged (width/height carry the new size)
                _controller.OnViewportChanged(width, height);
                break;
            // 2 AppearanceChanged, 3 Activated, 4 Deactivated, 6 RevealCompleted,
            // 10 PerformanceSettingsChanged, 11-12 interactive resize, 13-15
            // responsive transitions: no package-side behavior yet - these
            // land with the theme push and performance-policy config batches.
        }
    }

    public void Dispose() => _controller.Dispose();
}
