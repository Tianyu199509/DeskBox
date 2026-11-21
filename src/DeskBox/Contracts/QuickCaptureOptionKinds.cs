namespace DeskBox.Contracts;

/// <summary>
/// Canonical option values, bounds and normalizers for the Quick Capture
/// settings section (the single source the section editor binds against;
/// <c>SettingsService</c> keeps its historical constants and normalizers as
/// aliases of these).
/// </summary>
public static class QuickCaptureOptionKinds
{
    public const string DefaultViewRecords = "Records";
    public const string DefaultViewPinned = "Pinned";
    public const string DefaultViewRecent = "Recent";

    public const string FormatMarkdown = "Markdown";
    public const string FormatPlainText = "PlainText";

    public const string WideLayoutAuto = "Auto";
    public const string WideLayoutSinglePane = "SinglePane";
    public const string WideLayoutDualPane = "DualPane";

    public const string WideOpenReading = "Reading";
    public const string WideOpenEditing = "Editing";

    // Tab-style kinds are shared with the Todo section; only the Quick
    // Capture editor needs them contract-side today, so they live here.
    public const string TabStylePivot = "Pivot";
    public const string TabStyleButton = "Button";

    public const string EnterBehaviorCtrlEnterSaves = "CtrlEnterSaves";
    public const string EnterBehaviorEnterSaves = "EnterSaves";

    public const int MinItemPreviewLineCount = 1;
    public const int MaxItemPreviewLineCount = 10;

    // Effective text-size bounds shared with the appearance domain (the
    // global normalizer keeps its own default fallback in SettingsService).
    public const double MinTextSize = 10;
    public const double MaxTextSize = 16;

    public static string NormalizeDefaultView(string? view) =>
        view == DefaultViewPinned
            ? DefaultViewPinned
            : DefaultViewRecords;

    public static string NormalizeFormat(string? format) =>
        string.Equals(format, FormatPlainText, StringComparison.OrdinalIgnoreCase)
            ? FormatPlainText
            : FormatMarkdown;

    public static string NormalizeWideLayout(string? layout)
    {
        if (string.Equals(layout, WideLayoutSinglePane, StringComparison.OrdinalIgnoreCase))
        {
            return WideLayoutSinglePane;
        }

        return string.Equals(layout, WideLayoutDualPane, StringComparison.OrdinalIgnoreCase)
            ? WideLayoutDualPane
            : WideLayoutAuto;
    }

    public static string NormalizeWideOpenMode(string? mode) =>
        string.Equals(mode, WideOpenEditing, StringComparison.OrdinalIgnoreCase)
            ? WideOpenEditing
            : WideOpenReading;

    public static string NormalizeTabStyle(string? style) =>
        style == TabStylePivot ? TabStylePivot : TabStyleButton;

    public static string NormalizeEnterBehavior(string? behavior) =>
        string.Equals(
            behavior,
            EnterBehaviorEnterSaves,
            StringComparison.OrdinalIgnoreCase)
            ? EnterBehaviorEnterSaves
            : EnterBehaviorCtrlEnterSaves;

    public static int NormalizeItemPreviewLineCount(int lineCount) =>
        Math.Clamp(lineCount, MinItemPreviewLineCount, MaxItemPreviewLineCount);

    /// <summary>
    /// Snaps a slider step to the half-point grid and clamps it into the
    /// effective bounds; the coordinator's own write normalizer must agree.
    /// </summary>
    public static double NormalizeTextSizeStep(double value)
    {
        if (!double.IsFinite(value))
        {
            return MinTextSize;
        }

        return Math.Clamp(
            Math.Round(value * 2d, MidpointRounding.AwayFromZero) / 2d,
            MinTextSize,
            MaxTextSize);
    }
}

/// <summary>
/// Canonical attachment storage modes, owned here so the feature-widgets
/// editor can normalize its General-section combo without referencing the
/// settings adapter. <see cref="Services.SettingsService"/> keeps its
/// historical constants as aliases of these.
/// </summary>
public static class AttachmentStorageModes
{
    public const string Link = "Link";
    public const string Copy = "Copy";

    public static string Normalize(string? storageMode) =>
        string.Equals(storageMode, Copy, StringComparison.OrdinalIgnoreCase)
            ? Copy
            : Link;
}
