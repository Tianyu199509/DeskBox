using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Converts native lifecycle messages into stable recovery reasons. Keeping
/// this mapping independent from the Win32 window subclass makes the sleep,
/// display/DPI, session, and Explorer-restart recovery contract testable.
/// </summary>
internal static class AppLifecycleRecoverySignalClassifier
{
    internal const uint WmPowerBroadcast = 0x0218;
    internal const uint WmWtsSessionChange = 0x02B1;
    internal const uint WmDisplayChange = 0x007E;
    internal const uint WmDpiChanged = 0x02E0;

    private const uint PbtResumeAutomatic = 0x0012;
    private const uint PbtResumeSuspend = 0x0007;
    private const uint PbtResumeCritical = 0x0006;
    private const uint WtsSessionUnlock = 0x0008;
    private const uint WtsSessionLogon = 0x0005;
    private const uint WtsSessionRemoteConnect = 0x0009;

    internal static string? ResolveRecoveryReason(
        uint message,
        UIntPtr wParam,
        uint taskbarCreatedMessage) =>
        ResolveRecoveryReason(message, wParam, IntPtr.Zero, taskbarCreatedMessage);

    internal static string? ResolveRecoveryReason(
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint taskbarCreatedMessage)
    {
        uint eventValue = unchecked((uint)wParam.ToUInt64());
        if (message == WmPowerBroadcast &&
            eventValue is PbtResumeAutomatic or PbtResumeSuspend or PbtResumeCritical)
        {
            return "resume";
        }

        // Idle background apps get their working sets trimmed and hooks
        // starved while the display is off; "display on" is the earliest
        // reliable signal that the user is back and input should work again.
        // "display off" (Data=0) is a gate signal, not a recovery reason —
        // topology changes while the screen is off must not be applied.
        if (message == WmPowerBroadcast &&
            eventValue == Win32Helper.PbtPowerSettingChange)
        {
            return ResolveConsoleDisplayState(lParam) switch
            {
                1 => "display-power-on",
                0 => "display-power-off",
                _ => null
            };
        }

        if (message == WmWtsSessionChange &&
            eventValue is WtsSessionUnlock or WtsSessionLogon or WtsSessionRemoteConnect)
        {
            return eventValue == WtsSessionUnlock
                ? "session-unlock"
                : "session-reconnect";
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            return "display-message";
        }

        return message == taskbarCreatedMessage
            ? "explorer-restart"
            : null;
    }

    /// <summary>
    /// Console-display power state from a GUID_CONSOLE_DISPLAY_STATE
    /// broadcast: 1 = on, 0 = off, 2 = dim (treated as unknown here), -1 =
    /// not a console-display event.
    /// </summary>
    internal static int ResolveConsoleDisplayState(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            var setting = Marshal.PtrToStructure<Win32Helper.PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting != Win32Helper.ConsoleDisplayStatePowerSetting ||
                setting.DataLength < 1)
            {
                return -1;
            }

            return setting.Data switch
            {
                1 => 1,
                0 => 0,
                _ => 2
            };
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
