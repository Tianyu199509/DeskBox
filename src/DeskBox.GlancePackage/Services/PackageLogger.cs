namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Package-side verbose logger. The sink is wired to the host log callback
/// when the ABI session activates (Abi.Exports); before activation, or when
/// the host provides no callback, messages are dropped. D3 Phase 2: replaces
/// the host-mimicking App.LogVerbose seam, which silently discarded output -
/// package diagnostics now actually reach the host log.
/// </summary>
internal static class PackageLogger
{
    internal static Action<string>? Sink;

    internal static void LogVerbose(string message) => Sink?.Invoke(message);
}
