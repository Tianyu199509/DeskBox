using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// Delegates shell launches to the running Explorer desktop process so child
/// applications receive the same user environment as desktop-launched apps.
/// </summary>
internal static class ExplorerShellLaunchService
{

    public static bool TryOpen(
        string path,
        string workingDirectory,
        string verb,
        out string? error)
    {
        return TryOpen(
            path,
            workingDirectory,
            verb,
            out error,
            out _,
            out _);
    }

    internal static bool TryOpen(
        string path,
        string workingDirectory,
        string verb,
        out string? error,
        out ExplorerShellLaunchNativeCallResult? nativeResult)
    {
        return TryOpen(
            path,
            workingDirectory,
            verb,
            out error,
            out nativeResult,
            out _);
    }

    internal static bool TryOpen(
        string path,
        string workingDirectory,
        string verb,
        out string? error,
        out ExplorerShellLaunchNativeCallResult? nativeResult,
        out int hresult)
    {
        nativeResult = null;
        hresult = 0;
        TryGrantExplorerForegroundActivation();
        nativeResult = ExplorerShellLaunchNativeBackend.TryOpen(
            path,
            workingDirectory,
            verb);
        error = nativeResult.Success ? null : $"{nativeResult.Failure}: {nativeResult.Detail}";
        hresult = SelectFailureHResult(nativeResult);
        return nativeResult.Success;
    }

    /// <summary>
    /// Picks the HRESULT that best represents a failed native launch. The
    /// aggregate <see cref="ExplorerShellLaunchNativeCallResult.OperationHResult"/>
    /// is regularly S_OK while one phase carries the real code, so an
    /// RPC-class phase wins first — that is the signature the launch circuit
    /// breaker keys on — before any phase that actually ran and failed is
    /// surfaced.
    /// </summary>
    private static int SelectFailureHResult(ExplorerShellLaunchNativeCallResult result)
    {
        if (result.Success)
        {
            return 0;
        }

        int[] candidates =
        {
            result.OperationHResult,
            result.ComHResult,
            result.CreateHResult,
            result.WindowsHResult,
            result.DesktopHResult,
            result.DocumentHResult,
            result.ApplicationHResult,
            result.ExecuteHResult
        };

        foreach (int candidate in candidates)
        {
            if (ExplorerLaunchCircuitBreaker.IsRpcClassFailure(candidate))
            {
                return candidate;
            }
        }

        foreach (int candidate in candidates)
        {
            if (candidate < 0 && candidate != ShortcutNativeModule.HResultNotAttempted)
            {
                return candidate;
            }
        }

        return 0;
    }

    private static void TryGrantExplorerForegroundActivation()
    {
        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            App.Log("[ShellActivation] Explorer foreground grant skipped: shell window unavailable");
            return;
        }

        _ = Win32Helper.GetWindowThreadProcessId(shellWindow, out uint explorerProcessId);
        if (explorerProcessId == 0)
        {
            App.Log("[ShellActivation] Explorer foreground grant skipped: process unavailable");
            return;
        }

        Win32Helper.SetLastError(0);
        bool granted = Win32Helper.AllowSetForegroundWindow(explorerProcessId);
        int error = granted ? 0 : Marshal.GetLastWin32Error();
        if (granted)
        {
            App.LogVerbose(
                $"[ShellActivation] Explorer foreground grant succeeded " +
                $"pid={explorerProcessId}");
            return;
        }

        App.Log(
            $"[ShellActivation] Explorer foreground grant was unavailable " +
            $"pid={explorerProcessId} error={error}");
    }
}
