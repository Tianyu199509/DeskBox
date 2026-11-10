using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Read-only bridge from the user's To-do widgets to Glance. Only DeskBox's
/// own data is consulted (no external calendars): every enabled To-do widget's
/// snapshot is scanned for open, due-dated items.
/// </summary>
public sealed class GlanceTodoAgendaSource
{
    private readonly Func<IEnumerable<string>> _todoWidgetIds;
    private readonly Func<string, TodoWidgetStore> _storeFactory;

    public GlanceTodoAgendaSource(SettingsService? settingsService)
        : this(
            () => ResolveTodoWidgetIds(settingsService),
            widgetId => new TodoWidgetStore(widgetId))
    {
    }

    internal GlanceTodoAgendaSource(
        Func<IEnumerable<string>> todoWidgetIds,
        Func<string, TodoWidgetStore> storeFactory)
    {
        _todoWidgetIds = todoWidgetIds;
        _storeFactory = storeFactory;
    }

    /// <summary>
    /// Open items due within [<paramref name="start"/>, <paramref name="endInclusive"/>].
    /// When <paramref name="includeOverdueBefore"/> is set, open items due
    /// before that date are returned too (pinned to their original date).
    /// </summary>
    public async Task<IReadOnlyList<GlanceAgendaItem>> GetAsync(
        DateOnly start,
        DateOnly endInclusive,
        DateOnly? includeOverdueBefore,
        CancellationToken cancellationToken = default)
    {
        var items = new List<GlanceAgendaItem>();
        foreach (string widgetId in _todoWidgetIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            TodoWidgetData data;
            try
            {
                data = await _storeFactory(widgetId).LoadReadOnlySnapshotAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.Log($"[GlanceTodoAgendaSource] Failed to read todo store '{widgetId}': {ex.Message}");
                continue;
            }

            items.AddRange(Project(data.Items, widgetId, start, endInclusive, includeOverdueBefore));
        }

        return Sort(items);
    }

    internal string GetEnabledWidgetsSignature() =>
        string.Join("|", _todoWidgetIds());

    internal static IEnumerable<GlanceAgendaItem> Project(
        IEnumerable<TodoItem> source,
        string widgetId,
        DateOnly start,
        DateOnly endInclusive,
        DateOnly? includeOverdueBefore)
    {
        foreach (TodoItem item in source)
        {
            if (item.IsCompleted || item.IsDeleted || item.DueDate is not { } due ||
                string.IsNullOrWhiteSpace(item.Text))
            {
                continue;
            }

            DateTime local = due.ToLocalTime().DateTime;
            DateOnly date = DateOnly.FromDateTime(local);
            bool inRange = date >= start && date <= endInclusive;
            bool overdue = includeOverdueBefore is { } cutoff && date < cutoff;
            if (!inRange && !overdue)
            {
                continue;
            }

            yield return new GlanceAgendaItem(
                item.Id,
                item.Text.Trim(),
                date,
                item.HasDueTime ? TimeOnly.FromDateTime(local) : null,
                item.IsImportant,
                item.ColorMarker is null ? null : TodoItem.GetColorMarkerHex(item.ColorMarker),
                widgetId);
        }
    }

    internal static IReadOnlyList<GlanceAgendaItem> Sort(IEnumerable<GlanceAgendaItem> items) =>
        items
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Time is null ? 1 : 0)
            .ThenBy(item => item.Time ?? TimeOnly.MinValue)
            .ThenByDescending(item => item.IsImportant)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToArray();

    private static IEnumerable<string> ResolveTodoWidgetIds(SettingsService? settingsService)
    {
        if (settingsService is null ||
            !FeatureWidgetSettings.IsEnabled(settingsService.Settings, WidgetKind.Todo))
        {
            return [];
        }

        try
        {
            return settingsService.Settings.WidgetLayout.Widgets
                .Where(config => config.WidgetKind == WidgetKind.Todo &&
                    !config.IsDisabled &&
                    !string.IsNullOrWhiteSpace(config.Id))
                .Select(config => config.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceTodoAgendaSource] Failed to enumerate todo widgets: {ex.Message}");
            return [];
        }
    }
}
