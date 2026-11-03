namespace DeskBox.Contracts;

/// <summary>
/// Canonical option values, bounds and normalizers for the Todo settings
/// section (the single source the section editor binds against;
/// <c>SettingsService</c> keeps its historical constants and normalizers as
/// aliases of these). The preview-line-count, enter-behavior, tab-style and
/// effective-text-size kinds stay in <see cref="QuickCaptureOptionKinds"/>
/// because both content sections share them.
/// </summary>
public static class TodoOptionKinds
{
    public const string DefaultFilterAll = "All";
    public const string DefaultFilterActive = "Active";
    public const string DefaultFilterToday = "Today";
    public const string DefaultFilterOverdue = "Overdue";
    public const string DefaultFilterThisWeek = "ThisWeek";
    public const string DefaultFilterThisMonth = "ThisMonth";
    public const string DefaultFilterImportant = "Important";
    public const string DefaultFilterCompleted = "Completed";

    public const string LayoutModeAuto = "Auto";
    public const string LayoutModeSinglePane = "SinglePane";
    public const string LayoutModeDualPane = "DualPane";

    public const string NewTaskPositionTop = "Top";
    public const string NewTaskPositionBottom = "Bottom";

    public const int DefaultItemPreviewLineCount = 2;

    public const int DefaultReminderOffsetMinutes = 5;
    public const int MinReminderOffsetMinutes = 0;
    public const int MaxReminderOffsetMinutes = 1440;

    public const int DefaultAllDayReminderTimeMinutes = 9 * 60;
    public const int MinAllDayReminderTimeMinutes = 0;
    public const int MaxAllDayReminderTimeMinutes = (23 * 60) + 59;

    /// <summary>The offset choices offered by the reminder-offset combo.</summary>
    public static readonly int[] ReminderOffsetSteps = [0, 5, 10, 15, 30, 60, 1440];

    public static string NormalizeLayoutMode(
        string? mode,
        bool legacyUseWideDetailPane = true)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return legacyUseWideDetailPane
                ? LayoutModeAuto
                : LayoutModeSinglePane;
        }

        if (string.Equals(mode, LayoutModeSinglePane, StringComparison.OrdinalIgnoreCase))
        {
            return LayoutModeSinglePane;
        }

        return string.Equals(mode, LayoutModeDualPane, StringComparison.OrdinalIgnoreCase)
            ? LayoutModeDualPane
            : LayoutModeAuto;
    }

    public static string NormalizeNewTaskPosition(string? position) =>
        position == NewTaskPositionBottom
            ? NewTaskPositionBottom
            : NewTaskPositionTop;

    public static string NormalizeDefaultFilter(string? filter) => filter is
        DefaultFilterActive or
        DefaultFilterToday or
        DefaultFilterOverdue or
        DefaultFilterThisWeek or
        DefaultFilterThisMonth or
        DefaultFilterImportant or
        DefaultFilterCompleted
        ? filter
        : DefaultFilterAll;

    public static int NormalizeReminderOffsetMinutes(int minutes)
    {
        return minutes is 0 or 5 or 10 or 15 or 30 or 60 or 1440
            ? minutes
            : DefaultReminderOffsetMinutes;
    }

    public static int NormalizeAllDayReminderTimeMinutes(int minutes)
    {
        return minutes is >= MinAllDayReminderTimeMinutes and <= MaxAllDayReminderTimeMinutes
            ? minutes
            : DefaultAllDayReminderTimeMinutes;
    }
}
