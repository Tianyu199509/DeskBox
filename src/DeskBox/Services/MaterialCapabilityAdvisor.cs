namespace DeskBox.Services;

/// <summary>
/// One-time-per-session advisor that explains why the frosted-glass material
/// looks solid. Windows silently swaps Mica/Acrylic for the controller's
/// fallback color when transparency effects are turned off or battery saver
/// is active; the app cannot tell that happened from the backdrop pipeline,
/// so without this hint the material just "does not work" on those machines.
/// Only material users are warned — Solid has no glass to degrade.
/// </summary>
internal static class MaterialCapabilityAdvisor
{
    private static readonly HashSet<string> s_warnedCauses =
        new(StringComparer.Ordinal);
    private static int _isRunning;
    private static int s_energySaverSubscribed;

    /// <summary>
    /// Checks the system switches that degrade Mica/Acrylic and raises
    /// <paramref name="showWarning"/> once per cause per session. The callback
    /// receives localization keys and is responsible for showing the toast.
    /// Safe to call on every settings change; concurrent and duplicate calls
    /// are collapsed.
    /// </summary>
    public static void WarnIfMaterialDegraded(
        string? materialType,
        Action<string, string> showWarning)
    {
        if (!SettingsService.IsMicaMaterial(materialType) &&
            !SettingsService.IsAcrylicMaterial(materialType))
        {
            return;
        }

        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            return;
        }

        try
        {
            if (!WindowsCompatibilityService.AreAdvancedEffectsEnabled &&
                WarnOnce("transparency-off"))
            {
                App.Log(
                    "[Material] Transparency effects are off; Mica/Acrylic " +
                    "renders as the fallback color.");
                showWarning(
                    "Display.Material.TransparencyOff.Title",
                    "Display.Material.TransparencyOff.Body");
            }

            // Battery saver degrades system-backdrop controllers on both Win11
            // (Mica/Acrylic) and Win10 19041+ (DesktopAcrylicController).
            if ((WindowsCompatibilityService.SupportsMica ||
                 WindowsCompatibilityService.SupportsDesktopAcrylic) &&
                IsBatterySaverActive() &&
                WarnOnce("battery-saver"))
            {
                App.Log(
                    "[Material] Battery saver is on; Windows paused the " +
                    "Mica/Acrylic material.");
                showWarning(
                    "Display.Material.BatterySaver.Title",
                    "Display.Material.BatterySaver.Body");
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Material] Capability advisory failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private static bool WarnOnce(string cause)
    {
        lock (s_warnedCauses)
        {
            return s_warnedCauses.Add(cause);
        }
    }

    /// <summary>
    /// Hooks the process-wide energy-saver switch so battery saver turned on
    /// mid-session is explained too — startup and settings changes are the
    /// only other evaluation points, yet the switch can flip at any moment.
    /// The callback is expected to marshal onto the UI thread before
    /// evaluating, matching App.OnMaterialCapabilitySettingsChanged.
    /// </summary>
    public static void Initialize(Action warnIfMaterialDegradedOnUiThread)
    {
        if (Interlocked.Exchange(ref s_energySaverSubscribed, 1) == 1)
        {
            return;
        }

        // A process-wide static event: the subscription intentionally lives
        // for the whole process lifetime, so it is never unsubscribed.
        Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) =>
        {
            // Only the On transition warrants the hint; turning saver off
            // restores the material by itself and needs no explanation.
            if (!IsBatterySaverActive())
            {
                return;
            }

            warnIfMaterialDegradedOnUiThread();
        };

        // The transparency-effects switch can flip mid-session too (Win11
        // 22H2+ only — the service simply never raises the event on older
        // builds). Same polarity: only the Off transition degrades Mica.
        WindowsCompatibilityService.AdvancedEffectsEnabledChanged += () =>
        {
            if (WindowsCompatibilityService.AreAdvancedEffectsEnabled)
            {
                return;
            }

            warnIfMaterialDegradedOnUiThread();
        };
    }

    private static bool IsBatterySaverActive()
    {
        try
        {
            return Windows.System.Power.PowerManager.EnergySaverStatus ==
                Windows.System.Power.EnergySaverStatus.On;
        }
        catch
        {
            return false;
        }
    }
}
