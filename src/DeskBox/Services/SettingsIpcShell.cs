namespace DeskBox.Services;

/// <summary>
/// Shared transport plumbing for the Remote settings facades (deep review
/// R13): the fire-and-forget notify helper existed as four verbatim copies
/// and the void-request helper as five signature-drifted ones, so a fix to
/// either had to be applied per file. Behavior here is exactly what those
/// copies did — notifications never throw (the settings-host UI thread must
/// not block or fault on the pipe, batches R10/R11) and log refusals with
/// the caller's name; void requests stay transparent, propagating whatever
/// the transport throws and discarding the ok flag.
/// </summary>
internal static class SettingsIpcShell
{
    /// <summary>
    /// Sends a pure-notification request whose Task the caller discards;
    /// it never throws (the settings-host UI thread must not block or
    /// fault on the pipe, batches R10/R11) and logs refusals with the
    /// caller's name.
    /// </summary>
    internal static Task NotifyWithoutBlockingAsync(
        SettingsIpcClient client,
        string method,
        string? payload,
        string caller)
        => NotifyCoreAsync(client, method, payload, caller);

    private static async Task NotifyCoreAsync(
        SettingsIpcClient client,
        string method,
        string? payload,
        string caller)
    {
        try
        {
            (bool ok, _, string? error) = await client.RequestAsync(method, payload);
            if (!ok)
            {
                App.Log($"[SettingsIpc] {caller} failed: {error ?? "unknown error"}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsIpc] {caller} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Forwards a request whose result tuple nobody consumes; failures
    /// still surface to the caller as transport exceptions, exactly like
    /// the per-facade copies did.
    /// </summary>
    internal static Task RequestVoidAsync(
        SettingsIpcClient client,
        string method,
        string? payload = null,
        CancellationToken cancellationToken = default)
        => client.RequestAsync(method, payload, cancellationToken);
}
