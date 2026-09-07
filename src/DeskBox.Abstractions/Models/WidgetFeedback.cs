namespace DeskBox.Models;

public enum WidgetFeedbackSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record WidgetFeedbackRequest(
    string Message,
    WidgetFeedbackSeverity Severity = WidgetFeedbackSeverity.Info,
    string? DeduplicationKey = null,
    string? ActionText = null,
    Func<Task>? Action = null)
{
    public TimeSpan DisplayDuration => WidgetFeedbackPolicy.GetDisplayDuration(this);
}

public static class WidgetFeedbackPolicy
{
    public static TimeSpan GetDisplayDuration(WidgetFeedbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.ActionText) && request.Action is not null)
        {
            return TimeSpan.FromMilliseconds(5000);
        }

        return TimeSpan.FromMilliseconds(request.Severity switch
        {
            WidgetFeedbackSeverity.Info => 1800,
            WidgetFeedbackSeverity.Success => 1800,
            WidgetFeedbackSeverity.Warning => 3000,
            WidgetFeedbackSeverity.Error => 4500,
            _ => 1800
        });
    }

    public static bool Replaces(
        WidgetFeedbackRequest? current,
        WidgetFeedbackRequest incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return current is null ||
            string.IsNullOrWhiteSpace(current.DeduplicationKey) ||
            !string.Equals(
                current.DeduplicationKey,
                incoming.DeduplicationKey,
                StringComparison.Ordinal);
    }
}
