using System.Globalization;
using DeskBox.Models;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Preserves each widget's standalone logical size across group membership so
/// joining a larger/smaller group never permanently overwrites the member's own
/// dimensions when it is later detached or dissolved back onto the desktop.
/// Stored inside <see cref="WidgetConfig.Metadata"/> so group rollback snapshots
/// and settings persistence carry it automatically.
/// </summary>
internal static class WidgetGroupStandaloneSizeMemory
{
    public const string PreGroupWidthKey = "group.preStandaloneWidth";
    public const string PreGroupHeightKey = "group.preStandaloneHeight";
    public const string PreGroupBoundsVersionKey = "group.preStandaloneBoundsVersion";

    /// <summary>
    /// Records the widget's current standalone dimensions before group layout
    /// overwrites them. Members moving from one group directly into another
    /// keep their original pre-group dimensions untouched.
    /// </summary>
    public static void CaptureIfStandalone(WidgetConfig member, bool wasAlreadyGrouped)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (wasAlreadyGrouped)
        {
            return;
        }

        if (!double.IsFinite(member.Width) || member.Width <= 0 ||
            !double.IsFinite(member.Height) || member.Height <= 0)
        {
            return;
        }

        member.Metadata[PreGroupWidthKey] = member.Width.ToString(
            "R",
            CultureInfo.InvariantCulture);
        member.Metadata[PreGroupHeightKey] = member.Height.ToString(
            "R",
            CultureInfo.InvariantCulture);
        member.Metadata[PreGroupBoundsVersionKey] = member.BoundsCoordinateVersion.ToString(
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the remembered standalone size without mutating <paramref name="member"/>,
    /// falling back to the member's current size when no pre-group size was stored.
    /// </summary>
    public static bool TryPeekStandaloneSize(
        WidgetConfig member,
        out double width,
        out double height,
        out int boundsCoordinateVersion)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.Metadata.TryGetValue(PreGroupWidthKey, out string? widthRaw) &&
            member.Metadata.TryGetValue(PreGroupHeightKey, out string? heightRaw) &&
            double.TryParse(widthRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedW) &&
            double.TryParse(heightRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedH) &&
            double.IsFinite(parsedW) && parsedW > 0 &&
            double.IsFinite(parsedH) && parsedH > 0)
        {
            width = parsedW;
            height = parsedH;
            boundsCoordinateVersion =
                member.Metadata.TryGetValue(PreGroupBoundsVersionKey, out string? versionRaw) &&
                int.TryParse(versionRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVersion)
                    ? parsedVersion
                    : WidgetConfig.CurrentBoundsCoordinateVersion;
            return true;
        }

        width = member.Width;
        height = member.Height;
        boundsCoordinateVersion = member.BoundsCoordinateVersion;
        return false;
    }

    /// <summary>
    /// Resolves the physical pixel size a detached preview silhouette should use
    /// at <paramref name="dpiScale"/>, falling back to <paramref name="fallbackPhysicalSize"/>
    /// if dimensions are unavailable.
    /// </summary>
    public static SizeInt32 ResolveDetachedPhysicalSize(
        WidgetConfig? member,
        double dpiScale,
        SizeInt32 fallbackPhysicalSize)
    {
        if (member is null)
        {
            return fallbackPhysicalSize;
        }

        _ = TryPeekStandaloneSize(member, out double width, out double height, out int version);
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
        {
            return fallbackPhysicalSize;
        }

        double effectiveScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        if (version == WidgetConfig.CurrentBoundsCoordinateVersion)
        {
            return new SizeInt32(
                Math.Max(1, (int)Math.Round(width * effectiveScale)),
                Math.Max(1, (int)Math.Round(height * effectiveScale)));
        }

        return new SizeInt32(
            Math.Max(1, (int)Math.Round(width)),
            Math.Max(1, (int)Math.Round(height)));
    }

    /// <summary>
    /// Restores the remembered standalone size onto <paramref name="member"/>
    /// and clears the stored metadata keys.
    /// </summary>
    public static bool RestoreAndClear(WidgetConfig member)
    {
        ArgumentNullException.ThrowIfNull(member);
        bool hadStored = TryPeekStandaloneSize(
            member,
            out double width,
            out double height,
            out int version);
        member.Metadata.Remove(PreGroupWidthKey);
        member.Metadata.Remove(PreGroupHeightKey);
        member.Metadata.Remove(PreGroupBoundsVersionKey);
        if (!hadStored)
        {
            return false;
        }

        member.Width = width;
        member.Height = height;
        member.BoundsCoordinateVersion = version;
        return true;
    }
}
