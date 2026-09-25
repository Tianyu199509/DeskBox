namespace DeskBox.Services;

/// <summary>
/// One-shot fault points for isolated Debug group-window diagnostics. These
/// have no effect without a developer data root and are inert in Release.
/// </summary>
internal static class WidgetGroupFailureProbe
{
#if DEBUG
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        Consumed = new(StringComparer.Ordinal);
#endif

    internal static bool Consume(string stage)
    {
#if DEBUG
        string? dataRoot = Environment.GetEnvironmentVariable(
            "DESKBOX_DEV_DATA_ROOT");
        string? requested = Environment.GetEnvironmentVariable(
            "DESKBOX_DEV_GROUP_FAIL_STAGE");
        if (!IsRequested(stage, requested, !string.IsNullOrWhiteSpace(dataRoot)) ||
            !Consumed.TryAdd(stage, 0))
        {
            return false;
        }

        App.Log($"[WidgetGroupProbe] Injecting failure stage={stage}");
        return true;
#else
        return false;
#endif
    }

    internal static bool IsRequested(
        string stage,
        string? requested,
        bool hasDeveloperDataRoot)
    {
        return hasDeveloperDataRoot &&
               !string.IsNullOrWhiteSpace(stage) &&
               requested?.Split(',', StringSplitOptions.TrimEntries |
                                     StringSplitOptions.RemoveEmptyEntries)
                   .Contains(stage, StringComparer.Ordinal) == true;
    }

    internal static void ThrowIfRequested(string stage)
    {
        if (Consume(stage))
        {
            throw new InvalidOperationException(
                $"Injected isolated group failure at '{stage}'.");
        }
    }
}
