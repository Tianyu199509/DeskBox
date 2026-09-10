namespace DeskBox.GlancePackage.Rendering;

/// <summary>Freezes the intended viewport while the host animates its outer shell.</summary>
internal sealed class GlanceViewportCoordinator(double width, double height)
{
    private (double Width, double Height)? _origin;
    internal double Width { get; private set; } = width;
    internal double Height { get; private set; } = height;
    internal bool IsTransitionActive => _origin.HasValue;

    internal bool Resize(double width, double height) => !IsTransitionActive && Set(width, height);

    internal bool Begin(double width, double height)
    {
        if (!Valid(width, height)) return false;
        if (Math.Abs(width - Width) < 1 && Math.Abs(height - Height) < 1) return false;
        _origin ??= (Width, Height);
        return Set(width, height);
    }

    internal bool Complete(double width, double height)
    {
        if (!Valid(width, height)) return false;
        bool changed = Set(width, height);
        _origin = null;
        return changed;
    }

    internal bool Cancel()
    {
        if (_origin is not { } origin) return false;
        _origin = null;
        return Set(origin.Width, origin.Height);
    }

    private bool Set(double width, double height)
    {
        if (!Valid(width, height) || (Math.Abs(width - Width) < 1 && Math.Abs(height - Height) < 1)) return false;
        Width = width;
        Height = height;
        return true;
    }

    private static bool Valid(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}
