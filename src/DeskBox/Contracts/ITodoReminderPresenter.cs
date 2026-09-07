namespace DeskBox.Contracts;

/// <summary>
/// Host-internal capability port for presenting a reminder target inside
/// its widget (pluginization roadmap stage 3, cut point 1). Extracted from
/// WidgetManager.FeatureWidgets.cs where it penetrated the IWidgetContent
/// abstraction by pattern-matching the concrete TodoWidgetContent view type.
///
/// Host-internal port, NOT the future public extension capability API: this
/// seam exists so host code depends on an interface instead of another
/// feature's internals; the extension-facing capability contracts will be
/// separate data-oriented types (roadmap section 5, Extension Model hard
/// constraints).
///
/// Semantics the stage 3b adapter must preserve (current host behavior):
/// resolve the widget hosting the reminder - creating a Todo widget when no
/// live instance matches - wait for its content surface to load and commit,
/// and ask the content to reveal the item. <see cref="TodoReminderPresentationResult.ItemPresented"/>
/// reports only that the reveal call was made; <see cref="TodoReminderPresentationResult.TargetPresented"/>
/// is the end-to-end success signal (window visible + surface committed +
/// item revealed when one was requested) and callers must treat it as the
/// only success signal. Window handles and other host-specific diagnostics
/// stay inside the adapter's logging.
/// </summary>
public interface ITodoReminderPresenter
{
    /// <summary>
    /// Shows (or creates) the widget hosting the reminder and scrolls to /
    /// highlights the target item.
    /// </summary>
    Task<TodoReminderPresentationResult> PresentReminderTargetAsync(
        string? widgetId,
        string? itemId,
        bool preferTodayFilter);
}

/// <summary>Result of a reminder-target presentation attempt.</summary>
/// <param name="WidgetId">The widget that presented (or attempted to present) the target.</param>
/// <param name="ItemPresented">The reveal call succeeded; not a success signal on its own.</param>
/// <param name="TargetPresented">The success signal: visible committed surface and, when an item was requested, the item revealed.</param>
public sealed record TodoReminderPresentationResult(
    string WidgetId,
    bool ItemPresented,
    bool TargetPresented);
