using Microsoft.Graphics.Canvas.Effects;
using Windows.UI.Composition;

namespace DeskBox.Services;

internal sealed class FrostedGlassBrush : IDisposable
{
    private readonly CompositionBackdropBrush _source;
    internal CompositionEffectBrush Brush { get; }

    internal FrostedGlassBrush(Compositor compositor, double strength)
    {
        _source = compositor.CreateBackdropBrush();
        var effect = new GaussianBlurEffect
        {
            Name = "Frost",
            BlurAmount = FrostedGlassBlur.Radius(strength),
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("Backdrop")
        };
        using var factory = compositor.CreateEffectFactory(effect, ["Frost.BlurAmount"]);
        Brush = factory.CreateBrush();
        Brush.SetSourceParameter("Backdrop", _source);
    }

    internal void UpdateStrength(double strength) =>
        Brush.Properties.InsertScalar("Frost.BlurAmount", FrostedGlassBlur.Radius(strength));

    public void Dispose()
    {
        Brush.Dispose();
        _source.Dispose();
    }
}
