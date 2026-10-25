namespace DeskBox.Services;

internal static class FrostedGlassBlur
{
    internal static float Radius(double strength) =>
        (float)(32 * (double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 0.35));
}
