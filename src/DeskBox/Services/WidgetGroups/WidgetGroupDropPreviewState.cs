namespace DeskBox.Services;

/// <summary>
/// What a merge target should draw while another widget is dragged over it.
/// Produced by <see cref="WidgetManager"/>, rendered by the target's shell.
/// </summary>
/// <param name="Visible">Whether any preview is shown.</param>
/// <param name="Ready">The dwell completed: releasing now merges.</param>
/// <param name="Blocked">The target cannot accept this source.</param>
/// <param name="Zone">Which target zone the pointer rests on.</param>
/// <param name="Dwell">Duration of the arming countdown (progress ring).</param>
/// <param name="Message">Already-localized caption, or null for none.</param>
/// <param name="PointerOffsetY">Pointer Y in target-local DIPs (for keeping the badge clear of the dragged thumbnail), or NaN.</param>
public readonly record struct WidgetGroupDropPreviewState(
    bool Visible,
    bool Ready,
    bool Blocked,
    WidgetGroupDropPreviewZone Zone,
    TimeSpan Dwell,
    string? Message,
    double PointerOffsetY = double.NaN)
{
    public static WidgetGroupDropPreviewState Hidden { get; } =
        new(false, false, false, WidgetGroupDropPreviewZone.None, TimeSpan.Zero, null, double.NaN);
}

/// <summary>
/// Public mirror of the internal hit-test zone for the view layer.
/// </summary>
public enum WidgetGroupDropPreviewZone
{
    None,
    Title,
    Center
}
