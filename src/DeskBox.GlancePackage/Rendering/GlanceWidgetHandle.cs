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
            case 2: // AppearanceChanged: re-read effective host tokens
                _controller.OnAppearanceChanged();
                break;
            case 1: // RefreshRequested
                // Bit 0 marks a settings-only notification. flags=0 keeps
                // the full refresh behavior of ordinary ABI v4 callers.
                _controller.RefreshRequested(refreshImages: (flags & 1) == 0);
                break;
            case 5: // VisibilityChanged (flags bit 0: 1=visible)
                _controller.OnVisibilityChanged((flags & 1) != 0);
                break;
            case 6:
                _controller.OnRevealCompleted();
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
            case 10: // PerformanceSettingsChanged
                _controller.OnPerformanceSettingsChanged();
                break;
            case 11:
                _controller.BeginInteractiveResize();
                break;
            case 12:
                _controller.CompleteInteractiveResize(width, height);
                break;
            case 13:
                _controller.BeginResponsiveLayoutTransition(width, height);
                break;
            case 14:
                _controller.CompleteResponsiveLayoutTransition(width, height);
                break;
            case 15:
                _controller.CancelResponsiveLayoutTransition();
                break;
            // Activated/Deactivated have no additional content-side policy.
        }
    }

    public void Dispose() => _controller.Dispose();
}
