namespace DeskBox.Contracts;

/// <summary>
/// Capability surface for presenting a reminder target inside its widget
/// (pluginization roadmap stage 3, cut point 1). Extracted from
/// WidgetManager.FeatureWidgets.cs where it penetrated the IWidgetContent
/// abstraction by pattern-matching the concrete TodoWidgetContent view type.
/// The host implementation resolves the widget window, makes it visible,
/// and asks the content to reveal the specific reminder item; consumers
/// (currently the Todo reminder service) see only this contract.
/// </summary>
public interface ITodoReminderPresenter
{
    /// <summary>
    /// Shows the widget hosting the reminder and scrolls to / highlights the
    /// target item. Returns null when no matching live widget exists (the
    /// caller may then use the fallback toast-notification-only flow).
    /// </summary>
    Task<TodoReminderTargetPresentation?> TryPresentReminderTargetAsync(
        string? widgetId,
        string? itemId,
        bool preferTodayFilter);
}

/// <summary>Result of a successful reminder-target presentation.</summary>
public sealed record TodoReminderTargetPresentation(
    string WidgetId,
    bool ItemRevealed);
