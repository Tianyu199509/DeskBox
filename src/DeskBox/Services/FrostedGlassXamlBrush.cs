using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

internal sealed partial class FrostedGlassXamlBrush(FrameworkElement host) : XamlCompositionBrushBase
{
    private double _strength;
    private CompositionEffectBrush? _effect;
    private CompositionBackdropBrush? _source;

    internal void UpdateStrength(double strength)
    {
        _strength = strength;
        _effect?.Properties.InsertScalar("Frost.BlurAmount", FrostedGlassBlur.Radius(strength));
    }

    protected override void OnConnected()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _source = compositor.CreateBackdropBrush();
        var effect = new GaussianBlurEffect
        {
            Name = "Frost", BlurAmount = FrostedGlassBlur.Radius(_strength),
            BorderMode = EffectBorderMode.Hard, Source = new CompositionEffectSourceParameter("Backdrop")
        };
        using var factory = compositor.CreateEffectFactory(effect, ["Frost.BlurAmount"]);
        _effect = factory.CreateBrush();
        _effect.SetSourceParameter("Backdrop", _source);
        CompositionBrush = _effect;
    }

    protected override void OnDisconnected()
    {
        CompositionBrush = null;
        _effect?.Dispose(); _effect = null;
        _source?.Dispose(); _source = null;
    }
}
