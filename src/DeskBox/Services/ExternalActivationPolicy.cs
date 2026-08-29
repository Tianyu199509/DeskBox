namespace DeskBox.Services;

internal enum BareExternalActivationAction
{
    OpenSettingsOnly,
    RestoreAllWidgetsAndOpenSettings
}

internal readonly record struct BareExternalActivationContext(
    bool HasConfiguredFileWidgets,
    bool HasVisibleFileWidgets);

internal static class ExternalActivationPolicy
{
    internal static readonly TimeSpan BareActivationDuplicateWindow =
        TimeSpan.FromMilliseconds(750);

    public static BareExternalActivationAction DecideBareActivation(
        BareExternalActivationContext context)
    {
        return context.HasConfiguredFileWidgets && !context.HasVisibleFileWidgets
            ? BareExternalActivationAction.RestoreAllWidgetsAndOpenSettings
            : BareExternalActivationAction.OpenSettingsOnly;
    }

    public static bool ShouldCoalesceBareActivation(
        DateTimeOffset? lastActivationAtUtc,
        DateTimeOffset currentActivationAtUtc,
        bool isSettingsWindowOpen)
    {
        if (!isSettingsWindowOpen || lastActivationAtUtc is null)
        {
            return false;
        }

        TimeSpan elapsed = currentActivationAtUtc - lastActivationAtUtc.Value;
        return elapsed >= TimeSpan.Zero && elapsed <= BareActivationDuplicateWindow;
    }
}
