using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>A color-preserving desktop backdrop with one adjustable blur radius.</summary>
internal sealed partial class FrostedGlassBackdrop : WinUIEx.TransparentTintBackdrop
{
    private FrostedGlassBrush? _brush;
    private double _strength;

    internal FrostedGlassBackdrop(double strength) => _strength = strength;

    internal void UpdateStrength(double strength)
    {
        _strength = strength;
        _brush?.UpdateStrength(strength);
    }

    protected override Windows.UI.Composition.CompositionBrush CreateBrush(
        Windows.UI.Composition.Compositor compositor)
    {
        // This brush is attached to the window's system backdrop, below XAML.
        // Use the raw backdrop; host/acrylic brushes already apply system blur.
        _brush = new FrostedGlassBrush(compositor, _strength);
        return _brush.Brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        try { base.OnTargetDisconnected(target); }
        finally
        {
            _brush?.Dispose();
            _brush = null;
        }
    }
}
