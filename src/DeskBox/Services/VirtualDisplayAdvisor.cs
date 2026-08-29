using DeskBox.Helpers;

namespace DeskBox.Services;

/// <summary>
/// One-time-per-session advisor that warns when the primary monitor is backed
/// by a virtual display adapter. Certain virtual GPU drivers (cloud-gaming
/// clients) break WinUI 3 display metrics and render pipelines, which surfaced
/// as fail-fast crashes and widgets stacking at default coordinates. The
/// advice is purely informational: DeskBox never changes behavior based on it.
/// </summary>
internal static class VirtualDisplayAdvisor
{
    private static readonly HashSet<string> s_warnedDeviceIds =
        new(StringComparer.OrdinalIgnoreCase);
    private static int _isRunning;

    /// <summary>
    /// Checks the primary display adapter and, when it looks virtual, raises
    /// <paramref name="showWarning"/> once per device id per session. The
    /// callback receives localization keys and is responsible for showing the
    /// toast. Safe to call on every display-topology change; concurrent and
    /// duplicate calls are collapsed.
    /// </summary>
    public static void WarnIfPrimaryDisplayIsVirtual(
        Action<string, string> showWarning)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            return;
        }

        try
        {
            if (!Win32Helper.TryGetPrimaryDisplayAdapter(
                    out string deviceId,
                    out string deviceString))
            {
                return;
            }

            if (!IsLikelyVirtualAdapter(deviceId, deviceString))
            {
                return;
            }

            lock (s_warnedDeviceIds)
            {
                if (!s_warnedDeviceIds.Add(deviceId))
                {
                    return;
                }
            }

            App.Log(
                $"[Display] Virtual primary display adapter detected: " +
                $"{deviceId} ({deviceString})");

            showWarning(
                "Display.VirtualAdapter.Warning.Title",
                "Display.VirtualAdapter.Warning.Body");
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Display] Virtual display advisory failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    /// <summary>
    /// Conservative classification: only adapters enumerated under the ROOT
    /// device tree or explicitly named as virtual. Indirect-display drivers
    /// (streaming tools) are deliberately not flagged to avoid nagging users
    /// with working setups.
    /// </summary>
    private static bool IsLikelyVirtualAdapter(string deviceId, string deviceString)
    {
        return deviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ||
               deviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
    }
}
