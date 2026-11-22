using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Which part of a merge target the dragged widget's pointer rests on.
/// </summary>
internal enum WidgetGroupDropZone
{
    None,

    /// <summary>
    /// The visible title strip: the fast lane, armed after a short dwell.
    /// </summary>
    Title,

    /// <summary>
    /// The inner half of the target surface: the large, forgiving zone,
    /// armed after a longer dwell. The outer ring stays reserved for edge
    /// snapping so arranging widgets side by side never merges them.
    /// </summary>
    Center
}

internal static class WidgetGroupDropHitTestPolicy
{
    /// <summary>Fraction of each surface edge excluded from the center zone.</summary>
    public const double CenterZoneInsetFraction = 0.25;

    /// <summary>Logical (DIP) floor for the center zone on small targets.</summary>
    public const int CenterZoneMinimumLogicalSize = 80;

    public static readonly TimeSpan TitleDwell = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan CenterDwell = TimeSpan.FromMilliseconds(450);

    public static bool Contains(RectInt32? bounds, int screenX, int screenY)
    {
        if (bounds is not { Width: > 0, Height: > 0 } value)
        {
            return false;
        }

        long right = (long)value.X + value.Width;
        long bottom = (long)value.Y + value.Height;
        return screenX >= value.X &&
               screenX < right &&
               screenY >= value.Y &&
               screenY < bottom;
    }

    /// <summary>
    /// The inner rectangle of <paramref name="surface"/> left after insetting
    /// each edge by <see cref="CenterZoneInsetFraction"/>, grown back to
    /// <paramref name="minimumSize"/> on small targets and never exceeding
    /// the surface itself.
    /// </summary>
    public static RectInt32? GetCenterZone(RectInt32? surface, int minimumSize)
    {
        if (surface is not { Width: > 0, Height: > 0 } value)
        {
            return null;
        }

        int width = ResolveCenterExtent(value.Width, minimumSize);
        int height = ResolveCenterExtent(value.Height, minimumSize);
        return new RectInt32(
            value.X + ((value.Width - width) / 2),
            value.Y + ((value.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// Resolves the zone under the pointer. The title strip wins over the
    /// center zone where they overlap (small or collapsed targets) because it
    /// is the more deliberate aim and has the shorter dwell.
    /// </summary>
    public static WidgetGroupDropZone ResolveZone(
        RectInt32? surface,
        RectInt32? title,
        int screenX,
        int screenY,
        int minimumCenterSize)
    {
        if (Contains(title, screenX, screenY))
        {
            return WidgetGroupDropZone.Title;
        }

        return Contains(GetCenterZone(surface, minimumCenterSize), screenX, screenY)
            ? WidgetGroupDropZone.Center
            : WidgetGroupDropZone.None;
    }

    public static TimeSpan GetDwell(WidgetGroupDropZone zone) =>
        zone == WidgetGroupDropZone.Title ? TitleDwell : CenterDwell;

    private static int ResolveCenterExtent(int surfaceExtent, int minimumSize)
    {
        int inset = (int)Math.Round(surfaceExtent * CenterZoneInsetFraction);
        int extent = Math.Max(1, surfaceExtent - (2 * inset));
        return Math.Min(surfaceExtent, Math.Max(extent, Math.Max(1, minimumSize)));
    }
}
