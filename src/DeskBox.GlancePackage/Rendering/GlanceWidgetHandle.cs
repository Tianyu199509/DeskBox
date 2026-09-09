using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Receives host lifecycle events (ABI v4) and routes them to the widget's
/// visual tree. This is the package-side counterpart of IWidgetContent's
/// optional lifecycle interfaces. ViewportChanged also forwards the new size
/// to the view builder's debounced responsive rebuild.
/// </summary>
internal sealed class GlanceWidgetHandle(FrameworkElement view, Action<double, double>? onViewportChanged = null)
{
    private readonly FrameworkElement _view = view;
    private readonly Action<double, double>? _onViewportChanged = onViewportChanged;
    internal int EventsReceived;

    internal void OnLifecycleEvent(uint eventKind, double width, double height, uint flags)
    {
        EventsReceived++;
        switch (eventKind)
        {
            case 5: // VisibilityChanged
                bool visible = (flags & 1) != 0;
                _view.Opacity = visible ? 1.0 : 0.9; // subtle visual feedback for the probe
                break;
            case 7: // LongHidden
                // Stop expensive work (timer already stops via Unloaded).
                break;
            case 8: // CompactStateChanged
                bool collapsed = (flags & 1) != 0;
                _view.MaxHeight = collapsed ? 260 : double.PositiveInfinity;
                break;
            case 9: // ViewportChanged
                _view.Width = width;
                _view.Height = height;
                _onViewportChanged?.Invoke(width, height);
                break;
        }
    }
}
